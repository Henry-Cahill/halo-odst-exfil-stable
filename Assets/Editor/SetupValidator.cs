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
            errors += ValidateLagCompensation();
            errors += ValidateEnemySetup();
            errors += ValidateWeaverAllowlist();

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

                // A hit mask that omits the Hitbox layer produces the same symptom as
                // having no hitboxes at all: the raycast returns false on every shot,
                // with no error. Silent, and easy to cause from the Inspector.
                int hitboxLayer = LayerMask.NameToLayer("Hitbox");
                var maskProp = so.FindProperty("_hitMask");
                if (hitboxLayer >= 0 && maskProp != null &&
                    (maskProp.intValue & (1 << hitboxLayer)) == 0)
                {
                    Debug.LogError("[Validate] NetworkWeapon._hitMask does not include the " +
                                   "'Hitbox' layer. Every shot will miss silently.", prefab);
                    errors++;
                }
            }

            if (prefab.GetComponent<PlayerHealth>() == null)
            {
                Debug.LogError("[Validate] Player prefab has no PlayerHealth — it cannot take damage.", prefab);
                errors++;
            }

            // Inventory wiring. Both of these fail silently and destructively: a
            // missing registry means the player can hold nothing, and a missing
            // corpse prefab means gear is refused a route into the world on death,
            // which is the combat-log dupe this system exists to close.
            var inventory = prefab.GetComponent<Dropshock.Net.Items.PlayerInventory>();
            if (inventory == null)
            {
                Debug.LogWarning("[Validate] Player prefab has no PlayerInventory; " +
                                 "the player cannot carry or drop anything.", prefab);
                warnings++;
            }
            else
            {
                var invSo = new SerializedObject(inventory);

                if (invSo.FindProperty("_registry")?.objectReferenceValue == null)
                {
                    Debug.LogError("[Validate] PlayerInventory has no ItemRegistry — every item " +
                                   "id will fail to resolve and the player can hold nothing.", prefab);
                    errors++;
                }

                if (invSo.FindProperty("_corpseContainerPrefab")?.objectReferenceValue == null)
                {
                    Debug.LogError("[Validate] PlayerInventory has no corpse container prefab. " +
                                   "Death and disconnect cannot drop gear, which is exactly the " +
                                   "combat-logging exploit the drop path exists to prevent.", prefab);
                    errors++;
                }
            }

            return errors;
        }

        /// <summary>
        /// The master switch behind every other hitbox check. A correct prefab rig and
        /// correct C# still produce zero hits when this is off: Fusion records no hitbox
        /// snapshot history, so <c>Runner.LagCompensation.Raycast</c> has nothing to
        /// rewind and returns false forever, with no error from anywhere.
        /// </summary>
        private static int ValidateLagCompensation()
        {
            NetworkProjectConfig config = null;
            try
            {
                // Global loads the asset on demand; IsGlobalLoaded alone would make this
                // check skip itself in a fresh editor session, which is exactly when it
                // matters most.
                config = NetworkProjectConfigAsset.Global?.Config;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Validate] Could not load NetworkProjectConfig to verify lag " +
                                 $"compensation: {e.Message}");
                return 0;
            }

            if (config == null)
            {
                Debug.LogWarning("[Validate] NetworkProjectConfig is unavailable — could not verify " +
                                 "that lag compensation is enabled.");
                return 0;
            }

            if (!config.LagCompensation.Enabled)
            {
                Debug.LogError("[Validate] LagCompensation is DISABLED in NetworkProjectConfig. " +
                               "No hitbox history is recorded, so every server-side shot misses " +
                               "regardless of how the hitboxes are set up. Enable it under " +
                               "Fusion ▸ Network Project Config ▸ Lag Compensation.");
                return 1;
            }

            return 0;
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

        /// <summary>
        /// The AI pipeline, whose every failure mode is "no enemies appear".
        ///
        /// <para><c>EnemySpawner</c> opens with <c>if (_enemyPrefab == null) return;</c>
        /// — an unassigned prefab is a silent, permanent no-op with no log line of any
        /// kind. Same shape as the lag-compensation flag, same treatment.</para>
        /// </summary>
        private static int ValidateEnemySetup()
        {
            int errors = 0;

            var enemy = AssetDatabase.LoadAssetAtPath<GameObject>(EnemyPrefabBuilder.PrefabPath);
            if (enemy == null)
            {
                Debug.LogError($"[Validate] No enemy prefab at {EnemyPrefabBuilder.PrefabPath}. " +
                               "No AI can spawn. Run Dropshock ▸ Setup ▸ Build Test Scene.");
                return 1;
            }

            if (!enemy.TryGetComponent<NetworkObject>(out _))
            {
                Debug.LogError("[Validate] Enemy prefab has no NetworkObject; it cannot be spawned.", enemy);
                errors++;
            }

            // Lag compensation rewinds Fusion Hitboxes, not PhysX colliders. An enemy
            // without a populated HitboxRoot is unshootable, and fails silently.
            if (!enemy.TryGetComponent(out HitboxRoot enemyRoot))
            {
                Debug.LogError("[Validate] Enemy prefab has no HitboxRoot; lag-compensated fire " +
                               "cannot hit it. Every shot will miss silently.", enemy);
                errors++;
            }
            else if (enemyRoot.Hitboxes == null || enemyRoot.Hitboxes.Length == 0)
            {
                Debug.LogError("[Validate] Enemy prefab HitboxRoot.Hitboxes is empty. Assigning " +
                               "Hitbox.Root is not enough — the root's own array must list them.", enemy);
                errors++;
            }

            // The spawner must actually point at the prefab. It lives on the
            // GameSession prefab rather than in a scene — deliberately, because a
            // scene-placed NetworkBehaviour may never be registered with the
            // simulation — so this checks the prefab, not the loaded scenes.
            var session = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/GameSession.prefab");
            if (session == null)
            {
                Debug.LogWarning("[Validate] No GameSession prefab; cannot check EnemySpawner wiring.");
                return errors;
            }

            var spawners = session.GetComponentsInChildren<Dropshock.Net.AI.EnemySpawner>(true);
            if (spawners.Length == 0)
            {
                Debug.LogError("[Validate] GameSession prefab has no EnemySpawner; no AI will spawn.",
                               session);
                errors++;
            }

            foreach (var spawner in spawners)
            {
                var prop = new SerializedObject(spawner).FindProperty("_enemyPrefab");

                if (prop != null && prop.objectReferenceValue == null)
                {
                    Debug.LogError("[Validate] EnemySpawner has no enemy prefab assigned. It returns " +
                                   "immediately every tick and no AI will ever spawn — with no error " +
                                   "at runtime.", session);
                    errors++;
                }
            }

            return errors;
        }

        /// <summary>
        /// Fusion weaves only the assemblies named in <c>AssembliesToWeave</c>, by
        /// exact string match with no wildcards.
        ///
        /// <para>An assembly holding <c>NetworkBehaviour</c>s but missing from that
        /// list is not woven, and every <c>[Networked]</c> property in it silently
        /// stops replicating. No error, no warning — state just does not sync. This
        /// check exists because the Core/Net asmdef split put that failure one typo
        /// away.</para>
        /// </summary>
        private static int ValidateWeaverAllowlist()
        {
            const string configPath = "Assets/Photon/Fusion/Resources/NetworkProjectConfig.fusion";
            const string requiredAssembly = "Dropshock.Net";

            if (!System.IO.File.Exists(configPath))
            {
                Debug.LogWarning($"[Validate] No {configPath}; cannot check the weaver allowlist.");
                return 0;
            }

            // Deliberately a substring test rather than a JSON parse: this check must
            // not itself become something that fails quietly on a schema change.
            if (System.IO.File.ReadAllText(configPath).Contains($"\"{requiredAssembly}\""))
            {
                return 0;
            }

            Debug.LogError(
                $"[Validate] '{requiredAssembly}' is missing from AssembliesToWeave in {configPath}. " +
                "Fusion will not weave it, so every [Networked] property in that assembly stops " +
                "replicating — with no error at runtime. Add it to the array.");

            return 1;
        }
    }
}
