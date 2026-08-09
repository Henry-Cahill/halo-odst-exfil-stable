using Microsoft.Data.SqlClient;

// Applies Backend/sql/schema.sql to the configured SQL Server.
//
// The connection string comes from the DROPSHOCK_SQL environment variable and is
// never written to disk, never logged, and never echoed. The client half of this
// project must never see a credential — see ARCHITECTURE.md, "Nothing in the
// compound is client-authoritative" — and that rule starts with the tooling.
//
//   dotnet run --project Dropshock.Backend.Migrate            preflight only
//   dotnet run --project Dropshock.Backend.Migrate -- --apply apply the schema

string? conn = Environment.GetEnvironmentVariable("DROPSHOCK_SQL");
if (string.IsNullOrWhiteSpace(conn))
{
    Console.Error.WriteLine("DROPSHOCK_SQL is not set. Expected something like:");
    Console.Error.WriteLine("  Server=192.168.1.212;Database=Dropshock;User Id=dropshock_svc;" +
                            "Password=...;Encrypt=True;TrustServerCertificate=True");
    return 2;
}

bool apply = args.Contains("--apply");

// Echo the configuration being used, minus the password. A stale environment
// variable is invisible otherwise: the same command produces the same failure and
// nothing indicates which credential was actually tried.
try
{
    var parsed = new SqlConnectionStringBuilder(conn);
    Console.WriteLine($"Using  Server={parsed.DataSource}  Database={parsed.InitialCatalog}  " +
                      $"User={(string.IsNullOrEmpty(parsed.UserID) ? "(integrated)" : parsed.UserID)}");
}
catch (Exception e)
{
    Console.Error.WriteLine($"DROPSHOCK_SQL is not a valid connection string: {e.Message}");
    return 2;
}

string schemaPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "sql", "schema.sql");
schemaPath = Path.GetFullPath(schemaPath);

if (!File.Exists(schemaPath))
{
    Console.Error.WriteLine($"Schema not found at {schemaPath}");
    return 2;
}

await using var db = new SqlConnection(conn);
try
{
    await db.OpenAsync();
}
catch (SqlException e)
{
    // Report the failure without the connection string in it.
    Console.Error.WriteLine($"Could not connect: SqlException {e.Number}: {e.Message}");

    // The common causes are distinguishable and worth naming, because the raw
    // message sends people to check the password when the account is often not a
    // SQL principal at all.
    string hint = e.Number switch
    {
        18456 => "Login failed. Either the password is wrong, or this is not a SQL login.\n" +
                 "  An OS/SSH account (e.g. one you 'ssh' in as) is NOT a SQL principal —\n" +
                 "  SQL Server on Linux accepts SQL authentication only. Use 'sa' or a login\n" +
                 "  created with CREATE LOGIN.\n" +
                 "  Note: $env:DROPSHOCK_SQL persists for the whole PowerShell session —\n" +
                 "  check it with  $env:DROPSHOCK_SQL  before assuming it changed.",
        4060 => "The login is valid but cannot open that database. Does 'Dropshock' exist,\n" +
                "  and does this login have a user mapped in it?",
        53 or -1 => "Server not reachable. Check host, port 1433, and firewall.",
        _ => string.Empty,
    };

    if (hint.Length > 0) Console.Error.WriteLine("  " + hint);
    return 2;
}
catch (Exception e)
{
    Console.Error.WriteLine($"Could not connect: {e.GetType().Name}: {e.Message}");
    return 2;
}

Console.WriteLine($"Connected. Server={db.DataSource}  Database={db.Database}  Version={db.ServerVersion}");

// Preflight: refuse to run over an existing schema. A partially-applied script is
// worse than a clean refusal, and re-running CREATE TABLE would fail halfway and
// leave exactly that.
var existing = new List<string>();
await using (var cmd = new SqlCommand(
    "SELECT name FROM sys.tables WHERE schema_id = SCHEMA_ID('dbo') ORDER BY name", db))
await using (var reader = await cmd.ExecuteReaderAsync())
{
    while (await reader.ReadAsync()) existing.Add(reader.GetString(0));
}

Console.WriteLine(existing.Count == 0
    ? "Existing dbo tables: none"
    : $"Existing dbo tables: {string.Join(", ", existing)}");

string[] expected = { "Account", "CustodyRecord", "Item", "AppliedOperation", "ItemTransferLog" };
var clashes = expected.Where(existing.Contains).ToArray();

if (clashes.Length > 0)
{
    Console.Error.WriteLine($"\nRefusing to apply: these tables already exist: {string.Join(", ", clashes)}");
    Console.Error.WriteLine("Drop them deliberately, or point at an empty database.");
    return 1;
}

// Split on batch separators. sqlcmd's GO is not T-SQL and the driver will not
// accept it, so batches must be submitted individually.
string[] batches = File.ReadAllLines(schemaPath)
    .Aggregate(new List<List<string>> { new() }, (acc, line) =>
    {
        if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase)) acc.Add(new List<string>());
        else acc[^1].Add(line);
        return acc;
    })
    .Select(b => string.Join('\n', b).Trim())
    .Where(b => b.Length > 0)
    .ToArray();

Console.WriteLine($"Schema: {schemaPath}");
Console.WriteLine($"Batches: {batches.Length}");

if (!apply)
{
    Console.WriteLine("\nPreflight only. Re-run with --apply to execute.");
    return 0;
}

// One transaction for the whole schema. SQL Server supports transactional DDL, so
// a failure halfway leaves nothing behind rather than a half-built schema.
await using var tx = (SqlTransaction)await db.BeginTransactionAsync();

for (int i = 0; i < batches.Length; i++)
{
    try
    {
        await using var cmd = new SqlCommand(batches[i], db, tx);
        await cmd.ExecuteNonQueryAsync();
        Console.WriteLine($"  batch {i + 1}/{batches.Length} ok");
    }
    catch (SqlException e)
    {
        Console.Error.WriteLine($"\n  batch {i + 1}/{batches.Length} FAILED: {e.Message}");
        Console.Error.WriteLine("  ---");
        Console.Error.WriteLine("  " + string.Join("\n  ", batches[i].Split('\n').Take(12)));
        await tx.RollbackAsync();
        Console.Error.WriteLine("\nRolled back. Nothing was created.");
        return 1;
    }
}

await tx.CommitAsync();
Console.WriteLine("\nSchema applied and committed.");
return 0;
