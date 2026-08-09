using Dropshock.Core.Items;
using Dropshock.Net.Items;
using Fusion;
using UnityEditor;
using UnityEngine;

namespace Dropshock.EditorTools
{
    /// <summary>
    /// Builds the corpse/loot container prefab.
    ///
    /// <para>This prefab is what stands between a dead player's gear and oblivion.
    /// If <see cref="PlayerInventory"/> has no container to spawn it refuses to drop
    /// and logs an error, so a missing prefab is loud rather than a silent gear
    /// sink.</para>
    /// </summary>
    internal static class LootContainerPrefabBuilder
    {
        public const string PrefabPath = "Assets/Prefabs/CorpseContainer.prefab";

        public static GameObject Build(ItemRegistry registry)
        {
            SetupUtil.EnsureFolder("Assets", "Prefabs");

            var root = new GameObject("CorpseContainer");

            try
            {
                root.AddComponent<NetworkObject>();

                var loot = root.AddComponent<LootContainer>();
                SetupUtil.SetObject(loot, "_registry", registry);
                SetupUtil.SetInt(loot, "_width", 8);
                SetupUtil.SetInt(loot, "_height", 6);
                SetupUtil.SetInt(loot, "_maxStacks", 48);
                SetupUtil.SetFloat(loot, "_interactRange", 2.5f);

                // Placeholder visual so a dropped container is findable in the test
                // scene. Art replaces this; the collider is deliberately absent so it
                // never blocks movement or, worse, stops a bullet.
                var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
                visual.name = "Visual";
                visual.transform.SetParent(root.transform, false);
                visual.transform.localScale = new Vector3(0.6f, 0.35f, 0.45f);
                visual.transform.localPosition = new Vector3(0f, 0.175f, 0f);
                Object.DestroyImmediate(visual.GetComponent<Collider>());

                var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                Debug.Log($"[Dropshock Setup] Built corpse container prefab at {PrefabPath}");
                return prefab;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
