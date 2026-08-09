using Dropshock.Core;
using Fusion;
using UnityEngine;

namespace Dropshock.Net.Session
{
    public enum SessionState : byte
    {
        /// <summary>Waiting for players; countdown running.</summary>
        Lobby = 0,

        /// <summary>Players are in the drop pod / free-fall insertion.</summary>
        Dropping = 1,

        /// <summary>Live gameplay.</summary>
        InProgress = 2,

        /// <summary>Match resolved; results being shown before teardown.</summary>
        Ending = 3,
    }

    /// <summary>
    /// Server-owned match state. Exactly one exists per session, spawned by
    /// <see cref="NetworkLauncher"/> on the authority.
    /// <para>
    /// All mutation happens on the state authority. Clients read the replicated
    /// properties for UI and nothing else.
    /// </para>
    /// </summary>
    public sealed class GameSession : NetworkBehaviour
    {
        [SerializeField] private PlayerSpawnerSettings _spawnerSettings;

        [Networked] public SessionState State { get; private set; }
        [Networked] public GameModeId Mode { get; private set; }
        [Networked] public TickTimer StateTimer { get; private set; }
        [Networked] public int AlivePlayerCount { get; private set; }
        [Networked] public int ConnectedPlayerCount { get; private set; }

        private SessionConfig _config;
        private ChangeDetector _changes;

        /// <summary>
        /// Issues item instance ids for this session. Server-side only.
        ///
        /// <para>Lives here because exactly one <see cref="GameSession"/> exists per
        /// session: a source owned per-player would restart at 1 for each of them and
        /// hand different items identical ids, destroying the audit trail that makes
        /// duplication detectable. Ids issued here are unique within this raid only —
        /// items that survive to the stash must be reissued authoritative ids by the
        /// backend at extraction. See ARCHITECTURE.md, "Stash and persistence".</para>
        /// </summary>
        public Core.Items.IInstanceIdSource InstanceIds { get; private set; }

        /// <summary>Raised on every peer when the replicated state changes.</summary>
        public event System.Action<SessionState> OnStateChanged;

        public override void Spawned()
        {
            _changes = GetChangeDetector(ChangeDetector.Source.SimulationState);

            if (HasStateAuthority)
            {
                InstanceIds = new Core.Items.SessionInstanceIdSource();
            }
        }

        /// <summary>Called by the launcher on the server immediately after spawn.</summary>
        public void Initialise(SessionConfig config)
        {
            if (!HasStateAuthority) return;

            _config = config;
            Mode = config.Mode;

            // Seed from the runner rather than waiting for join callbacks. This
            // object is spawned after the scene loads, by which point the host — and
            // possibly other clients — have already joined and their OnPlayerJoined
            // events are long gone. Without this the session sees zero players alive,
            // concludes the squad was wiped, and immediately shuts down.
            int active = 0;
            foreach (var _ in Runner.ActivePlayers) active++;

            ConnectedPlayerCount = active;
            AlivePlayerCount = active;

            TransitionTo(SessionState.Lobby);
        }

        public override void FixedUpdateNetwork()
        {
            if (!HasStateAuthority) return;

            switch (State)
            {
                case SessionState.Lobby:
                    // Do not start a match nobody is in.
                    if (ConnectedPlayerCount > 0 && StateTimer.Expired(Runner))
                    {
                        TransitionTo(SessionState.Dropping);
                    }
                    break;

                case SessionState.Dropping:
                    if (StateTimer.Expired(Runner))
                    {
                        TransitionTo(SessionState.InProgress);
                    }
                    break;

                case SessionState.InProgress:
                    EvaluateWinCondition();
                    break;

                case SessionState.Ending:
                    if (StateTimer.Expired(Runner))
                    {
                        Runner.Shutdown(shutdownReason: ShutdownReason.Ok);
                    }
                    break;
            }
        }

        /// <summary>
        /// The core rule difference between the two modes.
        /// Co-op ends when the squad is wiped (extraction is handled per-player).
        /// Battle royale ends when one player/squad remains.
        /// </summary>
        private void EvaluateWinCondition()
        {
            switch (Mode)
            {
                case GameModeId.CoopExtraction:
                    if (ConnectedPlayerCount > 0 && AlivePlayerCount <= 0)
                    {
                        Debug.Log("[GameSession] Squad wiped — session over.");
                        TransitionTo(SessionState.Ending);
                    }
                    break;

                case GameModeId.BattleRoyale:
                    // TODO: this counts individuals. Replace with a squad-aware
                    // count once teams are implemented, or duos will end early.
                    if (AlivePlayerCount <= 1 && ConnectedPlayerCount > 1)
                    {
                        Debug.Log("[GameSession] One survivor — session over.");
                        TransitionTo(SessionState.Ending);
                    }
                    break;
            }
        }

        private void TransitionTo(SessionState next)
        {
            if (!HasStateAuthority) return;

            State = next;

            float duration = next switch
            {
                SessionState.Lobby => _config != null ? _config.LobbyCountdownSeconds : 15f,
                SessionState.Dropping => 8f,
                SessionState.Ending => 10f,
                _ => 0f,
            };

            StateTimer = duration > 0f
                ? TickTimer.CreateFromSeconds(Runner, duration)
                : default;

            Debug.Log($"[GameSession] -> {next}");
        }

        public void HandlePlayerJoined(PlayerRef player)
        {
            if (!HasStateAuthority) return;

            ConnectedPlayerCount++;
            AlivePlayerCount++;

            // Joining mid-match is legitimate for co-op extraction but must never
            // be allowed once a battle royale is live.
            if (Mode == GameModeId.BattleRoyale && State != SessionState.Lobby)
            {
                Debug.LogWarning($"[GameSession] Player {player} joined a live BR match. " +
                                 "Matchmaking should have prevented this.");
            }
        }

        public void HandlePlayerLeft(PlayerRef player)
        {
            if (!HasStateAuthority) return;

            ConnectedPlayerCount = Mathf.Max(0, ConnectedPlayerCount - 1);
            AlivePlayerCount = Mathf.Max(0, AlivePlayerCount - 1);
        }

        /// <summary>Server-side notification that a player died or extracted.</summary>
        public void NotifyPlayerNoLongerAlive()
        {
            if (!HasStateAuthority) return;
            AlivePlayerCount = Mathf.Max(0, AlivePlayerCount - 1);
        }

        public override void Render()
        {
            foreach (var change in _changes.DetectChanges(this))
            {
                if (change == nameof(State))
                {
                    OnStateChanged?.Invoke(State);
                }
            }
        }
    }

    /// <summary>Placeholder for spawn-point configuration; expanded when maps exist.</summary>
    [System.Serializable]
    public sealed class PlayerSpawnerSettings
    {
        public float SpawnRadius = 20f;
    }
}
