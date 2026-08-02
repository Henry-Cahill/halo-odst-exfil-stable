using UnityEngine;

namespace Dropshock.Core
{
    /// <summary>
    /// A single application of damage. Constructed on the server only — clients
    /// never author a DamageInfo, they only ever request that the server consider
    /// firing a weapon.
    /// </summary>
    public readonly struct DamageInfo
    {
        /// <summary>Raw damage before any armour/hitbox multipliers.</summary>
        public readonly float Amount;

        /// <summary>World-space point of impact, for FX and directional indicators.</summary>
        public readonly Vector3 Point;

        /// <summary>Direction the damage travelled, normalised.</summary>
        public readonly Vector3 Direction;

        /// <summary>Team of the instigator, used for friendly-fire rules.</summary>
        public readonly TeamId InstigatorTeam;

        /// <summary>
        /// Fusion PlayerRef of the instigator encoded as an int, or -1 for world/AI
        /// damage. Stored as int so Core stays free of any Fusion dependency.
        /// </summary>
        public readonly int InstigatorPlayerId;

        public DamageInfo(
            float amount,
            Vector3 point,
            Vector3 direction,
            TeamId instigatorTeam = TeamId.Unassigned,
            int instigatorPlayerId = -1)
        {
            Amount = amount;
            Point = point;
            Direction = direction;
            InstigatorTeam = instigatorTeam;
            InstigatorPlayerId = instigatorPlayerId;
        }
    }

    /// <summary>
    /// Anything the server can damage. Implementations must assert state authority
    /// before mutating any networked value.
    /// </summary>
    public interface IDamageable
    {
        bool IsAlive { get; }

        TeamId Team { get; }

        /// <summary>
        /// Apply damage. Server-authoritative: implementations are expected to
        /// early-out if the caller does not hold state authority.
        /// </summary>
        void ApplyDamage(in DamageInfo info);
    }
}
