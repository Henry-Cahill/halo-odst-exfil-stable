using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace Dropshock.Backend.ConcurrencyProbe;

/// <summary>Outcome of a probe. Deliberately not a boolean.</summary>
///
/// <remarks>
/// Several of these probes are <b>measurements</b>, not assertions. "Concurrent
/// readers block writers" is not a test that can pass or fail — it is a fact about
/// the current design that we either confirm or refute. Forcing that into PASS/FAIL
/// would either manufacture a failure for behaviour that is intentional, or hide a
/// defect behind a green run. See ARCHITECTURE.md, "Gotchas — things that fail
/// silently or misreport".
/// </remarks>
public enum Verdict
{
    /// <summary>The invariant held.</summary>
    Pass,

    /// <summary>The invariant was violated. This is a bug in the backend.</summary>
    Fail,

    /// <summary>Neither — a recorded observation about how the system behaves.</summary>
    Observed,

    /// <summary>
    /// The probe itself broke — a harness bug, a connection drop, a permissions
    /// problem. Distinct from <see cref="Fail"/> on purpose: reporting a crash in the
    /// probe as a backend correctness bug is exactly the misreporting this file is
    /// supposed to prevent. Still exits non-zero, because the run proved nothing.
    /// </summary>
    Aborted,

    /// <summary>Not run.</summary>
    Skipped,
}

/// <summary>
/// Shared plumbing for the concurrency probes: raw SQL, provisioning, cleanup and
/// reporting.
///
/// <para>This deliberately does not reuse <c>Dropshock.Backend.SqlTests</c>'s
/// <c>Check</c> helper. That harness is single-threaded and boolean; every
/// assertion here is either timed, concurrent, or both.</para>
/// </summary>
public sealed class Harness
{
    private readonly string _conn;
    private readonly List<(string Name, Verdict V, string Detail)> _results = new();

    /// <summary>Instance ids are a primary key across every account, so a run must
    /// not collide with a previous run's leftovers. One random base per run.</summary>
    public uint InstanceBase { get; } = (uint)Random.Shared.Next(10_000_000, 1_500_000_000);

    public Harness(string conn) => _conn = conn;

    public string ConnectionString => _conn;

    // ---- reporting -------------------------------------------------------

    public void Record(string name, Verdict v, string detail)
    {
        _results.Add((name, v, detail));
        string tag = v switch
        {
            Verdict.Pass => "PASS ",
            Verdict.Fail => "FAIL ",
            Verdict.Observed => "OBSRV",
            Verdict.Aborted => "ABORT",
            _ => "SKIP ",
        };
        Console.WriteLine($"  {tag}  {name}");
        if (!string.IsNullOrWhiteSpace(detail))
        {
            foreach (var line in detail.Split('\n')) Console.WriteLine($"           {line}");
        }
    }

    public void Section(string title) => Console.WriteLine($"\n-- {title} --");

    /// <summary>Prints the summary. Returns the process exit code.</summary>
    public int Summarise()
    {
        int pass = _results.Count(r => r.V == Verdict.Pass);
        int fail = _results.Count(r => r.V == Verdict.Fail);
        int obs = _results.Count(r => r.V == Verdict.Observed);
        int abort = _results.Count(r => r.V == Verdict.Aborted);
        int skip = _results.Count(r => r.V == Verdict.Skipped);

        Console.WriteLine($"\n{new string('=', 72)}");
        Console.WriteLine($"{pass} passed, {fail} failed, {obs} observed, {abort} aborted, {skip} skipped.");

        if (fail > 0)
        {
            Console.WriteLine("\nFAILED — these are correctness bugs in the backend:");
            foreach (var r in _results.Where(r => r.V == Verdict.Fail))
                Console.WriteLine($"  * {r.Name}");
        }

        if (abort > 0)
        {
            Console.WriteLine("\nABORTED — the probe broke, NOT the backend. Fix the harness and re-run:");
            foreach (var r in _results.Where(r => r.V == Verdict.Aborted))
                Console.WriteLine($"  * {r.Name}");
        }

        if (obs > 0)
        {
            Console.WriteLine("\nOBSERVED — behaviour to decide on, not necessarily bugs:");
            foreach (var r in _results.Where(r => r.V == Verdict.Observed))
                Console.WriteLine($"  * {r.Name}");
        }

        // A skipped probe is not a passing probe. Say so loudly: a concurrency
        // harness that quietly skipped its own hard cases is the failure mode this
        // whole exercise exists to avoid.
        if (skip > 0)
        {
            Console.WriteLine("\nSKIPPED — these proved nothing:");
            foreach (var r in _results.Where(r => r.V == Verdict.Skipped))
                Console.WriteLine($"  * {r.Name}");
        }

        return fail > 0 || abort > 0 ? 1 : 0;
    }

    // ---- raw SQL ---------------------------------------------------------

    public SqlConnection Open()
    {
        var db = new SqlConnection(_conn);
        db.Open();
        return db;
    }

    public void Exec(string sql, int timeoutSeconds = 60, Action<SqlCommand>? bind = null)
    {
        using var db = Open();
        using var cmd = new SqlCommand(sql, db) { CommandTimeout = timeoutSeconds };
        bind?.Invoke(cmd);
        cmd.ExecuteNonQuery();
    }

    public T? Scalar<T>(string sql, Action<SqlCommand>? bind = null)
    {
        using var db = Open();
        using var cmd = new SqlCommand(sql, db) { CommandTimeout = 60 };
        bind?.Invoke(cmd);
        object? o = cmd.ExecuteScalar();
        return o is null or DBNull ? default : (T)Convert.ChangeType(o, typeof(T));
    }

    // ---- provisioning ----------------------------------------------------

    public string NewAccountId(string prefix) => $"probe-{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    public void CreateAccount(string accountId) =>
        Exec("IF NOT EXISTS (SELECT 1 FROM dbo.Account WHERE AccountId = @a) " +
             "INSERT INTO dbo.Account (AccountId) VALUES (@a)",
             bind: c => c.Parameters.AddWithValue("@a", accountId));

    /// <summary>Bulk-seeds stash rows. Used to push an account over SQL Server's
    /// lock-escalation threshold (~5000 locks per statement).</summary>
    ///
    /// <remarks>
    /// Set-based on purpose. The row-by-row version this replaced needed
    /// <c>SqlCommand.Prepare</c>, which requires every parameter to have an
    /// explicitly set type — and it issued one round trip per row, which is minutes
    /// of wall clock at 14k rows. Generating the sequence server-side from
    /// <c>sys.all_objects</c> is one statement and one round trip.
    /// </remarks>
    public void SeedItems(string accountId, int count, uint firstInstanceId)
    {
        using var db = Open();
        using var cmd = new SqlCommand(@"
            WITH n AS (
                SELECT TOP (@count) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS i
                FROM sys.all_objects a CROSS JOIN sys.all_objects b
            )
            INSERT INTO dbo.Item (InstanceId, AccountId, ItemId, Count, Location)
            SELECT @first + i, @a, 1, 1, 'Stash' FROM n;", db) { CommandTimeout = 300 };

        cmd.Parameters.Add("@count", System.Data.SqlDbType.Int).Value = count;
        cmd.Parameters.Add("@first", System.Data.SqlDbType.BigInt).Value = (long)firstInstanceId;
        cmd.Parameters.Add("@a", System.Data.SqlDbType.NVarChar, 64).Value = accountId;

        int inserted = cmd.ExecuteNonQuery();
        if (inserted != count)
        {
            // sys.all_objects squared is normally millions of rows, but a locked-down
            // account can see far fewer. Silently seeding too few rows would sit below
            // the escalation threshold and make probe 5 report a false negative.
            throw new InvalidOperationException(
                $"Seed for {accountId} inserted {inserted} rows, expected {count}. " +
                "Probe 5 needs the full count to reach lock escalation.");
        }
    }

    /// <summary>Removes everything this run created. FK order matters.</summary>
    public void DeleteAccount(string accountId)
    {
        try
        {
            Exec("DELETE FROM dbo.ItemTransferLog WHERE AccountId = @a", 300,
                 c => c.Parameters.AddWithValue("@a", accountId));
            Exec("DELETE FROM dbo.Item WHERE AccountId = @a", 300,
                 c => c.Parameters.AddWithValue("@a", accountId));
            Exec("DELETE FROM dbo.CustodyRecord WHERE AccountId = @a", 300,
                 c => c.Parameters.AddWithValue("@a", accountId));
            Exec("DELETE FROM dbo.AppliedOperation WHERE AccountId = @a", 300,
                 c => c.Parameters.AddWithValue("@a", accountId));
            Exec("DELETE FROM dbo.Account WHERE AccountId = @a", 300,
                 c => c.Parameters.AddWithValue("@a", accountId));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"  (cleanup of {accountId} failed: {e.Message})");
        }
    }

    public int CountItems(string accountId) =>
        Scalar<int>("SELECT COUNT(*) FROM dbo.Item WHERE AccountId = @a",
                    c => c.Parameters.AddWithValue("@a", accountId));

    public int CountOperations(string accountId) =>
        Scalar<int>("SELECT COUNT(*) FROM dbo.AppliedOperation WHERE AccountId = @a",
                    c => c.Parameters.AddWithValue("@a", accountId));

    // ---- helpers ---------------------------------------------------------

    /// <summary>
    /// Holds <c>UPDLOCK, HOLDLOCK</c> on one account row for a fixed duration, the
    /// same lock <see cref="Sql.SqlAccountStore.Find"/> takes.
    ///
    /// <para>Returns once the lock is definitely held, so the caller can time a
    /// contending operation without racing the setup.</para>
    /// </summary>
    public Task HoldAccountLock(string accountId, TimeSpan duration, ManualResetEventSlim held)
        => Task.Run(() =>
        {
            using var db = Open();
            using var tx = db.BeginTransaction(System.Data.IsolationLevel.ReadCommitted);
            using (var cmd = new SqlCommand(
                "SELECT AccountId FROM dbo.Account WITH (UPDLOCK, HOLDLOCK) WHERE AccountId = @a", db, tx))
            {
                cmd.Parameters.AddWithValue("@a", accountId);
                cmd.ExecuteScalar();
            }
            held.Set();
            Thread.Sleep(duration);
            tx.Rollback();
        });

    /// <summary>Runs an action and reports how long it took and what it threw.</summary>
    public static (TimeSpan Elapsed, Exception? Error) Time(Action a)
    {
        var sw = Stopwatch.StartNew();
        try { a(); return (sw.Elapsed, null); }
        catch (Exception e) { return (sw.Elapsed, e); }
    }

    /// <summary>
    /// Runs <paramref name="body"/> on <paramref name="count"/> dedicated threads
    /// and waits for all of them.
    ///
    /// <para><b>Not Parallel.For.</b> Parallel.For is thread-pool scheduled and makes
    /// no guarantee about how many iterations run at once — it is free to run them
    /// one after another. Every probe here coordinates its threads with a Barrier, so
    /// a partial degree of parallelism would block forever waiting for participants
    /// that were never scheduled. Real threads are the only way to guarantee that N
    /// callers are genuinely in flight together.</para>
    /// </summary>
    public static void RunConcurrently(int count, Action<int> body)
    {
        var threads = new Thread[count];
        for (int i = 0; i < count; i++)
        {
            int n = i;
            threads[i] = new Thread(() => body(n)) { IsBackground = true, Name = $"probe-{n}" };
        }
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();
    }

    /// <summary>Unwraps to the SqlException, if there is one anywhere in the chain.</summary>
    public static SqlException? AsSql(Exception? e)
    {
        while (e is not null)
        {
            if (e is SqlException s) return s;
            e = e.InnerException;
        }
        return null;
    }

    public static string Describe(Exception? e)
    {
        if (e is null) return "no error";
        var s = AsSql(e);
        return s is not null
            ? $"{s.GetType().Name} number={s.Number} \"{s.Message.Split('\n')[0]}\""
            : $"{e.GetType().Name}: {e.Message.Split('\n')[0]}";
    }
}
