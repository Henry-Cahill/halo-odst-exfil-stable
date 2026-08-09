namespace Dropshock.Backend;

/// <summary>
/// The only path by which raid outcomes reach permanent storage.
///
/// <para><b>Every mutating method is idempotent</b>, keyed on a caller-supplied
/// operation id. That is the decision recorded in ARCHITECTURE.md: rather than
/// choosing whether a crash mid-commit loses gear or duplicates it, a retry either
/// completes the original write or observes it already done. Callers may retry
/// freely; they may not invent a new id for a retry.</para>
///
/// <para><b>The client never calls any of this.</b> These run behind an
/// authenticated service, against the server's own view of the raid. A client that
/// could name its own surviving loadout would simply declare itself rich.</para>
/// </summary>
public sealed class StashService
{
    private readonly IAccountStore _store;
    private readonly TimeProvider _clock;

    public StashService(IAccountStore store, TimeProvider? clock = null)
    {
        _store = store;
        // Server clock only. A client-supplied timestamp is never an input — see
        // "Offline production", which has the same rule for the same reason.
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Removes a loadout from the stash at raid start.
    ///
    /// <para>This is the checkout half of the model. Gear physically leaves the
    /// stash when the raid begins, which is what makes "death writes nothing"
    /// coherent: dying simply means no write-back ever happens, and the items are
    /// already gone. Extraction is the only path that returns anything.</para>
    /// </summary>
    public WriteResult CheckoutLoadout(string operationId, string accountId, IReadOnlyList<uint> instanceIds)
    {
        var account = _store.Find(accountId);
        if (account is null) return WriteResult.UnknownAccount;

        if (account.AppliedOperations.Contains(operationId)) return WriteResult.AlreadyApplied;

        // Validate the whole set before mutating anything. A partial checkout would
        // leave the stash in a state no single operation id describes, which is
        // precisely what idempotency is supposed to prevent.
        foreach (uint id in instanceIds)
        {
            if (!account.Stash.Any(s => s.InstanceId == id)) return WriteResult.ItemNotInStash;
        }

        foreach (uint id in instanceIds)
        {
            account.Stash.RemoveAll(s => s.InstanceId == id);
        }

        account.AppliedOperations.Add(operationId);
        _store.Save(account);
        return WriteResult.Applied;
    }

    /// <summary>
    /// Commits a successful extraction: the surviving loadout returns to the stash.
    ///
    /// <para>Death has no counterpart here on purpose. The loss is the absence of
    /// this call, not a separate deletion — which means a crash that prevents it
    /// costs the player their raid gear and never grants gear they did not earn.
    /// </para>
    /// </summary>
    public WriteResult CommitExtraction(string extractionId, string accountId, IReadOnlyList<StoredItem> survivingItems)
    {
        var account = _store.Find(accountId);
        if (account is null) return WriteResult.UnknownAccount;

        if (account.AppliedOperations.Contains(extractionId)) return WriteResult.AlreadyApplied;

        foreach (var item in survivingItems)
        {
            // Instance ids are unique for an item's life. The same id arriving twice
            // across the raid/stash boundary is duplication, and the whole point of
            // carrying the id is that it is detectable rather than invisible.
            //
            // Checks custody as well as the stash: dbo.Item's primary key spans both,
            // so an item in custody cannot simultaneously extract.
            if (account.PersistedInstanceIds.Contains(item.InstanceId))
            {
                throw new InvalidOperationException(
                    $"Duplicate InstanceId {item.InstanceId} entering stash for account " +
                    $"{accountId} via extraction {extractionId}. This is a duplication bug " +
                    "or an exploit; refusing to commit.");
            }
        }

        account.Stash.AddRange(survivingItems);
        account.AppliedOperations.Add(extractionId);
        _store.Save(account);
        return WriteResult.Applied;
    }

    /// <summary>
    /// Takes a player's gear into custody on abduction.
    ///
    /// <para>Refused if the slot is occupied. That refusal is load-bearing: the
    /// caller must then kill the player normally rather than abduct them, which is
    /// the decision recorded for "one custody slot per account".</para>
    /// </summary>
    public WriteResult ConfiscateToCustody(string custodyId, string accountId, IReadOnlyList<StoredItem> items)
    {
        var account = _store.Find(accountId);
        if (account is null) return WriteResult.UnknownAccount;

        if (account.AppliedOperations.Contains(custodyId)) return WriteResult.AlreadyApplied;
        if (account.Custody is not null) return WriteResult.CustodyOccupied;

        // Confiscation takes gear carried in a raid, which was checked out and is
        // therefore not persisted. An instance that is already stored means the
        // caller is confused about where the item is, and honouring it would put one
        // instance in two places — which the database refuses outright.
        var persisted = account.PersistedInstanceIds.ToHashSet();
        if (items.Any(i => persisted.Contains(i.InstanceId))) return WriteResult.ItemAlreadyPersisted;

        account.Custody = new CustodyRecord(custodyId, items.ToArray(), _clock.GetUtcNow());
        account.AppliedOperations.Add(custodyId);
        _store.Save(account);
        return WriteResult.Applied;
    }

    /// <summary>Escaping: custody returns to the stash, in one commit.</summary>
    public WriteResult RecoverCustody(string operationId, string accountId)
    {
        var account = _store.Find(accountId);
        if (account is null) return WriteResult.UnknownAccount;

        if (account.AppliedOperations.Contains(operationId)) return WriteResult.AlreadyApplied;
        if (account.Custody is null) return WriteResult.CustodyEmpty;

        account.Stash.AddRange(account.Custody.Items);
        account.Custody = null;
        account.AppliedOperations.Add(operationId);
        _store.Save(account);
        return WriteResult.Applied;
    }

    /// <summary>
    /// Failing to escape: custody is destroyed.
    ///
    /// <para>Destroyed rather than transferred, because no player profits from an
    /// abduction — see the captor decision in ARCHITECTURE.md. Awarding it to a
    /// killer would create a direct incentive to farm abductions.</para>
    /// </summary>
    public WriteResult ForfeitCustody(string operationId, string accountId)
    {
        var account = _store.Find(accountId);
        if (account is null) return WriteResult.UnknownAccount;

        if (account.AppliedOperations.Contains(operationId)) return WriteResult.AlreadyApplied;
        if (account.Custody is null) return WriteResult.CustodyEmpty;

        account.Custody = null;
        account.AppliedOperations.Add(operationId);
        _store.Save(account);
        return WriteResult.Applied;
    }

    /// <summary>
    /// Read-only view for the compound UI. Never networked into a raid.
    ///
    /// <para>Uses <see cref="IAccountStore.FindForRead"/>, not <c>Find</c>. Find takes
    /// a write lock for the store's lifetime, which meant a stash read blocked every
    /// write to that account — measured at the full duration of the read.</para>
    /// </summary>
    public IReadOnlyList<StoredItem> ReadStash(string accountId)
        => _store.FindForRead(accountId)?.Stash.ToArray() ?? Array.Empty<StoredItem>();

    public CustodyRecord? ReadCustody(string accountId) => _store.FindForRead(accountId)?.Custody;
}
