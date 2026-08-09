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
        [Tooltip("Auto-start in Host mode when entering play mode. Editor only. " +
                 "Turn OFF to get the manual Host/Server/Client picker, which is " +
                 "required for any test involving a real remote client.")]
        [SerializeField] private bool _autoStartHostInEditor = true;

        [Tooltip("Session name used by the editor picker. Both peers must match.")]
        [SerializeField] private string _devSessionName = "editor-test";

        private NetworkRunner _runner;
        private GameSession _session;

        /// <summary>Guards against a second StartGame cancelling the first in flight.</summary>
        private bool _starting;

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

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Attached in code rather than serialised into Bootstrap.unity: the type is
            // compiled out of release builds, and a serialised reference to a
            // compiled-out MonoBehaviour becomes a missing-script warning in the scene.
            if (GetComponent<DeathTestHarness>() == null)
            {
                gameObject.AddComponent<DeathTestHarness>();
            }
#endif
        }

        private void Start()
        {
#if UNITY_EDITOR
            if (_autoStartHostInEditor)
            {
                _ = StartSession(Fusion.GameMode.Host, _devSessionName);
            }
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>
        /// Minimal dev picker, shown only while no session is running.
        ///
        /// <para>Exists because lag compensation cannot be tested from a Host. The host
        /// player *is* the server, so their shots never traverse the network and the
        /// network conditioner does not apply to them — hit registration would appear
        /// to work even if rewind were completely broken. Verifying it requires a
        /// genuine remote Client, which needs a way to choose that mode.</para>
        /// </summary>
        private void OnGUI()
        {
            if (IsRunning || _starting || _autoStartHostInEditor) return;

            using (new GUILayout.AreaScope(new Rect(10, 10, 220, 130), "Dropshock dev start", GUI.skin.window))
            {
                if (GUILayout.Button("Server (authoritative, no local player)"))
                {
                    _ = StartServer(_devSessionName);
                }

                if (GUILayout.Button("Client (join — use this to test hit reg)"))
                {
                    _ = StartClient(_devSessionName);
                }

                if (GUILayout.Button("Host (server + local player)"))
                {
                    _ = StartSession(Fusion.GameMode.Host, _devSessionName);
                }
            }
        }
#endif

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

            if (_starting)
            {
                Debug.LogWarning($"[{nameof(NetworkLauncher)}] Start already in progress; ignoring.", this);
                return;
            }
            _starting = true;

            try
            {
                _sessionConfig.Validate();

                var sceneManager = gameObject.GetComponent<NetworkSceneManagerDefault>()
                                   ?? gameObject.AddComponent<NetworkSceneManagerDefault>();

                // Fusion loads the gameplay scene in Single mode, which unloads
                // Bootstrap — taking this GameObject, and the NetworkRunner running
                // the start operation, with it. The in-flight StartGame is then
                // cancelled and surfaces as ShutdownReason.OperationCanceled, which
                // names the symptom and not the cause.
                _runner.MakeDontDestroyOnLoad(gameObject);

                var args = new StartGameArgs
                {
                    GameMode = mode,
                    SessionName = sessionName,
                    PlayerCount = _sessionConfig.MaxPlayers,
                    SceneManager = sceneManager,
                };

                // Only the authority dictates the scene. A client receives it from the
                // server; specifying one here makes it race the server's own load.
                if (mode != Fusion.GameMode.Client)
                {
                    args.Scene = SceneRef.FromIndex(_gameplaySceneIndex);
                }

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
            finally
            {
                _starting = false;
            }
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

            // Drive the despawn directly, for the same reason joining is driven
            // directly: PlayerSpawner is a scene-placed SimulationBehaviour and its
            // IPlayerLeft callback may never be registered with the simulation, in
            // which case it silently never fires.
            //
            // This path is load-bearing — it is where a disconnecting player's loot
            // is dropped. If it does not run, combat-logging becomes the optimal way
            // to keep gear.
            DriveSpawnerLeft(runner, player);
        }

        /// <summary>
        /// Tells the gameplay scene's spawner that a player has gone, so their object
        /// is despawned and their carried loot reaches the world first.
        /// </summary>
        private void DriveSpawnerLeft(NetworkRunner runner, PlayerRef player)
        {
            if (!runner.IsServer) return;

            var spawner = FindFirstObjectByType<PlayerSpawner>();
            if (spawner == null)
            {
                Debug.LogError($"[{nameof(NetworkLauncher)}] Player {player} left but no " +
                               "PlayerSpawner was found: their object will not be despawned " +
                               "and their loot will not drop.");
                return;
            }

            spawner.HandlePlayerLeft(player, runner);
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
