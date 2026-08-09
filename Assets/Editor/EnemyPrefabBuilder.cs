using Dropshock.Net.AI;
using Fusion;
using UnityEditor;
using UnityEngine;

namespace Dropshock.EditorTools
{
    /// <summary>
    /// Builds the hostile NPC prefab.
    ///
    /// <para>The hitbox rig here is not optional decoration: lag-compensated
    /// raycasts resolve against Fusion <see cref="Hitbox"/> components, not PhysX
    /// colliders. An enemy without a populated <see cref="HitboxRoot"/> is
    /// unshootable, and fails silently — every shot simply misses.</para>
    /// </summary>
    internal static class EnemyPrefabBuilder
    {
        public const string PrefabPath = "Assets/Prefabs/Enemy.prefab";

        private const float Height = 1.8f;
        private const float Radius = 0.35f;

        public static GameObject Build(int hitboxLayer, GameObject lootContainerPrefab)
        {
            SetupUtil.EnsureFolder("Assets", "Prefabs");

            var root = new GameObject("Enemy");

            try
            {
                root.AddComponent<NetworkObject>();

                // Unlike the player, the enemy has no KCC replicating its own
                // transform, so it needs a NetworkTransform. The two would fight on
                // the player prefab; here there is nothing to fight with.
                root.AddComponent<NetworkTransform>();

                var health = root.AddComponent<EnemyHealth>();
                SetupUtil.SetFloat(health, "_maxHealth", 60f);
                SetupUtil.SetFloat(health, "_corpseSeconds", 0f);

                // Team is a [Networked] property, not a serialised field — it cannot
                // be set on the prefab. EnemyHealth.Spawned assigns Hostile on the
                // authority instead.

                if (lootContainerPrefab != null &&
                    lootContainerPrefab.TryGetComponent(out NetworkObject lootNetObj))
                {
                    SetupUtil.SetObject(health, "_lootContainerPrefab", lootNetObj);
                }

                var brain = root.AddComponent<EnemyBrain>();
                SetupUtil.SetFloat(brain, "_sightRange", 25f);
                SetupUtil.SetFloat(brain, "_attackRange", 12f);
                SetupUtil.SetFloat(brain, "_moveSpeed", 3.2f);
                SetupUtil.SetFloat(brain, "_damage", 9f);
                SetupUtil.SetFloat(brain, "_shotsPerMinute", 90f);

                // Sight is blocked by world geometry only. Including the hitbox layer
                // would let an enemy's own hitboxes block its line of fire.
                SetupUtil.SetLayerMask(brain, "_sightBlockers", 1 << LayerMask.NameToLayer("Default"));

                // --- Visual ------------------------------------------------------
                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.name = "Body";
                body.transform.SetParent(root.transform, false);
                body.transform.localPosition = new Vector3(0f, Height * 0.5f, 0f);
                body.transform.localScale = new Vector3(Radius * 2f, Height * 0.5f, Radius * 2f);
                Object.DestroyImmediate(body.GetComponent<Collider>());

                BuildHitboxes(root, hitboxLayer);

                var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                Debug.Log($"[Dropshock Setup] Built enemy prefab at {PrefabPath}");
                return prefab;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static void BuildHitboxes(GameObject root, int hitboxLayer)
        {
            var hitboxRoot = root.AddComponent<HitboxRoot>();

            var torso = SetupUtil.Child(root, "Hitbox_Torso", new Vector3(0f, 1.0f, 0f));
            torso.layer = hitboxLayer;
            var torsoHitbox = torso.AddComponent<Hitbox>();
            SetupUtil.SetObject(torsoHitbox, "Root", hitboxRoot);
            SetupUtil.TrySet(torsoHitbox, "BoxExtents", p => p.vector3Value = new Vector3(0.25f, 0.5f, 0.2f));

            var head = SetupUtil.Child(root, "Hitbox_Head", new Vector3(0f, 1.65f, 0f));
            head.layer = hitboxLayer;
            var headHitbox = head.AddComponent<Hitbox>();
            SetupUtil.SetObject(headHitbox, "Root", hitboxRoot);
            SetupUtil.TrySet(headHitbox, "BoxExtents", p => p.vector3Value = new Vector3(0.12f, 0.12f, 0.12f));

            // Linking the root back to its hitboxes is what actually registers them
            // with lag compensation. Assigning Hitbox.Root alone leaves this array
            // empty and the enemy is unshootable, with no error anywhere.
            SetupUtil.TrySet(hitboxRoot, "Hitboxes", p =>
            {
                p.arraySize = 2;
                p.GetArrayElementAtIndex(0).objectReferenceValue = torsoHitbox;
                p.GetArrayElementAtIndex(1).objectReferenceValue = headHitbox;
            });
        }
    }
}
