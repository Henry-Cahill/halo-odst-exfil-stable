using Dropshock.Core;
using Dropshock.Net.Player;
using Fusion;
using UnityEngine;

namespace Dropshock.Net.AI
{
    /// <summary>
    /// A minimal hostile: notice a player, close the distance, shoot them.
    ///
    /// <para><b>Runs on the server only.</b> Every decision and every point of damage
    /// originates here, on the state authority. Clients see the result through the
    /// replicated transform and nothing else — an AI that ran on clients would let a
    /// modified one decide it had been missed.</para>
    ///
    /// <para><b>Steering is deliberately direct, not navmesh.</b> The test scene has
    /// no baked NavMesh, and a NavMeshAgent would silently fail to move rather than
    /// fail loudly. This is enough to produce a real cause of death now; pathfinding
    /// replaces it once maps have geometry worth pathing around, and the seam is this
    /// one method.</para>
    /// </summary>
    [RequireComponent(typeof(EnemyHealth))]
    public sealed class EnemyBrain : NetworkBehaviour
    {
        [Header("Perception")]
        [Tooltip("Distance at which a living player is noticed.")]
        [SerializeField] private float _sightRange = 25f;

        [Tooltip("Distance at which the enemy stops closing and starts firing.")]
        [SerializeField] private float _attackRange = 12f;

        [Tooltip("Virtual metres added to a downed player's distance when RANKING " +
                 "targets. Applies to preference only, never to whether the player is " +
                 "visible, so a lone downed player is always still finished off. Set " +
                 "it at or above sightRange to guarantee any standing player wins.")]
        [SerializeField] private float _downedTargetPenalty = 40f;

        [Header("Movement")]
        [SerializeField] private float _moveSpeed = 3.2f;
        [SerializeField] private float _turnDegreesPerSecond = 540f;

        [Header("Weapon")]
        [SerializeField] private float _damage = 9f;
        [SerializeField] private float _shotsPerMinute = 90f;

        [Tooltip("Height above the pivot that shots originate from, so fire is not " +
                 "cast from the floor.")]
        [SerializeField] private float _muzzleHeight = 1.4f;

        [Tooltip("Layers that block line of sight. Excluding a layer here lets the " +
                 "enemy shoot through it.")]
        [SerializeField] private LayerMask _sightBlockers = ~0;

        [Networked] private TickTimer FireCooldown { get; set; }

        private EnemyHealth _health;
        private Transform _target;

        /// <summary>Who <see cref="_target"/> belongs to, kept so a switch can be reported.</summary>
        private PlayerRef _targetPlayer = PlayerRef.None;

        // Target switching is normally rare, but two near-equidistant candidates can
        // flip it every tick. Logging each flip would flood; suppressing them would
        // hide the thrash, which is itself the bug. So: report switches as they
        // happen, and if they exceed a sane rate, say that instead.
        private float _switchWindowStart;
        private int _switchesInWindow;
        private float _nextThrashWarning;

        public override void Spawned()
        {
            _health = GetComponent<EnemyHealth>();

            // Ranking is linear, so a downed candidate beats a standing one exactly
            // when raw + penalty < standing distance. The worst case a standing player
            // can present is _sightRange, so penalty >= _sightRange is precisely the
            // condition that makes "any standing player wins" a guarantee rather than
            // a tendency. Below it the rule holds only at some ranges, which is the
            // hardest kind of behaviour to diagnose from a playtest — it works until
            // the geometry happens to be wrong.
            if (_downedTargetPenalty < _sightRange)
            {
                Debug.LogWarning(
                    $"[AI] '{name}': downedTargetPenalty ({_downedTargetPenalty}m) is less than " +
                    $"sightRange ({_sightRange}m), so a nearby downed player can outrank a distant " +
                    "standing one and enemies will sometimes ignore a live threat to finish off a " +
                    $"body. Set the penalty to at least {_sightRange}m to make standing players " +
                    "always win.", this);
            }

            if (_downedTargetPenalty <= 0f)
            {
                Debug.LogWarning(
                    $"[AI] '{name}': downedTargetPenalty is {_downedTargetPenalty}, so downed " +
                    "players are not deprioritised at all. Enemies will camp bodies over standing " +
                    "threats and revives become impossible.", this);
            }
        }

        public override void FixedUpdateNetwork()
        {
            // Authority-only. This is the rule that makes AI damage trustworthy.
            if (!HasStateAuthority) return;
            if (_health == null || !_health.IsAlive) return;

            AcquireTarget();

            if (_target == null) return;

            Vector3 toTarget = _target.position - transform.position;
            toTarget.y = 0f;
            float distance = toTarget.magnitude;

            FaceTarget(toTarget);

            if (distance > _attackRange)
            {
                // Closing. Deliberately no obstacle avoidance yet — see class remarks.
                transform.position += toTarget.normalized * (_moveSpeed * Runner.DeltaTime);
                return;
            }

            TryFire();
        }

        /// <summary>
        /// Picks the nearest living player within sight range.
        ///
        /// <para>Re-evaluated every tick rather than latched, so a dead or despawned
        /// target cannot leave the enemy firing at nothing — and so the enemy
        /// switches to a closer threat.</para>
        /// </summary>
        private void AcquireTarget()
        {
            Transform best = null;
            PlayerRef bestPlayer = PlayerRef.None;
            float bestScore = float.MaxValue;
            float bestRawSqr = 0f;
            bool bestDowned = false;

            float sightRangeSqr = _sightRange * _sightRange;

            foreach (PlayerRef player in Runner.ActivePlayers)
            {
                NetworkObject obj = Runner.GetPlayerObject(player);
                if (obj == null) continue;

                // Never target the dead: it makes enemies look broken and wastes
                // damage on something that cannot receive it.
                var health = obj.GetComponent<PlayerHealth>();
                if (health == null || !health.IsAlive)
                {
                    TraceAi($"candidate {player}: skipped ({(health == null ? "no PlayerHealth" : "dead")})");
                    continue;
                }

                float rawSqr = (obj.transform.position - transform.position).sqrMagnitude;

                // Visibility is answered on RAW distance only. Being downed does not
                // make a player harder to see, and folding the penalty into this test
                // is what made a lone downed player unkillable: the penalty is larger
                // than sight range by design, so any downed candidate failed the sight
                // check outright instead of merely losing a ranking.
                if (rawSqr > sightRangeSqr)
                {
                    TraceAi($"candidate {player}: raw={Mathf.Sqrt(rawSqr):F1}m out of sight range {_sightRange}m");
                    continue;
                }

                // Preference is a separate question, and it is scored in LINEAR metres
                // because that is what the field is documented in. Adding penalty² to
                // a squared distance is not the same operation as adding penalty to a
                // distance, and the difference is the whole bug.
                bool downed = health.IsDowned;
                float score = Mathf.Sqrt(rawSqr) + (downed ? _downedTargetPenalty : 0f);

                bool rejected = score >= bestScore;

                // The distinction that matters when checking the penalty: a candidate
                // that would have been in contention on raw distance and lost purely
                // because it is downed. That is the penalty doing its job. It can no
                // longer cost a lone downed player its life, because with nothing else
                // in contention bestScore is still MaxValue and it wins regardless.
                bool rejectedOnlyByPenalty = rejected && downed && Mathf.Sqrt(rawSqr) < bestScore;

                TraceAi(
                    $"candidate {player}: raw={Mathf.Sqrt(rawSqr):F1}m downed={downed} " +
                    $"score={score:F1} -> " +
                    (rejectedOnlyByPenalty ? "REJECTED BY DOWNED PENALTY ALONE"
                                           : rejected ? "rejected" : "leading"));

                if (rejected) continue;

                bestScore = score;
                bestRawSqr = rawSqr;
                bestDowned = downed;
                best = obj.transform;
                bestPlayer = player;
            }

            ReportTargetChange(bestPlayer, bestRawSqr, bestDowned);

            _target = best;
            _targetPlayer = bestPlayer;
        }

        /// <summary>
        /// Logs acquisition, loss and switching of targets.
        ///
        /// <para>Unconditional, unlike <see cref="TraceAi"/>, because these are the
        /// events that answer "is the AI reacting to the downed state at all?" and a
        /// trace behind a define nobody has enabled answers nothing. They are
        /// event-driven rather than per-tick, so the volume is low — except when the
        /// choice oscillates, which is handled explicitly.</para>
        /// </summary>
        private void ReportTargetChange(PlayerRef newPlayer, float rawSqr, bool downed)
        {
            if (newPlayer == _targetPlayer) return;

            float now = Time.unscaledTime;
            if (now - _switchWindowStart > 1f)
            {
                _switchWindowStart = now;
                _switchesInWindow = 0;
            }
            _switchesInWindow++;

            // Oscillating between near-equidistant candidates. Report the condition
            // once rather than every flip; the rate is the diagnosis, not the flips.
            if (_switchesInWindow > 4)
            {
                if (now >= _nextThrashWarning)
                {
                    _nextThrashWarning = now + 5f;
                    Debug.LogWarning(
                        $"[AI] '{name}' is thrashing between targets " +
                        $"({_switchesInWindow} switches in the last second). Candidates are likely " +
                        "near-equidistant; targeting needs hysteresis.", this);
                }
                return;
            }

            if (newPlayer == PlayerRef.None)
            {
                Debug.Log($"[AI] '{name}' lost target (was {_targetPlayer})", this);
                return;
            }

            string from = _targetPlayer == PlayerRef.None ? "none" : _targetPlayer.ToString();
            Debug.Log(
                $"[AI] '{name}' target {from} -> {newPlayer} " +
                $"at {Mathf.Sqrt(rawSqr):F1}m downed={downed}", this);
        }

        /// <summary>
        /// Per-tick, per-candidate targeting detail, compiled out unless
        /// DROPSHOCK_TRACE_AI is defined (Project Settings ▸ Player ▸ Scripting Define
        /// Symbols).
        ///
        /// <para>This runs once per player per tick, so it cannot be unconditional.
        /// It exists because the downed penalty is otherwise unobservable: a penalty
        /// that does nothing, a penalty applied to the wrong player, and correct
        /// behaviour that merely looks wrong from outside all present identically —
        /// as an enemy walking somewhere. Same rationale as DROPSHOCK_TRACE_DOWNED
        /// and DROPSHOCK_TRACE_SPAWNER.</para>
        /// </summary>
        [System.Diagnostics.Conditional("DROPSHOCK_TRACE_AI")]
        private void TraceAi(string message)
        {
            Debug.Log($"[AI] tick={Runner.Tick} '{name}' {message}", this);
        }

        private void FaceTarget(Vector3 flatDirection)
        {
            if (flatDirection.sqrMagnitude < 0.0001f) return;

            Quaternion wanted = Quaternion.LookRotation(flatDirection.normalized, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, wanted, _turnDegreesPerSecond * Runner.DeltaTime);
        }

        private void TryFire()
        {
            if (!FireCooldown.ExpiredOrNotRunning(Runner)) return;
            if (_shotsPerMinute <= 0f) return;

            Vector3 origin = transform.position + Vector3.up * _muzzleHeight;
            Vector3 targetPoint = _target.position + Vector3.up * _muzzleHeight;
            Vector3 direction = (targetPoint - origin).normalized;
            float distance = Vector3.Distance(origin, targetPoint);

            // Line of sight, so enemies do not shoot through walls. A plain raycast is
            // correct here: unlike a player's shot there is nothing to rewind, because
            // the server is both shooter and authority — no client view to reconcile.
            if (Physics.Raycast(origin, direction, out RaycastHit hit, distance, _sightBlockers,
                                QueryTriggerInteraction.Ignore))
            {
                // Something solid is in the way, and it is not the target.
                if (hit.transform.root != _target.root) return;
            }

            FireCooldown = TickTimer.CreateFromSeconds(Runner, 60f / _shotsPerMinute);

            var damageable = _target.GetComponentInParent<IDamageable>();
            if (damageable == null || !damageable.IsAlive) return;

            damageable.ApplyDamage(new DamageInfo(
                amount: _damage,
                point: targetPoint,
                direction: direction,
                instigatorTeam: _health != null ? _health.Team : TeamId.Hostile,
                instigatorPlayerId: -1));
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.5f, 0f, 0.35f);
            Gizmos.DrawWireSphere(transform.position, _sightRange);
            Gizmos.color = new Color(1f, 0f, 0f, 0.5f);
            Gizmos.DrawWireSphere(transform.position, _attackRange);
        }
    }
}
