namespace Dropshock.Backend;

/// <summary>
/// An item as the backend stores it.
///
/// <para>Deliberately smaller than the raid-side <c>ItemStack</c>: grid position
/// and rotation are raid concerns, not persistence concerns. The stash is only ever
/// touched from the menu, so where a rifle sat in a corpse's grid is not worth
/// carrying across the boundary.</para>
/// </summary>
public readonly record struct StoredItem(uint InstanceId, ushort ItemId, ushort Count)
{
    public override string ToString() => $"#{InstanceId} item={ItemId} x{Count}";
}

/// <summary>
/// Gear taken from a player on abduction, held until they escape.
///
/// <para><b>One slot per account.</b> A player already holding custody gear cannot
/// be abducted again — they die normally instead. That bound is the whole reason
/// this is a single nullable record rather than a collection: it removes every
/// question about which batch an escape recovers.</para>
/// </summary>
public sealed record CustodyRecord(
    string CustodyId,
    IReadOnlyList<StoredItem> Items,
    DateTimeOffset ConfiscatedAtUtc);

/// <summary>
/// Everything the backend holds for one account.
///
/// <para><see cref="AppliedOperations"/> is what makes writes idempotent. Every
/// mutating call carries a caller-supplied id; applying the same id twice is a
/// no-op. See ARCHITECTURE.md, "Stash and persistence".</para>
/// </summary>
public sealed class Account
{
    public required string AccountId { get; init; }

    /// <summary>Permanent per-account storage. Never networked, never client-authoritative.</summary>
    public List<StoredItem> Stash { get; } = new();

    /// <summary>The single custody slot. Null when the player holds no confiscated gear.</summary>
    public CustodyRecord? Custody { get; set; }

    /// <summary>Ids of operations already applied, for idempotency.</summary>
    public HashSet<string> AppliedOperations { get; } = new(StringComparer.Ordinal);

    /// <summary>Every persisted instance id for this account, stash and custody together.</summary>
    public IEnumerable<uint> PersistedInstanceIds =>
        Stash.Select(s => s.InstanceId)
             .Concat(Custody?.Items.Select(c => c.InstanceId) ?? Enumerable.Empty<uint>());

    /// <summary>
    /// Throws if one instance id appears twice across stash and custody.
    ///
    /// <para>This mirrors the database exactly: <c>dbo.Item.InstanceId</c> is the
    /// primary key across ALL persisted items, so the same instance cannot be in two
    /// places. The in-memory store calls this on save so the fake is as strict as
    /// the real one — an earlier version was not, and passed a suite that SQL Server
    /// then rejected. A test double that accepts states the real store refuses is
    /// worse than no double at all.</para>
    /// </summary>
    public void AssertNoDuplicateInstances()
    {
        var seen = new HashSet<uint>();
        foreach (uint id in PersistedInstanceIds)
        {
            if (!seen.Add(id))
            {
                throw new InvalidOperationException(
                    $"InstanceId {id} is persisted twice for account {AccountId} " +
                    "(stash and custody). One instance cannot be in two places.");
            }
        }
    }
}

/// <summary>Why a write was refused. Callers must not treat a refusal as success.</summary>
public enum WriteResult
{
    /// <summary>Applied for the first time.</summary>
    Applied = 0,

    /// <summary>This exact operation id was already applied. The state is correct; do nothing.</summary>
    AlreadyApplied = 1,

    /// <summary>No such account.</summary>
    UnknownAccount = 2,

    /// <summary>Custody slot already occupied — the player cannot be abducted again.</summary>
    CustodyOccupied = 3,

    /// <summary>Nothing in custody to recover.</summary>
    CustodyEmpty = 4,

    /// <summary>An item named by the caller is not actually in the account's stash.</summary>
    ItemNotInStash = 5,

    /// <summary>
    /// An item is already persisted for this account, in the stash or in custody.
    /// <para>
    /// Confiscation takes gear a player is carrying <em>in a raid</em>, which by
    /// definition is not persisted — it was checked out at raid start. An instance
    /// arriving that is already stored means the caller is confused about where the
    /// item is, and honouring it would put one instance in two places.
    /// </para>
    /// </summary>
    ItemAlreadyPersisted = 6,
}
