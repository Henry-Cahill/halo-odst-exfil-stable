using System.Collections.Generic;
using UnityEngine;

namespace Dropshock.Core.Items
{
    /// <summary>
    /// Resolves item ids to definitions. One asset, referenced by everything that
    /// needs to interpret an <see cref="ItemStack"/>.
    ///
    /// <para>Ids are authored on each <see cref="ItemDef"/> rather than derived from
    /// this list's ordering. Index-derived ids would shift the moment someone
    /// reorders the array or an asset import changes order, silently reinterpreting
    /// every stored item in every stash — a data-loss bug with no error and no
    /// obvious symptom.</para>
    /// </summary>
    [CreateAssetMenu(menuName = "Dropshock/Item Registry", fileName = "ItemRegistry")]
    public sealed class ItemRegistry : ScriptableObject, IItemCatalog
    {
        [SerializeField] private ItemDef[] _items = new ItemDef[0];

        private Dictionary<ushort, ItemDef> _byId;

        public IReadOnlyList<ItemDef> Items => _items;

        public bool TryGetItem(ushort itemId, out ItemFootprint footprint)
        {
            if (TryGetDef(itemId, out ItemDef def))
            {
                footprint = def.ToFootprint();
                return true;
            }

            footprint = default;
            return false;
        }

        public bool TryGetDef(ushort itemId, out ItemDef def)
        {
            EnsureBuilt();
            return _byId.TryGetValue(itemId, out def);
        }

        private void EnsureBuilt()
        {
            if (_byId != null) return;

            _byId = new Dictionary<ushort, ItemDef>(_items.Length);

            for (int i = 0; i < _items.Length; i++)
            {
                ItemDef def = _items[i];
                if (def == null) continue;
                if (def.ItemId == ItemStack.Empty) continue;

                // First definition wins. Reassigning here would make lookup depend on
                // array order, which is precisely what authored ids exist to avoid.
                if (!_byId.ContainsKey(def.ItemId))
                {
                    _byId.Add(def.ItemId, def);
                }
            }
        }

        /// <summary>Drops the lookup so edits during play take effect on next access.</summary>
        public void Invalidate() => _byId = null;

        private void OnValidate()
        {
            Invalidate();
            ValidateIds();
        }

        /// <summary>
        /// Reports duplicate and reserved ids. A duplicate is the dangerous case: one
        /// definition simply becomes unreachable, and every stack of it resolves to a
        /// different item with no error anywhere.
        /// </summary>
        public void ValidateIds()
        {
            var seen = new Dictionary<ushort, ItemDef>(_items.Length);

            for (int i = 0; i < _items.Length; i++)
            {
                ItemDef def = _items[i];
                if (def == null)
                {
                    Debug.LogWarning($"[{nameof(ItemRegistry)}] '{name}' has an empty slot at index {i}.", this);
                    continue;
                }

                if (def.ItemId == ItemStack.Empty)
                {
                    Debug.LogError($"[{nameof(ItemRegistry)}] '{def.name}' has reserved ItemId 0 " +
                                   "and can never be stored.", this);
                    continue;
                }

                if (seen.TryGetValue(def.ItemId, out ItemDef existing))
                {
                    Debug.LogError($"[{nameof(ItemRegistry)}] Duplicate ItemId {def.ItemId}: " +
                                   $"'{existing.name}' and '{def.name}'. One is unreachable and " +
                                   "every stored stack of it will resolve to the other.", this);
                    continue;
                }

                seen.Add(def.ItemId, def);
            }
        }
    }
}
