using System.Collections.Generic;
using Dropshock.Core.Items;
using Fusion;
using UnityEngine;

namespace Dropshock.Net.Items
{
    /// <summary>
    /// A player's carried containers, and the entry point for every item request
    /// that player makes.
    ///
    /// <para><b>Clients send coordinates, never state.</b> A request names two
    /// containers, an instance id and a destination cell. The server reads item
    /// identity from its own memory, so a client that lies about <em>what</em> it is
    /// moving changes nothing. See ARCHITECTURE.md, "Authority model".</para>
    ///
    /// <para>Contents are not replicated. The owner is sent a snapshot by targeted
    /// RPC when something changes or when they open a container.</para>
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public sealed class PlayerInventory : NetworkBehaviour, IContainerHost
    {
        [Header("Grid")]
        [SerializeField, Min(1)] private int _width = 10;
        [SerializeField, Min(1)] private int _height = 6;
        [SerializeField, Min(1)] private int _maxStacks = 40;

        [Header("Catalog")]
        [SerializeField] private ItemRegistry _registry;

        [Header("Death")]
        [Tooltip("Container spawned to hold this player's gear when they die or " +
                 "disconnect. Without it, gear vanishes and combat-logging becomes " +
                 "the optimal way to keep it. Must be registered in NetworkProjectConfig.")]
        [SerializeField] private NetworkObject _corpseContainerPrefab;

        [Tooltip("Safety valve for the drop loop: how many corpse containers may be " +
                 "spawned if one cannot hold everything.")]
        [SerializeField, Min(1)] private int _maxCorpseContainers = 4;

        [Header("Debug loadout")]
        [Tooltip("Grant the items below on spawn. Test scaffolding only — real " +
                 "loadouts come from the player's stash at raid start.")]
        [SerializeField] private bool _grantDebugLoadout;

        [SerializeField] private DebugLoadoutEntry[] _debugLoadout = new DebugLoadoutEntry[0];

        [System.Serializable]
        public struct DebugLoadoutEntry
        {
            public ItemDef Item;
            [Min(1)] public int Count;
        }

        private GridContainer _container;
        private IInstanceIdSource _instanceIds;
        private readonly List<ItemStack> _overflow = new List<ItemStack>();
        private Player.PlayerHealth _health;

        /// <summary>Server-side contents. Null on clients, by design.</summary>
        public GridContainer Container => _container;

        public override void Spawned()
        {
            // Cached here rather than looked up per request: CanPlayerAccess runs on
            // every item move.
            _health = GetComponent<Player.PlayerHealth>();

            if (!HasStateAuthority) return;

            if (_registry == null)
            {
                Debug.LogError($"[{nameof(PlayerInventory)}] No ItemRegistry assigned; " +
                               "this player can never hold anything.", this);
                return;
            }

            _container = new GridContainer(
                (byte)Mathf.Clamp(_width, 1, 255),
                (byte)Mathf.Clamp(_height, 1, 255),
                _registry,
                _maxStacks);

            // Ids come from the session, never from here. One source per player would
            // restart at 1 for each of them and hand different items identical ids,
            // which is precisely the duplication the audit trail exists to catch.
            _instanceIds = FindFirstObjectByType<Session.GameSession>()?.InstanceIds;

            if (_grantDebugLoadout)
            {
                GrantDebugLoadout();
            }
        }

        /// <summary>
        /// Test scaffolding: fills the inventory so death drops can be observed
        /// before any loadout or stash system exists.
        /// </summary>
        private void GrantDebugLoadout()
        {
            if (_debugLoadout == null || _debugLoadout.Length == 0) return;

            if (_instanceIds == null)
            {
                // Refuse rather than invent ids locally. Colliding instance ids would
                // look like duplication later and be far harder to explain than an
                // empty test inventory now.
                Debug.LogError($"[{nameof(PlayerInventory)}] No GameSession instance-id source; " +
                               "refusing to grant the debug loadout. Items would receive " +
                               "colliding ids.", this);
                return;
            }

            int granted = 0;

            for (int i = 0; i < _debugLoadout.Length; i++)
            {
                DebugLoadoutEntry entry = _debugLoadout[i];
                if (entry.Item == null || entry.Count < 1) continue;

                PlacementResult result = _container.TryAddAnywhere(
                    entry.Item.ItemId,
                    (ushort)Mathf.Clamp(entry.Count, 1, ushort.MaxValue),
                    _instanceIds.Next());

                if (result == PlacementResult.Ok)
                {
                    granted++;
                }
                else
                {
                    Debug.LogWarning($"[{nameof(PlayerInventory)}] Debug loadout could not place " +
                                     $"'{entry.Item.DisplayName}': {result}.", this);
                }
            }

            Debug.Log($"[{nameof(PlayerInventory)}] {Object.InputAuthority} granted {granted} " +
                      $"debug stack(s).\n{ContainerDebug.Describe(_container, _registry, "inventory")}", this);
        }

        // --- IContainerHost ------------------------------------------------------

        public bool TryGetContainer(byte containerIndex, out GridContainer container)
        {
            container = containerIndex == 0 ? _container : null;
            return container != null;
        }

        public bool CanPlayerAccess(PlayerRef player, Vector3 playerPosition)
        {
            // Owner only. Proximity is irrelevant here — nobody may reach into
            // another player's rig, at any distance, ever. Trading needs its own
            // explicit, mutually-confirmed path.
            if (!HasStateAuthority || player != Object.InputAuthority) return false;

            // Going down freezes your gear as well as your gun. Without this a
            // bleeding-out player can still swap armour and reshuffle stacks, which
            // makes being downed a safe inventory screen rather than a crisis.
            if (_health != null && !_health.CanAct) return false;

            return true;
        }

        // --- Requests ------------------------------------------------------------

        /// <summary>
        /// Asks the server to move an item. Coordinates only — the server supplies
        /// every other fact from its own state.
        /// </summary>
        [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]
        public void RpcRequestMove(
            NetworkId fromHost, byte fromIndex,
            NetworkId toHost, byte toIndex,
            uint instanceId, byte x, byte y, NetworkBool rotated,
            RpcInfo info = default)
        {
            // info.Source is set by Fusion, not by the caller. Trusting a player id in
            // the payload instead would let anyone act as anyone.
            PlayerRef requester = info.Source;

            if (!TryResolve(fromHost, fromIndex, requester, out GridContainer from, out IContainerHost fromHostObj) ||
                !TryResolve(toHost, toIndex, requester, out GridContainer to, out IContainerHost toHostObj))
            {
                return;
            }

            TransferResult result = ItemTransfer.Move(from, to, instanceId, x, y, rotated);

            if (result != TransferResult.Ok) return;

            (fromHostObj as LootContainer)?.RefreshSummary();
            (toHostObj as LootContainer)?.RefreshSummary();

            PushSnapshot(fromHost, fromIndex, from, requester);
            if (!toHost.Equals(fromHost) || toIndex != fromIndex)
            {
                PushSnapshot(toHost, toIndex, to, requester);
            }
        }

        /// <summary>Asks the server for the current contents of a container.</summary>
        [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]
        public void RpcRequestSnapshot(NetworkId hostId, byte containerIndex, RpcInfo info = default)
        {
            if (!TryResolve(hostId, containerIndex, info.Source, out GridContainer container, out _)) return;
            PushSnapshot(hostId, containerIndex, container, info.Source);
        }

        /// <summary>
        /// Delivers container contents to a single player.
        ///
        /// <para>Targeted at one client on purpose. This is the mechanism that
        /// replaces replication, and broadening the target would reintroduce exactly
        /// the leak that design avoids.</para>
        /// </summary>
        [Rpc(RpcSources.StateAuthority, RpcTargets.InputAuthority)]
        public void RpcReceiveSnapshot(NetworkId hostId, byte containerIndex, ItemStackWire[] stacks)
        {
            OnSnapshotReceived?.Invoke(hostId, containerIndex, stacks);
        }

        /// <summary>Raised on the owning client when a snapshot arrives. UI hooks here.</summary>
        public event System.Action<NetworkId, byte, ItemStackWire[]> OnSnapshotReceived;

        // --- Death and disconnect ------------------------------------------------

        /// <summary>
        /// Moves everything this player carries into world containers at
        /// <paramref name="position"/>. Server-side, and the single path used by both
        /// death and disconnect so the two cannot drift apart.
        /// </summary>
        /// <returns>Stacks that could not be placed anywhere.</returns>
        public int DropAllToWorld(Vector3 position)
        {
            if (!HasStateAuthority || _container == null || _container.Count == 0) return 0;

            Debug.Log($"[{nameof(PlayerInventory)}] Dropping loot for {Object.InputAuthority}.\n" +
                      ContainerDebug.Describe(_container, _registry, "before drop"), this);

            if (_corpseContainerPrefab == null)
            {
                // Loud, because the silent version of this is gear deletion.
                Debug.LogError($"[{nameof(PlayerInventory)}] No corpse container prefab; " +
                               $"{_container.Count} stack(s) would be destroyed. Refusing to drop.", this);
                return _container.Count;
            }

            int spawned = 0;

            while (_container.Count > 0 && spawned < _maxCorpseContainers)
            {
                // Spread extra containers slightly so they are not coincident.
                Vector3 offset = spawned == 0
                    ? Vector3.zero
                    : Quaternion.Euler(0f, spawned * 90f, 0f) * (Vector3.forward * 0.6f);

                NetworkObject obj = Runner.Spawn(_corpseContainerPrefab, position + offset, Quaternion.identity);
                spawned++;

                if (obj == null || !obj.TryGetComponent(out LootContainer loot) || loot.Container == null)
                {
                    Debug.LogError($"[{nameof(PlayerInventory)}] Corpse container prefab has no usable " +
                                   $"{nameof(LootContainer)}; dropping aborted.", this);
                    break;
                }

                int moved = ItemTransfer.MoveAll(_container, loot.Container, _overflow);
                loot.RefreshSummary();

                Debug.Log($"[{nameof(PlayerInventory)}] Corpse container {spawned} received {moved} stack(s).\n" +
                          ContainerDebug.Describe(loot.Container, _registry, "corpse"), loot);

                // No progress means the remaining items cannot fit in an empty
                // container of this size — more containers will not help.
                if (moved == 0) break;
            }

            if (_container.Count > 0)
            {
                Debug.LogWarning($"[{nameof(PlayerInventory)}] {_container.Count} stack(s) could not be " +
                                 "dropped and remain on the despawning player. Increase corpse " +
                                 "container size or count.\n" +
                                 ContainerDebug.Describe(_container, _registry, "undropped"), this);
            }
            else
            {
                Debug.Log($"[{nameof(PlayerInventory)}] All loot dropped for {Object.InputAuthority}; " +
                          "inventory empty.", this);
            }

            return _container.Count;
        }

        // --- Internals -----------------------------------------------------------

        /// <summary>
        /// Resolves a container reference and authorises the requester in one step,
        /// so no call site can accidentally skip the access check.
        /// </summary>
        private bool TryResolve(NetworkId hostId, byte index, PlayerRef requester,
                                out GridContainer container, out IContainerHost host)
        {
            container = null;
            host = null;

            NetworkObject obj = Runner.FindObject(hostId);
            if (obj == null) return false;

            host = obj.GetComponent<IContainerHost>();
            if (host == null) return false;

            if (!host.TryGetContainer(index, out container)) return false;

            // Server's own position for the requester. A client-supplied position
            // would make every range check decorative.
            if (!TryGetPlayerPosition(requester, out Vector3 position)) return false;

            if (!host.CanPlayerAccess(requester, position))
            {
                container = null;
                return false;
            }

            return true;
        }

        private bool TryGetPlayerPosition(PlayerRef player, out Vector3 position)
        {
            if (player == Object.InputAuthority)
            {
                position = transform.position;
                return true;
            }

            NetworkObject obj = Runner.GetPlayerObject(player);
            if (obj != null)
            {
                position = obj.transform.position;
                return true;
            }

            position = default;
            return false;
        }

        private void PushSnapshot(NetworkId hostId, byte index, GridContainer container, PlayerRef target)
        {
            if (container == null || target != Object.InputAuthority) return;

            var wire = new ItemStackWire[container.Count];
            for (int i = 0; i < container.Count; i++)
            {
                wire[i] = ItemStackWire.From(container.Stacks[i]);
            }

            RpcReceiveSnapshot(hostId, index, wire);
        }
    }
}
