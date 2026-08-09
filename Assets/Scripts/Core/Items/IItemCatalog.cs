namespace Dropshock.Core.Items
{
    /// <summary>
    /// The subset of item data that placement logic needs.
    ///
    /// <para>An interface rather than a direct <c>ItemDef</c> reference so
    /// <see cref="GridContainer"/> stays free of UnityEngine and can be exercised by
    /// plain unit tests, and so the server can resolve items from a database-backed
    /// catalogue instead of ScriptableObjects if that ever becomes necessary.</para>
    /// </summary>
    public interface IItemCatalog
    {
        /// <summary>
        /// Looks up the placement properties of an item.
        /// </summary>
        /// <returns>False if the id is unknown — callers must treat that as a
        /// rejected operation, never as a default-sized item.</returns>
        bool TryGetItem(ushort itemId, out ItemFootprint footprint);
    }

    /// <summary>Placement-relevant properties of an item definition.</summary>
    public readonly struct ItemFootprint
    {
        /// <summary>Unrotated width in grid cells. Always >= 1.</summary>
        public readonly byte Width;

        /// <summary>Unrotated height in grid cells. Always >= 1.</summary>
        public readonly byte Height;

        /// <summary>Maximum items in one stack. 1 means unstackable.</summary>
        public readonly ushort MaxStack;

        public ItemFootprint(byte width, byte height, ushort maxStack)
        {
            Width = width;
            Height = height;
            MaxStack = maxStack;
        }

        /// <summary>Footprint after applying a rotation flag.</summary>
        public void GetSize(bool rotated, out byte width, out byte height)
        {
            width = rotated ? Height : Width;
            height = rotated ? Width : Height;
        }
    }
}
