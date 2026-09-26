using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class NameMatchingTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("names: equal price and unit count resolve distinct product kinds in MatchAll", Run(DistinctKinds)),
        ("names: USB socket versus LED lighting separates full equal-total baskets, not individual prices", Run(SocketAndLighting)),
        ("names: reordered plain names match without erasing numerical roles or accessory relations", Run(ReorderedNames)),
        ("names: translation abbreviation composite and unknown contenders block arbitrary choice", Run(UnknownContenders)),
        ("names: missing baskets duplicate names manual and fiscal priority remain guarded", Run(Guardrails)),
        ("names: old quantity-only memo is invalidated while cached details remain", Run(CacheVersion))
    ];
    private static Func<Task> Run(Action action) => () => { action(); return Task.CompletedTask; };
    private static readonly DateTimeOffset Time = new(2026, 9, 26, 21, 0, 0, TimeSpan.FromHours(3));
    private static ReceiptRecord Receipt(int n, long cents = 20100) => new()
    { Id = $"d8200000-0000-0000-0000-{n:D12}", Type = "SELL", Status = "DONE", FiscalDate = Time, TotalSumMinor = cents };
    private static MarketplaceOrder Order(int n, string name) => new()
    {
        Key = new(MarketplaceKind.Prom, "name-test-store", n.ToString()), Number = $"TEST-{n}",
        CreatedAt = Time.AddHours(-2), Total = 201, Currency = "UAH",
        Items = [new(name, "", 1, 201, 201)], ItemListComplete = true, ItemsComplete = true
    };
    private static ReceiptDetails Detail(ReceiptRecord r, string name) => new(r.Id, [new(name, "", 1, 201, 201)]) { ItemListComplete = true };
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
    private static void True(bool value) { if (!value) throw new Exception("Name matching assertion failed."); }

    private static void DistinctKinds()
    {
        var receipts = new[] { Receipt(1), Receipt(2) };
        var orders = new[] { Order(1, "Кабель USB"), Order(2, "Датчик руху PIR") };
        var details = new Dictionary<string, ReceiptDetails>
        { [receipts[0].Id] = Detail(receipts[0], "Кабель USB"), [receipts[1].Id] = Detail(receipts[1], "Датчик руху PIR") };
        foreach (var rs in new[] { receipts, receipts.Reverse().ToArray() })
        foreach (var os in new[] { orders, orders.Reverse().ToArray() })
        {
            var result = new ReceiptOrderMatchingService().MatchAll(rs, "name-account", os, [], true, details);
            Console.WriteLine($"  Names201: {result[receipts[0].Id].State}/{result[receipts[1].Id].State}");
            for (var i = 0; i < 2; i++)
            {
                var match = result[receipts[i].Id];
                Equal(ReceiptLinkState.Suggested, match.State); Equal(orders[i].Key, match.Order!.Key);
                Equal(AutomaticLinkBasis.AmountAndProducts, match.Basis);
                True(match.Explanation.Contains("Виключено") && match.CompetingOrderCount == 1);
            }
        }
    }

    private static ReceiptOrderMatch Match(string orderName, string receiptName) => new ReceiptOrderMatchingService().MatchAll(
        [Receipt(1)], "name-account", [Order(1, orderName)], [], true,
        new Dictionary<string, ReceiptDetails> { [Receipt(1).Id] = Detail(Receipt(1), receiptName) })[Receipt(1).Id];

    private static void SocketAndLighting()
    {
        const string socket = "Врізна USB розетка 12–24V на 4 порти з вольтметром (2×USB-C PD + 2×USB-A Fast Charg, 3.1A)";
        const string light = "LED підсвітка багажника 2 м, біла стрічка на кришку багажника, універсальна для авто";
        const string socketRu = "Врезная USB розетка 12-24V на 4 порта с вольтметром (2×USB-C PD + 2×USB-A Fast Charg, 3.1A)";
        const string lightRu = "LED подсветка багажника 2 м, белая лента на крышку багажника, универсальная для автомобиля";
        var matcher = new ReceiptOrderMatchingService();
        foreach (var quantity in new[] { 1, 2 })
        foreach (var translated in new[] { false, true })
        {
            var orders = new[] { Order(1, socket), Order(2, light) }.Select(o => o with
                { Total = 310 * quantity, Items = [new(o.Items[0].Name, "", quantity, 310, 310 * quantity)] }).ToArray();
            var receipts = new[] { Receipt(1, 31000 * quantity), Receipt(2, 31000 * quantity) };
            var details = new Dictionary<string, ReceiptDetails>
            {
                [receipts[0].Id] = new(receipts[0].Id, [new(translated ? socketRu : socket, "", quantity, 310, 310 * quantity)]) { ItemListComplete = true },
                [receipts[1].Id] = new(receipts[1].Id, [new(translated ? lightRu : light, "", quantity, 310, 310 * quantity)]) { ItemListComplete = true }
            };
            foreach (var os in new[] { orders, orders.Reverse().ToArray() })
            foreach (var rs in new[] { receipts, receipts.Reverse().ToArray() })
            {
                var matches = matcher.MatchAll(rs, "name-account", os, [], true, details);
                for (var i = 0; i < 2; i++)
                {
                    var match = matches[receipts[i].Id];
                    Equal(ReceiptLinkState.Suggested, match.State); Equal(orders[i].Key, match.Order!.Key);
                    Equal(translated ? ProductComparison.Insufficient : ProductComparison.Match, match.Products);
                    True(match.Explanation.Contains("Тип товару в назві") && match.Explanation.Contains("освітлення"));
                }
            }
        }

        // Same 355 total, four units and an identical additional line. The name check
        // must compare the remaining 310 products, not merely count rows or units.
        OrderItem[] Basket(string name) => [new(name, "", 1, 310, 310), new("USB-C гнездо 2PIN", "", 3, 15, 45)];
        var mixedOrders = new[] { Order(1, socket) with { Total = 355, Items = Basket(socket) },
            Order(2, light) with { Total = 355, Items = Basket(light) } };
        var r355 = Receipt(1, 35500);
        var mixedDetails = new Dictionary<string, ReceiptDetails>
            { [r355.Id] = new(r355.Id, Basket(light)) { ItemListComplete = true } };
        Equal(mixedOrders[1].Key, matcher.MatchAll([r355], "name-account", mixedOrders, [], true, mixedDetails)[r355.Id].Order!.Key);

        // An order containing a 310 item is not a 310 order. No partial-price linking.
        var r310 = Receipt(1, 31000);
        var differentTotals = new[] { mixedOrders[0] with { Total = 903,
            Items = [new(socket, "", 1, 310, 310), new("Інші позиції", "", 1, 593, 593)] }, mixedOrders[1] };
        var lineDetails = new Dictionary<string, ReceiptDetails>
            { [r310.Id] = new(r310.Id, [new(light, "", 1, 310, 310)]) { ItemListComplete = true } };
        var unmatched = matcher.MatchAll([r310], "name-account", differentTotals, [], true, lineDetails)[r310.Id];
        True(unmatched.Order is null && unmatched.State != ReceiptLinkState.Suggested);
        Equal(ProductComparison.Insufficient, Match(socket, socketRu).Products);
        Equal(ProductComparison.Insufficient, Match("USB розетка", "Автомобильная зарядка USB").Products);
    }

    private static void ReorderedNames()
    {
        foreach (var (a, b) in new[] { ("Червоний кабель USB", "Кабель USB червоний"), ("  КАБЕЛЬ\u00a0USB", "usb кабель") })
        {
            Equal(ProductComparison.Match, Match(a, b).Products);
            Equal(AutomaticLinkBasis.AmountAndProducts, Match(a, b).Basis);
        }
        foreach (var (a, b) in new[]
        {
            ("Cable for sensor", "Sensor for cable"),
            ("INPUT 12 V OUTPUT 24 V", "INPUT 24 V OUTPUT 12 V"),
            ("Вхід USB вихід DC", "Вхід DC вихід USB"),
            ("Датчик MODEL X Y", "Датчик MODEL Y X"),
            ("LEFT USB RIGHT DC", "LEFT DC RIGHT USB"),
            ("Кабель USB-C", "Кабель USB-A")
        }) True(Match(a, b).Products != ProductComparison.Match);
        Equal(ProductComparison.Contradiction, Match("Кабель USB-C", "Кабель USB-A").Products);
        Equal(ProductComparison.Contradiction, Match("Датчик 12 В", "Датчик 24 В").Products);
    }

    private static void UnknownContenders()
    {
        var r = Receipt(1); var matcher = new ReceiptOrderMatchingService();
        foreach (var (candidate, receiptName) in new[]
        {
            ("Кабель живлення", "Кабель питания"), ("Каб. USB", "Кабель USB"),
            ("Cable for sensor", "Датчик"), ("Cable tester", "Датчик"),
            ("Датчик / кабель", "Кабель USB"), ("Набір кабель USB", "Кабель USB"),
            ("Товар X", "Товар Y"), ("Адаптер живлення", "Зарядний пристрій"),
            ("Оболонка для кабелю", "Датчик"), ("Кабель живлення USB", "Кабель USB")
        })
        {
            var orders = new[] { Order(1, candidate), Order(2, receiptName) };
            var match = matcher.MatchAll([r], "name-account", orders, [], true,
                new Dictionary<string, ReceiptDetails> { [r.Id] = Detail(r, receiptName) })[r.Id];
            Equal(ReceiptLinkState.Candidates, match.State);
            True(match.Order is null && match.Ambiguous && match.CompetingOrderCount == 2);
        }
    }

    private static void Guardrails()
    {
        var r = Receipt(1); var cable = Order(1, "Кабель USB"); var sensor = Order(2, "Датчик руху");
        var details = new Dictionary<string, ReceiptDetails> { [r.Id] = Detail(r, "Кабель USB") };
        var matcher = new ReceiptOrderMatchingService();
        foreach (var incomplete in new[] { sensor with { Items = [] }, sensor with { ItemListComplete = false } })
            True(matcher.MatchAll([r], "name-account", [cable, incomplete], [], true, details)[r.Id].Order is null);
        True(matcher.MatchAll([r], "name-account", [cable, sensor], [], true)[r.Id].Order is null);
        True(matcher.MatchAll([r], "name-account", [cable, cable with { Key = sensor.Key }], [], true, details)[r.Id].Order is null);
        Equal(ReceiptLinkState.Incomplete, matcher.MatchAll([r], "name-account", [cable, sensor], [], false, details)[r.Id].State);
        var decision = new ReceiptOrderDecision { AccountContext = "name-account", ReceiptId = r.Id, ConfirmedOrder = sensor.Key };
        Equal(ReceiptLinkState.Manual, matcher.MatchAll([r], "name-account", [cable, sensor], [decision], true, details)[r.Id].State);
        Equal(ReceiptLinkState.Exact, matcher.MatchAll([r], "name-account", [cable, sensor with { ReceiptIds = [r.Id] }], [], true, details)[r.Id].State);
        decision.ConfirmedOrder = null; decision.SuppressAutomatic = true;
        True(matcher.MatchAll([r], "name-account", [cable, sensor], [decision], true, details)[r.Id].Order is null);
        // Known category nouns at the start may have bounded modifiers, but substrings are not nouns.
        Equal(ProductComparison.Contradiction, Match("Быстрая авто зарядка USB", "Датчик руху").Products);
        Equal(ProductComparison.Contradiction, Match("Разъем XT30", "Вимикач").Products);
        Equal(ProductComparison.Insufficient, Match("Кабельний тест", "Датчик").Products);
    }

    private static void CacheVersion()
    {
        var r = Receipt(1); var detail = Detail(r, "Кабель USB");
        var details = new Dictionary<string, ReceiptDetails> { [r.Id] = detail };
        var orders = new[] { Order(1, "Кабель USB"), Order(2, "Датчик руху") };
        var cache = new AutomaticMatchCache(); var service = new CachedReceiptOrderMatchingService();
        service.RememberDetails(cache, "name-account", r, detail);
        service.MatchAll(cache, [r], "name-account", orders, [], true, details);
        cache.Snapshots = cache.Snapshots.Select(s => s with { AlgorithmVersion = "same-kyiv-day-product-quantity-v2" }).ToList();
        True(!service.TryRestore(cache, [r], "name-account", orders, [], out _, details));
        True(service.TryGetDetails(cache, "name-account", r, out _));
        var result = service.MatchAll(cache, [r], "name-account", orders, [], true, details);
        True(!result.CacheHit); Equal(AutomaticLinkBasis.AmountAndProducts, result.Matches[r.Id].Basis);
    }
}
