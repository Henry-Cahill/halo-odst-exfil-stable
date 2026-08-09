using System.Text;
using Dropshock.Core.Items;

namespace Dropshock.Net.Items
{
    /// <summary>
    /// Human-readable container dumps for diagnosis.
    ///
    /// <para>Inventory bugs are quiet by nature: an item that fails to transfer, or
    /// that lands somewhere unexpected, produces no error — just gear that is not
    /// where the player left it. Printing the actual grid state at the moment of a
    /// death or a drop is the cheapest way to tell "the item moved" from "the item
    /// was destroyed".</para>
    /// </summary>
    public static class ContainerDebug
    {
        /// <summary>One line per stack, with resolved names where possible.</summary>
        public static string Describe(GridContainer container, ItemRegistry registry, string label = null)
        {
            if (container == null) return $"{label ?? "container"}: <null>";

            var sb = new StringBuilder();
            sb.Append(label ?? "container");
            sb.Append(" [").Append(container.Width).Append('x').Append(container.Height).Append(']');
            sb.Append(' ').Append(container.Count).Append('/').Append(container.MaxStacks).Append(" stacks");

            if (container.Count == 0)
            {
                sb.Append(" — empty");
                return sb.ToString();
            }

            for (int i = 0; i < container.Stacks.Count; i++)
            {
                ItemStack stack = container.Stacks[i];
                sb.AppendLine();
                sb.Append("  • ");

                // Falling back to the raw id matters: an unresolvable id is itself the
                // bug worth seeing, and hiding it behind "Unknown" would obscure that.
                if (registry != null && registry.TryGetDef(stack.ItemId, out ItemDef def))
                {
                    sb.Append(def.DisplayName);
                }
                else
                {
                    sb.Append("UNRESOLVED id=").Append(stack.ItemId);
                }

                sb.Append(" x").Append(stack.Count);
                sb.Append(" @(").Append(stack.X).Append(',').Append(stack.Y).Append(')');
                if (stack.Rotated) sb.Append(" rotated");
                sb.Append(" #").Append(stack.InstanceId);
            }

            return sb.ToString();
        }
    }
}
