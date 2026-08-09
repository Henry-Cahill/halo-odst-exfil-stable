using Dropshock.Backend;
using Dropshock.Backend.Sql;
using Microsoft.Data.SqlClient;

// The same rules the in-memory suite proves, re-run against a real SQL Server.
//
// This is the run that turns "these rules are coherent" into "these rules hold on
// SQL Server", which is a materially stronger claim: it exercises the schema's
// constraints, the transaction boundary, and the InstanceId primary key that makes
// duplication unstorable rather than merely detectable.
//
//   $env:DROPSHOCK_SQL = '...'
//   dotnet run --project Dropshock.Backend.SqlTests

string? conn = Environment.GetEnvironmentVariable("DROPSHOCK_SQL");
if (string.IsNullOrWhiteSpace(conn))
{
    Console.Error.WriteLine("DROPSHOCK_SQL is not set.");
    return 2;
}

var parsed = new SqlConnectionStringBuilder(conn);
Console.WriteLine($"Using  Server={parsed.DataSource}  Database={parsed.InitialCatalog}  User={parsed.UserID}");

var failures = new List<string>();
int run = 0;

void Check(string name, Func<bool> condition)
{
    run++;
    bool ok;
    try { ok = condition(); }
    catch (Exception e)
    {
        failures.Add($"{name} (threw {e.GetType().Name})");
        Console.WriteLine($"  FAIL  {name} — threw {e.GetType().Name}: {e.Message}");
        return;
    }
    if (ok) { Console.WriteLine($"  PASS  {name}"); return; }
    failures.Add(name);
    Console.WriteLine($"  FAIL  {name}");
}

// Each test account is unique, so a failed run never poisons the next one and the
// suite can be re-run without a manual cleanup step.
string acct = "test-" + Guid.NewGuid().ToString("N")[..12];
Console.WriteLine($"Test account: {acct}\n");

// A fresh store per operation: Find opens the transaction, Save commits it.
WriteResult Do(Func<StashService, WriteResult> action)
{
    using var store = new SqlAccountStore(conn!);
    return action(new StashService(store));
}

T Read<T>(Func<StashService, T> query)
{
    using var store = new SqlAccountStore(conn!);
    return query(new StashService(store));
}

var rifle = new StoredItem((uint)Random.Shared.Next(1_000_000, 9_000_000), 100, 1);
var pistol = new StoredItem(rifle.InstanceId + 1, 101, 1);
var ammo = new StoredItem(rifle.InstanceId + 2, 200, 30);

// Provision the account and seed the stash directly — this is the only place that
// bypasses StashService, because creating an account is not a game operation.
using (var seed = new SqlAccountStore(conn))
{
    seed.CreateAccount(acct);
    var a = seed.Find(acct)!;
    a.Stash.Add(rifle);
    a.Stash.Add(pistol);
    seed.Save(a);
}

Console.WriteLine("-- persistence --");
Check("seeded stash is readable from a new connection", () => Read(s => s.ReadStash(acct)).Count == 2);

Console.WriteLine("\n-- checkout --");
Check("checkout removes the named item", () =>
    Do(s => s.CheckoutLoadout("co-" + acct, acct, new[] { rifle.InstanceId })) == WriteResult.Applied
    && Read(s => s.ReadStash(acct)).Count == 1);

Check("checkout is idempotent across connections", () =>
    Do(s => s.CheckoutLoadout("co-" + acct, acct, new[] { rifle.InstanceId })) == WriteResult.AlreadyApplied
    && Read(s => s.ReadStash(acct)).Count == 1);

Console.WriteLine("\n-- extraction --");
Check("extraction returns surviving gear", () =>
    Do(s => s.CommitExtraction("ex-" + acct, acct, new[] { rifle, ammo })) == WriteResult.Applied
    && Read(s => s.ReadStash(acct)).Count == 3);

Check("extraction is idempotent — a retry does not duplicate", () =>
    Do(s => s.CommitExtraction("ex-" + acct, acct, new[] { rifle, ammo })) == WriteResult.AlreadyApplied
    && Read(s => s.ReadStash(acct)).Count == 3);

Console.WriteLine("\n-- custody: one slot --");

// Confiscated gear is carried IN A RAID, so it is not persisted — it was checked
// out at raid start. Using a stash item here was the original mistake: it asked the
// database to store one instance in two places, and the primary key refused.
var raidLoot = new StoredItem(rifle.InstanceId + 100, 300, 1);
var raidLoot2 = new StoredItem(rifle.InstanceId + 101, 301, 1);

Check("confiscating an item already in the stash is refused", () =>
    Do(s => s.ConfiscateToCustody("bad-" + acct, acct, new[] { rifle })) == WriteResult.ItemAlreadyPersisted);

Check("confiscation fills the slot", () =>
    Do(s => s.ConfiscateToCustody("cu-" + acct, acct, new[] { raidLoot })) == WriteResult.Applied);

Check("a second abduction is refused while custody is occupied", () =>
    Do(s => s.ConfiscateToCustody("cu2-" + acct, acct, new[] { raidLoot2 })) == WriteResult.CustodyOccupied);

Check("custody survives a reconnect", () =>
    Read(s => s.ReadCustody(acct))?.CustodyId == "cu-" + acct);

Console.WriteLine("\n-- custody: escape --");
Check("escaping returns custody to the stash", () =>
    Do(s => s.RecoverCustody("es-" + acct, acct)) == WriteResult.Applied
    && Read(s => s.ReadCustody(acct)) is null
    && Read(s => s.ReadStash(acct)).Any(i => i.InstanceId == raidLoot.InstanceId));

Check("recovery is idempotent", () =>
    Do(s => s.RecoverCustody("es-" + acct, acct)) == WriteResult.AlreadyApplied);

Console.WriteLine("\n-- schema enforces uniqueness --");
Check("the database refuses two rows with one InstanceId", () =>
{
    // Bypasses the service deliberately: this proves the PRIMARY KEY is doing the
    // work, not just the C# duplication guard above it.
    try
    {
        using var db = new SqlConnection(conn);
        db.Open();
        using var cmd = new SqlCommand(
            "INSERT INTO dbo.Item (InstanceId, AccountId, ItemId, Count, Location) " +
            "VALUES (@i, @a, 1, 1, 'Stash')", db);
        cmd.Parameters.AddWithValue("@i", (long)rifle.InstanceId);
        cmd.Parameters.AddWithValue("@a", acct);
        cmd.ExecuteNonQuery();
        return false; // should not reach here
    }
    catch (SqlException e) when (e.Number is 2627 or 2601)
    {
        return true; // PK / unique violation, as intended
    }
});

Console.WriteLine($"\n{run - failures.Count}/{run} passed.");
if (failures.Count > 0)
{
    Console.WriteLine("FAILED:\n  " + string.Join("\n  ", failures));
    return 1;
}
Console.WriteLine("All backend rules hold against SQL Server.");
return 0;
