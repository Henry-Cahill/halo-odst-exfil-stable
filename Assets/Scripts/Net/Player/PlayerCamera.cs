using Fusion;
using UnityEngine;

namespace Dropshock.Net.Player
{
    /// <summary>
    /// First-person camera rig for the locally controlled trooper.
    ///
    /// <para><b>Reads raw local input, not network state.</b> Even with prediction
    /// working perfectly, driving the camera from replicated look angles adds a tick
    /// of latency to aiming — and mouse look is the single most latency-sensitive
    /// thing in a shooter. Players will not consciously notice 33ms of movement lag,
    /// but they will absolutely feel it on the crosshair.</para>
    ///
    /// <para>Runs in <c>LateUpdate</c> so it reflects the final transform for the
    /// frame, and only attaches for the input authority — remote players' cameras
    /// are not our problem.</para>
    /// </summary>
    public sealed class PlayerCamera : NetworkBehaviour
    {
        [Header("Rig")]
        [Tooltip("Head/eye anchor on the player prefab. The camera snaps to this.")]
        [SerializeField] private Transform _eyeAnchor;

        [Header("Feel")]
        [Tooltip("Extra eye height offset, in metres.")]
        [SerializeField] private Vector3 _positionOffset = Vector3.zero;

        private Camera _camera;
        private PlayerInputCollector _input;

        /// <summary>True when this component created the camera and must clean it up.</summary>
        private bool _ownsCamera;

        public override void Spawned()
        {
            // Only the owning client gets a camera.
            if (!HasInputAuthority)
            {
                enabled = false;
                return;
            }

            _camera = Camera.main;

            if (_camera == null)
            {
                // Fusion loads the gameplay scene in Single mode, which can destroy
                // a camera that lived in the bootstrap scene. Rather than depend on
                // scene contents, make one.
                var go = new GameObject("PlayerCamera");
                go.tag = "MainCamera";
                _camera = go.AddComponent<Camera>();
                _camera.nearClipPlane = 0.05f;

                // Only add a listener if the scene has none — two active
                // AudioListeners produce a warning and undefined audio.
                if (FindFirstObjectByType<AudioListener>() == null)
                {
                    go.AddComponent<AudioListener>();
                }

                _ownsCamera = true;
                Debug.Log($"[{nameof(PlayerCamera)}] No MainCamera found; created one.", this);
            }

            // The input collector lives on the NetworkRunner object, not the player.
            _input = FindFirstObjectByType<PlayerInputCollector>();
            if (_input == null)
            {
                Debug.LogError($"[{nameof(PlayerCamera)}] No PlayerInputCollector found; " +
                               "camera cannot read local look.", this);
                enabled = false;
                return;
            }

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void LateUpdate()
        {
            if (_camera == null || _input == null) return;

            Transform anchor = _eyeAnchor != null ? _eyeAnchor : transform;

            _camera.transform.position = anchor.position + anchor.TransformVector(_positionOffset);

            // Straight from local input — zero network latency on the crosshair.
            Vector2 look = _input.LookAngles;
            _camera.transform.rotation = Quaternion.Euler(look.x, look.y, 0f);
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            if (!HasInputAuthority) return;

            if (_ownsCamera && _camera != null)
            {
                Destroy(_camera.gameObject);
            }

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}
