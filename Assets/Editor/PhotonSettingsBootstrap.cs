using Fusion.Editor;
using Fusion.Photon.Realtime;
using UnityEditor;
using UnityEngine;

namespace Dropshock.EditorTools
{
    /// <summary>
    /// Ensures <c>PhotonAppSettings</c> exists without needing the
    /// <c>Tools ▸ Fusion ▸ Realtime Settings</c> menu.
    ///
    /// <para><b>Why this exists:</b> the settings asset is generated on first
    /// access, not shipped with the SDK, and the only built-in way to create it is
    /// a menu item. Menu items live in compiled assemblies, so any compile error
    /// anywhere in the project makes Fusion's own menus vanish — right when you
    /// need them. This runs on domain reload instead, which does not depend on
    /// finding anything in the menu bar.</para>
    ///
    /// <para>Safe to leave in permanently: it creates the asset once and then does
    /// nothing on subsequent reloads.</para>
    /// </summary>
    internal static class PhotonSettingsBootstrap
    {
        private const string AppIdPropertyPath = "AppSettings.AppIdFusion";

        [InitializeOnLoadMethod]
        private static void EnsureSettingsAsset()
        {
            // Deferred: asset creation during domain reload can race the asset
            // database, which fails silently and leaves no asset behind.
            EditorApplication.delayCall += () =>
            {
                if (PhotonAppSettings.Global != null) return;

                FusionGlobalScriptableObjectUtils.EnsureAssetExists<PhotonAppSettings>();

                if (PhotonAppSettings.Global != null)
                {
                    Debug.Log("[Dropshock] Created PhotonAppSettings at " +
                              AssetDatabase.GetAssetPath(PhotonAppSettings.Global) +
                              " — select it and paste your App ID into 'App Id Fusion'.");
                }
            };
        }

        /// <summary>
        /// Selects the settings asset and reports whether an App ID is present.
        /// Backup for when the Fusion menu is unavailable.
        /// </summary>
        [MenuItem("Dropshock/Setup/Photon App Settings", priority = 20)]
        private static void SelectSettingsAsset()
        {
            FusionGlobalScriptableObjectUtils.EnsureAssetExists<PhotonAppSettings>();

            var settings = PhotonAppSettings.Global;
            if (settings == null)
            {
                Debug.LogError("[Dropshock] Could not create or locate PhotonAppSettings.");
                return;
            }

            Selection.activeObject = settings;
            EditorGUIUtility.PingObject(settings);

            var appId = new SerializedObject(settings).FindProperty(AppIdPropertyPath);
            if (appId == null || string.IsNullOrWhiteSpace(appId.stringValue))
            {
                Debug.LogWarning("[Dropshock] App Id Fusion is empty. Paste your App ID " +
                                 "from dashboard.photonengine.com into the selected asset. " +
                                 "Without it, StartGame fails with an authentication error.");
            }
            else
            {
                Debug.Log("[Dropshock] App Id Fusion is set.");
            }
        }
    }
}
