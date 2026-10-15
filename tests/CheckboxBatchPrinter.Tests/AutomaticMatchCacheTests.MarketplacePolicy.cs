using System.IO;
using System.Reflection;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static partial class AutomaticMatchCacheTests
{
    private static MarketplaceOrder RozetkaOrder(int n = 2) => Order(n) with
        { Key = new(MarketplaceKind.Rozetka, "f7000000000000000000000000000002", n.ToString()) };

    // Serialize formerly valid suggestions with genuine current document fingerprints.
    // Calling the current matcher to seed them would no longer exercise migration.
    private static CachedReceiptMatch LegacySuggestion(ReceiptRecord receipt, MarketplaceOrder order) =>
        (CachedReceiptMatch)typeof(CachedReceiptOrderMatchingService).GetMethod("Pack", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [new ReceiptOrderMatch(ReceiptLinkState.Suggested, order, "Legacy probable link", [order])
                { Basis = AutomaticLinkBasis.AmountAndProducts, GroupOrders = [order], CompetingReceiptIds = [receipt.Id], CompetingOrderCount = 1 }, receipt])!;

    private static async Task MarketplacePolicyRestart()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "marketplace-policy.dpapi"); var clock = new Clock();
            var service = new CachedReceiptOrderMatchingService(clock); var cache = Cache();
            var promReceipt = Receipt(); var rozetkaReceipt = Receipt(2); var prom = Order(); var rozetka = RozetkaOrder();
            service.RememberDetails(cache, Account, promReceipt, Detail(promReceipt));
            service.RememberDetails(cache, Account, rozetkaReceipt, Detail(rozetkaReceipt));
            service.MatchAll(cache, [promReceipt], Account, [prom], [], true);
            cache.Links[rozetkaReceipt.Id] = new(LegacySuggestion(rozetkaReceipt, rozetka), Time.ToUniversalTime());
            // Write an actual pre-policy protected payload, bypassing today's save-time pruning.
            await new DpapiCredentialStore(path).SavePasswordAsync(JsonSerializer.Serialize(new[] { cache }));
            var loaded = await new DpapiAutomaticMatchCacheStore(path, clock).LoadAsync(Account, 30);
            Eq(1, loaded.Links.Count); Yes(loaded.Links.ContainsKey(promReceipt.Id)); Eq(2, loaded.Details.Count);
            var result = service.MatchAll(loaded, [promReceipt, rozetkaReceipt], Account, [prom, rozetka], [], false);
            Eq(ReceiptLinkState.Suggested, result.Matches[promReceipt.Id].State);
            Yes(result.Matches[rozetkaReceipt.Id].Order is null);
            // Saving an in-memory legacy entry must also remove only that entry.
            loaded.Links[rozetkaReceipt.Id] = new(LegacySuggestion(rozetkaReceipt, rozetka), Time.ToUniversalTime());
            await new DpapiAutomaticMatchCacheStore(path, clock).SaveAsync(Account, loaded, 30);
            var saved = await new DpapiAutomaticMatchCacheStore(path, clock).LoadAsync(Account, 30);
            Eq(1, saved.Links.Count); Eq(2, saved.Details.Count);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void MarketplacePolicyGraphs()
    {
        var service = new CachedReceiptOrderMatchingService(new Clock()); var r = Receipt(2); var rozetka = RozetkaOrder();
        var forbidden = LegacySuggestion(r, rozetka);
        foreach (var algorithm in new[] { "same-kyiv-day-active-unlinked-unit-symbols-v15", CachedReceiptOrderMatchingService.AlgorithmVersion })
        {
            var cache = Cache();
            cache.Links[r.Id] = new(forbidden, Time.ToUniversalTime());
            cache.Snapshots.Add(new(algorithm, "synthetic-legacy-fingerprint", Time.ToUniversalTime(),
                new Dictionary<string, CachedReceiptMatch> { [r.Id] = forbidden }));
            Yes(!service.HasVerifiedSnapshot(cache, Account, new(), []));
            Eq(0, cache.Links.Count); Eq(0, cache.Snapshots.Count);
            Eq(0, service.RestoreKnownSuggestions(cache, [r], Account, [rozetka], []).Count);
            Eq(0, service.LinkedOrderKeys(cache, Account, [], [], []).Count);
            Yes(!service.TryRestore(cache, [r], Account, [rozetka], [], out _));
        }
        var imported = Cache(); var prom = Order(); var promReceipt = Receipt();
        imported.Snapshots.Add(new("same-kyiv-day-clean-names-payment-v8", "synthetic-import", Time.ToUniversalTime(),
            new Dictionary<string, CachedReceiptMatch> { [r.Id] = forbidden, [promReceipt.Id] = LegacySuggestion(promReceipt, prom) }));
        var keys = service.LinkedOrderKeys(imported, Account, [], [rozetka, prom], []);
        Yes(keys.SetEquals([prom.Key])); Eq(1, imported.Links.Count); Eq(1, imported.LinkSchema);
        // Removed-connection projection cannot resurrect a legacy Rozetka guess.
        var aliased = rozetka with { Key = rozetka.Key with { ConnectionId = "f7000000000000000000000000000003" } };
        imported.Links[r.Id] = new(forbidden, Time.ToUniversalTime());
        service.NormalizeConnectionAliases(imported, Account, [rozetka], [aliased], key => key == rozetka.Key ? aliased.Key : key);
        Eq(1, imported.Links.Count); Yes(!imported.Links.ContainsKey(r.Id));
    }

    private static void MarketplacePolicyAuthoritative()
    {
        var service = new CachedReceiptOrderMatchingService(new Clock()); var r = Receipt(2); var order = RozetkaOrder();
        foreach (var manual in new[] { false, true })
        {
            var cache = Cache(); var current = manual ? order : order with { ReceiptIds = [r.Id] };
            ReceiptOrderDecision[] decisions = manual
                ? [new() { AccountContext = Account, ReceiptId = r.Id, ConfirmedOrder = order.Key, UpdatedAtUtc = Time }] : [];
            var result = service.MatchAll(cache, [r], Account, [current], decisions, true);
            Eq(manual ? ReceiptLinkState.Manual : ReceiptLinkState.Exact, result.Matches[r.Id].State);
            Eq(0, cache.Links.Count);
            var replay = service.MatchAll(cache, [r], Account, [current], decisions, true);
            Yes(replay.CacheHit); Eq(result.Matches[r.Id].State, replay.Matches[r.Id].State);
            Eq(order.Key, replay.Matches[r.Id].Order!.Key);
            // Invalidate all prior graphs, including an old Exact conclusion made under weaker fiscal policy.
            cache.Snapshots = cache.Snapshots.Select(s => s with
                { AlgorithmVersion = "same-kyiv-day-active-unlinked-unit-symbols-v15" }).ToList();
            Yes(!service.TryRestore(cache, [r], Account, [current], decisions, out _));
        }
    }
}
