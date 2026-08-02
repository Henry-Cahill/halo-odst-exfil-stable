using Fusion;
using UnityEngine;

namespace Dropshock.Net.Player
{
    /// <summary>
    /// Buttons packed into the network input. Values are wire format — never
    /// renumber an existing entry, only append.
    /// </summary>
    public enum InputButton
    {
        Fire = 0,
        Aim = 1,
        Jump = 2,
        Sprint = 3,
        Crouch = 4,
        Reload = 5,
        Interact = 6,
        Vision = 7, // low-light vision toggle (the VISR-alike)
    }

    /// <summary>
    /// The ONLY thing a client is allowed to send about itself.
    /// <para>
    /// No positions, no velocities, no hit results. The server derives all state
    /// from this. Keep it small — it is sent every tick by every player, so on a
    /// 100-player battle royale each added byte costs bandwidth 100x over.
    /// </para>
    /// </summary>
    public struct NetInput : INetworkInput
    {
        public NetworkButtons Buttons;

        /// <summary>Local-space move intent, expected to be clamped to unit length.</summary>
        public Vector2 Move;

        /// <summary>
        /// Absolute look angles in degrees (x = pitch, y = yaw). Sent as absolute
        /// rather than delta so a dropped input packet does not permanently desync
        /// the client's aim from the server's.
        /// </summary>
        public Vector2 LookAngles;

        /// <summary>Requested weapon slot. The server validates this against the loadout.</summary>
        public byte WeaponSlot;
    }
}
