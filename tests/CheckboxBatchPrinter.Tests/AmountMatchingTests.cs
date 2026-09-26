using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class AmountMatchingTests
{
    private static readonly DateTimeOffset Time = new(2026, 9, 23, 12, 0, 0, TimeSpan.FromHours(3));
    private static readonly ReceiptOrderMatchingService Matcher = new();
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("product regression: translated cable remains a competitor in complete A/B/receipt graph", Run(TranslatedCandidateRegression)),
        ("product evidence: translations, reordered, abbreviated and mixed-script names stay uncertain", Run(UnknownNames)),
        ("product evidence: aligned voltage model size color connector and quantity differences explain exclusions", Run(EstablishedDifferences)),
        ("product evidence: reordered numbers, unaligned characteristics and foreign articles cannot prove difference", Run(UnalignedCharacteristics)),
        ("product evidence: unknown lines remain possible counterparts in multi-item baskets", Run(UnknownBasketLines)),
        ("amount graph: unique amount without details, prices or SKU and with complete basket", Run(Unique)),
        ("amount graph: 355 and 1400 match within Kyiv day, historical totals do not compete", Run(SameDayAmounts)),
        ("amount graph: Kyiv calendar dates handle UTC midnight and retain unknown-date competitors", Run(SameDayBoundaries)),
        ("amount graph: exact and manual evidence retain priority across calendar dates", Run(CrossDayEvidence)),
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
        Number = n.ToString(), CreatedAt = Time.AddHours(-1), Total = 290m, Currency = "UAH",
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
        // Explicit model field, not the formerly unjustified X/Y wording distinction.
        var wrong = M([r], [O(name: "Товар модель X")], D((r, "Товар модель Y")))[r.Id];
        Eq(ProductComparison.Contradiction, wrong.Products); Eq(ReceiptLinkState.Candidates, wrong.State);
    }

    private static void TranslatedCandidateRegression()
    {
        var receipt = R();
        MarketplaceOrder Cable(int id, string name) => O(id, name) with
        {
            Number = id == 1 ? "A" : "B", Buyer = null,
            Items = [new(name, "", 1m, 290m, 290m)], ItemListComplete = true
        };
        var a = Cable(1, "Кабель живлення");
        var b = Cable(2, "Кабель");
        var details = new Dictionary<string, ReceiptDetails>
        {
            [receipt.Id] = new(receipt.Id, [new("Кабель питания", "", 1m, 290m, 290m)]) { ItemListComplete = true }
        };
        var result = Matcher.MatchAll([receipt], "test-account", [a, b], [], coverageComplete: true, details)[receipt.Id];
        Console.WriteLine($"  A/B regression: state={result.State}; assigned={result.Order?.Number ?? "none"}; competing orders={result.CompetingOrderCount}");
        Eq(ReceiptLinkState.Candidates, result.State);
        Yes(result.Order is null && result.Ambiguous);
        Eq(2, result.CompetingOrderCount);
        Eq(2, result.Candidates.Count);
        var reversed = Matcher.MatchAll([receipt], "test-account", [b, a], [], true, details)[receipt.Id];
        Eq(result.State, reversed.State); Eq(result.Explanation, reversed.Explanation);
        Yes(result.GroupOrders.Select(o => o.Key).SequenceEqual(reversed.GroupOrders.Select(o => o.Key)));
    }

    private static void SameDayAmounts()
    {
        var day = new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.FromHours(3));
        var receipts = new[] { R(1, 35500, time: day.AddHours(13)), R(2, 140000, time: day.AddHours(14)),
            R(3, 35500, time: day.AddDays(-1).AddHours(13)) };
        var orders = new[]
        {
            O(1) with { CreatedAt = day.AddHours(8).AddMinutes(30), Total = 355m, Items = [], ItemsComplete = false },
            O(2) with { CreatedAt = day.AddHours(12).AddMinutes(53), Total = 1400m, Items = [], ItemsComplete = false },
            O(3) with { CreatedAt = day.AddDays(-1).AddHours(8), Total = 355m, Items = [], ItemsComplete = false }
        };
        var scope = new AmountMatchScope(30, new(day.AddDays(-30), day.AddDays(1)), "Усі завантажені документи");
        foreach (var rs in new[] { receipts, receipts.Reverse().ToArray() })
        foreach (var os in new[] { orders, orders.Reverse().ToArray() })
        {
            var result = M(rs, os, scope: scope);
            for (var index = 0; index < receipts.Length; index++)
            {
                var match = result[receipts[index].Id];
                Eq(ReceiptLinkState.Suggested, match.State); Eq(orders[index].Key, match.Order!.Key);
                Eq(AutomaticLinkBasis.UniqueAmount, match.Basis); Eq(1, match.CompetingOrderCount);
                Yes(match.Explanation.Contains("одна календарна дата"));
            }
        }
        var duplicate = orders[0] with { Key = O(4).Key, Buyer = new("Інший синтетичний покупець") };
        var ambiguous = M(receipts, [.. orders, duplicate], scope: scope)[receipts[0].Id];
        Eq(ReceiptLinkState.Candidates, ambiguous.State); Eq(2, ambiguous.CompetingOrderCount);
        Yes(ambiguous.Ambiguous && ambiguous.Order is null);
        Eq(ReceiptLinkState.Incomplete, M(receipts, orders, complete: false, scope: scope)[receipts[0].Id].State);
    }

    private static void SameDayBoundaries()
    {
        // Different UTC dates can be the same Kyiv date; one UTC date can contain two Kyiv dates.
        foreach (var (created, fiscal, eligible) in new[]
        {
            ("2026-09-25T21:30:00Z", "2026-09-26T12:00:00Z", true),
            ("2026-09-26T21:00:00Z", "2026-09-26T21:10:00Z", true),
            ("2026-09-26T20:59:59Z", "2026-09-26T21:10:00Z", false),
            ("2026-01-25T22:30:00Z", "2026-01-26T12:00:00Z", true),
            ("2026-01-25T21:59:59Z", "2026-01-25T22:10:00Z", false),
            ("2026-09-26T13:00:01+03:00", "2026-09-26T13:00:00+03:00", false)
        })
        {
            var receipt = R(time: DateTimeOffset.Parse(fiscal));
            var order = O() with { CreatedAt = DateTimeOffset.Parse(created) };
            var match = M([receipt], [order])[receipt.Id];
            Eq(eligible ? ReceiptLinkState.Suggested : ReceiptLinkState.NotFound, match.State);
        }
        var r = R(); var o = O();
        var unknownOrder = o with { Key = O(2).Key, CreatedAt = null };
        var competing = M([r], [o, unknownOrder])[r.Id];
        Eq(ReceiptLinkState.Candidates, competing.State); Eq(2, competing.CompetingOrderCount);
        Eq(ReceiptLinkState.Candidates, M([r], [unknownOrder])[r.Id].State);
        var unknownReceipt = new ReceiptRecord
        {
            Id = R(2).Id, Type = ReceiptTypes.Sell, Status = "DONE", TotalSumMinor = 29000
        };
        var unknownResult = M([r, unknownReceipt], [o]);
        NoAuto(unknownResult.Values); Eq(2, unknownResult[r.Id].CompetingReceiptIds.Count);
    }

    private static void CrossDayEvidence()
    {
        var r = R(); var older = O() with { CreatedAt = Time.AddDays(-45) };
        Eq(ReceiptLinkState.NotFound, M([r], [older])[r.Id].State);
        var manual = new ReceiptOrderDecision { AccountContext = "test-account", ReceiptId = r.Id, ConfirmedOrder = older.Key };
        Eq(ReceiptLinkState.Manual, M([r], [older], decisions: [manual])[r.Id].State);
        var exact = M([r], [older with { ReceiptIds = [r.Id] }])[r.Id];
        Eq(ReceiptLinkState.Exact, exact.State); Eq(older.Key, exact.Order!.Key);
        var second = R(2); var today = O(2);
        var reserved = M([r, second], [older, today], decisions: [manual]);
        Eq(ReceiptLinkState.Manual, reserved[r.Id].State); Eq(today.Key, reserved[second.Id].Order!.Key);
    }

    private static void DistinctProducts()
    {
        var rs = new[] { R(), R(2) }; var os = new[] { O(1, "Товар модель X"), O(2, "Товар модель Y") };
        var ds = D((rs[0], "Товар модель X"), (rs[1], "Товар модель Y"));
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
        Eq(ReceiptLinkState.Suggested, M([r], [o])[r.Id].State); // Earlier on the same Kyiv date.
        var older = o with { CreatedAt = Time.AddDays(-45) };
        NoAuto(M([r], [older]).Values);
        var range = new MarketplaceRange(Time.AddDays(-60), Time.AddDays(1));
        NoAuto(M([r], [older], scope: new(60, range, "60-day test range")).Values); // Expanding history does not relax same-day evidence.
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
        var wrong = M([r], [O(name: "Товар модель X")], D((r, "Товар модель Y")))[r.Id];
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
    private static ReceiptOrderMatch Products(string orderName, string receiptName, decimal orderQuantity = 1, decimal receiptQuantity = 1)
    {
        var r = R();
        var order = O(name: orderName) with { Items = [new(orderName, "shop-article", orderQuantity, 290m / orderQuantity, 290m)], ItemListComplete = true };
        var details = new Dictionary<string, ReceiptDetails>
        {
            [r.Id] = new(r.Id, [new(receiptName, "receipt-article", receiptQuantity, 290m / receiptQuantity, 290m)]) { ItemListComplete = true }
        };
        return M([r], [order], details)[r.Id];
    }

    private static void UnknownNames()
    {
        foreach (var (order, receipt) in new[]
        {
            ("Кабель живлення", "Кабель питания"),
            ("Червоний кабель USB", "Кабель USB червоний"),
            ("Кабель живлення USB", "Каб. USB"),
            ("Кабель USB Type-C", "Кабель USВ Type-C"), // Last character in USВ is Cyrillic.
            ("Товар X", "Товар Y"), // Different text alone is no longer a proof.
            ("Кабель", "Cable")
        })
        {
            var result = Products(order, receipt);
            Eq(ReceiptLinkState.Suggested, result.State); Eq(AutomaticLinkBasis.UniqueAmount, result.Basis);
            Eq(ProductComparison.Insufficient, result.Products);
            Yes(!result.Explanation.Contains("Виключено"));
        }
        Eq(ProductComparison.Match, Products("Кабель живлення", "  КАБЕЛЬ\u00a0живлення ").Products);
    }

    private static void EstablishedDifferences()
    {
        foreach (var (order, receipt, reason) in new[]
        {
            ("Адаптер 12 В", "Адаптер 24 В", "Напруга"),
            ("Датчик модель DC-100", "Датчик модель DC-200", "Модель"),
            ("Датчик MODEL DC-12V", "Датчик MODEL DC-24V", "Модель"),
            ("Кабель розмір 2 м", "Кабель розмір 3 м", "Розмір"),
            ("Червоний кабель USB", "Кабель USB чорний", "Колір"),
            ("Кабель USB-A", "Кабель USB-C", "Роз’єм"),
            ("Адаптер модель X 12 В чорний", "Червоний адаптер 24 В модель Y", "Модель")
        })
        {
            var result = Products(order, receipt);
            Eq(ReceiptLinkState.Candidates, result.State); Eq(ProductComparison.Contradiction, result.Products);
            Yes(result.Explanation.Contains("Виключено замовлення") && result.Explanation.Contains(reason));
            Eq(0, result.CompetingOrderCount); Yes(result.Order is null);
            var reversed = Products(receipt, order);
            Eq(result.State, reversed.State); Eq(result.Products, reversed.Products);
        }
        var quantity = Products("Кабель", "Кабель", orderQuantity: 2);
        Eq(ProductComparison.Contradiction, quantity.Products); Yes(quantity.Explanation.Contains("Кількість"));
    }

    private static void UnalignedCharacteristics()
    {
        foreach (var (order, receipt) in new[]
        {
            ("Адаптер 12 В розмір 2 м", "Адаптер розмір 2 м 12 В"),
            ("Датчик 12/24 В", "Датчик 24/12 В"),
            ("Кабель 2 м 12", "Кабель 12 2 м"),
            ("Кабель 2 12", "Кабель 12 3"),
            ("Датчик DC-12V", "Датчик DC-24V"), // Unlabelled code fragment is not an aligned voltage field.
            ("Датчик ABC-USB-A", "Датчик ABC-USB-C"),
            ("Датчик ABC-RED", "Датчик ABC-BLACK"),
            ("Адаптер вхід 12 В вихід 24 В", "Адаптер вихід 12 В вхід 24 В"),
            ("Кабель USB-A USB-C", "Кабель USB-A USB-B"),
            ("Адаптер 12 В", "Датчик 24 В"), // No established common product anchor.
            ("Червоний кабель", "Чорний датчик"),
            ("Кабель розмір 2 м", "Кабель розмір 200 см")
        })
        {
            var result = Products(order, receipt);
            Eq(ProductComparison.Insufficient, result.Products); Eq(AutomaticLinkBasis.UniqueAmount, result.Basis);
        }
        // Unrelated identifier namespaces are neither identity nor contradiction evidence.
        var r = R(); var skuOrder = O(name: "Кабель") with { Items = [new("Кабель", "A", 1, 290, 290)] };
        var details = new Dictionary<string, ReceiptDetails> { [r.Id] = new(r.Id, [new("Кабель", "B", 1, 290, 290)]) };
        Eq(ProductComparison.Match, M([r], [skuOrder], details)[r.Id].Products);
        skuOrder = skuOrder with { Items = [new("Кабель живлення", "SAME", 1, 290, 290)] };
        details[r.Id] = new(r.Id, [new("Кабель питания", "SAME", 1, 290, 290)]);
        Eq(ProductComparison.Insufficient, M([r], [skuOrder], details)[r.Id].Products);
    }

    private static void UnknownBasketLines()
    {
        var r = R();
        var order = O() with { Items = [new("Датчик модель X", "", 1, 150, 150), new("Датчик скор.", "", 1, 140, 140)] };
        var details = new Dictionary<string, ReceiptDetails>
        {
            [r.Id] = new(r.Id, [new("Датчик модель Y", "", 1, 150, 150), new("Інший датчик", "", 1, 140, 140)])
        };
        Eq(ProductComparison.Insufficient, M([r], [order], details)[r.Id].Products);
        order = order with { Items = [new("Кабель", "", 2, 145, 290)] };
        details[r.Id] = new(r.Id, [new("Кабель", "", 1, 150, 150), new("Кабель питания", "", 1, 140, 140)]);
        Eq(ProductComparison.Insufficient, M([r], [order], details)[r.Id].Products);
        var competitor = order with { Key = O(2).Key };
        NoAuto(M([r], [order, competitor], details).Values);
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
