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

        [Tooltip("Virtual metres added to a downed player's distance when choosing a " +
                 "target. Large enough that any standing player wins, small enough " +
                 "that a lone downed player is still finished off.")]
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

        public override void Spawned()
        {
            _health = GetComponent<EnemyHealth>();
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
            _target = null;
            float bestSqr = _sightRange * _sightRange;

            foreach (PlayerRef player in Runner.ActivePlayers)
            {
                NetworkObject obj = Runner.GetPlayerObject(player);
                if (obj == null) continue;

                // Never target the dead: it makes enemies look broken and wastes
                // damage on something that cannot receive it.
                var health = obj.GetComponent<PlayerHealth>();
                if (health == null || !health.IsAlive) continue;

                float sqr = (obj.transform.position - transform.position).sqrMagnitude;

                // Downed players stay targetable but are heavily deprioritised, so a
                // standing threat always wins. Ignoring them outright would make going
                // down a safe place to sit; preferring them would delete revives.
                if (health.IsDowned) sqr += _downedTargetPenalty * _downedTargetPenalty;

                if (sqr > bestSqr) continue;

                bestSqr = sqr;
                _target = obj.transform;
            }
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
