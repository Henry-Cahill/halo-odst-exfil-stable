using System.Collections.Generic;

namespace Dropshock.Core.Items
{
    /// <summary>Why a placement request was rejected. Never shown raw to players.</summary>
    public enum PlacementResult
    {
        Ok = 0,
        UnknownItem,
        OutOfBounds,
        Overlaps,
        ContainerFull,
        NotFound,
        InvalidCount,
    }

    /// <summary>
    /// A spatial grid container: items occupy a W×H footprint and must be placed
    /// somewhere they fit.
    ///
    /// <para><b>Server-side only.</b> This type holds authoritative state and performs
    /// the validation that makes client requests safe. A client may run an identical
    /// instance to drive its UI, but that copy decides nothing — every mutation the
    /// player sees must have been accepted here first. See ARCHITECTURE.md,
    /// "Authority model".</para>
    ///
    /// <para>Stored as a <em>list of placed stacks, not a grid of cells</em>.
    /// Occupancy is derived by walking the list. A cell array would duplicate
    /// information already present and cost more to persist and transmit; at
    /// realistic container sizes (tens of stacks) the linear scans here are cheaper
    /// than maintaining a second representation in sync.</para>
    /// </summary>
    public sealed class GridContainer
    {
        private readonly List<ItemStack> _stacks;
        private readonly IItemCatalog _catalog;

        /// <summary>Grid width in cells.</summary>
        public byte Width { get; }

        /// <summary>Grid height in cells.</summary>
        public byte Height { get; }

        /// <summary>
        /// Maximum number of distinct stacks, independent of grid area.
        ///
        /// <para>Not a networking constraint — contents are not replicated, so
        /// Fusion's compile-time <c>[Capacity(N)]</c> does not apply. This is a design
        /// and persistence bound only.</para>
        /// </summary>
        public int MaxStacks { get; }

        public IReadOnlyList<ItemStack> Stacks => _stacks;

        public int Count => _stacks.Count;

        public GridContainer(byte width, byte height, IItemCatalog catalog, int maxStacks = 64)
        {
            if (width == 0 || height == 0)
            {
                throw new System.ArgumentException("Container must have non-zero dimensions.");
            }

            _catalog = catalog ?? throw new System.ArgumentNullException(nameof(catalog));
            Width = width;
            Height = height;
            MaxStacks = maxStacks < 1 ? 1 : maxStacks;
            _stacks = new List<ItemStack>(System.Math.Min(MaxStacks, 32));
        }

        // --- Queries -------------------------------------------------------------

        /// <summary>Finds the stack occupying a cell, if any.</summary>
        public bool TryGetAt(byte x, byte y, out ItemStack stack)
        {
            for (int i = 0; i < _stacks.Count; i++)
            {
                if (Occupies(_stacks[i], x, y))
                {
                    stack = _stacks[i];
                    return true;
                }
            }

            stack = default;
            return false;
        }

        /// <summary>Finds a stack by its server-assigned instance id.</summary>
        public bool TryGetByInstance(uint instanceId, out ItemStack stack)
        {
            int index = IndexOfInstance(instanceId);
            if (index < 0)
            {
                stack = default;
                return false;
            }

            stack = _stacks[index];
            return true;
        }

        /// <summary>
        /// Tests whether a footprint would fit at a position.
        /// </summary>
        /// <param name="ignoreInstanceId">Instance to exclude from overlap tests, so a
        /// stack being moved does not collide with the space it currently occupies.</param>
        public PlacementResult CanPlace(ushort itemId, byte x, byte y, bool rotated, uint ignoreInstanceId = 0)
        {
            if (!_catalog.TryGetItem(itemId, out ItemFootprint footprint))
            {
                return PlacementResult.UnknownItem;
            }

            footprint.GetSize(rotated, out byte w, out byte h);

            // Widened to int deliberately: x + w can exceed byte range and would wrap
            // silently, turning an out-of-bounds placement into an apparently valid one.
            if (x + w > Width || y + h > Height)
            {
                return PlacementResult.OutOfBounds;
            }

            for (int i = 0; i < _stacks.Count; i++)
            {
                ItemStack other = _stacks[i];
                if (other.InstanceId == ignoreInstanceId && ignoreInstanceId != 0) continue;

                if (Intersects(other, x, y, w, h))
                {
                    return PlacementResult.Overlaps;
                }
            }

            return PlacementResult.Ok;
        }

        /// <summary>
        /// Finds the first position where an item fits, scanning row-major and trying
        /// the unrotated orientation first.
        /// </summary>
        public bool TryFindFreeSpace(ushort itemId, out byte x, out byte y, out bool rotated, bool allowRotation = true)
        {
            if (_catalog.TryGetItem(itemId, out ItemFootprint footprint))
            {
                // Square items gain nothing from a second pass.
                bool squareFootprint = footprint.Width == footprint.Height;
                int orientations = (allowRotation && !squareFootprint) ? 2 : 1;

                for (int o = 0; o < orientations; o++)
                {
                    bool tryRotated = o == 1;

                    for (byte cy = 0; cy < Height; cy++)
                    {
                        for (byte cx = 0; cx < Width; cx++)
                        {
                            if (CanPlace(itemId, cx, cy, tryRotated) == PlacementResult.Ok)
                            {
                                x = cx;
                                y = cy;
                                rotated = tryRotated;
                                return true;
                            }
                        }
                    }
                }
            }

            x = 0;
            y = 0;
            rotated = false;
            return false;
        }

        // --- Mutations -----------------------------------------------------------

        /// <summary>
        /// Places a stack at an explicit position. Rejects rather than relocating —
        /// a request that cannot be honoured exactly must fail, so the server never
        /// silently does something the player did not ask for.
        /// </summary>
        public PlacementResult TryPlaceAt(ushort itemId, ushort count, uint instanceId, byte x, byte y, bool rotated)
        {
            if (count == 0) return PlacementResult.InvalidCount;
            if (_stacks.Count >= MaxStacks) return PlacementResult.ContainerFull;

            PlacementResult fit = CanPlace(itemId, x, y, rotated);
            if (fit != PlacementResult.Ok) return fit;

            _stacks.Add(new ItemStack(itemId, count, x, y, rotated, instanceId));
            return PlacementResult.Ok;
        }

        /// <summary>
        /// Places a stack wherever it fits. Used for loot generation and for bulk
        /// transfers such as death drops, where no specific position is intended.
        /// </summary>
        public PlacementResult TryAddAnywhere(ushort itemId, ushort count, uint instanceId)
        {
            if (count == 0) return PlacementResult.InvalidCount;
            if (_stacks.Count >= MaxStacks) return PlacementResult.ContainerFull;

            if (!_catalog.TryGetItem(itemId, out _)) return PlacementResult.UnknownItem;

            if (!TryFindFreeSpace(itemId, out byte x, out byte y, out bool rotated))
            {
                return PlacementResult.ContainerFull;
            }

            _stacks.Add(new ItemStack(itemId, count, x, y, rotated, instanceId));
            return PlacementResult.Ok;
        }

        /// <summary>Moves a stack already in this container to a new position.</summary>
        public PlacementResult TryMoveWithin(uint instanceId, byte x, byte y, bool rotated)
        {
            int index = IndexOfInstance(instanceId);
            if (index < 0) return PlacementResult.NotFound;

            ItemStack stack = _stacks[index];

            // Excluding itself matters: an item almost always overlaps its own current
            // footprint when nudged by one cell.
            PlacementResult fit = CanPlace(stack.ItemId, x, y, rotated, instanceId);
            if (fit != PlacementResult.Ok) return fit;

            stack.X = x;
            stack.Y = y;
            stack.Rotated = rotated;
            _stacks[index] = stack;
            return PlacementResult.Ok;
        }

        /// <summary>
        /// Removes a stack and hands it back to the caller.
        ///
        /// <para>Transfers between containers must remove and re-place as one
        /// server-side operation. An item that is removed here and then fails to place
        /// elsewhere has been destroyed — callers are responsible for restoring it, or
        /// for checking the destination first.</para>
        /// </summary>
        public bool TryRemove(uint instanceId, out ItemStack removed)
        {
            int index = IndexOfInstance(instanceId);
            if (index < 0)
            {
                removed = default;
                return false;
            }

            removed = _stacks[index];
            _stacks.RemoveAt(index);
            return true;
        }

        /// <summary>Empties the container and returns everything that was in it.</summary>
        public List<ItemStack> RemoveAll()
        {
            var all = new List<ItemStack>(_stacks);
            _stacks.Clear();
            return all;
        }

        // --- Internals -----------------------------------------------------------

        private int IndexOfInstance(uint instanceId)
        {
            for (int i = 0; i < _stacks.Count; i++)
            {
                if (_stacks[i].InstanceId == instanceId) return i;
            }

            return -1;
        }

        private bool Occupies(in ItemStack stack, byte x, byte y)
        {
            if (!_catalog.TryGetItem(stack.ItemId, out ItemFootprint footprint)) return false;
            footprint.GetSize(stack.Rotated, out byte w, out byte h);

            return x >= stack.X && x < stack.X + w
                && y >= stack.Y && y < stack.Y + h;
        }

        private bool Intersects(in ItemStack stack, byte x, byte y, byte w, byte h)
        {
            if (!_catalog.TryGetItem(stack.ItemId, out ItemFootprint footprint)) return false;
            footprint.GetSize(stack.Rotated, out byte ow, out byte oh);

            return x < stack.X + ow && x + w > stack.X
                && y < stack.Y + oh && y + h > stack.Y;
        }
    }
}
