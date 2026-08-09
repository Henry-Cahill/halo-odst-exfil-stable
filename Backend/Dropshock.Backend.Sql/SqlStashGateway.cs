using Microsoft.Data.SqlClient;

namespace Dropshock.Backend.Sql;

/// <summary>
/// Classification of SQL Server errors that are worth retrying.
/// </summary>
public static class SqlTransientErrors
{
    /// <summary>
    /// True if the error is transient — the same operation, run again, has a real
    /// chance of succeeding.
    ///
    /// <para>Deliberately a short list. Retrying an error that is actually a bug
    /// (a constraint violation, a bad column) just delays the report and multiplies
    /// the load. Every entry here is a contention or connectivity failure, never a
    /// logic failure.</para>
    /// </summary>
    public static bool IsTransient(SqlException e) => e.Number switch
    {
        // Deadlock victim. The probe reaches this via lock escalation on dbo.Item:
        // Save deletes and rewrites every row for an account, so a large stash
        // escalates to a table lock and two accounts deadlock against each other.
        1205 => true,

        // Client-side command timeout. Measured as the real same-account failure:
        // the account row lock serialises correctly, and a caller that waits too
        // long gets this rather than a deadlock.
        -2 => true,

        // Server-side lock request timeout (SET LOCK_TIMEOUT). Same cause as -2,
        // reported from the other side.
        1222 => true,

        // Connection-level transients: severed mid-operation, or the server is
        // shutting down / failing over. Safe here only because every write carries
        // an operation id, so a replay is a no-op if the original committed.
        233 or 10053 or 10054 or 10060 or 10928 or 10929 or 40197 or 40501 or 40613 => true,

        _ => false,
    };

    /// <summary>Finds a SqlException anywhere in the chain.</summary>
    public static SqlException? Find(Exception? e)
    {
        while (e is not null)
        {
            if (e is SqlException s) return s;
            e = e.InnerException;
        }
        return null;
    }
}

/// <summary>How the gateway retries. Defaults are tuned for a raid-boundary write.</summary>
/// <param name="MaxAttempts">Total attempts including the first. 1 disables retry.</param>
/// <param name="BaseDelay">First backoff step; doubles each attempt.</param>
/// <param name="MaxDelay">Ceiling on a single backoff step.</param>
/// <param name="CommandTimeout">Per-command timeout for each attempt.</param>
public sealed record SqlRetryOptions(
    int MaxAttempts = 4,
    TimeSpan? BaseDelay = null,
    TimeSpan? MaxDelay = null,
    TimeSpan? CommandTimeout = null)
{
    public TimeSpan EffectiveBaseDelay => BaseDelay ?? TimeSpan.FromMilliseconds(50);
    public TimeSpan EffectiveMaxDelay => MaxDelay ?? TimeSpan.FromSeconds(2);
    public TimeSpan EffectiveCommandTimeout => CommandTimeout ?? SqlAccountStore.DefaultCommandTimeout;
}

/// <summary>Reported for each retried attempt, so callers can log or assert on it.</summary>
public sealed record RetryAttempt(int Attempt, int MaxAttempts, int SqlErrorNumber, TimeSpan Delay, string Message);

/// <summary>
/// The entry point for every backend operation. Owns the connection string and the
/// retry policy; hands out a <see cref="StashService"/> bound to a fresh store per
/// attempt.
///
/// <para><b>Why retry lives here and not in <see cref="SqlAccountStore"/>.</b> A
/// deadlock or a lock timeout does not fail one statement — SQL Server rolls back
/// and terminates the whole transaction. The store holds exactly one transaction for
/// its lifetime, so by the time the error surfaces the store is dead: there is
/// nothing left to retry against. Recovery means discarding it and re-running the
/// entire operation, Find included, on a new connection. Only something outside the
/// store can do that.</para>
///
/// <para><b>Why retrying is safe at all.</b> Every mutating operation carries a
/// caller-supplied operation id, and <c>dbo.AppliedOperation.OperationId</c> is a
/// primary key. Replaying an operation either completes a write that never committed
/// or observes <see cref="WriteResult.AlreadyApplied"/>. That property is the entire
/// licence for this class — a caller that invents a fresh operation id per attempt
/// destroys it and turns a retry into duplication.</para>
/// </summary>
public sealed class SqlStashGateway
{
    private readonly string _connectionString;
    private readonly SqlRetryOptions _options;
    private readonly TimeProvider? _clock;
    private readonly Action<RetryAttempt>? _onRetry;

    public SqlStashGateway(
        string connectionString,
        SqlRetryOptions? options = null,
        TimeProvider? clock = null,
        Action<RetryAttempt>? onRetry = null)
    {
        _connectionString = connectionString;
        _options = options ?? new SqlRetryOptions();
        _clock = clock;
        _onRetry = onRetry;
    }

    /// <summary>
    /// Runs one backend operation, retrying transient failures.
    ///
    /// <para><paramref name="operation"/> must be safe to run more than once. In
    /// practice that means it carries a fixed operation id, generated by the caller
    /// before the first attempt — never inside the delegate.</para>
    /// </summary>
    public T Execute<T>(Func<StashService, T> operation)
    {
        var rng = Random.Shared;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                // A fresh store, and therefore a fresh connection and transaction,
                // for every attempt. Reusing one after a deadlock is not possible.
                using var store = new SqlAccountStore(_connectionString, _options.EffectiveCommandTimeout);
                return operation(new StashService(store, _clock));
            }
            catch (Exception e) when (ShouldRetry(e, attempt, out var sql))
            {
                // Exponential backoff with full jitter. Jitter matters more than the
                // curve here: without it, two callers that deadlocked will back off
                // by the same amount and collide again on the same tick.
                var ceiling = TimeSpan.FromTicks(Math.Min(
                    _options.EffectiveBaseDelay.Ticks * (1L << (attempt - 1)),
                    _options.EffectiveMaxDelay.Ticks));
                var delay = TimeSpan.FromTicks((long)(ceiling.Ticks * rng.NextDouble()));

                _onRetry?.Invoke(new RetryAttempt(
                    attempt, _options.MaxAttempts, sql!.Number, delay, sql.Message.Split('\n')[0]));

                Thread.Sleep(delay);
            }
        }
    }

    /// <summary>Runs an operation with no return value.</summary>
    public void Execute(Action<StashService> operation) =>
        Execute<object?>(s => { operation(s); return null; });

    private bool ShouldRetry(Exception e, int attempt, out SqlException? sql)
    {
        sql = SqlTransientErrors.Find(e);

        // Out of attempts: let the original exception escape untouched. Wrapping it
        // would bury the error number the operator needs.
        if (sql is null || attempt >= _options.MaxAttempts) return false;

        return SqlTransientErrors.IsTransient(sql);
    }
}
