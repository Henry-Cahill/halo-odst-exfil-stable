using Dropshock.Core.Items;
using Fusion;
using UnityEngine;

namespace Dropshock.Net.Items
{
    /// <summary>
    /// A container that exists in the world: a corpse, a crate, a stash of loot.
    ///
    /// <para><b>Contents are server-side only.</b> The <see cref="GridContainer"/>
    /// here is plain memory and is never a <c>[Networked]</c> property. Only the
    /// summary flags below replicate, because they drive world visuals that everyone
    /// legitimately sees. Replicating contents would let any client in range read
    /// what is inside — loot-ESP through walls. See ARCHITECTURE.md, "Container
    /// representation".</para>
    /// </summary>
    public sealed class LootContainer : NetworkBehaviour, IContainerHost
    {
        [Header("Grid")]
        [SerializeField, Min(1)] private int _width = 8;
        [SerializeField, Min(1)] private int _height = 6;
        [SerializeField, Min(1)] private int _maxStacks = 48;

        [Header("Access")]
        [Tooltip("How close a player must be to interact. Checked server-side against " +
                 "the server's position for that player, never a client-reported one.")]
        [SerializeField] private float _interactRange = 2.5f;

        [Header("Catalog")]
        [SerializeField] private ItemRegistry _registry;

        /// <summary>Replicated so every peer can render an emptied container differently.</summary>
        [Networked] public NetworkBool IsEmpty { get; private set; }

        /// <summary>Replicated purely for world visuals — never the contents themselves.</summary>
        [Networked] public byte StackCount { get; private set; }

        private GridContainer _container;

        /// <summary>Server-side contents. Null on clients, by design.</summary>
        public GridContainer Container => _container;

        public override void Spawned()
        {
            // Clients never build a container: they have no contents to hold, and an
            // empty one would invite code that reads it as if it were authoritative.
            if (!HasStateAuthority) return;

            if (_registry == null)
            {
                Debug.LogError($"[{nameof(LootContainer)}] No ItemRegistry assigned; " +
                               "this container cannot resolve any item and will reject " +
                               "everything put into it.", this);
                return;
            }

            _container = new GridContainer(
                (byte)Mathf.Clamp(_width, 1, 255),
                (byte)Mathf.Clamp(_height, 1, 255),
                _registry,
                _maxStacks);

            RefreshSummary();
        }

        public bool TryGetContainer(byte containerIndex, out GridContainer container)
        {
            // A world container has exactly one grid; any other index is a malformed
            // or probing request.
            container = containerIndex == 0 ? _container : null;
            return container != null;
        }

        public bool CanPlayerAccess(PlayerRef player, Vector3 playerPosition)
        {
            if (!HasStateAuthority) return false;

            // Squared comparison avoids a square root on a path that runs per request.
            float sqr = (playerPosition - transform.position).sqrMagnitude;
            return sqr <= _interactRange * _interactRange;
        }

        /// <summary>
        /// Recomputes the replicated summary. Call after any server-side mutation —
        /// the flags are the only thing clients ever learn about this container.
        /// </summary>
        public void RefreshSummary()
        {
            if (!HasStateAuthority || _container == null) return;

            StackCount = (byte)Mathf.Min(_container.Count, byte.MaxValue);
            IsEmpty = _container.Count == 0;
        }
    }
}
