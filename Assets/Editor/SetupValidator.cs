using Dropshock.Net.Player;
using Dropshock.Net.Session;
using Dropshock.Net.Weapons;
using Fusion;
using UnityEditor;
using UnityEngine;

namespace Dropshock.EditorTools
{
    /// <summary>
    /// Checks the setup for the mistakes that fail *silently* at runtime.
    ///
    /// <para>Every check here corresponds to a real failure mode that produces no
    /// error and no obvious symptom — a player that never spawns, shots that never
    /// register, or a character that jitters only under latency. Cheaper to catch
    /// from a menu item than from a playtest.</para>
    /// </summary>
    internal static class SetupValidator
    {
        [MenuItem("Dropshock/Setup/Validate Setup", priority = 1)]
        public static void Validate()
        {
            int errors = 0;
            int warnings = 0;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabBuilder.PrefabPath);

            if (prefab == null)
            {
                Debug.LogError($"[Validate] No player prefab at {PlayerPrefabBuilder.PrefabPath}. " +
                               "Run Dropshock ▸ Setup ▸ Build Test Scene.");
                errors++;
            }
            else
            {
                errors += ValidatePlayerPrefab(prefab, ref warnings);
            }

            errors += ValidateScenes();
            errors += ValidateSessionConfigs(ref warnings);
            errors += ValidateLauncherWiring();

            if (errors == 0 && warnings == 0)
            {
                Debug.Log("[Validate] All checks passed.");
            }
            else
            {
                Debug.Log($"[Validate] Finished: {errors} error(s), {warnings} warning(s).");
            }
        }

        private static int ValidatePlayerPrefab(GameObject prefab, ref int warnings)
        {
            int errors = 0;

            if (prefab.GetComponent<NetworkObject>() == null)
            {
                Debug.LogError("[Validate] Player prefab has no NetworkObject — it cannot be spawned.", prefab);
                errors++;
            }

            // The specific mistake that causes "movement jitters under latency".
            // SimpleKCC replicates its own transform; a NetworkTransform alongside it
            // means two systems writing the same transform every tick.
            if (prefab.GetComponent<NetworkTransform>() != null)
            {
                Debug.LogError("[Validate] Player prefab has BOTH SimpleKCC and NetworkTransform. " +
                               "Remove the NetworkTransform — they fight over the transform and " +
                               "the symptom is jitter that only shows up under latency.", prefab);
                errors++;
            }

            // Lag compensation rewinds Fusion Hitboxes, not PhysX colliders. Without
            // a HitboxRoot, every server-side shot silently misses.
            var root = prefab.GetComponentInChildren<HitboxRoot>();
            if (root == null)
            {
                Debug.LogError("[Validate] Player prefab has no HitboxRoot. Lag-compensated " +
                               "raycasts will never register a hit on it.", prefab);
                errors++;
            }
            else
            {
                // Assigning Hitbox.Root is not enough — the root's own Hitboxes array
                // must list them, or lag compensation registers nothing and shots
                // silently miss with no error anywhere.
                var list = new SerializedObject(root).FindProperty("Hitboxes");
                if (list == null || list.arraySize == 0)
                {
                    Debug.LogError("[Validate] HitboxRoot.Hitboxes is empty. Hit registration " +
                                   "will silently fail — every server-side shot misses.", prefab);
                    errors++;
                }
            }

            if (prefab.GetComponentsInChildren<Hitbox>().Length == 0)
            {
                Debug.LogError("[Validate] Player prefab has no Hitbox components.", prefab);
                errors++;
            }

            var weapon = prefab.GetComponentInChildren<NetworkWeapon>();
            if (weapon == null)
            {
                Debug.LogWarning("[Validate] Player prefab has no NetworkWeapon.", prefab);
                warnings++;
            }
            else
            {
                var so = new SerializedObject(weapon);
                if (so.FindProperty("_config")?.objectReferenceValue == null)
                {
                    Debug.LogError("[Validate] NetworkWeapon has no WeaponConfig — it will never fire.", prefab);
                    errors++;
                }
                if (so.FindProperty("_muzzle")?.objectReferenceValue == null)
                {
                    Debug.LogWarning("[Validate] NetworkWeapon has no muzzle transform; " +
                                     "shots will originate from the player root, not the eye.", prefab);
                    warnings++;
                }
            }

            if (prefab.GetComponent<PlayerHealth>() == null)
            {
                Debug.LogError("[Validate] Player prefab has no PlayerHealth — it cannot take damage.", prefab);
                errors++;
            }

            return errors;
        }

        private static int ValidateScenes()
        {
            int errors = 0;
            var scenes = EditorBuildSettings.scenes;

            if (scenes.Length < 2)
            {
                Debug.LogError("[Validate] Expected 2 scenes in Build Settings " +
                               "(0=Bootstrap, 1=DropTest). Fusion identifies scenes by " +
                               "build index across the network, so this order matters.");
                errors++;
            }
            else
            {
                if (!scenes[0].path.EndsWith("Bootstrap.unity"))
                {
                    Debug.LogError($"[Validate] Build index 0 should be Bootstrap.unity, " +
                                   $"found {scenes[0].path}.");
                    errors++;
                }
                if (!scenes[1].path.EndsWith("DropTest.unity"))
                {
                    Debug.LogError($"[Validate] Build index 1 should be DropTest.unity, " +
                                   $"found {scenes[1].path}.");
                    errors++;
                }
            }

            return errors;
        }

        /// <summary>
        /// Checks the GameSession prefab exists and has what Fusion needs to spawn
        /// it. The launcher's reference lives in the Bootstrap scene, so it is only
        /// verified when that scene is open — checking the prefab itself works
        /// regardless.
        /// </summary>
        private static int ValidateLauncherWiring()
        {
            int errors = 0;

            var sessionPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/GameSession.prefab");
            if (sessionPrefab == null)
            {
                Debug.LogError("[Validate] No GameSession prefab at Assets/Prefabs/GameSession.prefab. " +
                               "Run Dropshock ▸ Setup ▸ Build Test Scene.");
                return 1;
            }

            if (sessionPrefab.GetComponent<NetworkObject>() == null)
            {
                Debug.LogError("[Validate] GameSession prefab has no NetworkObject — " +
                               "Runner.Spawn will fail at runtime.", sessionPrefab);
                errors++;
            }

            if (sessionPrefab.GetComponent<GameSession>() == null)
            {
                Debug.LogError("[Validate] GameSession prefab has no GameSession component.", sessionPrefab);
                errors++;
            }

            // Only checkable when Bootstrap is the open scene.
            var launcher = Object.FindFirstObjectByType<NetworkLauncher>();
            if (launcher != null)
            {
                var so = new SerializedObject(launcher);

                if (so.FindProperty("_gameSessionPrefab")?.objectReferenceValue == null)
                {
                    Debug.LogError("[Validate] NetworkLauncher has no GameSession prefab assigned — " +
                                   "the match state machine will never start.", launcher);
                    errors++;
                }

                if (so.FindProperty("_sessionConfig")?.objectReferenceValue == null)
                {
                    Debug.LogError("[Validate] NetworkLauncher has no SessionConfig assigned — " +
                                   "it will refuse to start a session.", launcher);
                    errors++;
                }
            }

            return errors;
        }

        private static int ValidateSessionConfigs(ref int warnings)
        {
            int errors = 0;
            var guids = AssetDatabase.FindAssets($"t:{nameof(SessionConfig)}");

            if (guids.Length == 0)
            {
                Debug.LogError("[Validate] No SessionConfig assets found.");
                return 1;
            }

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var config = AssetDatabase.LoadAssetAtPath<SessionConfig>(path);
                config?.Validate(); // logs its own AOI/persistence problems
            }

            return errors;
        }
    }
}
