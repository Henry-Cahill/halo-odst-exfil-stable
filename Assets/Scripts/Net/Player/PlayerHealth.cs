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

        [Networked] public float Health { get; private set; }
        [Networked] public float Shield { get; private set; }
        [Networked] public NetworkBool IsDead { get; private set; }
        [Networked] public TeamId Team { get; set; }
        [Networked] private TickTimer RegenDelayTimer { get; set; }

        private GameSession _session;
        private ChangeDetector _changes;

        public bool IsAlive => !IsDead;

        /// <summary>Raised on all peers when health or shield changes. UI hooks here.</summary>
        public event System.Action<float, float> OnVitalsChanged;

        /// <summary>Raised on all peers when this player dies.</summary>
        public event System.Action OnDied;

        public override void Spawned()
        {
            _changes = GetChangeDetector(ChangeDetector.Source.SimulationState);
            _session = FindFirstObjectByType<GameSession>();

            if (HasStateAuthority)
            {
                Health = _maxHealth;
                Shield = _maxShield;
                IsDead = false;
            }
        }

        public override void FixedUpdateNetwork()
        {
            if (!HasStateAuthority || IsDead) return;

            if (Shield < _maxShield && RegenDelayTimer.ExpiredOrNotRunning(Runner))
            {
                Shield = Mathf.Min(_maxShield, Shield + _shieldRegenPerSecond * Runner.DeltaTime);
            }
        }

        public void ApplyDamage(in DamageInfo info)
        {
            // Hard authority gate. A client calling this locally changes nothing
            // that anyone else will ever see.
            if (!HasStateAuthority) return;
            if (IsDead || info.Amount <= 0f) return;

            // Friendly fire is off in co-op. Revisit per-mode when teams land.
            if (info.InstigatorTeam != TeamId.Unassigned &&
                info.InstigatorTeam == Team &&
                info.InstigatorPlayerId != -1)
            {
                return;
            }

            RegenDelayTimer = TickTimer.CreateFromSeconds(Runner, _shieldRegenDelay);

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
                Die(info);
            }
        }

        private void Die(in DamageInfo info)
        {
            if (!HasStateAuthority || IsDead) return;

            IsDead = true;
            Health = 0f;
            Shield = 0f;

            // Co-op extraction: this is where the player's carried loot must be
            // dropped into the world so it can be looted. That is the whole point
            // of the genre and it MUST be server-side.
            // TODO: DropLootOnDeath() once the inventory system exists.

            _session?.NotifyPlayerNoLongerAlive();

            Debug.Log($"[PlayerHealth] {Object.InputAuthority} killed by player {info.InstigatorPlayerId}.");
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
                }
            }
        }
    }
}
