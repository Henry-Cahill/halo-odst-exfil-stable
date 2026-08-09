using Dropshock.Core.Items;
using Fusion;

namespace Dropshock.Net.Items
{
    /// <summary>
    /// Wire form of an <see cref="ItemStack"/>, for the targeted RPC that shows a
    /// container's contents to one player.
    ///
    /// <para>Exists because <see cref="ItemStack"/> is deliberately a plain struct
    /// with no Fusion dependency, and because contents are <b>not</b> replicated
    /// state — they are sent, on request, to the single player entitled to see them.
    /// Keeping the wire type separate makes that boundary explicit: if this struct
    /// ever ends up inside a <c>[Networked]</c> property, the loot-ESP hole is
    /// back.</para>
    /// </summary>
    public struct ItemStackWire : INetworkStruct
    {
        public ushort ItemId;
        public ushort Count;
        public byte X;
        public byte Y;
        public NetworkBool Rotated;
        public uint InstanceId;

        public static ItemStackWire From(in ItemStack stack) => new ItemStackWire
        {
            ItemId = stack.ItemId,
            Count = stack.Count,
            X = stack.X,
            Y = stack.Y,
            Rotated = stack.Rotated,
            InstanceId = stack.InstanceId,
        };

        public ItemStack ToStack() => new ItemStack(ItemId, Count, X, Y, Rotated, InstanceId);
    }
}
