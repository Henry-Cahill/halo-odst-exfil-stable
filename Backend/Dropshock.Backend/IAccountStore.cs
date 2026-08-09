namespace Dropshock.Backend;

/// <summary>
/// Persistence boundary. Kept deliberately narrow so the service logic can be
/// exercised without SQL Server, and so the eventual SQL implementation has an
/// obvious shape to satisfy.
/// </summary>
public interface IAccountStore
{
    Account? Find(string accountId);

    /// <summary>
    /// Loads an account for reading only. The result must not be passed to
    /// <see cref="Save"/>.
    ///
    /// <para>This exists because <see cref="Find"/> takes a write lock and holds it
    /// until the store is disposed — correct for a read-modify-write, badly wrong for
    /// the compound UI reading a stash. Measured: a read blocked every write to that
    /// account for the reader's whole lifetime. See
    /// Dropshock.Backend.ConcurrencyProbe, probe 3.</para>
    ///
    /// <para>Implementations must release whatever locks they take before returning,
    /// and must not leave a transaction open for a later <see cref="Save"/>.</para>
    /// </summary>
    Account? FindForRead(string accountId);

    /// <summary>
    /// Persists the whole account atomically.
    ///
    /// <para>The SQL implementation must make this one transaction covering the
    /// stash rows, the custody record and the applied-operation id together.
    /// Recording the operation id in a separate commit from the effect it describes
    /// would reintroduce exactly the crash window idempotency exists to close.</para>
    /// </summary>
    void Save(Account account);
}

/// <summary>
/// In-memory store for tests and local runs.
///
/// <para>Not a stepping stone to production: the real one is SQL Server, per
/// ARCHITECTURE.md. This exists so the idempotency and custody rules can be proven
/// before a database is stood up.</para>
/// </summary>
public sealed class InMemoryAccountStore : IAccountStore
{
    private readonly Dictionary<string, Account> _accounts = new(StringComparer.Ordinal);

    public Account CreateAccount(string accountId)
    {
        var account = new Account { AccountId = accountId };
        _accounts[accountId] = account;
        return account;
    }

    public Account? Find(string accountId)
        => _accounts.TryGetValue(accountId, out var a) ? a : null;

    /// <summary>No locking in memory, so reading is identical to Find.</summary>
    public Account? FindForRead(string accountId) => Find(accountId);

    public void Save(Account account)
    {
        // Enforce the same invariant the database enforces via the Item primary key.
        // Without this the fake accepts states SQL Server rejects, and the in-memory
        // suite reports success on a design that cannot actually persist.
        account.AssertNoDuplicateInstances();
        _accounts[account.AccountId] = account;
    }
}
