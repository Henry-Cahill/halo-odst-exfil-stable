using System.Collections.Generic;
using Dropshock.Core;
using Dropshock.Net.Player;
using Dropshock.Net.Session;
using Dropshock.Net.Weapons;
using Fusion;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Dropshock.EditorTools
{
    /// <summary>
    /// One-click construction of the playable test setup.
    ///
    /// <para>Run <b>Dropshock ▸ Setup ▸ Build Test Scene</b> after importing the
    /// Fusion 2 SDK and the SimpleKCC addon.</para>
    ///
    /// <para>Produces two scenes, matching how the game actually ships: a Bootstrap
    /// scene that owns the NetworkRunner and survives across matches, and a
    /// gameplay scene that Fusion loads over the top. Building the runner into the
    /// gameplay scene works in the editor and then breaks the moment you need to
    /// change maps between matches.</para>
    /// </summary>
    internal static class TestSceneBuilder
    {
        private const string ScenesFolder = "Assets/Scenes";
        private const string BootstrapScenePath = ScenesFolder + "/Bootstrap.unity";
        private const string GameplayScenePath = ScenesFolder + "/DropTest.unity";
        private const string ConfigFolder = "Assets/Settings/Dropshock";
        private const string GameSessionPrefabPath = "Assets/Prefabs/GameSession.prefab";

        private const int SpawnPointCount = 8;
        private const float SpawnRingRadius = 6f;

        [MenuItem("Dropshock/Setup/Build Test Scene", priority = 0)]
        public static void BuildAll()
        {
            if (!EditorUtility.DisplayDialog(
                    "Build Dropshock Test Scene",
                    "This will create/overwrite:\n\n" +
                    "• Assets/Scenes/Bootstrap.unity\n" +
                    "• Assets/Scenes/DropTest.unity\n" +
                    "• Assets/Prefabs/NetworkPlayer.prefab\n" +
                    "• Assets/Settings/Dropshock/*.asset\n\n" +
                    "Unsaved changes in the current scene will be lost.",
                    "Build", "Cancel"))
            {
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            SetupUtil.EnsureFolder("Assets", "Scenes");
            SetupUtil.EnsureFolder("Assets", "Prefabs");
            SetupUtil.EnsureFolder("Assets", "Settings");
            SetupUtil.EnsureFolder("Assets/Settings", "Dropshock");

            int hitboxLayer = SetupUtil.EnsureLayer("Hitbox");
            if (hitboxLayer < 0)
            {
                Debug.LogError("[Dropshock Setup] Aborted: could not create the Hitbox layer.");
                return;
            }

            var weaponConfig = CreateWeaponConfig();
            CreateSessionConfig();
            PlayerPrefabBuilder.Build(weaponConfig, hitboxLayer);
            BuildGameSessionPrefab();

            // Flush everything to disk BEFORE touching scenes. Creating assets and
            // then calling EditorSceneManager.NewScene triggers an asset database
            // reload that invalidates in-memory references — assigning one of those
            // afterwards silently serialises as null (fileID: 0).
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            BuildGameplayScene();
            BuildBootstrapScene();

            RegisterBuildScenes();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log(
                "[Dropshock Setup] Done.\n" +
                "NEXT: verify the player prefab is registered in NetworkProjectConfig, " +
                "then open Assets/Scenes/Bootstrap.unity and press Play.");

            EditorSceneManager.OpenScene(BootstrapScenePath);
        }

        // ------------------------------------------------------------------ assets

        private static WeaponConfig CreateWeaponConfig()
        {
            string path = $"{ConfigFolder}/WPN_Sidearm.asset";
            var config = AssetDatabase.LoadAssetAtPath<WeaponConfig>(path);

            if (config == null)
            {
                config = ScriptableObject.CreateInstance<WeaponConfig>();
                AssetDatabase.CreateAsset(config, path);
            }

            config.DisplayName = "M6 Sidearm";
            config.Damage = 22f;
            config.FireRate = 450f;
            config.Range = 120f;
            config.IsAutomatic = false;
            config.MagazineSize = 12;
            config.ReloadSeconds = 1.8f;
            EditorUtility.SetDirty(config);

            return config;
        }

        private static SessionConfig CreateSessionConfig()
        {
            string path = $"{ConfigFolder}/Session_CoopExtraction.asset";
            var config = AssetDatabase.LoadAssetAtPath<SessionConfig>(path);

            if (config == null)
            {
                config = ScriptableObject.CreateInstance<SessionConfig>();
                AssetDatabase.CreateAsset(config, path);
            }

            // Co-op slice first, per the agreed build order. A BR config gets added
            // alongside this one rather than by editing this asset.
            config.Mode = GameModeId.CoopExtraction;
            config.MaxPlayers = 4;
            config.UseAreaOfInterest = false;
            config.PersistLoadoutOnExtract = true;
            config.LobbyCountdownSeconds = 5f; // short, for iteration
            EditorUtility.SetDirty(config);

            return config;
        }

        // ------------------------------------------------------------------ scenes

        /// <summary>
        /// Gameplay scene: the map, spawn points, and the server-side spawner.
        /// No NetworkRunner here — Fusion loads this scene over the Bootstrap scene.
        /// </summary>
        private static void BuildGameplayScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Load AFTER the scene switch — see the note in BuildBootstrapScene.
            var playerPrefabGo = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabBuilder.PrefabPath);
            var playerPrefab = playerPrefabGo != null ? playerPrefabGo.GetComponent<NetworkObject>() : null;

            // Lighting, so the level is not pitch black.
            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;

            // The camera lives HERE, not in Bootstrap. Fusion loads this scene in
            // Single mode, which unloads Bootstrap — a camera there would be
            // destroyed on connect, giving "Display 1 No cameras rendering".
            var cameraGo = new GameObject("Main Camera");
            cameraGo.tag = "MainCamera";
            var cam = cameraGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f; // avoid clipping the first-person weapon
            cameraGo.AddComponent<AudioListener>();
            cameraGo.transform.position = new Vector3(0f, 2f, -10f);
            light.intensity = 1f;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            // Ground.
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(10f, 1f, 10f); // 100m x 100m

            // A couple of blocks to shoot at and break sightlines — enough to tell
            // whether lag-compensated hits are landing on cover vs players.
            CreateBlock("Cover_A", new Vector3(8f, 1.5f, 6f), new Vector3(4f, 3f, 1f));
            CreateBlock("Cover_B", new Vector3(-7f, 1.5f, -4f), new Vector3(1f, 3f, 5f));
            CreateBlock("Cover_C", new Vector3(0f, 1.5f, 14f), new Vector3(6f, 3f, 1f));

            // Spawn points arranged in a ring so 4 players do not spawn inside
            // each other, which reads as a physics bug.
            var spawnRoot = new GameObject("SpawnPoints");
            var spawnTransforms = new List<Transform>();

            for (int i = 0; i < SpawnPointCount; i++)
            {
                float angle = (360f / SpawnPointCount) * i * Mathf.Deg2Rad;
                var pos = new Vector3(
                    Mathf.Cos(angle) * SpawnRingRadius,
                    0.1f,
                    Mathf.Sin(angle) * SpawnRingRadius);

                var point = new GameObject($"Spawn_{i:00}");
                point.transform.SetParent(spawnRoot.transform, false);
                point.transform.position = pos;
                // Face the ring centre.
                point.transform.rotation = Quaternion.LookRotation(-pos.normalized, Vector3.up);

                spawnTransforms.Add(point.transform);
            }

            // Server-side spawner.
            var spawnerGo = new GameObject("PlayerSpawner");
            var spawner = spawnerGo.AddComponent<PlayerSpawner>();

            SetupUtil.SetObject(spawner, "_playerPrefab", playerPrefab);

            if (playerPrefab == null)
            {
                Debug.LogError("[Dropshock Setup] Player prefab did not load; the spawner " +
                               "will not spawn anyone. Assign it by hand in DropTest.unity.");
            }

            SetupUtil.TrySet(spawner, "_spawnPoints", p =>
            {
                p.arraySize = spawnTransforms.Count;
                for (int i = 0; i < spawnTransforms.Count; i++)
                {
                    p.GetArrayElementAtIndex(i).objectReferenceValue = spawnTransforms[i];
                }
            });

            EditorSceneManager.SaveScene(scene, GameplayScenePath);
            Debug.Log($"[Dropshock Setup] Built gameplay scene at {GameplayScenePath}");
        }

        /// <summary>
        /// Match-state prefab, spawned once by the server.
        /// <para>
        /// A bare NetworkObject with no transform meaning — it exists purely to give
        /// the session state machine a replicated home. It is a spawned prefab rather
        /// than a scene object so it can be despawned and recreated between matches
        /// without reloading the Bootstrap scene.
        /// </para>
        /// </summary>
        private static GameSession BuildGameSessionPrefab()
        {
            const string path = GameSessionPrefabPath;

            var root = new GameObject("GameSession");

            try
            {
                root.AddComponent<NetworkObject>();
                root.AddComponent<GameSession>();

                var saved = PrefabUtility.SaveAsPrefabAsset(root, path);
                Debug.Log($"[Dropshock Setup] Built GameSession prefab at {path}");

                return saved != null ? saved.GetComponent<GameSession>() : null;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// Bootstrap scene: owns the NetworkRunner, the launcher, local input, and
        /// the camera. Persists across matches.
        /// </summary>
        private static void BuildBootstrapScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Load AFTER the scene switch. NewScene invalidates asset references
            // acquired before it, so anything loaded earlier resolves to null.
            var sessionConfig = AssetDatabase.LoadAssetAtPath<SessionConfig>(
                $"{ConfigFolder}/Session_CoopExtraction.asset");
            var sessionPrefabGo = AssetDatabase.LoadAssetAtPath<GameObject>(GameSessionPrefabPath);
            var sessionPrefab = sessionPrefabGo != null ? sessionPrefabGo.GetComponent<GameSession>() : null;

            // No camera here on purpose — Fusion loads the gameplay scene in Single
            // mode and would destroy it. The camera lives in DropTest instead.
            var networkGo = new GameObject("NetworkRig");
            networkGo.AddComponent<NetworkRunner>();
            networkGo.AddComponent<PlayerInputCollector>();

            var launcher = networkGo.AddComponent<NetworkLauncher>();
            SetupUtil.SetObject(launcher, "_sessionConfig", sessionConfig);
            SetupUtil.SetObject(launcher, "_gameSessionPrefab", sessionPrefab);
            SetupUtil.SetInt(launcher, "_gameplaySceneIndex", 1);
            SetupUtil.SetBool(launcher, "_autoStartHostInEditor", true);

            // Verify the references actually took. A destroyed-object assignment
            // silently serialises as null, which then fails at runtime with
            // "Refusing to start without a SessionConfig" — far from the real cause.
            var check = new SerializedObject(launcher);
            if (check.FindProperty("_sessionConfig").objectReferenceValue == null)
            {
                Debug.LogError("[Dropshock Setup] SessionConfig did not bind to the launcher. " +
                               "Assign it by hand on the NetworkRig object in Bootstrap.unity.");
            }
            if (check.FindProperty("_gameSessionPrefab").objectReferenceValue == null)
            {
                Debug.LogError("[Dropshock Setup] GameSession prefab did not bind to the launcher. " +
                               "Assign it by hand on the NetworkRig object in Bootstrap.unity.");
            }

            EditorSceneManager.SaveScene(scene, BootstrapScenePath);
            Debug.Log($"[Dropshock Setup] Built bootstrap scene at {BootstrapScenePath}");
        }

        /// <summary>
        /// Build settings order matters: NetworkLauncher loads the gameplay scene by
        /// index, and Fusion identifies scenes across the network by build index.
        /// Bootstrap must be 0 and DropTest must be 1.
        /// </summary>
        private static void RegisterBuildScenes()
        {
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(BootstrapScenePath, true),
                new EditorBuildSettingsScene(GameplayScenePath, true),
            };

            Debug.Log("[Dropshock Setup] Build settings: 0=Bootstrap, 1=DropTest.");
        }

        private static GameObject CreateBlock(string name, Vector3 position, Vector3 scale)
        {
            var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = name;
            block.transform.position = position;
            block.transform.localScale = scale;
            return block;
        }
    }
}
