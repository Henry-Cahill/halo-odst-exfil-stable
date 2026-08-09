using Dropshock.Core;
using Dropshock.Net.Items;
using Fusion;
using UnityEngine;

namespace Dropshock.Net.AI
{
    /// <summary>
    /// Health for a hostile NPC. Server-authoritative, like everything that can
    /// change the outcome of a fight.
    ///
    /// <para>Implements the same <see cref="IDamageable"/> the weapon already fires
    /// at, so lag-compensated shots register on enemies through exactly the path
    /// verified against players — no second damage route to keep in sync.</para>
    /// </summary>
    public sealed class EnemyHealth : NetworkBehaviour, IDamageable
    {
        [Header("Pools")]
        [SerializeField] private float _maxHealth = 60f;

        [Header("Death")]
        [Tooltip("Container spawned to hold this enemy's drops. Optional — an enemy " +
                 "with no loot simply despawns.")]
        [SerializeField] private NetworkObject _lootContainerPrefab;

        [Tooltip("Seconds the corpse remains before despawning. 0 despawns immediately.")]
        [SerializeField, Min(0f)] private float _corpseSeconds = 0f;

        [Networked] public float Health { get; private set; }
        [Networked] public NetworkBool IsDead { get; private set; }
        [Networked] public TeamId Team { get; set; }

        [Networked] private TickTimer DespawnTimer { get; set; }

        public bool IsAlive => !IsDead;

        /// <summary>Raised on all peers when this enemy dies. FX and audio hook here.</summary>
        public event System.Action OnDied;

        private ChangeDetector _changes;

        public override void Spawned()
        {
            _changes = GetChangeDetector(ChangeDetector.Source.SimulationState);

            if (HasStateAuthority)
            {
                Health = _maxHealth;
                IsDead = false;

                // Enemies default to a team of their own so friendly-fire rules and
                // AI target selection both have something meaningful to compare.
                if (Team == TeamId.Unassigned) Team = TeamId.Hostile;
            }
        }

        public void ApplyDamage(in DamageInfo info)
        {
            if (!HasStateAuthority) return;
            if (IsDead || info.Amount <= 0f) return;

            // Hostiles do not shoot each other. Without this an AI firing through a
            // crowd would thin its own ranks and look broken.
            if (info.InstigatorTeam != TeamId.Unassigned && info.InstigatorTeam == Team)
            {
                return;
            }

            Health = Mathf.Max(0f, Health - info.Amount);

            if (Health <= 0f)
            {
                Die();
            }
        }

        private void Die()
        {
            if (!HasStateAuthority || IsDead) return;

            IsDead = true;
            Health = 0f;

            SpawnLoot();

            // Never despawn synchronously here. Die() is reached from ApplyDamage,
            // which is called from *another* object's FixedUpdateNetwork — removing a
            // NetworkObject while the simulation is mid-iteration disturbs state
            // replication bookkeeping. Defer to this object's own tick instead.
            DespawnTimer = TickTimer.CreateFromSeconds(Runner, Mathf.Max(_corpseSeconds, 0f));
        }

        public override void FixedUpdateNetwork()
        {
            if (!HasStateAuthority || !IsDead) return;

            if (DespawnTimer.Expired(Runner))
            {
                DespawnTimer = default;
                Runner.Despawn(Object);
            }
        }

        /// <summary>
        /// Spawns a loot container for this enemy's drops.
        ///
        /// <para>Currently empty — the container exists so the drop path is exercised
        /// and so loot tables have somewhere to deposit into. An enemy that drops
        /// nothing is still worth spawning a container for, because "no loot" and
        /// "loot silently lost" must be distinguishable.</para>
        /// </summary>
        private void SpawnLoot()
        {
            if (_lootContainerPrefab == null) return;

            NetworkObject obj = Runner.Spawn(_lootContainerPrefab, transform.position, Quaternion.identity);

            if (obj != null && obj.TryGetComponent(out LootContainer loot))
            {
                loot.RefreshSummary();
            }
        }

        public override void Render()
        {
            foreach (var change in _changes.DetectChanges(this))
            {
                if (change == nameof(IsDead) && IsDead)
                {
                    OnDied?.Invoke();
                }
            }
        }
    }
}
