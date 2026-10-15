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

    // Serialize historical suggestions with genuine current document fingerprints,
    // including suggestions that today's fiscal-evidence policy must reject.
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
            var first = service.MatchAll(cache, [rozetkaReceipt], Account, [rozetka], [], true,
                new Dictionary<string, ReceiptDetails> { [rozetkaReceipt.Id] = Detail(rozetkaReceipt) });
            Eq(ReceiptLinkState.Suggested, first.Matches[rozetkaReceipt.Id].State);
            Eq(AutomaticLinkBasis.AmountAndProducts, first.Matches[rozetkaReceipt.Id].Basis);
            await new DpapiAutomaticMatchCacheStore(path, clock).SaveAsync(Account, cache, 30);
            var loaded = await new DpapiAutomaticMatchCacheStore(path, clock).LoadAsync(Account, 30);
            Eq(2, loaded.Links.Count); Eq(2, loaded.Details.Count);
            var result = service.MatchAll(loaded, [promReceipt, rozetkaReceipt], Account, [prom, rozetka], [], false);
            Eq(ReceiptLinkState.Suggested, result.Matches[promReceipt.Id].State);
            Eq(ReceiptLinkState.Suggested, result.Matches[rozetkaReceipt.Id].State);
            Eq(rozetka.Key, result.Matches[rozetkaReceipt.Id].Order!.Key);
            var refreshed = rozetka with { RetrievedAtUtc = Time.AddMinutes(10), Status = "В дорозі" };
            var complete = service.MatchAll(loaded, [promReceipt, rozetkaReceipt], Account, [prom, refreshed], [], true);
            Eq(ReceiptLinkState.Suggested, complete.Matches[rozetkaReceipt.Id].State);
            Yes(ReferenceEquals(refreshed, complete.Matches[rozetkaReceipt.Id].Order));
            await new DpapiAutomaticMatchCacheStore(path, clock).SaveAsync(Account, loaded, 30);
            var saved = await new DpapiAutomaticMatchCacheStore(path, clock).LoadAsync(Account, 30);
            Eq(2, saved.Links.Count); Eq(2, saved.Details.Count);
            Eq(rozetka.Key, service.RestoreKnownSuggestions(saved, [promReceipt, rozetkaReceipt], Account,
                [prom, refreshed], [])[rozetkaReceipt.Id].Order!.Key);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void MarketplacePolicyGraphs()
    {
        var service = new CachedReceiptOrderMatchingService(new Clock()); var r = Receipt(2); var rozetka = RozetkaOrder();
        var fallback = LegacySuggestion(r, rozetka);
        foreach (var algorithm in new[] { "same-kyiv-day-active-unlinked-unit-symbols-v15", "same-kyiv-day-prom-only-rozetka-uuid-v16" })
        {
            var cache = Cache();
            cache.Links[r.Id] = new(fallback, Time.ToUniversalTime());
            cache.Snapshots.Add(new(algorithm, "synthetic-legacy-fingerprint", Time.ToUniversalTime(),
                new Dictionary<string, CachedReceiptMatch> { [r.Id] = fallback }));
            Yes(!service.HasVerifiedSnapshot(cache, Account, new(), []));
            Eq(1, cache.Links.Count); Eq(0, cache.Snapshots.Count);
            Eq(rozetka.Key, service.RestoreKnownSuggestions(cache, [r], Account, [rozetka], [])[r.Id].Order!.Key);
            Yes(service.LinkedOrderKeys(cache, Account, [r], [rozetka], []).SetEquals([rozetka.Key]));
            Yes(!service.TryRestore(cache, [r], Account, [rozetka], [], out _));
        }
        var current = Cache();
        service.MatchAll(current, [r], Account, [rozetka], [], true);
        Yes(service.TryRestore(current, [r], Account, [rozetka], [], out var replay));
        Eq(ReceiptLinkState.Suggested, replay[r.Id].State);
        var imported = Cache(); var prom = Order(); var promReceipt = Receipt();
        imported.Snapshots.Add(new("same-kyiv-day-clean-names-payment-v8", "synthetic-import", Time.ToUniversalTime(),
            new Dictionary<string, CachedReceiptMatch> { [r.Id] = fallback, [promReceipt.Id] = LegacySuggestion(promReceipt, prom) }));
        var keys = service.LinkedOrderKeys(imported, Account, [], [rozetka, prom], []);
        Yes(keys.SetEquals([prom.Key, rozetka.Key])); Eq(2, imported.Links.Count); Eq(1, imported.LinkSchema);
        // Alias migration may retain a keyless fallback, but new fiscal evidence releases it.
        var aliased = rozetka with { Key = rozetka.Key with { ConnectionId = "f7000000000000000000000000000003" } };
        service.NormalizeConnectionAliases(imported, Account, [rozetka], [aliased], key => key == rozetka.Key ? aliased.Key : key);
        Eq(2, imported.Links.Count); Eq(aliased.Key, imported.Links[r.Id].Match.Order);
        Eq(aliased.Key, service.RestoreKnownSuggestions(imported, [r], Account, [aliased], [])[r.Id].Order!.Key);
        var asserted = aliased with { FiscalReceiptNumbers = [r.FiscalCode] };
        Yes(!service.RestoreKnownSuggestions(imported, [r], Account, [asserted], []).ContainsKey(r.Id));
        Yes(!imported.Links.ContainsKey(r.Id)); Yes(imported.Links.ContainsKey(promReceipt.Id));
    }

    private static void MarketplacePolicyNewFiscalEvidence()
    {
        var service = new CachedReceiptOrderMatchingService(new Clock()); var r = Receipt(2); var other = Receipt(3, 99900);
        var order = RozetkaOrder();
        var code = new FiscalDocumentReference(FiscalDocumentKeyKind.FiscalCode, r.FiscalCode,
            "prro.prro_receipt_fiscal_code", order.Key, "Checkbox", "rozetka.prro");
        foreach (var mode in new[] { "same-uuid", "other-uuid", "unresolved-code", "manual" })
        {
            var cache = Cache();
            Eq(ReceiptLinkState.Suggested, service.MatchAll(cache, [r], Account, [order], [], true).Matches[r.Id].State);
            var updated = mode == "unresolved-code" ? order with { FiscalReferences = [code] }
                : order with { ReceiptIds = [mode == "same-uuid" ? r.Id : other.Id] };
            ReceiptOrderDecision[] decisions = mode == "manual"
                ? [new() { AccountContext = Account, ReceiptId = r.Id, ConfirmedOrder = order.Key, UpdatedAtUtc = Time }] : [];
            var changed = service.MatchAll(cache, [r, other], Account, [updated], decisions, true);
            Yes(!changed.CacheHit); Eq(0, cache.Links.Count);
            if (mode is "same-uuid" or "manual")
                Eq(mode == "manual" ? ReceiptLinkState.Manual : ReceiptLinkState.Exact, changed.Matches[r.Id].State);
            else Yes(changed.Matches[r.Id].Order is null);
            if (mode == "other-uuid")
            {
                Eq(ReceiptLinkState.Exact, changed.Matches[other.Id].State);
                Eq(order.Key, changed.Matches[other.Id].Order!.Key);
                Eq(999m, other.TotalSum); Eq(290m, changed.Matches[other.Id].Order!.Total);
            }
            if (mode == "unresolved-code") Eq(ReceiptLinkState.Incomplete, changed.Matches[r.Id].State);
            var restarted = JsonSerializer.Deserialize<AutomaticMatchCache>(JsonSerializer.Serialize(cache))!;
            var replay = service.MatchAll(restarted, [r, other], Account, [updated], decisions, true);
            Eq(changed.Matches[r.Id].State, replay.Matches[r.Id].State);
            Eq(changed.Matches[other.Id].State, replay.Matches[other.Id].State);
        }
        // Even a schema-valid saved fingerprint cannot bypass an already-present key.
        foreach (var asserted in new[] { order with { ReceiptIds = [other.Id] }, order with { FiscalReferences = [code] } })
        {
            var cache = Cache(); cache.Links[r.Id] = new(LegacySuggestion(r, asserted), Time.ToUniversalTime());
            Eq(0, service.RestoreKnownSuggestions(cache, [r], Account, [asserted], []).Count);
            Eq(0, cache.Links.Count);
        }
        // A different order's newly discovered ID also prevents replaying a fallback
        // while incomplete coverage postpones the authoritative assignment.
        var pendingCache = Cache();
        Eq(ReceiptLinkState.Suggested, service.MatchAll(pendingCache, [r], Account, [order], [], true).Matches[r.Id].State);
        var claimant = RozetkaOrder(3) with { ReceiptIds = [r.Id], Total = 999m };
        var pending = service.MatchAll(pendingCache, [r], Account, [order, claimant], [], false);
        Eq(ReceiptLinkState.Incomplete, pending.Matches[r.Id].State); Yes(pending.Matches[r.Id].Order is null);
        Eq(0, pendingCache.Links.Count);
        var completed = service.MatchAll(pendingCache, [r], Account, [order, claimant], [], true);
        Eq(ReceiptLinkState.Exact, completed.Matches[r.Id].State); Eq(claimant.Key, completed.Matches[r.Id].Order!.Key);
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
