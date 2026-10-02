using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class UnlinkedOrderMatchingTests
{
    private const string Account = "unlinked-order-account";
    private static readonly DateTimeOffset Day = new(2026, 10, 2, 0, 0, 0, TimeSpan.FromHours(3));
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("unlinked orders: saved morning sale leaves identical evening sale free across restart", Run(RepeatedSale)),
        ("unlinked orders: uncached morning and evening sales resolve the only complete allocation", Run(UncachedRepeatedSale)),
        ("unlinked orders: complete allocation does not guess among cycles or incomplete documents", Run(AllocationGuardrails)),
        ("unlinked orders: linked order outside receipt range cannot take a new receipt", Run(OutsideReceiptRange)),
        ("unlinked orders: new unlinked competitors stay ambiguous without changing saved pair", Run(UnlinkedAmbiguity)),
        ("unlinked orders: manual suppression, rejection and fiscal evidence retain priority", Run(DecisionsAndFiscalEvidence)),
        ("unlinked orders: changed amount or product invalidates that saved pair", Run(ChangedDocuments)),
        ("unlinked orders: previous algorithm snapshots migrate assigned pairs", Run(LegacySnapshot))
    ];
    private static Func<Task> Run(Action action) => () => { action(); return Task.CompletedTask; };
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Day.AddDays(1); }
    private static ReceiptRecord Receipt(int n, int hour, long amount = 32000) => new()
    {
        Id = $"ab000000-0000-0000-0000-{n:D12}", Serial = n, Type = "SELL", Status = "DONE",
        FiscalDate = Day.AddHours(hour).AddMinutes(13), TotalSumMinor = amount,
        Payments = [new("CASHLESS", "Платіж RozetkaPay", amount)]
    };
    private static MarketplaceOrder Order(int n, int hour) => new()
    {
        Key = new(MarketplaceKind.Prom, "same-store", n.ToString()), Number = n.ToString(),
        CreatedAt = Day.AddHours(hour).AddMinutes(11), Total = 320m, Currency = "UAH",
        PaymentMethod = "Пром-оплата", Items = [new("LED підсвітка багажника 2 м", "", 1, 320, 320)],
        ItemsComplete = true, ItemListComplete = true, RetrievedAtUtc = Day.AddHours(23)
    };
    private static ReceiptDetails Detail(ReceiptRecord r) => new(r.Id, Order(1, 11).Items) { ItemListComplete = true };
    private static Dictionary<string, ReceiptDetails> Details(params ReceiptRecord[] rs) => rs.ToDictionary(r => r.Id, Detail);
    private static AutomaticMatchCache Restart(AutomaticMatchCache cache) =>
        JsonSerializer.Deserialize<AutomaticMatchCache>(JsonSerializer.Serialize(cache))!;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Pair(IReadOnlyDictionary<string, ReceiptOrderMatch> matches, ReceiptRecord r, MarketplaceOrder o) =>
        Check(matches[r.Id].State == ReceiptLinkState.Suggested && matches[r.Id].Order?.Key == o.Key,
            $"Expected receipt {r.Serial} to retain/link order {o.Number}; got {matches[r.Id].Explanation}");

    private static void RepeatedSale()
    {
        var service = new CachedReceiptOrderMatchingService(new Clock());
        var cache = new AutomaticMatchCache(); var old = Receipt(4581, 12); var first = Order(431129512, 11);
        Pair(service.MatchAll(cache, [old], Account, [first], [], true, Details(old)).Matches, old, first);
        cache = Restart(cache); service = new(new Clock());
        var next = Receipt(4598, 21); var second = Order(431249067, 21);
        foreach (var reversed in new[] { false, true })
        {
            var result = service.MatchAll(cache, reversed ? [next, old] : [old, next], Account,
                reversed ? [second, first] : [first, second], [], true, Details(old, next)).Matches;
            Pair(result, old, first); Pair(result, next, second);
            Check(result[next.Id].Candidates.All(o => o.Key != first.Key), "Linked morning order remained a candidate.");
        }
        var incomplete = service.MatchAll(Restart(cache), [old, next], Account, [first, second], [], false).Matches;
        Pair(incomplete, old, first); Pair(incomplete, next, second);
    }
    private static void UncachedRepeatedSale()
    {
        foreach (var count in new[] { 2, 3, 12 })
        foreach (var reversed in new[] { false, true })
        {
            var service = new CachedReceiptOrderMatchingService(new Clock());
            var receipts = Enumerable.Range(0, count).Select(i => Receipt(4581 + i, count == 2 ? i == 0 ? 12 : 21 : 8 + i)).ToArray();
            var orders = Enumerable.Range(0, count).Select(i => Order(431129512 + i, count == 2 ? i == 0 ? 11 : 21 : 8 + i)).ToArray();
            var cache = new AutomaticMatchCache();
            var result = service.MatchAll(cache, reversed ? receipts.Reverse().ToArray() : receipts, Account,
                reversed ? orders.Reverse().ToArray() : orders, [], true, Details(receipts)).Matches;
            for (var i = 0; i < count; i++)
            {
                Pair(result, receipts[i], orders[i]);
                Check(!result[receipts[i].Id].Ambiguous, "The only complete allocation was marked ambiguous.");
            }
            var restored = service.MatchAll(Restart(cache), receipts, Account, orders, [], false).Matches;
            for (var i = 0; i < count; i++) Pair(restored, receipts[i], orders[i]);
        }
    }
    private static void AllocationGuardrails()
    {
        foreach (var mode in new[] { "cycle", "unbalanced", "missing", "loading", "incomplete", "suppressed", "rejected", "fiscal", "adjusted", "currency", "hall" })
        {
            var receipts = new[] { Receipt(1, 12), Receipt(2, 21) };
            var orders = new[] { Order(1, 11), Order(2, 21) };
            var details = Details(receipts);
            var decisions = Array.Empty<ReceiptOrderDecision>();
            if (mode == "cycle") orders[1] = Order(2, 11);
            if (mode == "unbalanced") orders = [.. orders, Order(3, 21)];
            if (mode == "missing") details.Remove(receipts[0].Id);
            if (mode == "suppressed") decisions = [new() { AccountContext = Account, ReceiptId = receipts[0].Id, SuppressAutomatic = true }];
            if (mode == "rejected") decisions = [new() { AccountContext = Account, ReceiptId = receipts[0].Id, RejectedOrders = [orders[0].Key] }];
            if (mode == "fiscal") orders[0] = orders[0] with { FiscalReceiptNumbers = ["unverified-reference"] };
            if (mode == "adjusted") orders[0] = orders[0] with { Discount = 1 };
            if (mode == "currency") orders[0] = orders[0] with { Currency = "" };
            if (mode == "hall")
            {
                receipts = [Receipt(1, 12), Receipt(2, 13), Receipt(3, 22)];
                orders = [Order(1, 11), Order(2, 21), Order(3, 21)];
                details = Details(receipts);
            }
            var result = new CachedReceiptOrderMatchingService(new Clock()).MatchAll(new(), receipts, Account, orders,
                decisions, mode != "incomplete", details, loading: mode == "loading" ? new HashSet<string> { receipts[0].Id } : null).Matches;
            Check(result.Values.All(m => m.Order is null), $"Incomplete or non-unique allocation was guessed: {mode}.");
        }
    }
    private static void OutsideReceiptRange()
    {
        var service = new CachedReceiptOrderMatchingService(new Clock()); var cache = new AutomaticMatchCache();
        var old = Receipt(1, 12); var next = Receipt(2, 21); var first = Order(1, 11); var second = Order(2, 21);
        service.MatchAll(cache, [old], Account, [first], [], true, Details(old));
        var result = service.MatchAll(Restart(cache), [next], Account, [first, second], [], true, Details(next)).Matches;
        Pair(result, next, second);
        Check(result[next.Id].Candidates.All(o => o.Key != first.Key), "Offscreen linked order competed.");
    }
    private static void UnlinkedAmbiguity()
    {
        var service = new CachedReceiptOrderMatchingService(new Clock()); var cache = new AutomaticMatchCache();
        var old = Receipt(1, 12); var first = Order(1, 11);
        service.MatchAll(cache, [old], Account, [first], [], true, Details(old));
        var next = Receipt(2, 21); var second = Order(2, 21); var third = Order(3, 21);
        var result = service.MatchAll(cache, [old, next], Account, [first, second, third], [], true, Details(old, next)).Matches;
        Pair(result, old, first);
        Check(result[next.Id].Order is null && result[next.Id].Ambiguous && result[next.Id].CompetingOrderCount == 2,
            "Two free orders were allocated arbitrarily.");
        var onlyLinked = service.MatchAll(Restart(cache), [old, next], Account, [first], [], true, Details(old, next)).Matches;
        Pair(onlyLinked, old, first); Check(onlyLinked[next.Id].Order is null, "Already linked order was reused.");
    }
    private static void DecisionsAndFiscalEvidence()
    {
        foreach (var mode in new[] { "suppressed", "rejected", "manual", "exact" })
        {
            var service = new CachedReceiptOrderMatchingService(new Clock()); var cache = new AutomaticMatchCache();
            var old = Receipt(1, 12); var first = Order(1, 11); var second = Order(2, 11);
            service.MatchAll(cache, [old], Account, [first], [], true, Details(old));
            var decisions = mode == "exact" ? Array.Empty<ReceiptOrderDecision>() : new[] { new ReceiptOrderDecision
            {
                AccountContext = Account, ReceiptId = old.Id, SuppressAutomatic = mode == "suppressed",
                RejectedOrders = mode == "rejected" ? [first.Key] : [], ConfirmedOrder = mode == "manual" ? second.Key : null
            }};
            if (mode == "exact") second = second with { ReceiptIds = [old.Id] };
            var result = service.MatchAll(cache, [old], Account, [first, second], decisions, true, Details(old)).Matches[old.Id];
            Check(mode switch
            {
                "manual" => result.State == ReceiptLinkState.Manual && result.Order?.Key == second.Key,
                "exact" => result.State == ReceiptLinkState.Exact && result.Order?.Key == second.Key,
                _ => result.Order is null
            }, $"Saved link ignored {mode}.");
        }
    }
    private static void ChangedDocuments()
    {
        foreach (var changeReceipt in new[] { false, true })
        {
            var service = new CachedReceiptOrderMatchingService(new Clock()); var cache = new AutomaticMatchCache();
            var old = Receipt(1, 12); var first = Order(1, 11);
            service.MatchAll(cache, [old], Account, [first], [], true, Details(old));
            if (changeReceipt) old = Receipt(1, 12, 33000);
            else first = first with { Items = [new("Інший товар", "", 1, 320, 320)] };
            var result = service.MatchAll(cache, [old], Account, [first], [], true, Details(old)).Matches[old.Id];
            Check(result.Order is null, "Materially changed document kept an obsolete link.");
        }
    }
    private static void LegacySnapshot()
    {
        var service = new CachedReceiptOrderMatchingService(new Clock()); var cache = new AutomaticMatchCache();
        var old = Receipt(1, 12); var first = Order(1, 11);
        service.MatchAll(cache, [old], Account, [first], [], true, Details(old));
        // Serialize the previous schema: only Details/Snapshots existed, without saved link state.
        cache = JsonSerializer.Deserialize<AutomaticMatchCache>(JsonSerializer.Serialize(new
        {
            cache.AccountContext, cache.RetentionDays, cache.Details,
            Snapshots = cache.Snapshots.Select(s => s with { AlgorithmVersion = "same-kyiv-day-clean-names-payment-v8" })
        }))!;
        var next = Receipt(2, 21); var second = Order(2, 21);
        var result = service.MatchAll(cache, [old, next], Account, [first, second], [], true, Details(old, next)).Matches;
        Pair(result, old, first); Pair(result, next, second);
    }
}
