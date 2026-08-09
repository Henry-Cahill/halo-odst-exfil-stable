using Dropshock.Core;
using Dropshock.Net.Session;
using Fusion;
using UnityEngine;

namespace Dropshock.Net.Player
{
    /// <summary>
    /// Networked health. All mutation is server-side; clients read for UI only.
    /// <para>
    /// Tuned toward the ODST fantasy: no regenerating energy shield, a small pool
    /// of stamina-style shielding that only recovers out of combat, and health that
    /// requires an item to restore. Numbers here are placeholders for design tuning.
    /// </para>
    /// </summary>
    public sealed class PlayerHealth : NetworkBehaviour, IDamageable
    {
        [Header("Pools")]
        [SerializeField] private float _maxHealth = 100f;
        [SerializeField] private float _maxShield = 45f;

        [Header("Shield Recovery")]
        [SerializeField] private float _shieldRegenPerSecond = 15f;
        [Tooltip("Seconds without taking damage before shield begins recovering.")]
        [SerializeField] private float _shieldRegenDelay = 6f;

        [Header("Downed")]
        [Tooltip("Size of the bleed-out pool entered on being downed. While downed " +
                 "this replaces Health: it drains with time AND with further damage, " +
                 "so one number covers both bleeding out and being finished off.")]
        [SerializeField] private float _downedPool = 100f;

        [Tooltip("Seconds to bleed out from a full pool if nobody intervenes.")]
        [SerializeField] private float _bleedOutSeconds = 45f;

        [Tooltip("Seconds a squadmate must hold Interact to complete a revive.")]
        [SerializeField] private float _reviveSeconds = 5f;

        [Tooltip("How close the reviver must be. Checked server-side against the " +
                 "server's positions, never a client-reported one.")]
        [SerializeField] private float _reviveRange = 2.5f;

        [Tooltip("Fraction of max health restored on being revived. Deliberately " +
                 "partial: a free full heal makes going down cost nothing.")]
        [SerializeField, Range(0.05f, 1f)] private float _reviveHealthFraction = 0.5f;

        [Tooltip("Can a player revive themselves? Off by default — self-revive turns " +
                 "downed into a soft speed bump for solo players and removes the " +
                 "squad dependency the state exists to create.")]
        [SerializeField] private bool _allowSelfRevive;

        [Networked] public float Health { get; private set; }
        [Networked] public float Shield { get; private set; }
        [Networked] public NetworkBool IsDead { get; private set; }
        [Networked] public TeamId Team { get; set; }
        [Networked] private TickTimer RegenDelayTimer { get; set; }

        /// <summary>
        /// Incapacitated but not dead: gear retained, revivable, bleeding out.
        /// <para>
        /// Replicated because every peer needs it — it drives the downed pose, the
        /// revive prompt on squadmates' screens, and AI target selection.
        /// </para>
        /// </summary>
        [Networked] public NetworkBool IsDowned { get; private set; }

        /// <summary>0..1 revive progress on this player. Replicated to drive the bar.</summary>
        [Networked] public float ReviveProgress { get; private set; }

        private GameSession _session;
        private ChangeDetector _changes;
        private Session.SessionConfig _sessionConfig;

        /// <summary>
        /// Last tick some reviver contributed progress. Server-only; used to decay
        /// progress when they stop or walk away.
        /// <para>
        /// Compared against <c>Runner.Tick</c> rather than set from a per-tick reset
        /// flag, because every player's <see cref="FixedUpdateNetwork"/> runs in the
        /// same pass and their relative order is not guaranteed. A reset flag would
        /// work or not depending on that order.
        /// </para>
        /// </summary>
        private int _lastReviveTick = int.MinValue;

        /// <summary>
        /// Set by <see cref="Die"/>, consumed on this object's next tick. Server-only,
        /// so it needs no rollback: the state authority does not resimulate.
        /// </summary>
        private bool _lootDropPending;

        /// <summary>
        /// Alive in the sense that matters to weapons and AI: a downed player is
        /// still a legal target and can still be damaged. Finishing someone off is
        /// meant to be possible.
        /// </summary>
        public bool IsAlive => !IsDead;

        /// <summary>
        /// May this player shoot, jump and act? False while downed.
        /// <para>
        /// Deliberately separate from <see cref="IsAlive"/>. Conflating the two is
        /// what makes a downed player either invulnerable or still able to fight,
        /// and both are wrong.
        /// </para>
        /// </summary>
        public bool CanAct => !IsDead && !IsDowned;

        /// <summary>Raised on all peers when health or shield changes. UI hooks here.</summary>
        public event System.Action<float, float> OnVitalsChanged;

        /// <summary>Raised on all peers when this player dies.</summary>
        public event System.Action OnDied;

        /// <summary>Raised on all peers when this player enters the downed state.</summary>
        public event System.Action OnDowned;

        /// <summary>Raised on all peers when this player is revived.</summary>
        public event System.Action OnRevived;

        public override void Spawned()
        {
            _changes = GetChangeDetector(ChangeDetector.Source.SimulationState);
            _session = FindFirstObjectByType<GameSession>();

            var launcher = FindFirstObjectByType<Session.NetworkLauncher>();
            _sessionConfig = launcher != null ? launcher.Config : null;

            if (HasStateAuthority)
            {
                Health = _maxHealth;
                Shield = _maxShield;
                IsDead = false;
                IsDowned = false;
                ReviveProgress = 0f;
            }
        }

        /// <summary>
        /// Whether this mode uses the downed state at all. Battle royale with no
        /// squad has nobody to revive you, so going down would just be a slower
        /// death with a worse camera.
        /// </summary>
        private bool DownedEnabled =>
            _sessionConfig == null || _sessionConfig.EnableDownedState;

        /// <summary>
        /// Whether squadmates can damage each other. **On in co-op by design.**
        ///
        /// <para>Defaults to true when there is no config, deliberately. The previous
        /// rule inferred this from team ids and refused damage when instigator and
        /// target matched — but it only engaged once teams were actually assigned,
        /// so in every scene that leaves <see cref="TeamId.Unassigned"/> in place it
        /// silently did nothing. A protection that is off exactly where it is
        /// untested is worse than no protection, because it reads as working.</para>
        /// </summary>
        private bool FriendlyFireEnabled =>
            _sessionConfig == null || _sessionConfig.EnableFriendlyFire;

        public override void FixedUpdateNetwork()
        {
            if (!HasStateAuthority) return;

            // Deferred from Die(). Dropping loot spawns NetworkObjects, and Die() is
            // reached from another object's FixedUpdateNetwork (the shooter's weapon),
            // so spawning there happens mid-iteration of the simulation. Doing it on
            // this object's own tick keeps the spawn inside a clean simulation step.
            if (_lootDropPending)
            {
                _lootDropPending = false;
                DropCarriedLoot();
            }

            if (IsDead) return;

            // This player's own downed bookkeeping.
            if (IsDowned)
            {
                TickBleedOut();
            }
            else if (Shield < _maxShield && RegenDelayTimer.ExpiredOrNotRunning(Runner))
            {
                Shield = Mathf.Min(_maxShield, Shield + _shieldRegenPerSecond * Runner.DeltaTime);
            }

            // This player acting as a reviver for somebody else. Driven from input
            // rather than an RPC: input is already tick-aligned and server-validated,
            // and an RPC would be a second, weaker path to the same state change.
            bool haveInput = GetInput(out NetInput input);
            bool interact = haveInput && input.Buttons.IsSet(InputButton.Interact);

            // Traced because "I pressed Interact and nothing happened" has four
            // distinct causes that are indistinguishable in a playtest: no input
            // reaching the server at all, the button not set, CanAct false, or the
            // target scan finding nobody.
            //
            // Only logged while Interact is actually held, or when input is missing
            // entirely. Tracing every tick regardless buries the interesting lines
            // under thousands of idle ones — which is what happened the first time.
            if (interact || !haveInput)
            {
                TraceDowned(
                    $"input={(haveInput ? "yes" : "NO INPUT")} interact={interact} " +
                    $"canAct={CanAct} downed={IsDowned}");
            }

            if (CanAct && interact)
            {
                DriveRevive();
            }
        }

        /// <summary>
        /// Drains the bleed-out pool. Time and bullets share one number, so being
        /// shot while down simply brings the same end forward.
        /// </summary>
        private void TickBleedOut()
        {
            // Decay revive progress once nobody has contributed for a tick. Anchored
            // to Runner.Tick rather than a per-tick reset flag because player order
            // within the update pass is not guaranteed.
            if (Runner.Tick > _lastReviveTick + 1 && ReviveProgress > 0f)
            {
                ReviveProgress = Mathf.Max(0f, ReviveProgress - Runner.DeltaTime / _reviveSeconds);
            }

            if (_bleedOutSeconds > 0.01f)
            {
                float drain = _downedPool / _bleedOutSeconds;
                Health = Mathf.Max(0f, Health - drain * Runner.DeltaTime);
            }

            // Once a second, not every tick. Enough to prove the drain is running and
            // at what rate; not enough to bury the rest of the log.
            if (Runner.Tick % 30 == 0)
            {
                TraceDowned(
                    $"bleedout health={Health:F1}/{_downedPool} " +
                    $"secs={_bleedOutSeconds} dt={Runner.DeltaTime:F4} " +
                    $"revive={ReviveProgress:F2} lastReviveTick={_lastReviveTick}");
            }

            if (Health <= 0f)
            {
                Die(new DamageInfo(0f, transform.position, Vector3.zero));
            }
        }

        /// <summary>
        /// Finds the nearest downed squadmate in range and advances their revive.
        ///
        /// <para>Server-side, using the server's own transforms. A client-supplied
        /// position would make the range check decorative, which is the same rule the
        /// inventory system follows for container access.</para>
        /// </summary>
        private void DriveRevive()
        {
            PlayerHealth best = null;
            float bestSqr = _reviveRange * _reviveRange;

            // Diagnostic counters. Two int increments while Interact is held; the
            // trace call itself compiles out entirely.
            int seen = 0, downedFound = 0, rejectedTeam = 0, rejectedRange = 0;
            float nearestSqr = float.MaxValue;

            var all = FindObjectsByType<PlayerHealth>(FindObjectsSortMode.None);
            foreach (var other in all)
            {
                seen++;
                if (other == this && !_allowSelfRevive) continue;
                if (!other.IsDowned || other.IsDead) continue;
                downedFound++;

                // Reviving an enemy is not a thing. Unassigned teams are treated as
                // one squad so this stays usable before teams are implemented.
                if (Team != TeamId.Unassigned && other.Team != TeamId.Unassigned &&
                    other.Team != Team)
                {
                    rejectedTeam++;
                    continue;
                }

                float sqr = (other.transform.position - transform.position).sqrMagnitude;
                if (sqr < nearestSqr) nearestSqr = sqr;

                if (sqr <= bestSqr)
                {
                    bestSqr = sqr;
                    best = other;
                }
                else
                {
                    rejectedRange++;
                }
            }

            TraceDowned(
                $"revive scan seen={seen} downed={downedFound} " +
                $"outOfRange={rejectedRange} wrongTeam={rejectedTeam} " +
                $"nearest={(nearestSqr == float.MaxValue ? -1f : Mathf.Sqrt(nearestSqr)):F2}m " +
                $"range={_reviveRange}m target={(best == null ? "NONE" : best.Object.InputAuthority.ToString())}");

            best?.ReceiveReviveProgress(Runner.DeltaTime / Mathf.Max(0.01f, _reviveSeconds),
                                        Runner.Tick);
        }

        /// <summary>
        /// Per-tick tracing for the downed/revive path, compiled out unless
        /// DROPSHOCK_TRACE_DOWNED is defined (Project Settings ▸ Player ▸ Scripting
        /// Define Symbols).
        ///
        /// <para>Same rationale as <see cref="Weapons.NetworkWeapon"/>'s shot tracing:
        /// every failure here is silent. Bleed-out not draining, a revive finding no
        /// target, and input never arriving all present identically — as nothing
        /// happening — and cannot be told apart from the log as it stands.</para>
        /// </summary>
        [System.Diagnostics.Conditional("DROPSHOCK_TRACE_DOWNED")]
        private void TraceDowned(string message)
        {
            Debug.Log($"[Downed] tick={Runner.Tick} player={Object.InputAuthority} {message}", this);
        }

        /// <summary>Server-side: accept revive progress from a reviver.</summary>
        private void ReceiveReviveProgress(float amount, int tick)
        {
            if (!HasStateAuthority || !IsDowned || IsDead) return;

            _lastReviveTick = tick;
            ReviveProgress = Mathf.Min(1f, ReviveProgress + amount);

            TraceDowned($"revive progress {ReviveProgress:F3} (+{amount:F4})");

            if (ReviveProgress >= 1f)
            {
                CompleteRevive();
            }
        }

        private void CompleteRevive()
        {
            if (!HasStateAuthority || !IsDowned) return;

            IsDowned = false;
            ReviveProgress = 0f;
            Health = Mathf.Max(1f, _maxHealth * _reviveHealthFraction);
            Shield = 0f;

            // Do not hand back a regenerating shield instantly; make standing up a
            // vulnerable moment rather than a reset button.
            RegenDelayTimer = TickTimer.CreateFromSeconds(Runner, _shieldRegenDelay);

            Debug.Log($"[PlayerHealth] {Object.InputAuthority} revived.");
        }

        public void ApplyDamage(in DamageInfo info)
        {
            // Hard authority gate. A client calling this locally changes nothing
            // that anyone else will ever see.
            if (!HasStateAuthority) return;
            if (IsDead || info.Amount <= 0f) return;

            // Friendly fire is ON in co-op by design — see SessionConfig.
            // EnableFriendlyFire. Modes that disable it fall back to the team check,
            // which is only meaningful once teams are actually assigned.
            if (!FriendlyFireEnabled &&
                info.InstigatorTeam != TeamId.Unassigned &&
                info.InstigatorTeam == Team &&
                info.InstigatorPlayerId != -1)
            {
                return;
            }

            RegenDelayTimer = TickTimer.CreateFromSeconds(Runner, _shieldRegenDelay);

            // While downed there is no shield and no separate pool: damage eats the
            // bleed-out reserve directly, so being shot on the ground just finishes
            // what the timer would have.
            if (IsDowned)
            {
                Health = Mathf.Max(0f, Health - info.Amount);
                if (Health <= 0f) Die(info);
                return;
            }

            float remaining = info.Amount;

            if (Shield > 0f)
            {
                float absorbed = Mathf.Min(Shield, remaining);
                Shield -= absorbed;
                remaining -= absorbed;
            }

            if (remaining > 0f)
            {
                Health = Mathf.Max(0f, Health - remaining);
            }

            if (Health <= 0f)
            {
                if (DownedEnabled)
                {
                    EnterDowned();
                }
                else
                {
                    Die(info);
                }
            }
        }

        /// <summary>
        /// Incapacitate rather than kill. Gear is retained — that is the whole point
        /// of the state, and it is what makes a revive worth attempting.
        /// </summary>
        private void EnterDowned()
        {
            if (!HasStateAuthority || IsDead || IsDowned) return;

            IsDowned = true;
            Health = _downedPool;
            Shield = 0f;
            ReviveProgress = 0f;
            _lastReviveTick = int.MinValue;

            // Deliberately does NOT notify the session. A downed player is still
            // alive for win-condition purposes: they are revivable, and counting them
            // as lost would wipe a squad that is about to get back up. Bleed-out
            // reaches Die(), which does the notifying.
            Debug.Log($"[PlayerHealth] {Object.InputAuthority} downed.");
        }

        private void Die(in DamageInfo info)
        {
            if (!HasStateAuthority || IsDead) return;

            IsDead = true;
            IsDowned = false;
            ReviveProgress = 0f;
            Health = 0f;
            Shield = 0f;

            // Co-op extraction: the player's carried loot must reach the world so it
            // can be looted. Queued rather than done here — see FixedUpdateNetwork.
            // The delay is one tick and nothing can pick the gear up in that window,
            // because the corpse containers do not exist yet.
            _lootDropPending = true;

            _session?.NotifyPlayerNoLongerAlive();

            Debug.Log($"[PlayerHealth] {Object.InputAuthority} killed by player {info.InstigatorPlayerId}.");
        }

        /// <summary>
        /// Spills this player's inventory into world containers.
        ///
        /// <para>Shared with the disconnect path in <see cref="PlayerSpawner"/> so the
        /// two cannot drift apart: if dying drops gear but disconnecting does not,
        /// combat-logging becomes the optimal way to keep it.</para>
        /// </summary>
        private void DropCarriedLoot()
        {
            if (!HasStateAuthority) return;

            var inventory = GetComponent<Items.PlayerInventory>();
            if (inventory == null) return;

            inventory.DropAllToWorld(transform.position);
        }

        /// <summary>Server-side heal, e.g. from a consumed medkit.</summary>
        public void Heal(float amount)
        {
            if (!HasStateAuthority || IsDead || amount <= 0f) return;
            Health = Mathf.Min(_maxHealth, Health + amount);
        }

        public override void Render()
        {
            foreach (var change in _changes.DetectChanges(this))
            {
                switch (change)
                {
                    case nameof(Health):
                    case nameof(Shield):
                        OnVitalsChanged?.Invoke(Health, Shield);
                        break;

                    case nameof(IsDead):
                        if (IsDead) OnDied?.Invoke();
                        break;

                    case nameof(IsDowned):
                        // Fires on the way up as well as down. Die() clears IsDowned
                        // in the same tick it sets IsDead, so check IsDead too or a
                        // death reads as a revive on the client.
                        if (IsDowned) OnDowned?.Invoke();
                        else if (!IsDead) OnRevived?.Invoke();
                        break;
                }
            }
        }
    }
}
