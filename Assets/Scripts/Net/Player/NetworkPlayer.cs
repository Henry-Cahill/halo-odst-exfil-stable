using Dropshock.Net.Session;
using Fusion;
using Fusion.Addons.SimpleKCC;
using UnityEngine;

namespace Dropshock.Net.Player
{
    /// <summary>
    /// Client-predicted, server-authoritative trooper movement built on SimpleKCC.
    ///
    /// <para><b>Why SimpleKCC instead of CharacterController:</b> Fusion re-simulates
    /// past ticks when a server snapshot arrives that disagrees with what the client
    /// predicted. Unity's <c>CharacterController</c> cannot be rewound — its internal
    /// PhysX state does not roll back with the network state, so resimulation
    /// produces different results than the original simulation and the player
    /// visibly jitters. SimpleKCC stores its entire state in networked properties,
    /// so replaying tick N twice yields the identical result. That determinism is
    /// the whole basis of prediction.</para>
    ///
    /// <para><b>Authority model is unchanged.</b> The client predicts its own
    /// movement so it feels instant, but the server simulates the same input and its
    /// result always wins. A modified client that predicts itself across the map is
    /// simply corrected on the next snapshot.</para>
    /// </summary>
    [RequireComponent(typeof(SimpleKCC))]
    public sealed class NetworkPlayer : NetworkBehaviour
    {
        [Header("Movement")]
        [SerializeField] private float _walkSpeed = 4.5f;
        [SerializeField] private float _sprintSpeed = 7.0f;
        [SerializeField] private float _crouchSpeed = 2.0f;
        [SerializeField] private float _jumpImpulse = 6.0f;

        [Tooltip("ODST troopers are heavy. Low values feel floaty, high feels sluggish.")]
        [SerializeField] private float _groundAcceleration = 18f;

        [Tooltip("Air control. Keep well below ground acceleration or movement " +
                 "becomes bunny-hoppy and hard to hit.")]
        [SerializeField] private float _airAcceleration = 3f;

        [Tooltip("Downward acceleration. ODST troopers are heavy — Earth gravity " +
                 "(-9.81) reads as floaty in a shooter.")]
        [SerializeField] private float _gravity = -18f;

        [Header("Look")]
        [SerializeField] private float _minPitch = -89f;
        [SerializeField] private float _maxPitch = 89f;

        [Tooltip("Transform pitched by look input. Usually the camera/head root.")]
        [SerializeField] private Transform _head;

        private SimpleKCC _kcc;
        private SessionConfig _sessionConfig;

        /// <summary>
        /// Horizontal velocity carried across ticks. MUST be [Networked] — a plain
        /// field would not roll back during resimulation, so a corrected tick would
        /// replay against the wrong starting velocity and drift.
        /// </summary>
        [Networked] private Vector3 MoveVelocity { get; set; }

        /// <summary>
        /// Previous tick's buttons, networked for the same reason as MoveVelocity.
        /// Edge detection ("was jump pressed this tick?") against a local field is
        /// the single most common Fusion prediction bug: during resimulation the
        /// local field already holds a future value and the press is lost.
        /// </summary>
        [Networked] private NetworkButtons PreviousButtons { get; set; }

        /// <summary>Replicated look so remote peers aim this player's head correctly.</summary>
        public Vector2 LookAngles => new(_kcc.GetLookRotation().x, _kcc.GetLookRotation().y);

        public bool IsGrounded => _kcc != null && _kcc.IsGrounded;

        public override void Spawned()
        {
            _kcc = GetComponent<SimpleKCC>();

            // SimpleKCC has no serialised gravity field — it is a runtime setting,
            // and it takes a scalar (downward acceleration), not a vector.
            _kcc.SetGravity(_gravity);

            // SimpleKCC replicates its own transform state, so this object must NOT
            // also carry a NetworkTransform — the two would fight each other.
            var launcher = FindFirstObjectByType<NetworkLauncher>();
            _sessionConfig = launcher != null ? launcher.Config : null;
        }

        public override void FixedUpdateNetwork()
        {
            // GetInput returns true on the state authority AND on the input authority.
            // That second case is what enables prediction — the owning client runs
            // the exact same movement code the server will run.
            if (GetInput(out NetInput input))
            {
                ProcessInput(input);

                // Store AFTER consuming, so next tick's edge detection is correct.
                PreviousButtons = input.Buttons;
            }
            else if (HasStateAuthority)
            {
                // Server lost this client's input packet. Coast rather than freeze:
                // stopping dead on packet loss reads as a rubber-band stutter, and
                // leaving them airborne with no gravity is worse.
                ProcessMissingInput();
            }

            if (HasStateAuthority)
            {
                UpdateAreaOfInterest();
            }
        }

        private void ProcessInput(NetInput input)
        {
            ApplyLook(input);
            ApplyMovement(input);
        }

        private void ApplyLook(NetInput input)
        {
            // Clamp server-side as well as client-side. A modified client can send
            // any pitch it likes; bounding it here keeps aim states physical.
            float pitch = Mathf.Clamp(input.LookAngles.x, _minPitch, _maxPitch);

            // SimpleKCC owns look rotation as networked state, which means it rolls
            // back correctly on resimulation and drives the body yaw for us.
            _kcc.SetLookRotation(pitch, input.LookAngles.y);
        }

        private void ApplyMovement(NetInput input)
        {
            bool crouching = input.Buttons.IsSet(InputButton.Crouch);
            bool sprinting = input.Buttons.IsSet(InputButton.Sprint) && !crouching;

            // Sprinting backwards is a classic movement exploit; require forward intent.
            if (sprinting && input.Move.y <= 0.1f) sprinting = false;

            float targetSpeed = crouching ? _crouchSpeed
                              : sprinting ? _sprintSpeed
                              : _walkSpeed;

            // Re-clamp: never trust the client's reported magnitude.
            Vector2 move = Vector2.ClampMagnitude(input.Move, 1f);

            // Build the move vector in the KCC's yaw space so movement follows look.
            Vector3 worldDir = _kcc.TransformRotation * new Vector3(move.x, 0f, move.y);
            Vector3 desiredVelocity = worldDir * targetSpeed;

            float accel = _kcc.IsGrounded ? _groundAcceleration : _airAcceleration;

            Vector3 velocity = MoveVelocity;
            velocity.x = Mathf.MoveTowards(velocity.x, desiredVelocity.x, accel * Runner.DeltaTime);
            velocity.z = Mathf.MoveTowards(velocity.z, desiredVelocity.z, accel * Runner.DeltaTime);
            velocity.y = 0f; // Vertical motion is owned by the KCC's own gravity.

            // Edge-detect the jump against networked previous buttons so it survives
            // resimulation and cannot be held to auto-bhop.
            float jumpImpulse = 0f;
            if (input.Buttons.WasPressed(PreviousButtons, InputButton.Jump) && _kcc.IsGrounded)
            {
                jumpImpulse = _jumpImpulse;
            }

            _kcc.Move(velocity, jumpImpulse);

            MoveVelocity = velocity;
        }

        /// <summary>
        /// Server-side fallback when a client's input packet is dropped. Decays the
        /// player's momentum instead of hard-stopping them, and keeps the KCC ticking
        /// so gravity still applies.
        /// </summary>
        private void ProcessMissingInput()
        {
            Vector3 velocity = MoveVelocity;
            velocity = Vector3.MoveTowards(velocity, Vector3.zero, _groundAcceleration * Runner.DeltaTime);
            velocity.y = 0f;

            _kcc.Move(velocity);
            MoveVelocity = velocity;
        }

        /// <summary>
        /// Battle royale scaling. Fusion clears AOI regions every tick, so this must
        /// be re-registered each tick rather than set once. Without it the server
        /// replicates ~100 players to ~100 clients and bandwidth grows quadratically.
        /// </summary>
        private void UpdateAreaOfInterest()
        {
            if (_sessionConfig == null || !_sessionConfig.UseAreaOfInterest) return;
            if (!Object.InputAuthority.IsRealPlayer) return;

            Runner.AddPlayerAreaOfInterest(
                Object.InputAuthority,
                transform.position,
                _sessionConfig.AreaOfInterestRadius);
        }

        public override void Render()
        {
            // Runs once per rendered frame after resimulation has settled, so this is
            // the safe place to drive visuals from network state.
            if (_head != null)
            {
                _head.localRotation = Quaternion.Euler(_kcc.GetLookRotation().x, 0f, 0f);
            }
        }
    }
}
