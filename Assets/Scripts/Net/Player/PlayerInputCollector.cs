using Fusion;
using Fusion.Sockets;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Dropshock.Net.Player
{
    /// <summary>
    /// Samples local hardware input and hands it to Fusion once per tick.
    /// <para>
    /// This runs on the local client only and has no authority over anything.
    /// It accumulates mouse look in Update() (which runs more often than the
    /// network tick) so fast flicks between ticks are not dropped.
    /// </para>
    /// </summary>
    public sealed class PlayerInputCollector : MonoBehaviour, INetworkRunnerCallbacks
    {
        [Header("Look")]
        [Tooltip("Degrees of rotation per pixel of mouse movement. Input System " +
                 "reports raw pixels, so this is a much smaller number than the " +
                 "equivalent legacy-axis sensitivity would be.")]
        [SerializeField] private float _mouseSensitivity = 0.05f;
        [SerializeField] private float _minPitch = -89f;
        [SerializeField] private float _maxPitch = 89f;
        [SerializeField] private bool _invertPitch = false;

        private NetworkRunner _runner;
        private Vector2 _lookAngles;
        private bool _inputEnabled = true;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        [Header("Dev: lag-compensation test")]
        [Tooltip("Auto-strafe left/right without needing window focus. Toggle at " +
                 "runtime with F8. Only one window receives keyboard input, so a " +
                 "human cannot drive a moving target and a shooter at the same time — " +
                 "and a stationary target registers hits even when rewind is broken.")]
        [SerializeField] private bool _autoStrafe;

        [Tooltip("Seconds for a full left-right-left cycle.")]
        [SerializeField] private float _autoStrafePeriod = 2f;
#endif

        /// <summary>Current local look angles, so the local camera can read them without waiting for the server.</summary>
        public Vector2 LookAngles => _lookAngles;

        private void Awake()
        {
            _runner = GetComponent<NetworkRunner>();
            if (_runner == null)
            {
                Debug.LogError($"[{nameof(PlayerInputCollector)}] Must sit on the same " +
                               "GameObject as the NetworkRunner.", this);
                enabled = false;
            }
        }

        private void Update()
        {
            if (!_inputEnabled) return;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var kb = Keyboard.current;
            if (kb != null && kb.f8Key.wasPressedThisFrame)
            {
                _autoStrafe = !_autoStrafe;
                Debug.Log($"[{nameof(PlayerInputCollector)}] Auto-strafe {(_autoStrafe ? "ON" : "OFF")}. " +
                          "Move focus to the shooting client now.");
            }
#endif

            AccumulateLook();
        }

        private void AccumulateLook()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;

            // Input System reports raw pixel delta, already accumulated for the
            // frame. Unlike the legacy axes it carries no built-in smoothing or
            // scaling, which is what we want — smoothing on aim is a feel killer.
            Vector2 delta = mouse.delta.ReadValue();

            _lookAngles.y += delta.x * _mouseSensitivity;
            _lookAngles.x += (_invertPitch ? delta.y : -delta.y) * _mouseSensitivity;
            _lookAngles.x = Mathf.Clamp(_lookAngles.x, _minPitch, _maxPitch);

            // Keep yaw bounded so it stays precise after a long session.
            if (_lookAngles.y > 360f) _lookAngles.y -= 360f;
            else if (_lookAngles.y < -360f) _lookAngles.y += 360f;
        }

        /// <summary>Suppress gameplay input (menus, death cam, cutscenes).</summary>
        public void SetInputEnabled(bool value) => _inputEnabled = value;

        public void OnInput(NetworkRunner runner, NetworkInput input)
        {
            var data = new NetInput();

            var keyboard = Keyboard.current;
            var mouse = Mouse.current;

            if (_inputEnabled && keyboard != null)
            {
                var move = new Vector2(
                    (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f),
                    (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f));

                // Clamp rather than normalise so analogue sticks keep their
                // magnitude while keyboard diagonals do not exceed unit speed.
                data.Move = Vector2.ClampMagnitude(move, 1f);
                data.LookAngles = _lookAngles;

                data.Buttons.Set(InputButton.Jump, keyboard.spaceKey.isPressed);
                data.Buttons.Set(InputButton.Sprint, keyboard.leftShiftKey.isPressed);
                data.Buttons.Set(InputButton.Crouch, keyboard.leftCtrlKey.isPressed);
                data.Buttons.Set(InputButton.Reload, keyboard.rKey.isPressed);
                data.Buttons.Set(InputButton.Interact, keyboard.eKey.isPressed);
                data.Buttons.Set(InputButton.Vision, keyboard.vKey.isPressed);

                if (mouse != null)
                {
                    data.Buttons.Set(InputButton.Fire, mouse.leftButton.isPressed);
                    data.Buttons.Set(InputButton.Aim, mouse.rightButton.isPressed);
                }
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Overrides lateral movement only, and goes through the normal input
            // path — the server simulates this exactly like a human strafe, so the
            // lag-compensation rewind under test is the real one.
            if (_autoStrafe && _autoStrafePeriod > 0.001f)
            {
                float phase = (float)(_runner != null ? _runner.SimulationTime : Time.timeAsDouble);
                data.Move = new Vector2(Mathf.Sin(phase * 2f * Mathf.PI / _autoStrafePeriod), 0f);
            }
#endif

            input.Set(data);
        }

        // --- Unused INetworkRunnerCallbacks members -------------------------------
        // NetworkLauncher owns session lifecycle; this component only supplies input.

        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player) { }
        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player) { }
        public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
        public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason) { }
        public void OnConnectedToServer(NetworkRunner runner) { }
        public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }
        public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
        public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }
        public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
        public void OnSessionListUpdated(NetworkRunner runner, System.Collections.Generic.List<SessionInfo> sessionList) { }
        public void OnCustomAuthenticationResponse(NetworkRunner runner, System.Collections.Generic.Dictionary<string, object> data) { }
        public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
        public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, System.ReadOnlySpan<byte> data) { }
        public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
        public void OnSceneLoadDone(NetworkRunner runner) { }
        public void OnSceneLoadStart(NetworkRunner runner) { }
        public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
    }
}
