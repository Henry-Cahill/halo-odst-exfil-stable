using System.Data;
using Microsoft.Data.SqlClient;

namespace Dropshock.Backend.Sql;

/// <summary>
/// SQL Server implementation of <see cref="IAccountStore"/>.
///
/// <para><b>One operation per instance.</b> <see cref="Find"/> opens a transaction
/// and holds it until <see cref="Save"/> commits or the store is disposed. That is
/// what makes a service call atomic end to end: the read that decides whether an
/// operation was already applied and the write that applies it happen inside the
/// same transaction, so two concurrent callers cannot both decide "not yet
/// applied".</para>
///
/// <para>The row lock is taken on <c>Account</c> with <c>UPDLOCK, HOLDLOCK</c>,
/// which serialises operations per account without serialising the whole table.
/// Beyond that, <c>AppliedOperation.OperationId</c> is a primary key, so even if
/// the locking were wrong the database would still refuse a second application of
/// the same id. Two independent guards, for the same reason the in-memory path has
/// two.</para>
///
/// <para><b>This serialisation is measured, not assumed.</b> Dropshock.Backend.ConcurrencyProbe
/// probes 1 and 2 put eight concurrent callers on one account and confirmed no lost
/// update, and confirmed that a duplicated operation id is caught by the lock rather
/// than by the primary-key backstop.</para>
///
/// <para><b>Transient failures are not handled here.</b> A deadlock or a lock timeout
/// kills the whole transaction, so recovering means re-running the entire operation
/// against a fresh store — which this class cannot do to itself. That is
/// <see cref="SqlStashGateway"/>'s job. Callers should go through the gateway rather
/// than constructing this directly.</para>
/// </summary>
public sealed class SqlAccountStore : IAccountStore, IDisposable
{
    /// <summary>
    /// Default per-command timeout.
    ///
    /// <para>Previously this was left unset, which silently inherited SqlClient's own
    /// 30s default — the probe measured a blocked caller parked for exactly 30.0s
    /// before failing with error -2. Thirty seconds of a game server thread waiting on
    /// a lock is far too long; the value is now explicit so it is a decision rather
    /// than an accident.</para>
    /// </summary>
    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(10);

    private readonly SqlConnection _db;
    private readonly int _commandTimeoutSeconds;
    private SqlTransaction? _tx;
    private Account? _loaded;

    public SqlAccountStore(string connectionString, TimeSpan? commandTimeout = null)
    {
        _commandTimeoutSeconds = (int)Math.Ceiling((commandTimeout ?? DefaultCommandTimeout).TotalSeconds);
        _db = new SqlConnection(connectionString);
        _db.Open();
    }

    private SqlCommand Command(string sql, SqlTransaction? tx) =>
        new(sql, _db, tx) { CommandTimeout = _commandTimeoutSeconds };

    // ---- reading ---------------------------------------------------------

    /// <summary>
    /// Loads an account for a read-modify-write, taking the account's write lock and
    /// holding it until <see cref="Save"/> or <see cref="Dispose"/>.
    /// </summary>
    public Account? Find(string accountId)
    {
        _tx ??= _db.BeginTransaction(IsolationLevel.ReadCommitted);
        _loaded = Load(accountId, _tx, forUpdate: true);
        return _loaded;
    }

    /// <summary>
    /// Loads an account without taking a write lock, releasing everything before
    /// returning. The result must not be passed to <see cref="Save"/>.
    ///
    /// <para>The three reads still share a transaction so they see a coherent account
    /// rather than three unrelated points in time, but no <c>UPDLOCK</c>/<c>HOLDLOCK</c>
    /// hint is applied, so shared locks are released per statement under read
    /// committed instead of being held to the end. The transaction is rolled back
    /// before returning — a read has nothing to commit.</para>
    ///
    /// <para>If you want readers that never block and are never blocked, turn on
    /// <c>READ_COMMITTED_SNAPSHOT</c> for the database; this method then becomes
    /// non-blocking with no code change. That is a database-level decision, so it is
    /// deliberately not done here.</para>
    /// </summary>
    public Account? FindForRead(string accountId)
    {
        if (_tx is not null)
        {
            // Reusing a write transaction for a "read" would silently reintroduce the
            // held lock this method exists to avoid.
            throw new InvalidOperationException(
                "FindForRead was called on a store that already has a write transaction open. " +
                "Use a separate store for reads.");
        }

        using var tx = _db.BeginTransaction(IsolationLevel.ReadCommitted);
        var account = Load(accountId, tx, forUpdate: false);
        tx.Rollback();
        return account;
    }

    private Account? Load(string accountId, SqlTransaction tx, bool forUpdate)
    {
        // UPDLOCK + HOLDLOCK: take the account's write lock now and hold it for the
        // life of the transaction. Without this, two callers could both read "no
        // such operation applied" before either wrote. Omitted for reads, which have
        // nothing to protect and would only block writers.
        string hint = forUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : "";

        using (var cmd = Command($"SELECT AccountId FROM dbo.Account{hint} WHERE AccountId = @a", tx))
        {
            cmd.Parameters.AddWithValue("@a", accountId);
            if (cmd.ExecuteScalar() is null) return null;
        }

        var account = new Account { AccountId = accountId };
        var custodyItems = new List<StoredItem>();

        using (var cmd = Command(
            "SELECT InstanceId, ItemId, Count, Location FROM dbo.Item WHERE AccountId = @a", tx))
        {
            cmd.Parameters.AddWithValue("@a", accountId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var item = new StoredItem((uint)r.GetInt64(0), (ushort)r.GetInt32(1), (ushort)r.GetInt32(2));
                if (r.GetString(3) == "Stash") account.Stash.Add(item);
                else custodyItems.Add(item);
            }
        }

        using (var cmd = Command(
            "SELECT CustodyId, ConfiscatedAtUtc FROM dbo.CustodyRecord WHERE AccountId = @a", tx))
        {
            cmd.Parameters.AddWithValue("@a", accountId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                account.Custody = new CustodyRecord(
                    r.GetString(0), custodyItems, new DateTimeOffset(r.GetDateTime(1), TimeSpan.Zero));
            }
        }

        using (var cmd = Command("SELECT OperationId FROM dbo.AppliedOperation WHERE AccountId = @a", tx))
        {
            cmd.Parameters.AddWithValue("@a", accountId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) account.AppliedOperations.Add(r.GetString(0));
        }

        return account;
    }

    // ---- writing ---------------------------------------------------------

    /// <summary>
    /// Persists the account and commits.
    ///
    /// <para>Item rows, the custody record and the applied-operation ids are written
    /// in the same transaction that <see cref="Find"/> opened. Splitting them would
    /// reintroduce the crash window idempotency exists to close — see
    /// ARCHITECTURE.md, "What the SQL implementation must get right".</para>
    /// </summary>
    public void Save(Account account)
    {
        if (_tx is null) throw new InvalidOperationException("Save without a preceding Find.");

        if (!ReferenceEquals(_loaded, account))
        {
            // The lock and the idempotency check both belong to the account instance
            // Find returned. Saving a different one writes decisions that were made
            // against an unlocked read.
            throw new InvalidOperationException(
                "Save was given an account that this store did not load via Find.");
        }

        // Rewrite this account's items. Safe because the account row is locked for
        // the life of the transaction, so nobody else is editing them concurrently.
        Exec("DELETE FROM dbo.Item WHERE AccountId = @a", c => c.Parameters.AddWithValue("@a", account.AccountId));

        foreach (var item in account.Stash) InsertItem(account.AccountId, item, "Stash");

        Exec("DELETE FROM dbo.CustodyRecord WHERE AccountId = @a",
             c => c.Parameters.AddWithValue("@a", account.AccountId));

        if (account.Custody is not null)
        {
            Exec("INSERT INTO dbo.CustodyRecord (AccountId, CustodyId, ConfiscatedAtUtc) VALUES (@a, @c, @t)", c =>
            {
                c.Parameters.AddWithValue("@a", account.AccountId);
                c.Parameters.AddWithValue("@c", account.Custody.CustodyId);
                c.Parameters.AddWithValue("@t", account.Custody.ConfiscatedAtUtc.UtcDateTime);
            });

            foreach (var item in account.Custody.Items) InsertItem(account.AccountId, item, "Custody");
        }

        // Only ids not already persisted. The primary key makes a duplicate throw,
        // which is the backstop if the lock above ever fails to serialise.
        var existing = new HashSet<string>(StringComparer.Ordinal);
        using (var cmd = Command("SELECT OperationId FROM dbo.AppliedOperation WHERE AccountId = @a", _tx))
        {
            cmd.Parameters.AddWithValue("@a", account.AccountId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) existing.Add(r.GetString(0));
        }

        foreach (var op in account.AppliedOperations.Where(o => !existing.Contains(o)))
        {
            Exec("INSERT INTO dbo.AppliedOperation (OperationId, AccountId, Kind) VALUES (@o, @a, @k)", c =>
            {
                c.Parameters.AddWithValue("@o", op);
                c.Parameters.AddWithValue("@a", account.AccountId);
                c.Parameters.AddWithValue("@k", "Operation");
            });
        }

        _tx.Commit();
        _tx.Dispose();
        _tx = null;
    }

    private void InsertItem(string accountId, StoredItem item, string location) =>
        Exec("INSERT INTO dbo.Item (InstanceId, AccountId, ItemId, Count, Location) VALUES (@i, @a, @t, @c, @l)", c =>
        {
            c.Parameters.AddWithValue("@i", (long)item.InstanceId);
            c.Parameters.AddWithValue("@a", accountId);
            c.Parameters.AddWithValue("@t", (int)item.ItemId);
            c.Parameters.AddWithValue("@c", (int)item.Count);
            c.Parameters.AddWithValue("@l", location);
        });

    private void Exec(string sql, Action<SqlCommand> bind)
    {
        using var cmd = Command(sql, _tx);
        bind(cmd);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Creates an account. Test and provisioning helper, not a game path.</summary>
    public void CreateAccount(string accountId)
    {
        using var cmd = Command(
            "IF NOT EXISTS (SELECT 1 FROM dbo.Account WHERE AccountId = @a) " +
            "INSERT INTO dbo.Account (AccountId) VALUES (@a)", _tx);
        cmd.Parameters.AddWithValue("@a", accountId);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        // An uncommitted transaction here means the operation failed partway. Roll
        // it back explicitly rather than relying on connection teardown.
        //
        // After a deadlock the server has already rolled back and killed the
        // transaction, so Rollback throws. That is expected and must not mask the
        // original SqlException on the way out.
        try { _tx?.Rollback(); }
        catch (InvalidOperationException) { /* already rolled back by the server */ }
        catch (SqlException) { /* connection is gone; nothing left to roll back */ }

        _tx?.Dispose();
        _db.Dispose();
    }
}
