using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace Dropshock.Net.Player
{
    /// <summary>
    /// Server-side player spawning. Lives in the gameplay scene.
    /// <para>
    /// Spawning is authority-only. The client never tells the server where it
    /// spawns — otherwise "spawn inside the extraction zone" becomes a one-line
    /// exploit.
    /// </para>
    /// </summary>
    public sealed class PlayerSpawner : SimulationBehaviour, IPlayerJoined, IPlayerLeft
    {
        [Tooltip("Player prefab root. Must have a NetworkObject and be registered " +
                 "in NetworkProjectConfig.")]
        [SerializeField] private NetworkObject _playerPrefab;

        [Tooltip("Explicit spawn points. If empty, players spawn at the origin.")]
        [SerializeField] private Transform[] _spawnPoints;

        private readonly Dictionary<PlayerRef, NetworkObject> _spawned = new();
        private int _nextSpawnIndex;

        /// <summary>
        /// Diagnostic only — reports whether Fusion bound this scene-placed
        /// SimulationBehaviour to a runner.
        /// <para>
        /// Spawning does NOT depend on this. A SimulationBehaviour that is not
        /// attached to a NetworkObject may never be registered with the simulation,
        /// in which case FixedUpdateNetwork and the IPlayerJoined / IPlayerLeft
        /// callbacks silently never fire. <see cref="Session.NetworkLauncher"/>
        /// therefore drives <see cref="SpawnMissing"/> directly.
        /// </para>
        /// </summary>
        private void Start()
        {
            if (Runner != null)
            {
                Debug.Log($"[{nameof(PlayerSpawner)}] Start: bound to a runner " +
                          $"(IsServer={Runner.IsServer}, IsRunning={Runner.IsRunning}).", this);
            }
            else
            {
                Debug.Log($"[{nameof(PlayerSpawner)}] Start: not bound to a runner. " +
                          "Spawning will be driven by NetworkLauncher instead.", this);
            }
        }

        /// <summary>One-shot proof that the simulation is actually ticking this object.</summary>
        private bool _loggedFirstTick;

        public void PlayerJoined(PlayerRef player)
        {
            if (!Runner.IsServer) return;
            SpawnFor(player);
        }

        /// <summary>
        /// Reconciles spawned players against the runner's active list every tick.
        /// <para>
        /// <see cref="IPlayerJoined"/> alone is not enough: the host joins during
        /// StartGame, before Fusion has loaded the gameplay scene containing this
        /// spawner, so that join event fires into a scene that does not exist yet and
        /// is lost. Late joiners hit the same window while the scene streams in.
        /// Reconciling makes spawning self-healing regardless of ordering.
        /// </para>
        /// </summary>
        public override void FixedUpdateNetwork()
        {
            if (Runner == null) return;

            if (!_loggedFirstTick)
            {
                _loggedFirstTick = true;
                int active = 0;
                foreach (var _ in Runner.ActivePlayers) active++;
                Debug.Log($"[{nameof(PlayerSpawner)}] First tick " +
                          $"(IsServer={Runner.IsServer}, ActivePlayers={active}).", this);
            }

            if (!Runner.IsServer) return;

            foreach (var player in Runner.ActivePlayers)
            {
                if (!_spawned.ContainsKey(player))
                {
                    SpawnFor(player);
                }
            }
        }

        /// <summary>
        /// Spawns anyone in <paramref name="runner"/>'s active list who has no object yet.
        /// <para>
        /// Public so <see cref="Session.NetworkLauncher"/> can drive it directly. The
        /// launcher receives runner callbacks reliably; a scene-placed
        /// SimulationBehaviour does not always get registered with the simulation, so
        /// this does not depend on that happening.
        /// </para>
        /// </summary>
        public void SpawnMissing(NetworkRunner runner)
        {
            if (runner == null || !runner.IsServer) return;

            foreach (var player in runner.ActivePlayers)
            {
                if (!_spawned.ContainsKey(player))
                {
                    SpawnFor(player, runner);
                }
            }
        }

        private void SpawnFor(PlayerRef player) => SpawnFor(player, Runner);

        private void SpawnFor(PlayerRef player, NetworkRunner runner)
        {
            if (runner == null) return;
            if (_spawned.ContainsKey(player)) return;

            if (_playerPrefab == null)
            {
                Debug.LogError($"[{nameof(PlayerSpawner)}] Player prefab not assigned.", this);
                return;
            }

            GetSpawnPose(out Vector3 position, out Quaternion rotation);

            // inputAuthority = player: that client may send input for this object,
            // but the server retains state authority over everything it does.
            var obj = runner.Spawn(_playerPrefab, position, rotation, inputAuthority: player);
            _spawned[player] = obj;

            Debug.Log($"[{nameof(PlayerSpawner)}] Spawned player {player} at {position}.");
        }

        public void PlayerLeft(PlayerRef player)
        {
            if (!Runner.IsServer) return;

            if (_spawned.Remove(player, out var obj) && obj != null)
            {
                // TODO (co-op extraction): a disconnecting player must drop their
                // carried loot rather than vanish with it, or combat-logging becomes
                // the optimal way to keep gear. This is a launch-blocking rule.
                Runner.Despawn(obj);
            }
        }

        private void GetSpawnPose(out Vector3 position, out Quaternion rotation)
        {
            if (_spawnPoints == null || _spawnPoints.Length == 0)
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                return;
            }

            var point = _spawnPoints[_nextSpawnIndex % _spawnPoints.Length];
            _nextSpawnIndex++;

            position = point.position;
            rotation = point.rotation;
        }
    }
}
