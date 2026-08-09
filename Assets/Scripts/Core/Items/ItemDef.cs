using UnityEngine;

namespace Dropshock.Core.Items
{
    /// <summary>
    /// Authoring data for one item type.
    ///
    /// <para><b>Only <see cref="ItemId"/> ever crosses a network or database
    /// boundary.</b> ScriptableObject references are not serialisable across either,
    /// and sending item <em>properties</em> would let a modified client claim its
    /// rifle is a 1×1 stack of five hundred. The server resolves every id through its
    /// own catalogue.</para>
    /// </summary>
    [CreateAssetMenu(menuName = "Dropshock/Item Definition", fileName = "Item_")]
    public sealed class ItemDef : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Stable numeric id. Assign explicitly and NEVER reuse or renumber — " +
                 "saved stashes and databases store this value, so changing it " +
                 "silently turns every stored copy into a different item. 0 is " +
                 "reserved to mean 'empty'.")]
        [SerializeField] private ushort _itemId;

        [SerializeField] private string _displayName = "Unnamed";

        [Header("Grid footprint")]
        [Tooltip("Unrotated width in cells.")]
        [SerializeField, Min(1)] private int _gridWidth = 1;

        [Tooltip("Unrotated height in cells.")]
        [SerializeField, Min(1)] private int _gridHeight = 1;

        [Header("Stacking")]
        [Tooltip("Maximum items per stack. 1 means unstackable.")]
        [SerializeField, Min(1)] private int _maxStack = 1;

        [Header("World")]
        [Tooltip("Model spawned when this item exists physically in the world.")]
        [SerializeField] private GameObject _worldPrefab;

        public ushort ItemId => _itemId;
        public string DisplayName => _displayName;
        public byte GridWidth => (byte)Mathf.Clamp(_gridWidth, 1, 255);
        public byte GridHeight => (byte)Mathf.Clamp(_gridHeight, 1, 255);
        public ushort MaxStack => (ushort)Mathf.Clamp(_maxStack, 1, ushort.MaxValue);
        public GameObject WorldPrefab => _worldPrefab;

        public bool IsStackable => MaxStack > 1;

        public ItemFootprint ToFootprint() => new ItemFootprint(GridWidth, GridHeight, MaxStack);

        private void OnValidate()
        {
            // Catching this at author time matters: id 0 is indistinguishable from an
            // empty cell, and the failure would surface as items vanishing from a
            // stash rather than as anything resembling a bad id.
            if (_itemId == ItemStack.Empty)
            {
                Debug.LogWarning(
                    $"[{nameof(ItemDef)}] '{name}' has ItemId 0, which is reserved for " +
                    "'empty'. Assign a non-zero id or this item can never be stored.", this);
            }
        }
    }
}
