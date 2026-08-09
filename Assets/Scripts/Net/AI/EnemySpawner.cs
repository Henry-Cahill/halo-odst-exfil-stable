using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace Dropshock.Net.AI
{
    /// <summary>
    /// Spawns and maintains a population of hostiles.
    ///
    /// <para>Driven from <see cref="FixedUpdateNetwork"/> on the server, and
    /// tolerant of being spawned before or after players — it simply keeps the count
    /// topped up.</para>
    ///
    /// <para><b>On the logging in here.</b> Every way this component can decline to
    /// spawn used to be a bare <c>return</c>: not the authority, no prefab, population
    /// already met, timer still running, and nowhere clear to place. All five present
    /// identically — as nothing happening — which is why "do enemies spawn at all?"
    /// could not be answered from the log, and why the AI's downed-targeting penalty
    /// stayed blocked behind it. The startup and spawn lines are unconditional
    /// because a trace behind a define nobody has enabled answers nothing; the
    /// per-tick detail is conditional because it runs at tick rate.</para>
    /// </summary>
    public sealed class EnemySpawner : NetworkBehaviour
    {
        [Header("Prefab")]
        [SerializeField] private NetworkObject _enemyPrefab;

        [Header("Population")]
        [Tooltip("How many hostiles to keep alive at once.")]
        [SerializeField, Min(0)] private int _targetCount = 4;

        [Tooltip("Seconds between replacing a dead hostile.")]
        [SerializeField, Min(0f)] private float _respawnSeconds = 8f;

        [Header("Placement")]
        [SerializeField] private float _spawnRadius = 18f;

        [Tooltip("Minimum distance from any living player, so hostiles never " +
                 "materialise in someone's face.")]
        [SerializeField] private float _minPlayerDistance = 12f;

        [Networked] private TickTimer RespawnTimer { get; set; }

        private readonly List<NetworkObject> _alive = new List<NetworkObject>();

        /// <summary>Guards the misconfiguration error so it reports once, not per tick.</summary>
        private bool _loggedMissingPrefab;

        private int _spawnedTotal;

        /// <summary>Rate limit for the placement warning, which can otherwise recur every tick.</summary>
        private float _nextPlacementWarningTime;

        public override void Spawned()
        {
            if (!HasStateAuthority)
            {
                // Expected on clients. Traced rather than logged so a normal client
                // session does not carry a line that looks like a problem.
                TraceSpawner("not state authority — this instance will never spawn anything");
                return;
            }

            Debug.Log(
                $"[Spawner] active on '{name}': target={_targetCount} respawn={_respawnSeconds}s " +
                $"radius={_spawnRadius}m minPlayerDistance={_minPlayerDistance}m " +
                $"prefab={(_enemyPrefab == null ? "MISSING" : _enemyPrefab.name)}", this);

            // Legal (Min(0)) but almost certainly a mistake, and it is indistinguishable
            // from a broken spawner at runtime: the population is permanently satisfied.
            if (_targetCount == 0)
            {
                Debug.LogWarning(
                    $"[Spawner] '{name}' has targetCount=0, so it will never spawn. " +
                    "If that is intentional, disable the component instead.", this);
            }

            // The spawn area being entirely inside the player exclusion radius is a
            // configuration deadlock: every placement attempt will fail forever.
            if (_minPlayerDistance >= _spawnRadius)
            {
                Debug.LogWarning(
                    $"[Spawner] '{name}' has minPlayerDistance ({_minPlayerDistance}m) >= " +
                    $"spawnRadius ({_spawnRadius}m). Any player near the spawner will block " +
                    "the whole area and nothing will spawn.", this);
            }
        }

        public override void FixedUpdateNetwork()
        {
            if (!HasStateAuthority) return;

            if (_enemyPrefab == null)
            {
                if (!_loggedMissingPrefab)
                {
                    _loggedMissingPrefab = true;
                    Debug.LogError(
                        $"[Spawner] '{name}' has no enemy prefab assigned. No hostiles will " +
                        "ever spawn. Assign one on the component.", this);
                }
                return;
            }

            // Despawned enemies leave null entries behind; prune before counting or
            // the population silently decays as the list fills with corpses.
            int before = _alive.Count;
            for (int i = _alive.Count - 1; i >= 0; i--)
            {
                if (_alive[i] == null || !_alive[i].IsValid) _alive.RemoveAt(i);
            }
            if (_alive.Count != before)
            {
                TraceSpawner($"pruned {before - _alive.Count} dead; alive={_alive.Count}/{_targetCount}");
            }

            if (_alive.Count >= _targetCount)
            {
                TraceSpawner($"population met ({_alive.Count}/{_targetCount})");
                return;
            }

            if (!RespawnTimer.ExpiredOrNotRunning(Runner))
            {
                TraceSpawner($"respawn timer running ({RespawnTimer.RemainingTime(Runner) ?? 0f:F2}s left)");
                return;
            }

            if (TrySpawnOne())
            {
                RespawnTimer = TickTimer.CreateFromSeconds(Runner, _respawnSeconds);
            }
        }

        private bool TrySpawnOne()
        {
            // A handful of attempts, then give up until the next tick. Looping until
            // success would stall the simulation whenever players cover the area.
            const int MaxAttempts = 8;

            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                Vector2 disc = Random.insideUnitCircle * _spawnRadius;
                Vector3 candidate = transform.position + new Vector3(disc.x, 0f, disc.y);

                if (!IsClearOfPlayers(candidate)) continue;

                NetworkObject obj = Runner.Spawn(_enemyPrefab, candidate, Quaternion.identity);
                if (obj == null)
                {
                    // Runner.Spawn refusing is not a placement problem — it means the
                    // prefab is not registered in the NetworkProjectConfig, or the
                    // runner is shutting down. Distinct failure, distinct message.
                    Debug.LogError(
                        $"[Spawner] Runner.Spawn returned null for '{_enemyPrefab.name}'. " +
                        "Is the prefab registered in the NetworkProjectConfig?", this);
                    return false;
                }

                _alive.Add(obj);
                _spawnedTotal++;

                // Unconditional and low volume: at most one per _respawnSeconds. This is
                // the line that answers "did anything spawn?".
                Debug.Log(
                    $"[Spawner] spawned '{_enemyPrefab.name}' #{_spawnedTotal} at " +
                    $"{candidate.x:F1},{candidate.z:F1} on attempt {attempt + 1}/{MaxAttempts}; " +
                    $"alive={_alive.Count}/{_targetCount}", this);

                return true;
            }

            // Every attempt landed too close to a player. Recoverable and possibly
            // routine during a firefight, but permanent if the radii are misconfigured,
            // so it is surfaced — rate limited, because it retries every tick.
            if (Time.unscaledTime >= _nextPlacementWarningTime)
            {
                _nextPlacementWarningTime = Time.unscaledTime + 5f;
                Debug.LogWarning(
                    $"[Spawner] '{name}' found no clear spot in {MaxAttempts} attempts " +
                    $"(radius={_spawnRadius}m, minPlayerDistance={_minPlayerDistance}m, " +
                    $"alive={_alive.Count}/{_targetCount}). Players may be covering the spawn area.",
                    this);
            }
            else
            {
                TraceSpawner($"no clear spot in {MaxAttempts} attempts");
            }

            return false;
        }

        private bool IsClearOfPlayers(Vector3 position)
        {
            float minSqr = _minPlayerDistance * _minPlayerDistance;

            foreach (PlayerRef player in Runner.ActivePlayers)
            {
                NetworkObject obj = Runner.GetPlayerObject(player);
                if (obj == null) continue;

                if ((obj.transform.position - position).sqrMagnitude < minSqr) return false;
            }

            return true;
        }

        /// <summary>
        /// Per-tick tracing for the spawn loop, compiled out unless
        /// DROPSHOCK_TRACE_SPAWNER is defined (Project Settings ▸ Player ▸ Scripting
        /// Define Symbols).
        ///
        /// <para>Same rationale as <see cref="Player.PlayerHealth"/>'s downed tracing
        /// and <see cref="Weapons.NetworkWeapon"/>'s shot tracing: these branches run
        /// at tick rate, so they cannot be logged unconditionally, but each of them is
        /// a reason the spawner did nothing this tick and is otherwise invisible.</para>
        /// </summary>
        [System.Diagnostics.Conditional("DROPSHOCK_TRACE_SPAWNER")]
        private void TraceSpawner(string message)
        {
            Debug.Log($"[Spawner] tick={Runner.Tick} '{name}' {message}", this);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.4f);
            Gizmos.DrawWireSphere(transform.position, _spawnRadius);

            // The exclusion radius is why placement fails; drawing only the spawn
            // radius hides half the picture.
            Gizmos.color = new Color(0.3f, 0.6f, 1f, 0.35f);
            Gizmos.DrawWireSphere(transform.position, _minPlayerDistance);
        }
    }
}
