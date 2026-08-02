namespace Dropshock.Core
{
    /// <summary>
    /// Which product mode a session is running. Drives interest management,
    /// persistence, and win conditions. See ARCHITECTURE.md.
    /// </summary>
    public enum GameModeId
    {
        /// <summary>Small squad, PvE, persistent gear, extract-or-lose-it.</summary>
        CoopExtraction = 0,

        /// <summary>Large lobby, PvP, no persistence, last squad standing.</summary>
        BattleRoyale = 1,
    }

    /// <summary>
    /// Team identity. Team 0 is reserved for "unassigned" so a default-constructed
    /// value can never be mistaken for a real team.
    /// </summary>
    public enum TeamId : byte
    {
        Unassigned = 0,
        Squad1 = 1,
        Squad2 = 2,
        Hostile = 200,
    }
}
