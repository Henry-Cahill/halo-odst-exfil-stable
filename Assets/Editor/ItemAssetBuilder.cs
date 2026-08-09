using System.Collections.Generic;
using Dropshock.Core.Items;
using UnityEditor;
using UnityEngine;

namespace Dropshock.EditorTools
{
    /// <summary>
    /// Creates the starter item definitions and the registry that resolves them.
    ///
    /// <para>Generated rather than hand-authored for the same reason as the scenes:
    /// the wiring stays reviewable in a diff and survives a rebuild. Ids are written
    /// here explicitly and must never be renumbered — saved stashes store the id, so
    /// changing one silently reinterprets every stored copy of that item.</para>
    /// </summary>
    internal static class ItemAssetBuilder
    {
        public const string RegistryPath = "Assets/Settings/Dropshock/ItemRegistry.asset";
        private const string ItemFolder = "Assets/Settings/Dropshock/Items";

        /// <summary>
        /// Authored ids. Append only — never reuse, never renumber.
        /// </summary>
        private struct Seed
        {
            public ushort Id;
            public string AssetName;
            public string DisplayName;
            public int Width;
            public int Height;
            public int MaxStack;
        }

        private static readonly Seed[] Seeds =
        {
            new Seed { Id = 1, AssetName = "Item_PistolMag",  DisplayName = "M6 Magazine",       Width = 1, Height = 1, MaxStack = 8 },
            new Seed { Id = 2, AssetName = "Item_RifleMag",   DisplayName = "MA5 Magazine",      Width = 1, Height = 2, MaxStack = 6 },
            new Seed { Id = 3, AssetName = "Item_Biofoam",    DisplayName = "Biofoam Canister",  Width = 1, Height = 2, MaxStack = 3 },
            new Seed { Id = 4, AssetName = "Item_Sidearm",    DisplayName = "M6 Sidearm",        Width = 2, Height = 2, MaxStack = 1 },
            new Seed { Id = 5, AssetName = "Item_Rifle",      DisplayName = "MA5 Rifle",         Width = 4, Height = 2, MaxStack = 1 },
            new Seed { Id = 6, AssetName = "Item_Salvage",    DisplayName = "Salvaged Circuitry", Width = 1, Height = 1, MaxStack = 20 },
        };

        public static ItemRegistry Build()
        {
            SetupUtil.EnsureFolder("Assets/Settings/Dropshock", "Items");

            var defs = new List<ItemDef>(Seeds.Length);

            foreach (Seed seed in Seeds)
            {
                defs.Add(CreateOrUpdate(seed));
            }

            var registry = AssetDatabase.LoadAssetAtPath<ItemRegistry>(RegistryPath);

            if (registry == null)
            {
                registry = ScriptableObject.CreateInstance<ItemRegistry>();
                AssetDatabase.CreateAsset(registry, RegistryPath);
            }

            var so = new SerializedObject(registry);
            SerializedProperty list = so.FindProperty("_items");
            list.arraySize = defs.Count;

            for (int i = 0; i < defs.Count; i++)
            {
                list.GetArrayElementAtIndex(i).objectReferenceValue = defs[i];
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(registry);

            // Surfaces duplicate or reserved ids now, at build time, rather than as
            // items silently resolving to the wrong definition at runtime.
            registry.Invalidate();
            registry.ValidateIds();

            Debug.Log($"[Dropshock Setup] Item registry built with {defs.Count} definitions.");
            return registry;
        }

        private static ItemDef CreateOrUpdate(Seed seed)
        {
            string path = $"{ItemFolder}/{seed.AssetName}.asset";
            var def = AssetDatabase.LoadAssetAtPath<ItemDef>(path);

            if (def == null)
            {
                def = ScriptableObject.CreateInstance<ItemDef>();
                AssetDatabase.CreateAsset(def, path);
            }

            SetupUtil.SetInt(def, "_itemId", seed.Id);
            SetupUtil.TrySet(def, "_displayName", p => p.stringValue = seed.DisplayName);
            SetupUtil.SetInt(def, "_gridWidth", seed.Width);
            SetupUtil.SetInt(def, "_gridHeight", seed.Height);
            SetupUtil.SetInt(def, "_maxStack", seed.MaxStack);

            EditorUtility.SetDirty(def);
            return def;
        }
    }
}
