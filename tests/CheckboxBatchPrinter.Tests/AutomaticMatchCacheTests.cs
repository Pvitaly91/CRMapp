using System.IO;
using System.Text;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class AutomaticMatchCacheTests
{
    private const string Account = "synthetic-checkbox-account";
    private static readonly DateTimeOffset Time = new(2026, 9, 27, 10, 0, 0, TimeSpan.FromHours(3));
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("automatic cache: DPAPI restart restores details and complete graph without manual promotion", Restart),
        ("automatic cache: new order or receipt competitors invalidate complete cached graph", Run(ChangedCompetitors)),
        ("automatic cache: manual suppressions and exact evidence invalidate probable results", Run(DecisionsAndEvidence)),
        ("automatic cache: verified fiscal evidence, details and scope are fingerprinted", Run(FingerprintInputs)),
        ("automatic cache: retention, algorithm version and account isolation are bounded", RetentionAndAccounts),
        ("automatic cache: corrupt optional file rebuilds independently of durable decisions", Corruption)
    ];

    private static Func<Task> Run(Action action) => () => { action(); return Task.CompletedTask; };
    private static ReceiptRecord Receipt(int n = 1, long amount = 29000) => new()
    {
        Id = $"e7000000-0000-0000-0000-{n:D12}", Type = "SELL", Status = "DONE", FiscalDate = Time,
        TotalSumMinor = amount, FiscalCode = $"SYNTHETIC-{n}"
    };
    private static MarketplaceOrder Order(int n = 1) => new()
    {
        Key = new(MarketplaceKind.Prom, "f7000000000000000000000000000001", n.ToString()), Total = 290m,
        Currency = "UAH", CreatedAt = Time.AddHours(-1), Items = [new("Синтетичний кабель", "", 1, 290m, 290m)],
        ItemListComplete = true, ItemsComplete = true, RetrievedAtUtc = Time
    };
    private static ReceiptDetails Detail(ReceiptRecord receipt) => new(receipt.Id, [new("Синтетичний кабель", "", 1, 290m, 290m)])
        { ItemListComplete = true };
    private static AutomaticMatchCache Cache() => new() { AccountContext = Account };
    private static void Eq<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
    private static void Yes(bool value) { if (!value) throw new Exception("Automatic cache assertion failed."); }
    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "checkbox-auto-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Time;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }

    private static async Task Restart()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "automatic-matches.dpapi"); var clock = new Clock();
            var service = new CachedReceiptOrderMatchingService(clock); var cache = Cache(); var r = Receipt(); var o = Order();
            var d = Detail(r); var details = new Dictionary<string, ReceiptDetails> { [r.Id] = d };
            service.RememberDetails(cache, Account, r, d);
            var first = service.MatchAll(cache, [r], Account, [o], [], true, details);
            Yes(!first.CacheHit); Eq(ReceiptLinkState.Suggested, first.Matches[r.Id].State);
            await new DpapiAutomaticMatchCacheStore(path, clock).SaveAsync(Account, cache, 30);
            Yes(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)).Contains("Синтетичний кабель"));
            var restarted = await new DpapiAutomaticMatchCacheStore(path, clock).LoadAsync(Account, 30);
            var restartedService = new CachedReceiptOrderMatchingService(clock);
            Yes(restartedService.TryGetDetails(restarted, Account, r, out var restoredDetails));
            Eq(d.Items[0], restoredDetails.Items[0]);
            var refresh = o with { RetrievedAtUtc = Time.AddMinutes(10), Buyer = new("NEW SYNTHETIC BUYER") };
            var result = restartedService.MatchAll(restarted, [r], Account, [refresh], [], true, details);
            Yes(result.CacheHit); Eq(ReceiptLinkState.Suggested, result.Matches[r.Id].State);
            Eq(AutomaticLinkBasis.AmountAndProducts, result.Matches[r.Id].Basis);
            Yes(ReferenceEquals(refresh, result.Matches[r.Id].Order));
            Yes(restartedService.TryRestore(restarted, [r], Account, [refresh], [], out _, details));
            var incomplete = restartedService.MatchAll(restarted, [r], Account, [refresh], [], false, details);
            Yes(!incomplete.CacheHit); Eq(ReceiptLinkState.Incomplete, incomplete.Matches[r.Id].State);
            var clear = JsonSerializer.Serialize(restarted);
            Yes(!clear.Contains("NEW SYNTHETIC BUYER") && !clear.Contains("PaymentStatus"));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void ChangedCompetitors()
    {
        var service = new CachedReceiptOrderMatchingService(new Clock()); var cache = Cache(); var r = Receipt(); var o = Order();
        Eq(ReceiptLinkState.Suggested, service.MatchAll(cache, [r], Account, [o], [], true).Matches[r.Id].State);
        var duplicate = Order(2);
        Yes(!service.TryRestore(cache, [r], Account, [o, duplicate], [], out _));
        var manyOrders = service.MatchAll(cache, [r], Account, [o, duplicate], [], true);
        Yes(!manyOrders.CacheHit); Yes(manyOrders.Matches[r.Id].Ambiguous); Eq(2, manyOrders.Matches[r.Id].CompetingOrderCount);
        var reordered = service.MatchAll(cache, [r], Account, [duplicate, o], [], true);
        Yes(reordered.CacheHit); Yes(reordered.Matches[r.Id].Ambiguous);
        var manyReceipts = service.MatchAll(cache, [r, Receipt(2)], Account, [o], [], true);
        Yes(!manyReceipts.CacheHit); Yes(manyReceipts.Matches.Values.All(m => m.State != ReceiptLinkState.Suggested));
        Yes(service.MatchAll(cache, [Receipt(2), r], Account, [o], [], true).CacheHit);
    }

    private static void DecisionsAndEvidence()
    {
        var service = new CachedReceiptOrderMatchingService(new Clock()); var cache = Cache(); var r = Receipt(); var o = Order();
        service.MatchAll(cache, [r], Account, [o], [], true);
        var suppression = new ReceiptOrderDecision { AccountContext = Account, ReceiptId = r.Id, SuppressAutomatic = true, UpdatedAtUtc = Time };
        var suppressed = service.MatchAll(cache, [r], Account, [o], [suppression], true);
        Yes(!suppressed.CacheHit); Yes(suppressed.Matches[r.Id].State != ReceiptLinkState.Suggested);
        var manual = new ReceiptOrderDecision { AccountContext = Account, ReceiptId = r.Id, ConfirmedOrder = o.Key, UpdatedAtUtc = Time };
        var manualResult = service.MatchAll(cache, [r], Account, [o], [manual], true);
        Yes(!manualResult.CacheHit); Eq(ReceiptLinkState.Manual, manualResult.Matches[r.Id].State);
        var exact = service.MatchAll(cache, [r], Account, [o with { ReceiptIds = [r.Id] }], [], true);
        Yes(!exact.CacheHit); Eq(ReceiptLinkState.Exact, exact.Matches[r.Id].State);
        var rejected = new ReceiptOrderDecision { AccountContext = Account, ReceiptId = r.Id, RejectedOrders = [o.Key], UpdatedAtUtc = Time };
        Yes(service.MatchAll(cache, [r], Account, [o], [rejected], true).Matches[r.Id].Order is null);
    }

    private static void FingerprintInputs()
    {
        var service = new CachedReceiptOrderMatchingService(new Clock()); var cache = Cache(); var r = Receipt(); var o = Order();
        var scope = new AmountMatchScope(30, new(Time.AddDays(-1), Time.AddDays(1)), "first presentation");
        service.MatchAll(cache, [r], Account, [o], [], true, scope: scope);
        Yes(service.MatchAll(cache, [r], Account, [o], [], true, scope: scope with { Description = "new presentation" }).CacheHit);
        Yes(!service.MatchAll(cache, [r], Account, [o], [], true, scope: scope with { HistoryDays = 31 }).CacheHit);
        Yes(!service.MatchAll(cache, [r], Account, [o], [], true, scope: scope with { OrderRange = null }).CacheHit);
        var detail = Detail(r); service.RememberDetails(cache, Account, r, detail);
        Yes(!service.TryGetDetails(cache, Account, Receipt(amount: 29100), out _));
        Yes(!service.MatchAll(cache, [r], Account, [o], [], true, new Dictionary<string, ReceiptDetails> { [r.Id] = detail }, scope).CacheHit);
        var reference = new FiscalDocumentReference(FiscalDocumentKeyKind.FiscalCode, r.FiscalCode, "synthetic-field", o.Key, "Checkbox", "doc")
            { VerifiedReceiptId = r.Id, VerifiedAccountContext = Account };
        var verified = o with { FiscalReferences = [reference] };
        Eq(ReceiptLinkState.Exact, service.MatchAll(cache, [r], Account, [verified], [], true).Matches[r.Id].State);
        var unverified = JsonSerializer.Deserialize<MarketplaceOrder>(JsonSerializer.Serialize(verified))!;
        Yes(!service.TryRestore(cache, [r], Account, [unverified], [], out _));
        var withoutEvidence = service.MatchAll(cache, [r], Account, [unverified], [], true);
        Yes(!withoutEvidence.CacheHit); Yes(withoutEvidence.Matches[r.Id].State != ReceiptLinkState.Exact);
        Yes(!service.TryRestore(cache, [r], Account, [o], [], out _, loading: new HashSet<string> { r.Id }));
    }

    private static async Task RetentionAndAccounts()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "cache.dpapi"); var clock = new Clock();
            var service = new CachedReceiptOrderMatchingService(clock); var cache = Cache(); var r = Receipt(); var o = Order();
            service.RememberDetails(cache, Account, r, Detail(r)); service.MatchAll(cache, [r], Account, [o], [], true);
            var store = new DpapiAutomaticMatchCacheStore(path, clock); await store.SaveAsync(Account, cache, 1);
            var other = await store.LoadAsync("other-account", 1); Eq(0, other.Details.Count); Eq(0, other.Snapshots.Count);
            Yes(!service.TryGetDetails(cache, "other-account", r, out _));
            Yes(!service.TryRestore(cache, [r], "other-account", [o], [], out _));
            service.RememberDetails(other, "other-account", r, Detail(r)); await store.SaveAsync("other-account", other, 1);
            Eq(1, (await store.LoadAsync(Account, 1)).Details.Count);
            clock.Now = Time.AddHours(20);
            var same = await store.LoadAsync(Account, 1);
            Yes(service.MatchAll(same, [r], Account, [o], [], true).CacheHit);
            service.RememberDetails(same, Account, r, Detail(r)); await store.SaveAsync(Account, same, 1);
            clock.Now = Time.AddDays(2);
            var expired = await store.LoadAsync(Account, 1); Eq(0, expired.Details.Count); Eq(0, expired.Snapshots.Count);
            clock.Now = Time;
            cache.Snapshots = cache.Snapshots.Select(s => s with { AlgorithmVersion = "old" }).ToList();
            Yes(!service.TryRestore(cache, [r], Account, [o], [], out _));
            for (var n = 2; n < 20; n++) service.MatchAll(cache, [Receipt(n)], Account, [o], [], true);
            Yes(cache.Snapshots.Count <= CachedReceiptOrderMatchingService.MaximumSnapshots);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task Corruption()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "cache.dpapi"); var clock = new Clock();
            await File.WriteAllTextAsync(path, "not-a-dpapi-file");
            var store = new DpapiAutomaticMatchCacheStore(path, clock);
            Eq(0, (await store.LoadAsync(Account, 30)).Snapshots.Count);
            await new DpapiCredentialStore(path).SavePasswordAsync("{not valid json");
            Eq(0, (await store.LoadAsync(Account, 30)).Details.Count);
            await new DpapiCredentialStore(path).SavePasswordAsync("[null]");
            Eq(0, (await store.LoadAsync(Account, 30)).Details.Count);
            var cache = await store.LoadAsync(Account, 30); var r = Receipt(); var o = Order();
            var manual = new ReceiptOrderDecision { AccountContext = Account, ReceiptId = r.Id, ConfirmedOrder = o.Key, UpdatedAtUtc = Time };
            var service = new CachedReceiptOrderMatchingService(clock);
            Eq(ReceiptLinkState.Manual, service.MatchAll(cache, [r], Account, [o], [manual], true).Matches[r.Id].State);
            await store.SaveAsync(Account, cache, 30);
            Yes(service.MatchAll(await store.LoadAsync(Account, 30), [r], Account, [o], [manual], true).CacheHit);
        }
        finally { Directory.Delete(directory, true); }
    }
}
