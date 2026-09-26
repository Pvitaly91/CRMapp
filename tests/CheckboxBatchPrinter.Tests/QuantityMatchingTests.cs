using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class QuantityMatchingTests
{
    private static readonly DateTimeOffset Time = new(2026, 9, 26, 21, 0, 0, TimeSpan.FromHours(3));
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("quantity: equal201 orders with one and two units resolve via full MatchAll graph", Run(EqualAmounts)),
        ("quantity: merged and split lines do not manufacture quantity uniqueness", Run(CountsNotLines)),
        ("quantity: partial unknown fractional adjusted and loading data stay competitors", Run(UncertainCounts)),
        ("quantity: equal counts remain ambiguous and exact manual evidence keeps priority", Run(PriorityAndAmbiguity)),
        ("quantity: old algorithm cache expires without losing reusable receipt details", Run(CacheVersion))
    ];
    private static Func<Task> Run(Action action) => () => { action(); return Task.CompletedTask; };
    private static ReceiptRecord Receipt(int n) => new()
    { Id = $"c8100000-0000-0000-0000-{n:D12}", Type = "SELL", Status = "DONE", FiscalDate = Time, TotalSumMinor = 20100 };
    private static MarketplaceOrder Order(int n, params OrderItem[] items) => new()
    {
        Key = new(MarketplaceKind.Prom, "quantity-test-store", n.ToString()), Number = $"TEST-{n}",
        CreatedAt = Time.AddHours(-2), Total = 201m, Currency = "UAH", Items = items,
        ItemListComplete = true, ItemsComplete = true
    };
    private static OrderItem[] Single => [new("Кабель живлення", "", 1, 201, 201)];
    private static OrderItem[] Double => [new("Кабель живлення", "", 1, 100, 100), new("Адаптер живлення", "", 1, 101, 101)];
    private static ReceiptDetails Details(ReceiptRecord r, bool two) => new(r.Id, two
        ? [new("Кабель питания", "", 1, 100, 100), new("Адаптер питания", "", 1, 101, 101)]
        : [new("Кабель питания", "", 1, 201, 201)]) { ItemListComplete = true };
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
    private static void True(bool value) { if (!value) throw new Exception("Quantity scenario assertion failed."); }

    private static void EqualAmounts()
    {
        var receipts = new[] { Receipt(1), Receipt(2) };
        var orders = new[] { Order(1, Single), Order(2, Double) };
        var details = new Dictionary<string, ReceiptDetails>
        { [receipts[0].Id] = Details(receipts[0], false), [receipts[1].Id] = Details(receipts[1], true) };
        foreach (var rs in new[] { receipts, receipts.Reverse().ToArray() })
        foreach (var os in new[] { orders, orders.Reverse().ToArray() })
        {
            var result = new ReceiptOrderMatchingService().MatchAll(rs, "quantity-account", os, [], true, details);
            Console.WriteLine($"  Quantity201: {result[receipts[0].Id].State}/{result[receipts[1].Id].State}");
            for (var i = 0; i < 2; i++)
            {
                var match = result[receipts[i].Id];
                Equal(ReceiptLinkState.Suggested, match.State);
                Equal(orders[i].Key, match.Order!.Key);
                True(!match.Ambiguous && match.CompetingOrderCount == 1);
                Equal(AutomaticLinkBasis.AmountAndQuantity, match.Basis);
                Equal(ProductComparison.Insufficient, match.Products); // Count does not prove translated names identical.
                True(match.Explanation.Contains("Кількість товарних одиниць") && match.Explanation.Contains("Виключено"));
                Equal("Автозв’язок: сума й кількість товарів", match.StatusLabel);
            }
        }
    }

    private static ReceiptOrderMatch Match(ReceiptRecord receipt, MarketplaceOrder[] orders, ReceiptDetails? details,
        bool complete = true, IReadOnlySet<string>? loading = null) => new ReceiptOrderMatchingService().MatchAll(
        [receipt], "quantity-account", orders, [], complete,
        details is null ? null : new Dictionary<string, ReceiptDetails> { [receipt.Id] = details }, loading: loading)[receipt.Id];

    private static void CountsNotLines()
    {
        var r = Receipt(1);
        var merged = Order(1, new OrderItem("Комплектний кабель", "", 2, 100.5m, 201));
        var split = Order(2, Double);
        var result = Match(r, [merged, split, Order(3, Single)], Details(r, true));
        Equal(ReceiptLinkState.Candidates, result.State);
        Equal(2, result.CompetingOrderCount);
        True(result.Order is null && result.Ambiguous);
        // Without the other two-unit candidate, 1 row x 2 units beats 1 row x 1 unit.
        result = Match(r, [merged, Order(3, Single)], Details(r, true));
        Equal(merged.Key, result.Order!.Key);
        Equal(AutomaticLinkBasis.AmountAndQuantity, result.Basis);
    }

    private static void UncertainCounts()
    {
        var r = Receipt(1); var one = Order(1, Single); var two = Order(2, Double);
        var variants = new[]
        {
            two with { ItemListComplete = false }, two with { Items = [] },
            two with { Items = [new("Невідомий товар", "", null, null)] },
            two with { Items = [new("Невідомий товар", "", 0m, null)] },
            two with { Items = [new("Невідомий товар", "", -1m, null)] },
            two with { Items = [new("Невідомий товар", "", 0.5m, null)] },
            two with { Items = [new("A", "", decimal.MaxValue, null), new("B", "", decimal.MaxValue, null)] },
            two with { Discount = 10m }, two with { AmountComparisonIssue = "Часткова оплата" },
            two with { Items = [new("Невідомий товар", "", 2m, 100m, 200m)] }
        };
        foreach (var variant in variants)
        {
            var result = Match(r, [one, variant], Details(r, false));
            Equal(ReceiptLinkState.Candidates, result.State);
            True(result.Ambiguous && result.Order is null && result.CompetingOrderCount == 2);
        }
        foreach (var detail in new[] { null, Details(r, false) with { ItemListComplete = false },
            Details(r, false) with { Items = [new("Кабель питания", "", null, null)] } })
            True(Match(r, [one, two], detail).Order is null);
        Equal(ReceiptLinkState.Incomplete, Match(r, [one, two], Details(r, false), complete: false).State);
        True(Match(r, [one, two], Details(r, false), loading: new HashSet<string> { r.Id }).Order is null);
    }

    private static void PriorityAndAmbiguity()
    {
        var r = Receipt(1); var one = Order(1, Single); var two = Order(2, Double);
        var result = Match(r, [one, one with { Key = two.Key, Buyer = new("Інший синтетичний покупець") }], Details(r, false));
        True(result.Ambiguous && result.Order is null);
        var details = new Dictionary<string, ReceiptDetails> { [r.Id] = Details(r, false) };
        var manual = new ReceiptOrderDecision { AccountContext = "quantity-account", ReceiptId = r.Id, ConfirmedOrder = two.Key };
        var matcher = new ReceiptOrderMatchingService();
        Equal(ReceiptLinkState.Manual, matcher.MatchAll([r], "quantity-account", [one, two], [manual], true, details)[r.Id].State);
        var exact = matcher.MatchAll([r], "quantity-account", [one, two with { ReceiptIds = [r.Id] }], [], true, details)[r.Id];
        Equal(ReceiptLinkState.Exact, exact.State); Equal(two.Key, exact.Order!.Key);
        var suppressed = new ReceiptOrderDecision { AccountContext = "quantity-account", ReceiptId = r.Id, SuppressAutomatic = true };
        True(matcher.MatchAll([r], "quantity-account", [one, two], [suppressed], true, details)[r.Id].Order is null);
        // Product/model contradiction still wins even if unit counts match.
        var wrong = Order(1, new OrderItem("Адаптер 12 В", "", 1, 201, 201));
        True(Match(r, [wrong], new(r.Id, [new("Адаптер 24 В", "", 1, 201, 201)])).Order is null);
    }

    private static void CacheVersion()
    {
        var r = Receipt(1); var cache = new AutomaticMatchCache(); var service = new CachedReceiptOrderMatchingService();
        var detail = Details(r, false); var details = new Dictionary<string, ReceiptDetails> { [r.Id] = detail };
        var orders = new[] { Order(1, Single), Order(2, Double) };
        service.RememberDetails(cache, "quantity-account", r, detail);
        service.MatchAll(cache, [r], "quantity-account", orders, [], true, details);
        cache.Snapshots = cache.Snapshots.Select(s => s with { AlgorithmVersion = "same-kyiv-day-product-evidence-v1" }).ToList();
        True(!service.TryRestore(cache, [r], "quantity-account", orders, [], out _, details));
        True(service.TryGetDetails(cache, "quantity-account", r, out _));
        var updated = service.MatchAll(cache, [r], "quantity-account", orders, [], true, details);
        True(!updated.CacheHit);
        Equal(AutomaticLinkBasis.AmountAndQuantity, updated.Matches[r.Id].Basis);
    }
}
