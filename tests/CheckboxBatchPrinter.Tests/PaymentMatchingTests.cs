using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class PaymentMatchingTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("payments: product names resolve charger versus switch before payment is considered", Run(NamesBeforePayment)),
        ("names: complete surface mount housing accessory and mixed-kit names compare without category dictionary", Run(HousingAndMounting)),
        ("payments: exact different names precede payment while missing baskets remain competitors", Run(PaymentNeedsIdenticalBaskets)),
        ("payments: equal amount and quantity Prom orders resolve NovaPay versus Prom-payment receipts", Run(PromPaymentPair)),
        ("payments: explicit Ukrainian Russian and case variants split identical complete baskets", Run(Variants)),
        ("payments: generic custom unsupported and missing methods remain competing graph nodes", Run(UnknownMethods)),
        ("payments: mixed partial negative unknown cash and overflow receipt payments stay uncertain", Run(PartialPayments)),
        ("payments: amount day coverage products manual fiscal and rejected decisions retain priority", Run(Guardrails)),
        ("payments: duplicate payments remain ambiguous and changed payment invalidates cached conclusions", Run(CacheAndCompetition))
    ];
    private static Func<Task> Run(Action test) => () => { test(); return Task.CompletedTask; };
    private static readonly DateTimeOffset Day = new(2026, 10, 1, 16, 0, 0, TimeSpan.FromHours(3));
    private static ReceiptRecord Receipt(int n, string label, params ReceiptPayment[] extra) => new()
    {
        Id = $"e7200000-0000-0000-0000-{n:D12}", Type = "SELL", Status = "DONE", FiscalDate = Day,
        TotalSumMinor = 28000, Payments = [new("CASHLESS", label, 28000), .. extra]
    };
    private static MarketplaceOrder Order(int n, string payment) => new()
    {
        Key = new(MarketplaceKind.Prom, "synthetic-payment-store", n.ToString()), Number = $"TEST-PAYMENT-{n}",
        CreatedAt = Day.AddHours(-1), Total = 280, Currency = "UAH", PaymentMethod = payment,
        DeliveryMethod = "Нова Пошта", Shipments = [new("nova_poshta", "")],
        Items = [new("Запасна кришка", "", 1, 280, 280)], ItemsComplete = true, ItemListComplete = true
    };
    private static Dictionary<string, ReceiptDetails> Details(ReceiptRecord[] receipts, MarketplaceOrder[] orders) => receipts.Select((r, i) =>
        new ReceiptDetails(r.Id, orders[i].Items) { ItemListComplete = true }).ToDictionary(d => d.Id);
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private const string Charger = "Накладна USB зарядка 12–24V на 4 порти, 2 USB-A + 2 Type-C PD, швидка зарядка для авто";
    private const string BatterySwitch = "Вимикач маси АКБ 12/24V 500A відсікач мінуса , Battery Disconnect Switch, прозорий корпус";
    private static void NamesBeforePayment()
    {
        var orders = new[] { Order(1, "Післяплата") with { Items = [new(Charger, "", 1, 280, 280)] },
            Order(2, "Пром-оплата") with { Items = [new(BatterySwitch, "", 1, 280, 280)] } };
        foreach (var labels in new[] { new[] { "", "" }, new[] { "Платіж NovaPay", "Платіж RozetkaPay" }, new[] { "Платіж RozetkaPay", "Платіж NovaPay" } })
        {
            var receipts = new[] { Receipt(1, labels[0]), Receipt(2, labels[1]) };
            var details = Details(receipts, orders);
            foreach (var rs in new[] { receipts, receipts.Reverse().ToArray() })
            foreach (var os in new[] { orders, orders.Reverse().ToArray() })
            {
                var result = Match(rs, os, details);
                for (var i = 0; i < 2; i++)
                {
                    Equal(orders[i].Key, result[receipts[i].Id].Order?.Key);
                    Equal(AutomaticLinkBasis.AmountAndProducts, result[receipts[i].Id].Basis);
                }
            }
        }
    }

    private static void PaymentNeedsIdenticalBaskets()
    {
        var receipts = new[] { Receipt(1, "Платіж NovaPay"), Receipt(2, "Платіж RozetkaPay") };
        var orders = new[] { Order(1, "Післяплата") with { Items = [new("Запасна кришка", "", 1, 280, 280)] },
            Order(2, "Пром-оплата") with { Items = [new("Кухонний рушник", "", 1, 280, 280)] } };
        Check(Match(receipts, orders).Values.All(m => m.Order is null), "Payment replaced missing baskets.");
        var distinctNames = Match(receipts, orders, Details(receipts, orders));
        for (var i = 0; i < 2; i++)
        {
            Equal(orders[i].Key, distinctNames[receipts[i].Id].Order!.Key);
            Equal(AutomaticLinkBasis.AmountAndProducts, distinctNames[receipts[i].Id].Basis);
        }
        var identical = new[] { Order(1, "Післяплата"), Order(2, "Пром-оплата") };
        var complete = Details(receipts, identical);
        var unknownCompetitor = Order(3, "Післяплата") with { Items = [], ItemListComplete = false, ItemsComplete = false };
        Check(Match(receipts, [.. identical, unknownCompetitor], complete).Values.All(m => m.Order is null), "Unknown competitor was ignored by the payment tie-break.");
        var loading = new ReceiptOrderMatchingService().MatchAll(receipts, "payment-test-account", identical, [], true, complete,
            loading: new HashSet<string> { receipts[0].Id });
        Check(loading.Values.All(m => m.Order is null), "Payment was used before product loading finished.");
    }

    private static void HousingAndMounting()
    {
        var receipts = new[] { Receipt(1, ""), Receipt(2, "") };
        foreach (var charger in new[] { "Накладна USB зарядка", "Накладной USB разъем", "Surface-mount USB charger" })
        foreach (var name in new[] { "Вимикач маси, прозорий корпус", "Выключатель массы, прозрачный корпус", "Switch, transparent case", "Switch, black plastic case" })
        {
            var orders = new[] { Order(1, "") with { Items = [new(charger, "", 1, 280, 280)] },
                Order(2, "") with { Items = [new(name, "", 1, 280, 280)] } };
            var result = Match(receipts, orders, Details(receipts, orders));
            for (var i = 0; i < 2; i++) Equal(orders[i].Key, result[receipts[i].Id].Order?.Key);
        }
        foreach (var uncertain in new[] { "Корпус для вимикача", "Вимикач, корпус", "Вимикач та прозорий корпус",
            "Вимикач, прозорий корпус і зарядка", "Комплект: вимикач, прозорий корпус", "Накладна панель для USB зарядки",
            "Switch, transparent case holder", "Case for switch", "Невідомий вимикач, прозорий корпус" })
        {
            var orders = new[] { Order(1, "") with { Items = [new(Charger, "", 1, 280, 280)] },
                Order(2, "") with { Items = [new(uncertain, "", 1, 280, 280)] } };
            var result = Match(receipts, orders, Details(receipts, orders));
            for (var i = 0; i < 2; i++)
            {
                Equal(orders[i].Key, result[receipts[i].Id].Order!.Key);
                Equal(AutomaticLinkBasis.AmountAndProducts, result[receipts[i].Id].Basis);
            }
        }
    }
    private static void PromPaymentPair()
    {
        var receipts = new[] { Receipt(1, "Платіж NovaPay"), Receipt(2, "Платіж RozetkaPay") };
        var orders = new[] { Order(1, "Наложенный платеж"), Order(2, "Пром-оплата") };
        var details = Details(receipts, orders);
        foreach (var rs in new[] { receipts, receipts.Reverse().ToArray() })
        foreach (var os in new[] { orders, orders.Reverse().ToArray() })
        {
            var result = new ReceiptOrderMatchingService().MatchAll(rs, "payment-test-account", os, [], true, details);
            Console.WriteLine($"  Payment280: {result[receipts[0].Id].State}/{result[receipts[1].Id].State}");
            for (var i = 0; i < 2; i++)
            {
                Equal(ReceiptLinkState.Suggested, result[receipts[i].Id].State);
                Equal(orders[i].Key, result[receipts[i].Id].Order!.Key);
                Check(!result[receipts[i].Id].Ambiguous, "Payment pair remained ambiguous.");
                Equal(AutomaticLinkBasis.AmountAndPayment, result[receipts[i].Id].Basis);
                Equal(PaymentComparison.Match, result[receipts[i].Id].Payments);
                Equal("Автозв’язок: сума й оплата", result[receipts[i].Id].StatusLabel);
                Check(result[receipts[i].Id].Explanation.Contains("Оплата відрізняється") && result[receipts[i].Id].Explanation.Contains("ймовірний"), "Payment rationale missing.");
            }
        }
    }

    private static IReadOnlyDictionary<string, ReceiptOrderMatch> Match(ReceiptRecord[] receipts, MarketplaceOrder[] orders,
        IReadOnlyDictionary<string, ReceiptDetails>? details = null, bool complete = true, IReadOnlyList<ReceiptOrderDecision>? decisions = null) =>
        new ReceiptOrderMatchingService().MatchAll(receipts, "payment-test-account", orders, decisions ?? [], complete, details);

    private static void Variants()
    {
        foreach (var cod in new[] { "Післяплата", "Накладений платіж", "Наложенный платёж", "  НАЛОЖЕННЫЙ\u00a0ПЛАТЕЖ ", "NovaPay" })
        foreach (var online in new[] { "Пром-оплата", "Пром оплата", "Промоплата", "RozetkaPay" })
        {
            var receipts = new[] { Receipt(1, " платіж NOVAPAY "), Receipt(2, "ПЛАТЕЖ rozetkapay") };
            var orders = new[] { Order(1, cod), Order(2, online) };
            var result = Match(receipts, orders, Details(receipts, orders));
            Equal(orders[0].Key, result[receipts[0].Id].Order!.Key); Equal(orders[1].Key, result[receipts[1].Id].Order!.Key);
            Equal(ProductComparison.Match, result[receipts[0].Id].Products);
        }
        var row = Receipt(1, "Платіж NovaPay");
        var sameOrders = new[] { Order(1, "Пром-оплата"), Order(2, "Пром-оплата") };
        var mismatch = Match([row], sameOrders, Details([row], [sameOrders[0]])).Values.Single();
        Equal(PaymentComparison.Contradiction, mismatch.Payments); Equal(ProductComparison.Insufficient, mismatch.Products);
        Equal("Сума збігається, оплата відрізняється", mismatch.StatusLabel);
    }

    private static void UnknownMethods()
    {
        var receipts = new[] { Receipt(1, "Платіж NovaPay"), Receipt(2, "Платіж RozetkaPay") };
        var cod = Order(1, "Наложенный платеж"); var online = Order(2, "Пром-оплата");
        foreach (var unknown in new[]
        {
            cod with { PaymentMethod = "" }, cod with { PaymentMethod = "Картка" }, cod with { PaymentMethod = "Безготівковий платіж" },
            cod with { PaymentMethod = "Пром-оплата або NovaPay" }, cod with { PaymentMethod = "Передплата" },
            cod with { PaymentMethod = "Оплата на рахунок" }, cod with { PaymentMethod = "NovaPay тест" },
            cod with { Shipments = [], DeliveryMethod = "" }, cod with { Shipments = [new("rozetka_delivery", "")], DeliveryMethod = "Rozetka Delivery" },
            cod with { Shipments = [new("nova_poshta", "")], DeliveryMethod = "Rozetka Delivery" },
            cod with { Shipments = [new("nova_poshta", ""), new("unknown", "")] }
        })
            Check(Match(receipts, [unknown, online], Details(receipts, [unknown, online])).Values.All(m => m.Order is null), "Unknown order payment was dropped from the graph.");
        var rozetka = cod with { Key = cod.Key with { Marketplace = MarketplaceKind.Rozetka } };
        var promOnly = Match(receipts, [rozetka, online], Details(receipts, [rozetka, online]));
        Check(promOnly.Values.All(m => m.Order?.Key.Marketplace != MarketplaceKind.Rozetka),
            "Rozetka payment entered the Prom-only heuristic graph.");
        Equal(online.Key, promOnly[receipts[1].Id].Order!.Key);
        foreach (var label in new[] { "", "Картка", "Безготівкова оплата", "Платіж", "NovaPay або RozetkaPay", "SuperNovaPay" })
            Check(Match([Receipt(1, label), receipts[1]], [cod, online], Details(receipts, [cod, online])).Values.All(m => m.Order is null), "Unknown receipt payment was dropped.");
    }

    private static ReceiptRecord WithPayments(ReceiptRecord r, params ReceiptPayment[] payments) => new()
    { Id = r.Id, Type = r.Type, Status = r.Status, FiscalDate = r.FiscalDate, TotalSumMinor = r.TotalSumMinor, Payments = payments };
    private static void PartialPayments()
    {
        var first = Receipt(1, "Платіж NovaPay"); var second = Receipt(2, "Платіж RozetkaPay");
        var orders = new[] { Order(1, "Післяплата"), Order(2, "Пром-оплата") };
        foreach (var uncertain in new[]
        {
            WithPayments(first), WithPayments(first, new ReceiptPayment("CASHLESS", "Платіж NovaPay", 14000)),
            WithPayments(first, new("CASHLESS", "Платіж NovaPay", 14000), new("CASHLESS", "Платіж RozetkaPay", 14000)),
            WithPayments(first, new("CASHLESS", "Платіж NovaPay", 28000), new("CASHLESS", "Платіж NovaPay", 0)),
            WithPayments(first, new("CASHLESS", "Платіж NovaPay", 30000), new("CASHLESS", "Платіж NovaPay", -2000)),
            WithPayments(first, new ReceiptPayment("CASH", "Платіж NovaPay", 28000)),
            WithPayments(first, new("CASHLESS", "Платіж NovaPay", 14000), new("CASHLESS", "Картка", 14000)),
            WithPayments(first, new("CASHLESS", "Платіж NovaPay", long.MaxValue), new("CASHLESS", "Платіж NovaPay", 1))
        }) Check(Match([uncertain, second], orders, Details([first, second], orders)).Values.All(m => m.Order is null), "Unreliable payment removed a competitor.");
        var split = WithPayments(first, new("CASHLESS", "NovaPay", 10000), new("CASHLESS", "Платіж NovaPay", 18000));
        Equal(orders[0].Key, Match([split, second], orders, Details([first, second], orders))[first.Id].Order!.Key);
    }

    private static void Guardrails()
    {
        var receipts = new[] { Receipt(1, "Платіж NovaPay"), Receipt(2, "Платіж RozetkaPay") };
        var orders = new[] { Order(1, "Післяплата"), Order(2, "Пром-оплата") };
        Check(Match(receipts, orders, Details(receipts, orders), complete: false).Values.All(m => m.Order is null), "Incomplete coverage accepted.");
        Equal(ReceiptLinkState.NotFound, Match([receipts[0]], [orders[0] with { Total = 290 }]).Values.Single().State);
        Equal(ReceiptLinkState.NotFound, Match([receipts[0]], [orders[0] with { CreatedAt = Day.AddDays(-1) }]).Values.Single().State);
        var conflict = Match([receipts[0]], [orders[0] with { Items = [new("Вимикач 12 В", "", 1, 280, 280)] }],
            new Dictionary<string, ReceiptDetails> { [receipts[0].Id] = new(receipts[0].Id, [new("Вимикач 24 В", "", 1, 280, 280)]) { ItemListComplete = true } }).Values.Single();
        Check(conflict.Order is null && conflict.Products == ProductComparison.Contradiction, "Payment overrode a product contradiction.");
        var manual = new ReceiptOrderDecision { AccountContext = "payment-test-account", ReceiptId = receipts[0].Id, ConfirmedOrder = orders[1].Key };
        Equal(ReceiptLinkState.Manual, Match(receipts, orders, Details(receipts, orders), decisions: [manual])[receipts[0].Id].State);
        Equal(orders[1].Key, Match(receipts, orders, Details(receipts, orders), decisions: [manual])[receipts[0].Id].Order!.Key);
        var exact = orders[1] with { ReceiptIds = [receipts[0].Id] };
        Equal(ReceiptLinkState.Exact, Match(receipts, [orders[0], exact], Details(receipts, orders))[receipts[0].Id].State);
        var rejected = new ReceiptOrderDecision { AccountContext = "payment-test-account", ReceiptId = receipts[0].Id, RejectedOrders = [orders[0].Key] };
        Check(Match(receipts, orders, Details(receipts, orders), decisions: [rejected])[receipts[0].Id].Order is null, "Rejected pair restored.");
        var suppressed = new ReceiptOrderDecision { AccountContext = "payment-test-account", ReceiptId = receipts[0].Id, SuppressAutomatic = true };
        Check(Match(receipts, orders, Details(receipts, orders), decisions: [suppressed])[receipts[0].Id].Order is null, "Suppressed pair restored.");
        var issue = orders[0] with { AmountComparisonIssue = "Передоплата: неповна сума." };
        Check(Match(receipts, [issue, orders[1]], Details(receipts, orders)).Values.All(m => m.Order is null), "Adjusted total was used for exclusion.");
    }

    private static void CacheAndCompetition()
    {
        var receipts = new[] { Receipt(1, "Платіж NovaPay"), Receipt(2, "Платіж RozetkaPay") };
        var orders = new[] { Order(1, "Післяплата"), Order(2, "Пром-оплата") };
        var same = orders[0] with { Key = orders[0].Key with { OrderId = "3" }, Buyer = new("Synthetic other buyer") };
        var result = Match(receipts, [.. orders, same], Details(receipts, orders));
        Check(result[receipts[0].Id].Order is null && result[receipts[0].Id].Ambiguous, "Equal payment broke buyer ambiguity.");
        Equal(orders[1].Key, result[receipts[1].Id].Order!.Key);
        var service = new CachedReceiptOrderMatchingService(); var cache = new AutomaticMatchCache();
        var details = Details(receipts, orders);
        foreach (var r in receipts) service.RememberDetails(cache, "payment-test-account", r, details[r.Id]);
        service.MatchAll(cache, receipts, "payment-test-account", orders, [], true, details);
        var json = System.Text.Json.JsonSerializer.Serialize(cache);
        var restarted = System.Text.Json.JsonSerializer.Deserialize<AutomaticMatchCache>(json)!;
        Check(service.TryRestore(restarted, receipts, "payment-test-account", orders, [], out var restored, details), "Payment cache missed after restart.");
        Equal(PaymentComparison.Match, restored[receipts[0].Id].Payments); Equal(AutomaticLinkBasis.AmountAndPayment, restored[receipts[0].Id].Basis);
        Check(!service.TryRestore(restarted, receipts, "payment-test-account", [.. orders, same], [], out _, details), "Hidden payment competitor reused old graph.");
        foreach (var oldVersion in new[] { "same-kyiv-day-product-names-v4", "same-kyiv-day-products-payment-v5", "same-kyiv-day-products-first-payment-v6" })
        {
            cache.Snapshots = cache.Snapshots.Select(s => s with { AlgorithmVersion = oldVersion }).ToList();
            Check(!service.TryRestore(cache, receipts, "payment-test-account", orders, [], out _, details), "Old graph was replayed: " + oldVersion);
        }
        Check(receipts.All(r => service.TryGetDetails(cache, "payment-test-account", r, out _)), "Cached details lost on version bump.");
        var singleCache = new AutomaticMatchCache();
        service.MatchAll(singleCache, [receipts[0]], "payment-test-account", [orders[0]], [], true);
        var changed = orders[0] with { PaymentMethod = "Картка" };
        Check(service.RestoreKnownSuggestions(singleCache, [receipts[0]], "payment-test-account", [changed], []).Count == 0,
            "Partial restore ignored changed order payment fingerprint.");
    }
}
