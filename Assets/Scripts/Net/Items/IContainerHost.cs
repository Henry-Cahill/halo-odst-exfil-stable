using Dropshock.Core.Items;
using Fusion;
using UnityEngine;

namespace Dropshock.Net.Items
{
    /// <summary>
    /// Anything that owns server-side <see cref="GridContainer"/>s and can say who is
    /// allowed to touch them.
    ///
    /// <para>Access control lives on the host rather than in a central switch because
    /// the rule differs per host and gets stricter over time: a player's own rig is
    /// owner-only, a corpse is proximity-based, and a stash is closed to everyone
    /// during a defence session. A single shared rule would eventually be relaxed to
    /// fit the loosest case.</para>
    /// </summary>
    public interface IContainerHost
    {
        /// <summary>Resolves one of this host's containers by index.</summary>
        bool TryGetContainer(byte containerIndex, out GridContainer container);

        /// <summary>
        /// Server-side authority check: may this player touch this container, right
        /// now, from where they are standing?
        /// </summary>
        /// <param name="player">Requesting player.</param>
        /// <param name="playerPosition">Server's position for that player — never a
        /// client-supplied one, or range checks become meaningless.</param>
        bool CanPlayerAccess(PlayerRef player, Vector3 playerPosition);
    }
}
