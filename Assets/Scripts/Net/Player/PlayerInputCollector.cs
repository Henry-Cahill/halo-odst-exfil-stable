using Fusion;
using UnityEngine;

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
        [SerializeField] private float _mouseSensitivity = 0.12f;
        [SerializeField] private float _minPitch = -89f;
        [SerializeField] private float _maxPitch = 89f;
        [SerializeField] private bool _invertPitch = false;

        private NetworkRunner _runner;
        private Vector2 _lookAngles;
        private bool _inputEnabled = true;

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
            AccumulateLook();
        }

        private void AccumulateLook()
        {
            // Using legacy Input axes for raw mouse delta keeps this readable while
            // the Input System actions asset is still the HDRP template default.
            // TODO: migrate to the project's InputSystem_Actions asset.
            float dx = Input.GetAxisRaw("Mouse X") * _mouseSensitivity;
            float dy = Input.GetAxisRaw("Mouse Y") * _mouseSensitivity;

            _lookAngles.y += dx;
            _lookAngles.x += _invertPitch ? dy : -dy;
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

            if (_inputEnabled)
            {
                var move = new Vector2(
                    Input.GetAxisRaw("Horizontal"),
                    Input.GetAxisRaw("Vertical"));

                // Clamp rather than normalise so analogue sticks keep their
                // magnitude while keyboard diagonals do not exceed unit speed.
                data.Move = Vector2.ClampMagnitude(move, 1f);
                data.LookAngles = _lookAngles;

                data.Buttons.Set(InputButton.Fire, Input.GetMouseButton(0));
                data.Buttons.Set(InputButton.Aim, Input.GetMouseButton(1));
                data.Buttons.Set(InputButton.Jump, Input.GetKey(KeyCode.Space));
                data.Buttons.Set(InputButton.Sprint, Input.GetKey(KeyCode.LeftShift));
                data.Buttons.Set(InputButton.Crouch, Input.GetKey(KeyCode.LeftControl));
                data.Buttons.Set(InputButton.Reload, Input.GetKey(KeyCode.R));
                data.Buttons.Set(InputButton.Interact, Input.GetKey(KeyCode.E));
                data.Buttons.Set(InputButton.Vision, Input.GetKey(KeyCode.V));
            }

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
        public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, System.ArraySegment<byte> data) { }
        public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
        public void OnSceneLoadDone(NetworkRunner runner) { }
        public void OnSceneLoadStart(NetworkRunner runner) { }
        public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
    }
}
