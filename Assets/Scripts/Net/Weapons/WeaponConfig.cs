using UnityEngine;

namespace Dropshock.Net.Weapons
{
    /// <summary>
    /// Static weapon tuning. Lives on the server as the source of truth — the
    /// client has a copy for prediction and UI, but the server's copy is the one
    /// that decides what actually happened.
    /// </summary>
    [CreateAssetMenu(fileName = "WeaponConfig", menuName = "Dropshock/Weapon Config")]
    public sealed class WeaponConfig : ScriptableObject
    {
        [Header("Identity")]
        public string DisplayName = "Sidearm";

        [Header("Ballistics")]
        [Tooltip("Damage before hitbox multipliers.")]
        public float Damage = 20f;

        [Tooltip("Rounds per minute.")]
        public float FireRate = 600f;

        [Tooltip("Max effective range in metres. Beyond this the shot simply misses.")]
        public float Range = 150f;

        public bool IsAutomatic = true;

        [Header("Accuracy")]
        [Tooltip("Cone half-angle in degrees while hip-firing.")]
        public float HipSpreadDegrees = 2.5f;

        [Tooltip("Cone half-angle in degrees while aiming down sights.")]
        public float AimSpreadDegrees = 0.2f;

        [Header("Ammo")]
        public int MagazineSize = 30;
        public float ReloadSeconds = 2.2f;

        /// <summary>Seconds between shots, derived from fire rate.</summary>
        public float SecondsPerShot => FireRate > 0f ? 60f / FireRate : 0.1f;
    }
}
