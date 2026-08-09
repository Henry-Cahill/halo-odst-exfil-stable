#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Dropshock.Core;
using Fusion;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Dropshock.Net.Player
{
    /// <summary>
    /// Kills a player on demand (F9) so the death path can be exercised without
    /// staging a firefight.
    ///
    /// <para>Death is the harder of the two gear-dropping paths to trigger
    /// deliberately: it needs a shooter, a target, enough damage to finish them, and
    /// two windows that cannot both hold keyboard focus. Disconnect, by contrast, is
    /// one click on a virtual player. This closes that gap, so
    /// <see cref="PlayerInventory.DropAllToWorld"/> can be checked against a
    /// populated inventory in a few seconds.</para>
    ///
    /// <para><b>Server-only.</b> On a client these keys do nothing at all — otherwise
    /// the harness would be a client-authoritative way to kill other players, which
    /// is exactly the shape of thing the rest of this codebase refuses.</para>
    ///
    /// <para><b>Targets a remote player, never the local one.</b> In Host mode the
    /// host is the server, so a result from killing themselves would not exercise
    /// replication at all. Refusing is better than reporting something that means
    /// nothing — the same reasoning as the lag-compensation test in
    /// ARCHITECTURE.md.</para>
    ///
    /// <para>Compiled out of release builds. Attached to the runner object at runtime
    /// by <see cref="Session.NetworkLauncher"/> rather than serialised into
    /// <c>Bootstrap.unity</c>, because a serialised reference to a compiled-out
    /// MonoBehaviour becomes a missing-script warning in the scene.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DeathTestHarness : MonoBehaviour
    {
        [Tooltip("Damage applied by the kill key. Must exceed health + shield.")]
        [SerializeField] private float _lethalDamage = 1000f;

        private NetworkRunner _runner;

        private void Awake()
        {
            _runner = GetComponent<NetworkRunner>();
            if (_runner == null)
            {
                Debug.LogError($"[{nameof(DeathTestHarness)}] Must sit on the same " +
                               "GameObject as the NetworkRunner.", this);
                enabled = false;
            }
        }

        private void Update()
        {
            if (_runner == null || !_runner.IsRunning || !_runner.IsServer) return;

            var kb = Keyboard.current;
            if (kb == null || !kb.f9Key.wasPressedThisFrame) return;

            KillFirstRemotePlayer();
        }

        private void KillFirstRemotePlayer()
        {
            // Read straight off the live PlayerHealth components rather than asking
            // the spawner for its table: nothing here needs to know how spawning
            // bookkeeping works, and a lookup that cannot mutate anything is a
            // smaller thing to own.
            var healths = FindObjectsByType<PlayerHealth>(FindObjectsSortMode.None);

            foreach (var health in healths)
            {
                if (health.Object == null) continue;
                if (health.Object.InputAuthority == _runner.LocalPlayer) continue;
                if (!health.IsAlive) continue;

                Debug.Log($"[DeathTest] Killing {health.Object.InputAuthority} " +
                          $"at tick {_runner.Tick}.", health);

                health.ApplyDamage(new DamageInfo(
                    _lethalDamage, health.transform.position, Vector3.forward));
                return;
            }

            Debug.LogWarning("[DeathTest] No living remote player to kill. Connect a " +
                             "client — killing the host's own player would not " +
                             "exercise replication.");
        }
    }
}
#endif
