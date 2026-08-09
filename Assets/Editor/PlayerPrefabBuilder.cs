using Dropshock.Core.Items;
using Dropshock.Net.Player;
using Dropshock.Net.Weapons;
using Fusion;
using Fusion.Addons.SimpleKCC;
using UnityEditor;
using UnityEngine;

namespace Dropshock.EditorTools
{
    /// <summary>
    /// Builds the networked player prefab.
    /// <para>
    /// Written as a script rather than a committed .prefab so the wiring is
    /// reviewable in a diff, re-runnable after SDK upgrades, and doesn't depend on
    /// hand-authored GUIDs.
    /// </para>
    /// </summary>
    internal static class PlayerPrefabBuilder
    {
        public const string PrefabPath = "Assets/Prefabs/NetworkPlayer.prefab";

        private const float CapsuleHeight = 1.8f;
        private const float CapsuleRadius = 0.35f;
        private const float EyeHeight = 1.65f;

        public static GameObject Build(WeaponConfig weaponConfig, int hitboxLayer,
                                      ItemRegistry itemRegistry = null,
                                      GameObject corpseContainerPrefab = null)
        {
            SetupUtil.EnsureFolder("Assets", "Prefabs");

            var root = new GameObject("NetworkPlayer");

            try
            {
                // --- Networking ------------------------------------------------
                root.AddComponent<NetworkObject>();

                // SimpleKCC replicates its own transform state. Deliberately no
                // NetworkTransform here — the two would fight for the transform.
                var kcc = root.AddComponent<SimpleKCC>();
                ConfigureKcc(kcc);

                // --- Visual body ------------------------------------------------
                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.name = "Body";
                body.transform.SetParent(root.transform, false);
                body.transform.localPosition = new Vector3(0f, CapsuleHeight * 0.5f, 0f);
                body.transform.localScale = new Vector3(CapsuleRadius * 2f, CapsuleHeight * 0.5f, CapsuleRadius * 2f);

                // The visual must not collide — the KCC owns collision.
                Object.DestroyImmediate(body.GetComponent<Collider>());

                // --- Rig ---------------------------------------------------------
                var head = SetupUtil.Child(root, "Head", new Vector3(0f, EyeHeight, 0f));
                var muzzle = SetupUtil.Child(head, "Muzzle", new Vector3(0f, 0f, 0.4f));

                // --- Hitboxes ----------------------------------------------------
                // Lag compensation rewinds Fusion Hitboxes, NOT raw PhysX colliders.
                // Without these, server-side hit detection silently never registers.
                BuildHitboxes(root, hitboxLayer);

                // --- Gameplay components ----------------------------------------
                var player = root.AddComponent<NetworkPlayer>();
                SetupUtil.SetObject(player, "_head", head.transform);

                root.AddComponent<PlayerHealth>();

                var camera = root.AddComponent<PlayerCamera>();
                SetupUtil.SetObject(camera, "_eyeAnchor", head.transform);

                var weapon = root.AddComponent<NetworkWeapon>();
                SetupUtil.SetObject(weapon, "_config", weaponConfig);
                SetupUtil.SetObject(weapon, "_muzzle", muzzle.transform);

                // Trace against hitboxes and default world geometry only.
                int hitMask = (1 << hitboxLayer) | (1 << LayerMask.NameToLayer("Default"));
                SetupUtil.SetLayerMask(weapon, "_hitMask", hitMask);

                // --- Inventory ---------------------------------------------------
                // Both references matter for different reasons: without the registry
                // the player can hold nothing, and without the corpse prefab their
                // gear cannot reach the world on death or disconnect.
                var inventory = root.AddComponent<Dropshock.Net.Items.PlayerInventory>();
                SetupUtil.SetObject(inventory, "_registry", itemRegistry);
                SetupUtil.SetInt(inventory, "_width", 10);
                SetupUtil.SetInt(inventory, "_height", 6);
                SetupUtil.SetInt(inventory, "_maxStacks", 40);

                if (corpseContainerPrefab != null &&
                    corpseContainerPrefab.TryGetComponent(out NetworkObject corpseNetObj))
                {
                    SetupUtil.SetObject(inventory, "_corpseContainerPrefab", corpseNetObj);
                }

                // Test scaffolding: without a starting loadout there is nothing to
                // drop, and a death drop that moves zero items looks identical to one
                // that is broken.
                AssignDebugLoadout(inventory, itemRegistry);

                var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                Debug.Log($"[Dropshock Setup] Built player prefab at {PrefabPath}");
                return prefab;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// Fills the debug loadout with a spread of footprints — 1×1, 1×2, 2×2 and
        /// 4×2 — so a death drop exercises real grid packing rather than a handful of
        /// single cells that would fit anywhere.
        /// </summary>
        private static void AssignDebugLoadout(Component inventory, ItemRegistry registry)
        {
            if (registry == null) return;

            (ushort id, int count)[] wanted =
            {
                (5, 1),   // MA5 Rifle, 4x2
                (4, 1),   // M6 Sidearm, 2x2
                (2, 3),   // MA5 Magazine, 1x2
                (3, 2),   // Biofoam Canister, 1x2
                (6, 12),  // Salvaged Circuitry, 1x1
            };

            var so = new SerializedObject(inventory);
            SerializedProperty list = so.FindProperty("_debugLoadout");

            if (list == null)
            {
                Debug.LogWarning("[Dropshock Setup] PlayerInventory has no _debugLoadout field.");
                return;
            }

            list.arraySize = wanted.Length;

            for (int i = 0; i < wanted.Length; i++)
            {
                SerializedProperty element = list.GetArrayElementAtIndex(i);

                registry.TryGetDef(wanted[i].id, out ItemDef def);
                element.FindPropertyRelative("Item").objectReferenceValue = def;
                element.FindPropertyRelative("Count").intValue = wanted[i].count;
            }

            so.ApplyModifiedPropertiesWithoutUndo();

            SetupUtil.SetBool(inventory, "_grantDebugLoadout", true);
        }

        private static void ConfigureKcc(SimpleKCC kcc)
        {
            // Verified against the prefab SimpleKCC actually serialises: the shape
            // lives in a nested `_settings` struct, not in top-level fields.
            // There is no serialised gravity — SimpleKCC exposes SetGravity() at
            // runtime instead, so NetworkPlayer applies it in Spawned().
            SetupUtil.TrySet(kcc, "_settings.Radius", p => p.floatValue = CapsuleRadius);
            SetupUtil.TrySet(kcc, "_settings.Height", p => p.floatValue = CapsuleHeight);
        }

        /// <summary>
        /// A minimal two-part hitbox rig: head and torso. Enough to prove
        /// lag-compensated hit registration and to support headshot multipliers
        /// later. Real character hitboxes get bound to bones once there is a rig.
        /// </summary>
        private static void BuildHitboxes(GameObject root, int hitboxLayer)
        {
            var hitboxRoot = root.AddComponent<HitboxRoot>();

            var torso = SetupUtil.Child(root, "Hitbox_Torso", new Vector3(0f, 1.0f, 0f));
            torso.layer = hitboxLayer;
            var torsoHitbox = torso.AddComponent<Hitbox>();
            SetupUtil.SetObject(torsoHitbox, "Root", hitboxRoot);
            SetupUtil.TrySet(torsoHitbox, "BoxExtents", p => p.vector3Value = new Vector3(0.25f, 0.5f, 0.2f));

            var head = SetupUtil.Child(root, "Hitbox_Head", new Vector3(0f, EyeHeight, 0f));
            head.layer = hitboxLayer;
            var headHitbox = head.AddComponent<Hitbox>();
            SetupUtil.SetObject(headHitbox, "Root", hitboxRoot);
            SetupUtil.TrySet(headHitbox, "BoxExtents", p => p.vector3Value = new Vector3(0.12f, 0.12f, 0.12f));

            // Link the root back to its hitboxes. Assigning Hitbox.Root alone leaves
            // HitboxRoot.Hitboxes empty, and an empty root registers nothing with the
            // lag compensation system — every server-side shot then silently misses
            // with no error anywhere.
            SetupUtil.TrySet(hitboxRoot, "Hitboxes", p =>
            {
                p.arraySize = 2;
                p.GetArrayElementAtIndex(0).objectReferenceValue = torsoHitbox;
                p.GetArrayElementAtIndex(1).objectReferenceValue = headHitbox;
            });
        }
    }
}
