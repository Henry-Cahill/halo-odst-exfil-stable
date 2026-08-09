using Dropshock.Backend;

// A plain console runner rather than xunit: this project has zero external
// dependencies, so it builds and runs anywhere the SDK exists, with no restore and
// no network. The rules under test are small and the assertions are explicit.

var failures = new List<string>();
int run = 0;

void Check(string name, Func<bool> condition)
{
    run++;
    bool ok;
    try
    {
        ok = condition();
    }
    catch (Exception e)
    {
        // An unexpected throw is a failure, not a crash. Letting it escape would
        // kill the run before the summary printed, so a caller grepping the output
        // would see neither PASS nor FAIL and could read that as success.
        failures.Add($"{name} (threw {e.GetType().Name})");
        Console.WriteLine($"  FAIL  {name} — threw {e.GetType().Name}: {e.Message}");
        return;
    }

    if (ok) { Console.WriteLine($"  PASS  {name}"); return; }
    failures.Add(name);
    Console.WriteLine($"  FAIL  {name}");
}

(StashService svc, InMemoryAccountStore store) NewFixture(params StoredItem[] stash)
{
    var store = new InMemoryAccountStore();
    var account = store.CreateAccount("acct-1");
    account.Stash.AddRange(stash);
    return (new StashService(store), store);
}

var rifle = new StoredItem(1, 100, 1);
var pistol = new StoredItem(2, 101, 1);
var ammo = new StoredItem(3, 200, 30);

Console.WriteLine("\n-- checkout --");
{
    var (svc, _) = NewFixture(rifle, pistol);

    Check("checkout removes the named items", () =>
        svc.CheckoutLoadout("raid-1", "acct-1", new uint[] { 1 }) == WriteResult.Applied
        && svc.ReadStash("acct-1").Count == 1);

    Check("checkout is idempotent — replay is a no-op", () =>
        svc.CheckoutLoadout("raid-1", "acct-1", new uint[] { 1 }) == WriteResult.AlreadyApplied
        && svc.ReadStash("acct-1").Count == 1);

    Check("checkout of an item not in the stash is refused", () =>
        svc.CheckoutLoadout("raid-2", "acct-1", new uint[] { 999 }) == WriteResult.ItemNotInStash);

    Check("a refused checkout mutates nothing", () =>
        svc.ReadStash("acct-1").Count == 1);
}

Console.WriteLine("\n-- partial validation --");
{
    var (svc, _) = NewFixture(rifle, pistol);

    // One valid id and one bogus one. The valid item must NOT be removed.
    var result = svc.CheckoutLoadout("raid-1", "acct-1", new uint[] { 1, 999 });

    Check("a checkout naming any missing item removes nothing", () =>
        result == WriteResult.ItemNotInStash && svc.ReadStash("acct-1").Count == 2);
}

Console.WriteLine("\n-- extraction --");
{
    var (svc, _) = NewFixture(rifle);
    svc.CheckoutLoadout("raid-1", "acct-1", new uint[] { 1 });

    Check("extraction returns surviving gear", () =>
        svc.CommitExtraction("extract-1", "acct-1", new[] { rifle, ammo }) == WriteResult.Applied
        && svc.ReadStash("acct-1").Count == 2);

    Check("extraction is idempotent — a retry does not duplicate", () =>
        svc.CommitExtraction("extract-1", "acct-1", new[] { rifle, ammo }) == WriteResult.AlreadyApplied
        && svc.ReadStash("acct-1").Count == 2);
}

Console.WriteLine("\n-- death writes nothing --");
{
    var (svc, _) = NewFixture(rifle, pistol);
    svc.CheckoutLoadout("raid-1", "acct-1", new uint[] { 1, 2 });

    // Death is modelled by the absence of a CommitExtraction call.
    Check("dying loses the checked-out loadout and nothing else", () =>
        svc.ReadStash("acct-1").Count == 0);
}

Console.WriteLine("\n-- duplication is detected, not absorbed --");
{
    var (svc, _) = NewFixture(rifle);

    // The rifle is still in the stash; an extraction claiming the same instance id
    // would mean the item exists twice.
    bool threw = false;
    try { svc.CommitExtraction("extract-1", "acct-1", new[] { rifle }); }
    catch (InvalidOperationException) { threw = true; }

    Check("an extraction re-introducing a live InstanceId throws", () =>
        threw);
    Check("the refused extraction left the stash untouched", () =>
        svc.ReadStash("acct-1").Count == 1);
}

Console.WriteLine("\n-- custody: one slot --");
{
    var (svc, _) = NewFixture();

    Check("confiscation fills the slot", () =>
        svc.ConfiscateToCustody("cust-1", "acct-1", new[] { rifle }) == WriteResult.Applied
        && svc.ReadCustody("acct-1")?.Items.Count == 1);

    Check("a second abduction is refused while custody is occupied", () =>
        svc.ConfiscateToCustody("cust-2", "acct-1", new[] { pistol }) == WriteResult.CustodyOccupied);

    Check("the refused abduction did not disturb the held gear", () =>
        svc.ReadCustody("acct-1")?.CustodyId == "cust-1"
        && svc.ReadCustody("acct-1")?.Items.Count == 1);

    Check("confiscation is idempotent", () =>
        svc.ConfiscateToCustody("cust-1", "acct-1", new[] { rifle }) == WriteResult.AlreadyApplied);
}

Console.WriteLine("\n-- custody: escape and forfeit --");
{
    var (svc, _) = NewFixture();
    svc.ConfiscateToCustody("cust-1", "acct-1", new[] { rifle, pistol });

    Check("escaping returns custody to the stash", () =>
        svc.RecoverCustody("esc-1", "acct-1") == WriteResult.Applied
        && svc.ReadStash("acct-1").Count == 2
        && svc.ReadCustody("acct-1") is null);

    Check("recovery is idempotent — a retry does not duplicate", () =>
        svc.RecoverCustody("esc-1", "acct-1") == WriteResult.AlreadyApplied
        && svc.ReadStash("acct-1").Count == 2);

    Check("a freed slot allows a later abduction", () =>
        svc.ConfiscateToCustody("cust-2", "acct-1", new[] { ammo }) == WriteResult.Applied);

    Check("forfeiting destroys custody", () =>
        svc.ForfeitCustody("forfeit-1", "acct-1") == WriteResult.Applied
        && svc.ReadCustody("acct-1") is null);

    Check("forfeiting an empty slot is refused", () =>
        svc.ForfeitCustody("forfeit-2", "acct-1") == WriteResult.CustodyEmpty);
}

Console.WriteLine("\n-- one instance cannot be in two places --");
{
    // The case the in-memory suite originally missed: confiscating an item that is
    // still in the stash. It passed here and failed on SQL Server's primary key,
    // which is how the gap was found. Both stores now refuse it.
    var (svc, _) = NewFixture(rifle);

    Check("confiscating an item already in the stash is refused", () =>
        svc.ConfiscateToCustody("cust-1", "acct-1", new[] { rifle }) == WriteResult.ItemAlreadyPersisted);

    Check("the refused confiscation left the slot empty", () =>
        svc.ReadCustody("acct-1") is null && svc.ReadStash("acct-1").Count == 1);

    // And the reverse direction: extracting an item that is sitting in custody.
    var (svc2, _) = NewFixture();
    svc2.ConfiscateToCustody("cust-2", "acct-1", new[] { pistol });

    bool threw = false;
    try { svc2.CommitExtraction("ex-1", "acct-1", new[] { pistol }); }
    catch (InvalidOperationException) { threw = true; }

    Check("extracting an item held in custody throws", () => threw);
}

Console.WriteLine("\n-- unknown account --");
{
    var (svc, _) = NewFixture();
    Check("writes against an unknown account are refused", () =>
        svc.CommitExtraction("x", "nobody", new[] { rifle }) == WriteResult.UnknownAccount);
}

// The SQL store's Find takes UPDLOCK+HOLDLOCK and holds it until dispose, so a read
// that went through Find blocked every write to that account for the read's whole
// duration — measured at 4.78s against a 5s read in Dropshock.Backend.ConcurrencyProbe,
// probe 3. The fix is FindForRead, which takes no lock hint.
//
// That fix lives in the SQL store, but the routing decision lives in StashService,
// and nothing here would notice if a later edit quietly pointed ReadStash back at
// Find. This asserts the routing, which is the part that can regress silently.
Console.WriteLine("\n-- reads do not take the write path --");
{
    var spy = new RoutingSpyStore();
    spy.CreateAccount("acct-1");
    var svc = new StashService(spy);

    _ = svc.ReadStash("acct-1");
    _ = svc.ReadCustody("acct-1");

    Check("ReadStash and ReadCustody go through FindForRead, never Find", () =>
        spy.FindForReadCalls == 2 && spy.FindCalls == 0);

    spy.Reset();
    svc.CommitExtraction("op-1", "acct-1", new[] { rifle });

    Check("a write still goes through the locking Find", () =>
        spy.FindCalls == 1 && spy.FindForReadCalls == 0);
}

Console.WriteLine($"\n{run - failures.Count}/{run} passed.");
if (failures.Count > 0)
{
    Console.WriteLine("FAILED:\n  " + string.Join("\n  ", failures));
    return 1;
}
Console.WriteLine("All backend rules hold.");
return 0;

/// <summary>Counts which load path StashService chose. Behaviour is otherwise the
/// in-memory store's.</summary>
sealed class RoutingSpyStore : IAccountStore
{
    private readonly InMemoryAccountStore _inner = new();

    public int FindCalls { get; private set; }
    public int FindForReadCalls { get; private set; }

    public void Reset() { FindCalls = 0; FindForReadCalls = 0; }

    public Account CreateAccount(string accountId) => _inner.CreateAccount(accountId);

    public Account? Find(string accountId)
    {
        FindCalls++;
        return _inner.Find(accountId);
    }

    public Account? FindForRead(string accountId)
    {
        FindForReadCalls++;
        return _inner.FindForRead(accountId);
    }

    public void Save(Account account) => _inner.Save(account);
}
