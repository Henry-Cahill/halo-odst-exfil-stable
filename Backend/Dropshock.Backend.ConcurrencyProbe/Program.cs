using System.Diagnostics;
using Dropshock.Backend;
using Dropshock.Backend.ConcurrencyProbe;
using Dropshock.Backend.Sql;
using Microsoft.Data.SqlClient;

// Concurrency probe for the account backend.
//
// WHAT THIS IS FOR
// ----------------
// ARCHITECTURE.md ("The account backend") records that the UPDLOCK/HOLDLOCK
// serialisation in SqlAccountStore is "reasoned about but untested", and that there
// is no deadlock retry anywhere in the backend. Dropshock.Backend.SqlTests cannot
// close either gap: it is strictly single-threaded, opening one store at a time, so
// it can never put two callers on one account.
//
// This probe puts two or more callers on one account at once and reports what
// actually happens.
//
// WHAT TO EXPECT
// --------------
// Probes 1 and 2 are expected to PASS. Every caller's first action in Find is to
// take UPDLOCK+HOLDLOCK on its own dbo.Account row, so two callers on one account
// request the same lock on the same resource in the same order. That is clean
// serialisation, not a deadlock — the second caller blocks and then proceeds.
//
// That is exactly why probes 3-5 exist. If the same-account path cannot deadlock,
// then the missing 1205 handling has to be justified (or dismissed) by finding
// where a deadlock CAN come from, and by showing what the more likely failure on
// the same-account path actually is:
//
//   3. Reads take a WRITE lock. StashService.ReadStash/ReadCustody go through Find,
//      which takes UPDLOCK+HOLDLOCK unconditionally and holds it until Dispose.
//      Neither ever calls Save. A compound-UI stash read therefore blocks all writes
//      to that account for its whole lifetime.
//
//   4. The realistic same-account failure is a TIMEOUT, not a deadlock. No
//      CommandTimeout is set anywhere in SqlAccountStore, so it is the 30s default,
//      and the error is -2. There is no handling for -2 either.
//
//   5. A real 1205 needs lock-order inversion or lock escalation. Save deletes and
//      rewrites every item row for an account, so a large stash escalates to a table
//      lock on dbo.Item — and that deadlocks ACROSS accounts.
//
// Usage:
//   export DROPSHOCK_SQL='Server=...;Database=...;User ID=...;Password=...;TrustServerCertificate=True'
//   dotnet run --project Dropshock.Backend.ConcurrencyProbe
//
// Flags:
//   --skip-slow             skip probe 4 (it deliberately waits out a 30s timeout)
//   --skip-escalation       skip probe 5 (it inserts ~14k rows, then cleans up)
//   --escalation-rows N     rows per account for probe 5 (default 7000)
//   --concurrency N         concurrent callers for probe 1 (default 8)
//
// Exit code is 1 if any probe FAILED. "Observed" results do not fail the run; they
// are design questions, and the summary lists them separately.

string? conn = Environment.GetEnvironmentVariable("DROPSHOCK_SQL");
if (string.IsNullOrWhiteSpace(conn))
{
    Console.Error.WriteLine("DROPSHOCK_SQL is not set.");
    return 2;
}

bool skipSlow = args.Contains("--skip-slow");
bool skipEscalation = args.Contains("--skip-escalation");
int escalationRows = ArgInt("--escalation-rows", 7000);
int concurrency = ArgInt("--concurrency", 8);

int ArgInt(string name, int fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int v) ? v : fallback;
}

var parsed = new SqlConnectionStringBuilder(conn);
Console.WriteLine($"Using  Server={parsed.DataSource}  Database={parsed.InitialCatalog}  User={parsed.UserID}");
Console.WriteLine($"Concurrency={concurrency}  EscalationRows={escalationRows}\n");

var h = new Harness(conn);
var provisioned = new List<string>();

// Preflight, in the same spirit as Dropshock.Backend.Migrate's: fail fast and
// legibly on a bad connection string or an unmigrated database, rather than dying
// halfway through a probe with accounts already provisioned.
try
{
    using var db = h.Open();
    using var cmd = new SqlCommand("SELECT @@VERSION", db) { CommandTimeout = 15 };
    string version = ((string)cmd.ExecuteScalar()!).Split('\n')[0].Trim();
    Console.WriteLine($"Connected: {version}");

    var required = new[] { "Account", "Item", "AppliedOperation", "CustodyRecord" };
    var missing = required.Where(t =>
        h.Scalar<int>("SELECT COUNT(*) FROM sys.tables WHERE name = @t AND SCHEMA_NAME(schema_id) = 'dbo'",
                      c => c.Parameters.AddWithValue("@t", t)) == 0).ToArray();

    if (missing.Length > 0)
    {
        Console.Error.WriteLine($"\nSchema is missing: {string.Join(", ", missing)}");
        Console.Error.WriteLine("Run Dropshock.Backend.Migrate --apply first.");
        return 2;
    }
}
catch (Exception e)
{
    Console.Error.WriteLine($"\nCannot reach the database: {Harness.Describe(e)}");
    if (e is NotSupportedException && e.Message.Contains("Globalization Invariant"))
    {
        Console.Error.WriteLine(
            "\nMicrosoft.Data.SqlClient does not support Globalization Invariant Mode.\n" +
            "If DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 is set (Tools/typecheck.sh sets it),\n" +
            "unset it and install libicu — the probe cannot connect otherwise.");
    }
    return 2;
}

string NewAccount(string prefix)
{
    string id = h.NewAccountId(prefix);
    h.CreateAccount(id);
    provisioned.Add(id);
    return id;
}

try
{
    // =====================================================================
    // 1. Does UPDLOCK/HOLDLOCK actually serialise concurrent writers?
    // =====================================================================
    //
    // This is the open question ARCHITECTURE.md flags. The failure being hunted is
    // a LOST UPDATE, and Save's shape makes it the natural one: it DELETEs every
    // item row for the account and rewrites them from the in-memory snapshot that
    // Find loaded. If two callers can both read before either writes, the second
    // commit erases the first caller's item — and nothing throws, because each
    // operation id is distinct so the AppliedOperation primary key never fires.
    //
    // N callers, N distinct operation ids, N distinct items, one account.
    // Correct outcome: N items and N operations. Anything less is a lost update.
    h.Section($"1. concurrent writers on one account ({concurrency} callers, distinct operation ids)");
    {
        string acct = NewAccount("serialise");
        // Nullable: WriteResult's default is 0 == Applied, so a non-nullable array
        // would silently count a thread that threw as a success.
        var results = new WriteResult?[concurrency];
        var errors = new Exception?[concurrency];
        using var gate = new Barrier(concurrency);

        Harness.RunConcurrently(concurrency, n =>
        {
            var item = new StoredItem(h.InstanceBase + (uint)n, 100, 1);
            try
            {
                gate.SignalAndWait(TimeSpan.FromSeconds(60));   // start together, maximise overlap
                using var store = new SqlAccountStore(conn!);
                results[n] = new StashService(store).CommitExtraction($"ex-{acct}-{n}", acct, new[] { item });
            }
            catch (Exception e) { errors[n] = e; }
        });

        int applied = results.Count(r => r == WriteResult.Applied);
        int items = h.CountItems(acct);
        int ops = h.CountOperations(acct);
        int threw = errors.Count(e => e is not null);
        var distinctErrors = errors.Where(e => e is not null)
                                   .Select(Harness.Describe).Distinct().ToArray();

        string detail =
            $"applied={applied}/{concurrency}  threw={threw}  rows in dbo.Item={items}  rows in dbo.AppliedOperation={ops}" +
            (distinctErrors.Length > 0 ? "\nerrors: " + string.Join("; ", distinctErrors) : "");

        if (items == concurrency && ops == concurrency && threw == 0)
        {
            h.Record("UPDLOCK/HOLDLOCK serialises concurrent writers — no lost update",
                     Verdict.Pass, detail);
        }
        else if (threw == 0 && items < concurrency)
        {
            // The dangerous outcome: silent data loss with every call reporting success.
            h.Record("LOST UPDATE — a committed write was erased by a concurrent one",
                     Verdict.Fail, detail + "\nEvery caller reported success. This is silent gear loss.");
        }
        else
        {
            h.Record("concurrent writers did not all complete cleanly", Verdict.Fail, detail);
        }
    }

    // =====================================================================
    // 2. Two callers, the SAME operation id, at the same time.
    // =====================================================================
    //
    // This is the idempotency contract under contention. StashService checks
    // "already applied?" and writes inside one transaction, so exactly one caller
    // must win. Note the two independent guards described on SqlAccountStore: the
    // lock, and AppliedOperation's primary key behind it. A 2627/2601 here is
    // meaningful — it means the LOCK failed and the primary key caught it. That is
    // the backstop firing, which is a pass for durability and a fail for the lock.
    h.Section("2. two callers, same operation id, simultaneously");
    {
        string acct = NewAccount("idem");
        var item = new StoredItem(h.InstanceBase + 500_000, 101, 1);
        string opId = $"ex-{acct}-same";

        var results = new WriteResult?[2];
        var errors = new Exception?[2];
        using var gate = new Barrier(2);

        Harness.RunConcurrently(2, n =>
        {
            try
            {
                gate.SignalAndWait(TimeSpan.FromSeconds(60));
                using var store = new SqlAccountStore(conn!);
                results[n] = new StashService(store).CommitExtraction(opId, acct, new[] { item });
            }
            catch (Exception e) { errors[n] = e; }
        });

        int applied = results.Count(r => r == WriteResult.Applied);
        int already = results.Count(r => r == WriteResult.AlreadyApplied);
        int items = h.CountItems(acct);
        int ops = h.CountOperations(acct);
        var pkViolation = errors.Select(Harness.AsSql).FirstOrDefault(s => s?.Number is 2627 or 2601);

        string detail =
            $"Applied={applied}  AlreadyApplied={already}  rows in dbo.Item={items}  rows in dbo.AppliedOperation={ops}" +
            (errors.Any(e => e is not null)
                ? "\nerrors: " + string.Join("; ", errors.Where(e => e is not null).Select(Harness.Describe))
                : "");

        if (applied == 1 && already == 1 && items == 1 && ops == 1)
        {
            h.Record("same operation id applied exactly once under contention", Verdict.Pass, detail);
        }
        else if (pkViolation is not null && items == 1 && ops == 1)
        {
            // Durability held, but only because of the backstop.
            h.Record("lock did NOT serialise — AppliedOperation primary key caught the duplicate",
                     Verdict.Fail,
                     detail + "\nThe database stayed correct, but via the PK backstop rather than the lock." +
                              "\nThis is the case that needs a 1205/2627 retry, since the loser's whole operation is lost.");
        }
        else if (items > 1 || ops > 1)
        {
            h.Record("DUPLICATION — one operation id applied twice", Verdict.Fail, detail);
        }
        else
        {
            h.Record("same operation id under contention produced an unexpected outcome", Verdict.Fail, detail);
        }
    }

    // =====================================================================
    // 3. Do reads block writes?
    // =====================================================================
    //
    // StashService.ReadStash and ReadCustody call Find, which unconditionally takes
    // UPDLOCK+HOLDLOCK (SqlAccountStore.cs:40-41), and never call Save — so the
    // transaction is held open until Dispose rolls it back. The lock therefore lives
    // for the lifetime of the store, not for the lifetime of the SELECT.
    //
    // Holding a store open here simulates a slow read (large stash, slow link, or a
    // caller that keeps the store in scope). If the write below waits out the hold,
    // reads block writers.
    //
    // This originally reported OBSERVED: a 5s read blocked a concurrent write for
    // 4.78s. StashService.ReadStash now goes through IAccountStore.FindForRead, which
    // takes no UPDLOCK/HOLDLOCK and rolls back before returning, so this probe should
    // now PASS. It is kept as the regression test for that fix.
    h.Section("3. does a read hold a write lock against concurrent writers?");
    {
        string acct = NewAccount("readlock");
        var hold = TimeSpan.FromSeconds(5);
        using var readerHasLock = new ManualResetEventSlim(false);

        var reader = Task.Run(() =>
        {
            // Exactly what ReadStash does internally, held open for `hold`.
            // With FindForRead this no longer holds a lock; before the fix it did.
            using var store = new SqlAccountStore(conn!);
            _ = new StashService(store).ReadStash(acct);
            readerHasLock.Set();
            Thread.Sleep(hold);
        });

        readerHasLock.Wait(TimeSpan.FromSeconds(30));
        Thread.Sleep(250);  // let the reader settle inside its hold

        var (elapsed, error) = Harness.Time(() =>
        {
            using var store = new SqlAccountStore(conn!);
            new StashService(store).CommitExtraction(
                $"ex-{acct}-blocked", acct, new[] { new StoredItem(h.InstanceBase + 600_000, 102, 1) });
        });

        reader.Wait();

        string detail = $"reader held for {hold.TotalSeconds:F1}s; concurrent write took {elapsed.TotalSeconds:F2}s ({Harness.Describe(error)})";

        if (error is null && elapsed > hold.Subtract(TimeSpan.FromSeconds(1)))
        {
            h.Record("a READ blocks all writes to the account for the reader's lifetime",
                     Verdict.Observed,
                     detail +
                     "\nFind takes UPDLOCK+HOLDLOCK even on the read-only path, and holds it until Dispose." +
                     "\nStashService.ReadStash/ReadCustody never call Save, so this lock buys nothing." +
                     "\nFix: a read-only Find overload with no lock hint, or snapshot isolation for reads.");
        }
        else if (error is null)
        {
            h.Record("reads do not block writers", Verdict.Pass, detail);
        }
        else
        {
            h.Record("concurrent write threw while a read was in progress", Verdict.Fail, detail);
        }
    }

    // =====================================================================
    // 4. What does a blocked caller actually get? (timeout, error -2)
    // =====================================================================
    //
    // SqlAccountStore never sets CommandTimeout, so every command uses the 30s
    // default. On the same-account path the realistic production failure is not 1205
    // but -2 "Execution Timeout Expired", and there is no handling for that either.
    // A retry policy written only for 1205 would not catch this.
    h.Section("4. a blocked caller waits out the default 30s command timeout");
    if (skipSlow)
    {
        h.Record("blocked caller receives error -2 after the default timeout", Verdict.Skipped,
                 "--skip-slow was passed. This probe proved nothing.");
    }
    else
    {
        string acct = NewAccount("timeout");
        var hold = TimeSpan.FromSeconds(40);   // longer than the 30s default
        using var lockHeld = new ManualResetEventSlim(false);

        Console.WriteLine($"           (holding a lock for {hold.TotalSeconds:F0}s — this probe is slow by design)");
        var blocker = h.HoldAccountLock(acct, hold, lockHeld);
        lockHeld.Wait(TimeSpan.FromSeconds(30));
        Thread.Sleep(250);

        var (elapsed, error) = Harness.Time(() =>
        {
            using var store = new SqlAccountStore(conn!);
            new StashService(store).CommitExtraction(
                $"ex-{acct}-timeout", acct, new[] { new StoredItem(h.InstanceBase + 700_000, 103, 1) });
        });

        var sql = Harness.AsSql(error);
        string detail = $"blocked for {elapsed.TotalSeconds:F1}s, then: {Harness.Describe(error)}";

        if (sql?.Number == -2)
        {
            h.Record("a blocked caller gets error -2 (timeout), NOT 1205", Verdict.Observed,
                     detail +
                     "\nNo CommandTimeout is set in SqlAccountStore, so this is the 30s default." +
                     "\nA retry policy scoped to 1205 alone would not catch this case." +
                     "\nDecide: explicit CommandTimeout, and whether -2 is retryable (it is only safe " +
                     "because Save is idempotent — the operation id makes a replay a no-op).");
        }
        else if (error is null)
        {
            h.Record("a blocked caller completed without timing out", Verdict.Observed,
                     detail + "\nThe lock was released before the timeout elapsed.");
        }
        else
        {
            h.Record("a blocked caller failed with an unexpected error", Verdict.Observed, detail);
        }

        blocker.Wait();
    }

    // =====================================================================
    // 5. Can a real 1205 be produced? (lock escalation across accounts)
    // =====================================================================
    //
    // Same-account traffic cannot deadlock: the lock order is identical. A 1205
    // needs lock-order inversion, and Save's delete-and-rewrite of every item row
    // supplies it once a stash is big enough for SQL Server to escalate row locks to
    // a table lock on dbo.Item (~5000 locks). At that point an operation on account A
    // holds the whole table and an operation on B wants it too.
    //
    // NOTE: this drives raw SQL, not StashService, because no service method touches
    // two accounts today. It models Save's ACCESS PATTERN, and it is a forward-looking
    // probe: the rebirth transaction (ARCHITECTURE.md, "The rebirth transaction is
    // the hard part") is the feature that will make this reachable through the
    // service layer.
    h.Section($"5. lock escalation across two accounts ({escalationRows} rows each)");
    if (skipEscalation)
    {
        h.Record("a genuine 1205 deadlock is reachable via lock escalation", Verdict.Skipped,
                 "--skip-escalation was passed. This probe proved nothing.");
    }
    else
    {
        string acctA = NewAccount("escA");
        string acctB = NewAccount("escB");

        Console.WriteLine($"           (seeding {escalationRows * 2} rows — this takes a moment)");
        h.SeedItems(acctA, escalationRows, h.InstanceBase + 1_000_000);
        h.SeedItems(acctB, escalationRows, h.InstanceBase + 1_000_000 + (uint)escalationRows);

        // Touches every item row for one account, taking write locks, exactly as
        // Save's DELETE does — but without destroying the rows, so the probe can
        // repeat. SET Count = Count still acquires the locks.
        static void TouchAll(SqlConnection db, SqlTransaction tx, string accountId)
        {
            using var cmd = new SqlCommand(
                "UPDATE dbo.Item SET Count = Count WHERE AccountId = @a", db, tx) { CommandTimeout = 120 };
            cmd.Parameters.AddWithValue("@a", accountId);
            cmd.ExecuteNonQuery();
        }

        int deadlocks = 0, attempts = 0;
        SqlException? firstDeadlock = null;
        var otherErrors = new List<string>();

        // Several rounds: a deadlock is a race, and one round can simply serialise.
        for (int round = 0; round < 5 && deadlocks == 0; round++)
        {
            attempts++;
            using var gate = new Barrier(2);
            var errs = new Exception?[2];

            Harness.RunConcurrently(2, n =>
            {
                // Opposite lock order is the entire point.
                string first = n == 0 ? acctA : acctB;
                string second = n == 0 ? acctB : acctA;
                bool reachedBarrier = false;
                try
                {
                    using var db = h.Open();
                    using var tx = db.BeginTransaction(System.Data.IsolationLevel.ReadCommitted);
                    TouchAll(db, tx, first);

                    // Bounded wait. TouchAll above can throw (timeout, escalation
                    // contention), and an unbounded barrier would then hang the other
                    // task forever — a probe that hangs reports nothing at all.
                    reachedBarrier = true;
                    gate.SignalAndWait(TimeSpan.FromSeconds(120));   // both hold their first account

                    TouchAll(db, tx, second);  // now cross
                    tx.Commit();
                }
                catch (Exception e)
                {
                    errs[n] = e;
                    // Only release the barrier if this task never signalled it.
                    if (!reachedBarrier)
                    {
                        try { gate.RemoveParticipant(); } catch { /* raced with the other task */ }
                    }
                }
            });

            foreach (var e in errs.Where(e => e is not null))
            {
                var s = Harness.AsSql(e);
                if (s?.Number == 1205) { deadlocks++; firstDeadlock ??= s; }
                else if (s is not null) otherErrors.Add(Harness.Describe(e));
                else otherErrors.Add(Harness.Describe(e));
            }
        }

        string detail = $"{attempts} round(s), {deadlocks} deadlock(s)" +
                        (otherErrors.Count > 0 ? "\nother errors: " + string.Join("; ", otherErrors.Distinct()) : "");

        if (deadlocks > 0)
        {
            h.Record("a genuine 1205 deadlock IS reachable — nothing in the backend handles it",
                     Verdict.Observed,
                     detail +
                     $"\nfirst: \"{firstDeadlock!.Message.Split('\n')[0]}\"" +
                     "\nSqlAccountStore has no catch for 1205, so this surfaces to the caller as an" +
                     "\nunhandled SqlException and the whole operation is lost." +
                     "\nRetry is safe here precisely because StashService is idempotent: replaying the" +
                     "\nsame operation id either completes the write or reports AlreadyApplied.");
        }
        else
        {
            h.Record("no 1205 produced under lock escalation at this row count", Verdict.Observed,
                     detail +
                     $"\nTry a higher --escalation-rows (current {escalationRows}); escalation is ~5000 locks" +
                     "\nper statement and depends on server memory and configuration." +
                     "\nCheck: SELECT * FROM sys.dm_db_index_operational_stats for escalation counts.");
        }
    }
    // =====================================================================
    // 6. Does the retry actually recover a blocked operation?
    // =====================================================================
    //
    // Probes 4 and 5 establish that both failure modes are real and unhandled. This
    // is the other half: proof that SqlStashGateway turns them into a completed
    // operation rather than a lost one.
    //
    // Driven with a timeout rather than a deadlock, because a timeout can be
    // provoked on demand and precisely: hold the account lock, give the gateway a
    // command timeout shorter than the hold, and every attempt fails with -2 until
    // the lock is released. A 1205 takes the identical path — SqlStashGateway's
    // ShouldRetry does not distinguish them — but cannot be scheduled to order.
    //
    // The operation id is fixed across attempts, which is the property that makes
    // the whole thing safe. If it were generated inside the delegate, a retry after
    // a write that actually committed would duplicate the items.
    h.Section("6. does SqlStashGateway recover a blocked operation?");
    {
        string acct = NewAccount("retry");
        var hold = TimeSpan.FromSeconds(12);
        using var lockHeld = new ManualResetEventSlim(false);

        var retries = new List<RetryAttempt>();
        var gateway = new SqlStashGateway(
            conn!,
            new SqlRetryOptions(
                MaxAttempts: 12,
                BaseDelay: TimeSpan.FromMilliseconds(250),
                MaxDelay: TimeSpan.FromSeconds(1),
                CommandTimeout: TimeSpan.FromSeconds(2)),
            onRetry: a => { lock (retries) retries.Add(a); });

        Console.WriteLine($"           (holding a lock for {hold.TotalSeconds:F0}s while the gateway retries)");
        var blocker = h.HoldAccountLock(acct, hold, lockHeld);
        lockHeld.Wait(TimeSpan.FromSeconds(30));
        Thread.Sleep(250);

        WriteResult? outcome = null;
        string opId = $"ex-{acct}-retried";   // fixed across every attempt, deliberately
        var (elapsed, error) = Harness.Time(() =>
        {
            outcome = gateway.Execute(s => s.CommitExtraction(
                opId, acct, new[] { new StoredItem(h.InstanceBase + 800_000, 104, 1) }));
        });

        blocker.Wait();

        int items = h.CountItems(acct);
        int ops = h.CountOperations(acct);
        var numbers = retries.Select(r => r.SqlErrorNumber).Distinct().OrderBy(n => n).ToArray();
        string detail =
            $"outcome={outcome?.ToString() ?? "threw"}  attempts retried={retries.Count}  " +
            $"elapsed={elapsed.TotalSeconds:F1}s  rows in dbo.Item={items}  rows in dbo.AppliedOperation={ops}" +
            (numbers.Length > 0 ? $"\nretried on: {string.Join(", ", numbers)}" : "") +
            (error is not null ? $"\nerror: {Harness.Describe(error)}" : "");

        if (outcome == WriteResult.Applied && retries.Count > 0 && items == 1 && ops == 1)
        {
            h.Record("gateway retried through the block and applied the operation exactly once",
                     Verdict.Pass, detail);
        }
        else if (retries.Count == 0)
        {
            // Nothing was retried, so this run proved nothing about the retry path.
            h.Record("gateway was never forced to retry — this proved nothing",
                     Verdict.Skipped,
                     detail + "\nThe lock was released before the first attempt timed out." +
                              "\nRaise the hold or lower CommandTimeout and re-run.");
        }
        else if (items > 1 || ops > 1)
        {
            h.Record("RETRY DUPLICATED THE WRITE", Verdict.Fail,
                     detail + "\nIdempotency did not hold across a retry. This is the failure mode " +
                              "\nthat makes retrying unsafe; do not ship the gateway until this is understood.");
        }
        else
        {
            h.Record("gateway did not recover the blocked operation", Verdict.Fail, detail);
        }
    }
}
catch (Exception fatal)
{
    // Anything reaching here aborted the run partway. Record it as a failure and
    // fall through to cleanup — the escalation probe can have tens of thousands of
    // rows provisioned, and leaking those would poison the next run.
    h.Record("probe run aborted", Verdict.Aborted,
             Harness.Describe(fatal) + "\nProbes after this point did not run.");
}
finally
{
    Console.WriteLine("\n-- cleanup --");
    foreach (string a in provisioned) h.DeleteAccount(a);
    Console.WriteLine($"  removed {provisioned.Count} probe account(s)");
}

return h.Summarise();
