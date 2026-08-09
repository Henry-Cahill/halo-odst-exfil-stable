using System.Collections.Generic;

namespace Dropshock.Core.Items
{
    /// <summary>Outcome of a transfer between two containers.</summary>
    public enum TransferResult
    {
        Ok = 0,
        SourceNotFound,
        DestinationRejected,
        SameContainer,
        NothingToMove,
    }

    /// <summary>
    /// Moves stacks between containers without ever destroying one.
    ///
    /// <para><b>Why this exists as its own type.</b>
    /// <see cref="GridContainer.TryRemove"/> hands a stack back and forgets it, so a
    /// naive "remove, then place" loses the item outright whenever the destination
    /// rejects it — a full corpse, a bad position, an unknown id. Losing persistent
    /// gear to a failed UI drag is the worst possible bug in an extraction shooter,
    /// so every transfer goes through here.</para>
    ///
    /// <para>The order is always <em>validate destination, then remove, then place</em>.
    /// Removing first and hoping is what creates the hole.</para>
    ///
    /// <para>Server-side only. All of these run after the server has already decided
    /// the requesting player may touch both containers.</para>
    /// </summary>
    public static class ItemTransfer
    {
        /// <summary>
        /// Moves one stack to an explicit position in another container.
        /// </summary>
        public static TransferResult Move(
            GridContainer from, GridContainer to, uint instanceId,
            byte x, byte y, bool rotated)
        {
            if (from == null || to == null) return TransferResult.SourceNotFound;

            if (ReferenceEquals(from, to))
            {
                // Within one container this is a reposition, not a transfer — and it
                // must ignore its own footprint when testing overlap, which a
                // remove-and-place would get wrong in the opposite direction.
                return to.TryMoveWithin(instanceId, x, y, rotated) == PlacementResult.Ok
                    ? TransferResult.Ok
                    : TransferResult.DestinationRejected;
            }

            if (!from.TryGetByInstance(instanceId, out ItemStack stack))
            {
                return TransferResult.SourceNotFound;
            }

            // Validate BEFORE removing. This is the whole point of the type.
            if (to.CanPlace(stack.ItemId, x, y, rotated) != PlacementResult.Ok)
            {
                return TransferResult.DestinationRejected;
            }

            if (!from.TryRemove(instanceId, out stack))
            {
                return TransferResult.SourceNotFound;
            }

            PlacementResult placed = to.TryPlaceAt(stack.ItemId, stack.Count, stack.InstanceId, x, y, rotated);

            if (placed != PlacementResult.Ok)
            {
                // Should be unreachable given the check above, but "should be" is not
                // a guarantee worth an item. Put it back exactly where it was.
                from.TryPlaceAt(stack.ItemId, stack.Count, stack.InstanceId, stack.X, stack.Y, stack.Rotated);
                return TransferResult.DestinationRejected;
            }

            return TransferResult.Ok;
        }

        /// <summary>
        /// Moves one stack to wherever it fits in the destination.
        /// </summary>
        public static TransferResult MoveAnywhere(GridContainer from, GridContainer to, uint instanceId)
        {
            if (from == null || to == null) return TransferResult.SourceNotFound;
            if (ReferenceEquals(from, to)) return TransferResult.SameContainer;

            if (!from.TryGetByInstance(instanceId, out ItemStack stack))
            {
                return TransferResult.SourceNotFound;
            }

            if (!to.TryFindFreeSpace(stack.ItemId, out byte x, out byte y, out bool rotated))
            {
                return TransferResult.DestinationRejected;
            }

            return Move(from, to, instanceId, x, y, rotated);
        }

        /// <summary>
        /// Empties one container into another — death drops and disconnect drops.
        ///
        /// <para>Moves as much as fits and reports what did not. <b>Anything left
        /// behind is still in <paramref name="from"/> and must be dealt with by the
        /// caller</b>; for a death drop that means spawning a second container rather
        /// than letting the remainder despawn with the body, which would be a
        /// disguised gear sink.</para>
        /// </summary>
        public static int MoveAll(GridContainer from, GridContainer to, List<ItemStack> notMoved = null)
        {
            notMoved?.Clear();

            if (from == null || to == null || ReferenceEquals(from, to)) return 0;

            // Snapshot first: the source list is mutated as items move out of it.
            var source = new List<ItemStack>(from.Stacks);
            int moved = 0;

            for (int i = 0; i < source.Count; i++)
            {
                ItemStack stack = source[i];

                if (MoveAnywhere(from, to, stack.InstanceId) == TransferResult.Ok)
                {
                    moved++;
                }
                else
                {
                    notMoved?.Add(stack);
                }
            }

            return moved;
        }
    }
}
