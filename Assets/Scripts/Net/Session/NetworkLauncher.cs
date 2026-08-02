using System.Collections.Generic;
using Dropshock.Net.Player;
using Fusion;
using Fusion.Sockets;
using UnityEngine;

namespace Dropshock.Net.Session
{
    /// <summary>
    /// Owns the NetworkRunner lifecycle: starts a dedicated server, or connects a
    /// client to one.
    /// <para>
    /// Deliberately supports <see cref="Fusion.GameMode.Server"/> (headless,
    /// authoritative, no local player) and <see cref="Fusion.GameMode.Client"/>.
    /// Host mode is available for fast in-editor iteration only — shipping a
    /// player-hosted extraction shooter hands the host the ability to read and
    /// mutate everyone's inventory. See ARCHITECTURE.md.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(NetworkRunner))]
    public sealed class NetworkLauncher : MonoBehaviour, INetworkRunnerCallbacks
    {
        [Header("Config")]
        [SerializeField] private SessionConfig _sessionConfig;

        [Tooltip("Prefab spawned once by the server to own match state.")]
        [SerializeField] private GameSession _gameSessionPrefab;

        [Tooltip("Scene index of the gameplay map to load once the session starts.")]
        [SerializeField] private int _gameplaySceneIndex = 1;

        [Header("Editor Convenience")]
        [Tooltip("Auto-start in Host mode when entering play mode. Editor only.")]
        [SerializeField] private bool _autoStartHostInEditor = true;

        private NetworkRunner _runner;
        private GameSession _session;

        public SessionConfig Config => _sessionConfig;
        public bool IsRunning => _runner != null && _runner.IsRunning;

        private void Awake()
        {
            _runner = GetComponent<NetworkRunner>();
            _runner.ProvideInput = true;

            if (_sessionConfig == null)
            {
                Debug.LogError($"[{nameof(NetworkLauncher)}] No SessionConfig assigned. " +
                               "Cannot determine mode, player cap, or AOI settings.", this);
            }
        }

        private void Start()
        {
#if UNITY_EDITOR
            if (_autoStartHostInEditor)
            {
                _ = StartSession(Fusion.GameMode.Host, "editor-test");
            }
#endif
        }

        /// <summary>Boot a dedicated authoritative server. This is the shipping path.</summary>
        public System.Threading.Tasks.Task StartServer(string sessionName)
            => StartSession(Fusion.GameMode.Server, sessionName);

        /// <summary>Join an existing session as a non-authoritative client.</summary>
        public System.Threading.Tasks.Task StartClient(string sessionName)
            => StartSession(Fusion.GameMode.Client, sessionName);

        private async System.Threading.Tasks.Task StartSession(Fusion.GameMode mode, string sessionName)
        {
            if (_sessionConfig == null)
            {
                Debug.LogError($"[{nameof(NetworkLauncher)}] Refusing to start without a SessionConfig.", this);
                return;
            }

            _sessionConfig.Validate();

            var sceneManager = gameObject.GetComponent<NetworkSceneManagerDefault>()
                               ?? gameObject.AddComponent<NetworkSceneManagerDefault>();

            var args = new StartGameArgs
            {
                GameMode = mode,
                SessionName = sessionName,
                PlayerCount = _sessionConfig.MaxPlayers,
                Scene = SceneRef.FromIndex(_gameplaySceneIndex),
                SceneManager = sceneManager,
            };

            Debug.Log($"[{nameof(NetworkLauncher)}] Starting {mode} session '{sessionName}' " +
                      $"({_sessionConfig.Mode}, max {_sessionConfig.MaxPlayers}).");

            var result = await _runner.StartGame(args);

            if (!result.Ok)
            {
                Debug.LogError($"[{nameof(NetworkLauncher)}] Failed to start session: " +
                               $"{result.ShutdownReason} — {result.ErrorMessage}");
                return;
            }

            Debug.Log($"[{nameof(NetworkLauncher)}] Session started.");
        }

        public void OnSceneLoadDone(NetworkRunner runner)
        {
            if (!runner.IsServer) return;

            if (_session == null)
            {
                if (_gameSessionPrefab == null)
                {
                    Debug.LogError($"[{nameof(NetworkLauncher)}] No GameSession prefab assigned; " +
                                   "match flow will not run.", this);
                }
                else
                {
                    _session = runner.Spawn(_gameSessionPrefab, Vector3.zero, Quaternion.identity);
                    _session.Initialise(_sessionConfig);
                }
            }

            // Players who joined before this scene existed never reached the spawner.
            // Drive it directly now that the gameplay scene is loaded.
            DriveSpawner(runner);
        }

        /// <summary>
        /// Finds the gameplay scene's spawner and asks it to spawn anyone missing.
        /// <para>
        /// The launcher owns this because it reliably receives runner callbacks,
        /// whereas a scene-placed SimulationBehaviour may never be registered with
        /// the simulation — in which case it silently never ticks.
        /// </para>
        /// </summary>
        private void DriveSpawner(NetworkRunner runner)
        {
            if (!runner.IsServer) return;

            var spawner = FindFirstObjectByType<PlayerSpawner>();
            if (spawner == null)
            {
                Debug.LogWarning($"[{nameof(NetworkLauncher)}] No PlayerSpawner in the loaded " +
                                 "scene; nobody will spawn.");
                return;
            }

            spawner.SpawnMissing(runner);
        }

        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
        {
            if (!runner.IsServer) return;
            _session?.HandlePlayerJoined(player);

            // No-op if the gameplay scene has not loaded yet; OnSceneLoadDone
            // catches those players instead.
            DriveSpawner(runner);
        }

        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
        {
            if (!runner.IsServer) return;
            _session?.HandlePlayerLeft(player);
        }

        public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
        {
            Debug.Log($"[{nameof(NetworkLauncher)}] Shutdown: {shutdownReason}");
            _session = null;
        }

        public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason)
        {
            Debug.LogWarning($"[{nameof(NetworkLauncher)}] Disconnected: {reason}");
        }

        public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason)
        {
            Debug.LogError($"[{nameof(NetworkLauncher)}] Connect failed to {remoteAddress}: {reason}");
        }

        // --- Remaining callbacks -------------------------------------------------
        // Input is supplied by PlayerInputCollector, not here.

        public void OnInput(NetworkRunner runner, NetworkInput input) { }
        public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
        public void OnConnectedToServer(NetworkRunner runner) { }
        public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
        public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
        public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList) { }
        public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
        public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
        public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, System.ReadOnlySpan<byte> data) { }
        public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
        public void OnSceneLoadStart(NetworkRunner runner) { }
        public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
    }
}
