namespace Dropshock.Core.Items
{
    /// <summary>
    /// One placed stack of items inside a <see cref="GridContainer"/>.
    ///
    /// <para><b>Deliberately not an INetworkStruct.</b> Container contents are never
    /// replicated — they live in server memory and are sent to a single player by
    /// targeted RPC when that player opens the container. Replicated contents would
    /// reach every client whose area of interest includes the object, and anything a
    /// client receives, a modified client can read: that is loot-ESP, seeing which
    /// corpses and crates are worth pushing through walls. See ARCHITECTURE.md,
    /// "Container representation".</para>
    ///
    /// <para>Carries an item <em>id</em> rather than a definition reference so the
    /// same struct can cross a wire or a database boundary unchanged.</para>
    /// </summary>
    public struct ItemStack
    {
        /// <summary>Registry id. <see cref="Empty"/> (0) means no item.</summary>
        public ushort ItemId;

        /// <summary>Number of items in this stack. Always >= 1 for a real stack.</summary>
        public ushort Count;

        /// <summary>Top-left occupied cell, in container space.</summary>
        public byte X;

        /// <summary>Top-left occupied cell, in container space.</summary>
        public byte Y;

        /// <summary>True when the footprint is rotated 90°, swapping width and height.</summary>
        public bool Rotated;

        /// <summary>
        /// Server-assigned, unique for the life of the item.
        ///
        /// <para>Exists so duplication is detectable after the fact: the same instance
        /// appearing in two places is a bug or an exploit, and without it neither is
        /// visible. Never generated client-side.</para>
        /// </summary>
        public uint InstanceId;

        /// <summary>Reserved id meaning "no item". Never assigned to a real definition.</summary>
        public const ushort Empty = 0;

        public bool IsEmpty => ItemId == Empty || Count == 0;

        public ItemStack(ushort itemId, ushort count, byte x, byte y, bool rotated, uint instanceId)
        {
            ItemId = itemId;
            Count = count;
            X = x;
            Y = y;
            Rotated = rotated;
            InstanceId = instanceId;
        }

        public override string ToString()
            => IsEmpty ? "<empty>" : $"item:{ItemId} x{Count} @({X},{Y}){(Rotated ? " R" : "")} #{InstanceId}";
    }
}
