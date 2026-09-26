using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class AmountMatchingTests
{
    private static readonly DateTimeOffset Time = new(2026, 9, 23, 12, 0, 0, TimeSpan.FromHours(3));
    private static readonly ReceiptOrderMatchingService Matcher = new();
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("amount graph: unique amount without details, prices or SKU and with complete basket", Run(Unique)),
        ("amount graph: products distinguish equal totals, not API order or timestamp", Run(DistinctProducts)),
        ("amount graph: identical buyers' orders stay ambiguous in 2:2, 2:1 and 1:2", Run(Ambiguity)),
        ("amount graph: missing competitor details and unknown totals never manufacture uniqueness", Run(MissingCompetitors)),
        ("amount graph: late competitor withdraws suggestion, confirmed evidence reserves history", Run(Reservations)),
        ("amount graph: decimal cents, foreign currency, dates and adjusted documents", Run(Boundaries)),
        ("amount graph: loading and incomplete pagination are distinct, repeatable states", Run(Loading)),
        ("amount graph: name models and quantities contradict, abbreviations and translations are unknown", Run(Names)),
        ("amount graph: receipt parser unknown total is not zero and special payments are guarded", Run(ReceiptSemantics)),
        ("amount graph: separately charged delivery only passes with known product-total evidence", Run(Delivery)),
        ("amount graph: large identical-price component stays wholly ambiguous", Run(LargeGroup))
    ];
    private static Func<Task> Run(Action test) => () => { test(); return Task.CompletedTask; };
    private static ReceiptRecord R(int n = 1, long cents = 29000, string type = "SELL", string status = "DONE", DateTimeOffset? time = null) =>
        new() { Id = $"b9000000-0000-0000-0000-{n:D12}", Serial = n, Type = type, Status = status, TotalSumMinor = cents, FiscalDate = time ?? Time };
    private static OrderItem[] I(string name = "Товар X", decimal? quantity = 1) => [new(name, "", quantity, null)];
    private static MarketplaceOrder O(int n = 1, string name = "Товар X") => new()
    {
        Key = new(n % 2 == 0 ? MarketplaceKind.Rozetka : MarketplaceKind.Prom, $"store-{n}", n.ToString()),
        Number = n.ToString(), CreatedAt = Time.AddDays(-1), Total = 290m, Currency = "UAH",
        Buyer = new($"Синтетичний покупець {n}"), Items = I(name), ItemsComplete = true
    };
    private static Dictionary<string, ReceiptDetails> D(params (ReceiptRecord R, string Name)[] rows) =>
        rows.ToDictionary(p => p.R.Id, p => new ReceiptDetails(p.R.Id, I(p.Name)));
    private static IReadOnlyDictionary<string, ReceiptOrderMatch> M(ReceiptRecord[] rs, MarketplaceOrder[] os,
        IReadOnlyDictionary<string, ReceiptDetails>? details = null, IReadOnlyList<ReceiptOrderDecision>? decisions = null,
        bool complete = true, AmountMatchScope? scope = null, IReadOnlySet<string>? loading = null) =>
        Matcher.MatchAll(rs, "test-account", os, decisions ?? [], complete, details, scope, loading);
    private static void Eq<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
    private static void Yes(bool ok) { if (!ok) throw new Exception("Amount scenario assertion failed."); }
    private static void NoAuto(IEnumerable<ReceiptOrderMatch> values) => Yes(values.All(m => m.State != ReceiptLinkState.Suggested));

    private static void Unique()
    {
        var r = R(); var o = O();
        var noDetails = M([r], [o])[r.Id];
        Eq(ReceiptLinkState.Suggested, noDetails.State); Eq(AutomaticLinkBasis.UniqueAmount, noDetails.Basis);
        Eq(ProductComparison.Insufficient, noDetails.Products); Yes(noDetails.Explanation.Contains("недостатньо даних"));
        var full = M([r], [o], D((r, "Товар X")))[r.Id];
        Eq(AutomaticLinkBasis.AmountAndProducts, full.Basis); Eq(o.Key, full.Order!.Key);
        var noPrices = o with { Items = I(), ItemListComplete = true, ItemsComplete = false };
        Eq(AutomaticLinkBasis.AmountAndProducts, M([r], [noPrices], D((r, "Товар X")))[r.Id].Basis);
        Eq(AutomaticLinkBasis.UniqueAmount, M([r], [o with { Items = [], ItemsComplete = false }])[r.Id].Basis);
        Yes(M([r], [o with { Currency = "" }])[r.Id].Explanation.Contains("Валюта API не підтверджена"));
        var wrong = M([r], [o], D((r, "Товар Y")))[r.Id];
        Eq(ProductComparison.Contradiction, wrong.Products); Eq(ReceiptLinkState.Candidates, wrong.State);
    }

    private static void DistinctProducts()
    {
        var rs = new[] { R(), R(2) }; var os = new[] { O(), O(2, "Товар Y") };
        var ds = D((rs[0], "Товар X"), (rs[1], "Товар Y"));
        for (var i = 0; i < 4; i++)
        {
            var result = M(i % 2 == 0 ? rs : rs.Reverse().ToArray(), i < 2 ? os : os.Reverse().ToArray(), ds);
            Eq(os[0].Key, result[rs[0].Id].Order!.Key); Eq(os[1].Key, result[rs[1].Id].Order!.Key);
            Eq(2, result.Values.Select(m => m.Order!.Key).Distinct().Count());
        }
    }
    private static void Ambiguity()
    {
        foreach (var (nr, no) in new[] { (2, 2), (1, 2), (2, 1) })
        {
            var rs = Enumerable.Range(1, nr).Select(n => R(n)).ToArray();
            var os = Enumerable.Range(1, no).Select(n => O(n)).ToArray();
            var ds = D(rs.Select(r => (r, "Товар X")).ToArray());
            foreach (var result in M(rs, os, ds).Values)
            {
                Eq(ReceiptLinkState.Candidates, result.State); Yes(result.Ambiguous);
                Eq(nr, result.CompetingReceiptIds.Count); Eq(no, result.CompetingOrderCount);
            }
            NoAuto(M(rs.Reverse().ToArray(), os.Reverse().ToArray(), ds).Values);
        }
    }
    private static void MissingCompetitors()
    {
        var r = R(); var o = O();
        NoAuto(M([r], [o, O(2) with { Items = [], ItemsComplete = false }], D((r, "Товар X"))).Values);
        NoAuto(M([r, R(2)], [o], D((r, "Товар X"))).Values);
        NoAuto(M([r], [o, O(2) with { Total = null }]).Values);
        NoAuto(M([r], [o, O(2) with { CreatedAt = null }]).Values);
        // Rejection is not a new proof; suppressed/rejected nodes remain competitors.
        var reject = new ReceiptOrderDecision { AccountContext = "test-account", ReceiptId = r.Id, RejectedOrders = [O(2).Key] };
        NoAuto(M([r], [o, O(2)], decisions: [reject]).Values);
        NoAuto(M([r, R(2)], [o], decisions: [new() { AccountContext = "test-account", ReceiptId = R(2).Id, SuppressAutomatic = true }]).Values);
    }
    private static void Reservations()
    {
        var r = R(); var r2 = R(2); var o = O();
        Eq(ReceiptLinkState.Suggested, M([r], [o])[r.Id].State);
        NoAuto(M([r], [o, O(2)]).Values);
        var manual = new ReceiptOrderDecision { AccountContext = "test-account", ReceiptId = r.Id, ConfirmedOrder = o.Key };
        var result = M([r, r2], [o, O(2)], decisions: [manual]);
        Eq(ReceiptLinkState.Manual, result[r.Id].State); Eq(O(2).Key, result[r2.Id].Order!.Key);
        // Confirmed outside loaded receipt dates also reserves the order.
        NoAuto(M([r2], [o], decisions: [manual]).Values);
        var fiscal = M([r, r2], [o with { ReceiptIds = [r.Id] }, O(2)]);
        Eq(ReceiptLinkState.Exact, fiscal[r.Id].State); Eq(O(2).Key, fiscal[r2.Id].Order!.Key);
        NoAuto(M([r], [o with { ReceiptIds = [r2.Id] }]).Values);
        // Different receipt types can still share exact/manual evidence.
        var returned = R(3, type: "RETURN");
        var linkedReturn = M([r, returned], [o with { ReceiptIds = [r.Id] }],
            new Dictionary<string, ReceiptDetails> { [returned.Id] = new(returned.Id, [], RelatedReceiptId: r.Id) });
        Eq(ReceiptLinkState.Exact, linkedReturn[returned.Id].State);
    }
    private static void Boundaries()
    {
        var r = R(); var o = O();
        foreach (var other in new[] { o with { Total = 290.01m }, o with { Currency = "USD" }, o with { Total = null },
            o with { DeliveryCost = 20 }, o with { Discount = 5 }, o with { AmountComparisonIssue = "Часткова оплата" },
            o with { CreatedAt = Time.AddSeconds(1) } })
            NoAuto(M([r], [other]).Values);
        foreach (var receipt in new[] { R(type: "RETURN"), R(status: "CREATED"), R(cents: 0), R(cents: -29000) })
            NoAuto(M([receipt], [o]).Values);
        Eq(ReceiptLinkState.Suggested, M([r], [o])[r.Id].State); // yesterday -> today
        var older = o with { CreatedAt = Time.AddDays(-45) };
        NoAuto(M([r], [older]).Values);
        var range = new MarketplaceRange(Time.AddDays(-60), Time.AddDays(1));
        Eq(ReceiptLinkState.Suggested, M([r], [older], scope: new(60, range, "60-day test range"))[r.Id].State);
        NoAuto(M([r], [older], scope: new(10, new(Time.AddDays(-10), Time.AddDays(1)))).Values);
        var malformedLines = o with { Items = [new("Товар X", "", 1m, 290m, 280m)] };
        NoAuto(M([r], [malformedLines], D((r, "Товар X"))).Values);
    }
    private static void Loading()
    {
        var r = R(); var o = O();
        var result = M([r], [o], loading: new HashSet<string> { r.Id })[r.Id];
        Eq(ProductComparison.Loading, result.Products); Eq(ReceiptLinkState.Suggested, result.State);
        Eq(ReceiptLinkState.Incomplete, M([r], [o], complete: false)[r.Id].State);
        var wrong = M([r], [o], D((r, "Товар Y")))[r.Id];
        Eq(ReceiptLinkState.Candidates, wrong.State);
        var rs = new[] { r, R(2) };
        NoAuto(M(rs, [o], D((r, "Товар X")), loading: new HashSet<string> { rs[1].Id }).Values);
    }
    private static void Names()
    {
        var r = R(); var o = O() with { Items = I("Датчик 12/24 В") };
        Eq(ProductComparison.Contradiction, M([r], [o], D((r, "Датчик 12/48 В")))[r.Id].Products);
        Eq(ProductComparison.Contradiction, M([r], [O() with { Items = I(quantity: 2) }], D((r, "Товар X")))[r.Id].Products);
        Eq(ProductComparison.Insufficient, M([r], [o], D((r, "Датчик")))[r.Id].Products);
        Eq(AutomaticLinkBasis.UniqueAmount, M([r], [O() with { Items = I("Кабель") }], D((r, "Cable")))[r.Id].Basis);
        var split = O() with { Items = [new("Товар X", "one", 0.5m, 10m), new("товар  X", "two", 0.5m, 500m)] };
        Eq(ProductComparison.Match, M([r], [split], D((r, "Товар X")))[r.Id].Products);
    }
    private static void ReceiptSemantics()
    {
        var r = ReceiptParser.ParsePage($$"""{"results":[{"id":"{{R().Id}}","type":"SELL","status":"DONE","fiscal_date":"{{Time:O}}"}]}""").Single();
        Yes(!r.TotalKnown); NoAuto(M([r], [O() with { Total = 0 }]).Values);
        var special = ReceiptParser.ParsePage($$"""{"results":[{"id":"{{R().Id}}","type":"SELL","status":"DONE","total_sum":29000,"fiscal_date":"{{Time:O}}","pre_payment_relation_id":"{{R(2).Id}}"}]}""").Single();
        Yes(special.AmountComparisonIssue.Length > 0); NoAuto(M([special], [O()]).Values);
    }

    private static void Delivery()
    {
        var r = R();
        var separate = O() with { DeliveryCost = 80, Items = [new("Товар X", "", 1, 290)] };
        var result = M([r], [separate], D((r, "Товар X")))[r.Id];
        Eq(ReceiptLinkState.Suggested, result.State); Yes(result.Explanation.Contains("Доставка вказана окремо"));
        NoAuto(M([r], [separate with { Items = [new("Товар X", "", 1, 210)] }]).Values);
        NoAuto(M([r], [separate with { Items = I() }]).Values);
        NoAuto(M([r], [separate with { Discount = 5 }]).Values);
        Eq(290m, result.Order!.Total); // Neither delivery nor commission changes source amounts.
    }

    private static void LargeGroup()
    {
        var receipts = Enumerable.Range(1, 200).Select(n => R(n)).ToArray();
        var orders = Enumerable.Range(1, 200).Select(n => O(n)).ToArray();
        var result = M(receipts, orders);
        NoAuto(result.Values);
        Yes(result.Values.All(m => m.CompetingOrderCount == 200 && m.CompetingReceiptIds.Count == 200));
    }
}
