using Dropshock.Core;
using UnityEngine;

namespace Dropshock.Net.Session
{
    /// <summary>
    /// The single switch between co-op extraction and battle royale.
    /// Everything that differs between the two modes should be expressed here
    /// rather than by forking gameplay code.
    /// </summary>
    [CreateAssetMenu(fileName = "SessionConfig", menuName = "Dropshock/Session Config")]
    public sealed class SessionConfig : ScriptableObject
    {
        [Header("Mode")]
        public GameModeId Mode = GameModeId.CoopExtraction;

        [Tooltip("Hard cap on players for this session type.")]
        [Range(1, 200)]
        public int MaxPlayers = 4;

        [Header("Interest Management")]
        [Tooltip(
            "Co-op sessions are small enough that everyone can see everyone. " +
            "Battle royale MUST enable this or the server will try to replicate " +
            "~100 players to ~100 clients every tick.")]
        public bool UseAreaOfInterest = false;

        [Tooltip("Replication radius in metres around each player when AOI is enabled. " +
                 "Must comfortably exceed the longest sightline/weapon range on the map, " +
                 "or distant players will pop in.")]
        [Range(25f, 1000f)]
        public float AreaOfInterestRadius = 250f;

        [Header("Persistence")]
        [Tooltip("Co-op extraction carries gear in and out. Battle royale does not.")]
        public bool PersistLoadoutOnExtract = true;

        [Header("Match Flow")]
        [Tooltip("Seconds to wait in Lobby before the drop begins.")]
        public float LobbyCountdownSeconds = 15f;

        /// <summary>
        /// Sanity-check the config against its own mode. Misconfiguring AOI on a
        /// 100-player session is a silent performance cliff, so it is loud instead.
        /// </summary>
        public void Validate()
        {
            if (Mode == GameModeId.BattleRoyale && !UseAreaOfInterest)
            {
                Debug.LogError(
                    $"[SessionConfig:{name}] BattleRoyale with UseAreaOfInterest disabled. " +
                    "Replication cost scales with players squared. Enable AOI.", this);
            }

            if (Mode == GameModeId.BattleRoyale && PersistLoadoutOnExtract)
            {
                Debug.LogWarning(
                    $"[SessionConfig:{name}] BattleRoyale should not persist loadouts.", this);
            }

            if (MaxPlayers > 8 && !UseAreaOfInterest)
            {
                Debug.LogWarning(
                    $"[SessionConfig:{name}] {MaxPlayers} players without AOI. " +
                    "Profile bandwidth before shipping this.", this);
            }
        }

        private void OnValidate() => Validate();
    }
}
