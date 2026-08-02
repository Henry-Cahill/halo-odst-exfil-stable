using UnityEditor;
using UnityEngine;

namespace Dropshock.EditorTools
{
    /// <summary>
    /// Shared helpers for the setup tooling.
    /// </summary>
    internal static class SetupUtil
    {
        /// <summary>
        /// Sets a serialized field by name, returning false if the field does not
        /// exist rather than throwing.
        /// <para>
        /// Used defensively for third-party (Fusion/SimpleKCC) components whose
        /// private field names can change between SDK versions. A missing field
        /// produces a warning you can act on, not a broken build script.
        /// </para>
        /// </summary>
        public static bool TrySet(Object target, string propertyPath, System.Action<SerializedProperty> apply)
        {
            if (target == null) return false;

            var so = new SerializedObject(target);
            var prop = so.FindProperty(propertyPath);

            if (prop == null)
            {
                Debug.LogWarning(
                    $"[Dropshock Setup] '{target.GetType().Name}' has no serialized field " +
                    $"'{propertyPath}'. The SDK version may differ from what this script " +
                    "expects — set it by hand in the Inspector.");
                return false;
            }

            apply(prop);
            so.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }

        public static bool SetObject(Object target, string path, Object value)
            => TrySet(target, path, p => p.objectReferenceValue = value);

        public static bool SetFloat(Object target, string path, float value)
            => TrySet(target, path, p => p.floatValue = value);

        public static bool SetInt(Object target, string path, int value)
            => TrySet(target, path, p => p.intValue = value);

        public static bool SetBool(Object target, string path, bool value)
            => TrySet(target, path, p => p.boolValue = value);

        public static bool SetLayerMask(Object target, string path, int mask)
            => TrySet(target, path, p => p.intValue = mask);

        /// <summary>Ensures a layer exists, returning its index (-1 if none free).</summary>
        public static int EnsureLayer(string layerName)
        {
            int existing = LayerMask.NameToLayer(layerName);
            if (existing >= 0) return existing;

            var asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (asset == null || asset.Length == 0)
            {
                Debug.LogError("[Dropshock Setup] Could not open TagManager.asset.");
                return -1;
            }

            var tagManager = new SerializedObject(asset[0]);
            var layers = tagManager.FindProperty("layers");

            // Indices 0-7 are Unity built-ins; user layers start at 8.
            for (int i = 8; i < layers.arraySize; i++)
            {
                var element = layers.GetArrayElementAtIndex(i);
                if (string.IsNullOrEmpty(element.stringValue))
                {
                    element.stringValue = layerName;
                    tagManager.ApplyModifiedPropertiesWithoutUndo();
                    Debug.Log($"[Dropshock Setup] Created layer '{layerName}' at index {i}.");
                    return i;
                }
            }

            Debug.LogError($"[Dropshock Setup] No free user layer for '{layerName}'.");
            return -1;
        }

        public static void EnsureFolder(string parent, string child)
        {
            if (!AssetDatabase.IsValidFolder($"{parent}/{child}"))
            {
                AssetDatabase.CreateFolder(parent, child);
            }
        }

        /// <summary>Creates a child GameObject at a local position.</summary>
        public static GameObject Child(GameObject parent, string name, Vector3 localPosition)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = localPosition;
            return go;
        }
    }
}
