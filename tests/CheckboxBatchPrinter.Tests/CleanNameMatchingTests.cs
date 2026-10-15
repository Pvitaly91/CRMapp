using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class CleanNameMatchingTests
{
    private const string Account = "clean-name-synthetic-account";
    private static readonly DateTimeOffset Day = new(2026, 10, 1, 16, 0, 0, TimeSpan.FromHours(3));
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("clean names: 50 units of XT60H-F match apostrophe versus question mark for Prom only", Run(FiftyUnits)),
        ("clean names: symbols are removed but letters digits word order and numeric boundaries survive", Run(SymbolsAndBoundaries)),
        ("clean names: ohm variants match a full basket without changing source text or bypassing competitors and v14 cache invalidation", Run(OhmBasketAndCache)),
        ("clean names: every cleaned name keeps its own aggregated quantity", Run(CompleteBasket)),
        ("clean names: normalization collisions stay ambiguous regardless of input order", Run(Collisions)),
        ("clean names: empty cleaned names and incomplete baskets preserve competitors", Run(InsufficientEvidence)),
        ("clean names: payment only separates otherwise identical cleaned baskets", Run(PaymentTieBreak)),
        ("clean names: v7 graphs expire while details and explicit decisions survive", Run(CacheMigration))
    ];
    private static Func<Task> Run(Action test) => () => { test(); return Task.CompletedTask; };
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
    private static ReceiptRecord Receipt(int n = 1, long total = 65000) => new()
    { Id = $"e7500000-0000-0000-0000-{n:D12}", Type = "SELL", Status = "DONE", FiscalDate = Day, TotalSumMinor = total };
    private static MarketplaceOrder Order(int n, string name, MarketplaceKind marketplace = MarketplaceKind.Prom) => new()
    {
        Key = new(marketplace, "clean-name-synthetic-shop", n.ToString()), Number = $"SYNTHETIC-{n}",
        CreatedAt = Day.AddHours(-1), Total = 650, Currency = "UAH", ItemListComplete = true, ItemsComplete = true,
        Items = [new(name, "marketplace-code", 50, 13, 650)]
    };
    private static ReceiptDetails Detail(ReceiptRecord receipt, params OrderItem[] items) => new(receipt.Id, items) { ItemListComplete = true };
    private static IReadOnlyDictionary<string, ReceiptOrderMatch> Match(ReceiptRecord[] receipts, MarketplaceOrder[] orders,
        IReadOnlyDictionary<string, ReceiptDetails> details) => new ReceiptOrderMatchingService().MatchAll(receipts, Account, orders, [], true, details);
    private static ReceiptOrderMatch Compare(string left, string right)
    {
        var receipt = Receipt(); var order = Order(1, left);
        return Match([receipt], [order], new Dictionary<string, ReceiptDetails>
            { [receipt.Id] = Detail(receipt, new OrderItem(right, "receipt-code", 50, 13, 650)) })[receipt.Id];
    }

    private static void FiftyUnits()
    {
        foreach (var marketplace in new[] { MarketplaceKind.Prom, MarketplaceKind.Rozetka })
        {
            var receipt = Receipt(); var order = Order(1, "Роз’єм XT60H-F, силовий", marketplace);
            var details = new Dictionary<string, ReceiptDetails> { [receipt.Id] =
                Detail(receipt, new OrderItem("Роз?єм XT60H-F, силовий", "different-code", 50, 13, 650)) };
            var result = Match([receipt], [order], details)[receipt.Id];
            if (marketplace == MarketplaceKind.Rozetka)
            {
                Check(result.Order is null && result.State != ReceiptLinkState.Suggested,
                    "Rozetka must not link through cleaned product names.");
                Equal(0, result.Candidates.Count); Equal(AutomaticLinkBasis.None, result.Basis);
                continue;
            }
            Equal(order.Key, result.Order?.Key); Equal(ReceiptLinkState.Suggested, result.State);
            Equal(ProductComparison.Match, result.Products); Equal(AutomaticLinkBasis.AmountAndProducts, result.Basis);
        }
    }

    private static void SymbolsAndBoundaries()
    {
        foreach (var (left, right) in new[]
        {
            ("Роз’єм XT60H-F", "  РОЗ?ЄМ\tXT60HF  "), ("Розʼєм XT60", "Роз?єм XT60"), ("Набір «Весна»", "Набір Весна"),
            ("Роз\u02bcєм XT60", "Роз\u02bbєм XT60"), ("Йодована сіль", "И\u0306одована сіль"),
            ("Товар \U00010428", "ТОВАР \U00010400"),
            ("Зарядка 2×USB", "Зарядка 2?USB"), ("Перехідник USB→DC", "Перехідник USB?DC"),
            ("Товар©®™★⚡🔌 A+B_(C)/D\\E", "Товар ABCDE"),
            ("Кабель 1.5 мм", "Кабель 1?5 мм"), ("Зарядка 12–24V", "Зарядка 12/24V"),
            ("Модуль 4/8Ω", "Модуль 4/8?"), ("Модуль 4/8Ω", "Модуль 4/8?"), ("Модуль 4/8ω", "Модуль 4/8")
        })
        {
            Equal(ProductComparison.Match, Compare(left, right).Products);
            Equal(ProductComparison.Match, Compare(right, left).Products);
        }
        foreach (var (left, right) in new[]
        {
            ("Кабель 1.5 мм", "Кабель 15 мм"), ("Зарядка 12/24V", "Зарядка 1224V"),
            ("Модуль 4Ω", "Модуль 8?"), ("Модуль 4Ω8", "Модуль 48"), ("Кабель USB", "Кабель USВ"),
            ("Модуль α", "Модуль β"), ("Модуль ΩA", "Модуль ΩB"),
            ("Вимикач 12V", "Вимикач 24V"), ("Роз’єм XT60H-F", "Роз’єм XT60H-M"),
            ("Кабель живлення", "Кабель питания"), ("Червоний кабель USB", "Кабель USB червоний"),
            ("Кабель USB", "Кабель USB подовжувач"), ("Кабель USB", "Кабель US?"),
            ("AB C", "A BC"), ("Йодована сіль", "Иодована сіль")
        })
        {
            var result = Compare(left, right);
            Equal(ProductComparison.Contradiction, result.Products); Check(result.Order is null, "Different cleaned names were linked.");
        }
    }

    private static void OhmBasketAndCache()
    {
        var receipt = Receipt(total: 27100);
        var order = Order(1, "Модуль аудіопідсилювача PAM8403 5V USB, 3W+3W для динаміків 4/8Ω") with
        {
            Total = 271,
            Items = [new("Модуль аудіопідсилювача PAM8403 5V USB, 3W+3W для динаміків 4/8Ω", "a", 2, 65.5m, 131),
                new("USB-C (Type-C) панельний роз’єм", "b", 4, 5, 20), new("Модуль драйвера LED 10W", "c", 3, 40, 120)]
        };
        var detail = Detail(receipt, order.Items.Select(item => item with
            { Name = item.Name.Replace('Ω', '?').Replace('’', '?'), Sku = "receipt-" + item.Sku }).ToArray());
        var details = new Dictionary<string, ReceiptDetails> { [receipt.Id] = detail };
        var originalOrderName = order.Items[0].Name;
        var originalReceiptName = detail.Items[0].Name;
        foreach (var marketplace in new[] { MarketplaceKind.Prom, MarketplaceKind.Rozetka })
        {
            var current = order with { Key = order.Key with { Marketplace = marketplace } };
            var result = Match([receipt], [current], details)[receipt.Id];
            if (marketplace == MarketplaceKind.Rozetka)
            {
                Check(result.Order is null && result.State != ReceiptLinkState.Suggested,
                    "Normalizing unit symbols must not enable Rozetka basket linking.");
                Equal(0, result.Candidates.Count); Equal(AutomaticLinkBasis.None, result.Basis);
                continue;
            }
            Equal(current.Key, result.Order?.Key); Equal(ProductComparison.Match, result.Products);
            Equal(AutomaticLinkBasis.AmountAndProducts, result.Basis);
            var changedModel = current with { Items = [current.Items[0] with { Name = current.Items[0].Name.Replace("PAM8403", "PAM8406") }, .. current.Items.Skip(1)] };
            Equal(ProductComparison.Contradiction, Match([receipt], [changedModel], details)[receipt.Id].Products);
            var changedQuantity = current with { Items = [current.Items[0] with { Quantity = 1 }, .. current.Items.Skip(1)] };
            Equal(ProductComparison.Contradiction, Match([receipt], [changedQuantity], details)[receipt.Id].Products);
        }
        foreach (var orders in new[]
        {
            new[] { order, order with { Key = order.Key with { OrderId = "duplicate-ohm" }, Items = detail.Items } },
            new[] { order with { Key = order.Key with { OrderId = "duplicate-ohm" }, Items = detail.Items }, order }
        })
        {
            var ambiguous = Match([receipt], orders, details)[receipt.Id];
            Check(ambiguous.Order is null && ambiguous.Ambiguous, "Removing a unit symbol must not choose between otherwise equal orders.");
        }

        var service = new CachedReceiptOrderMatchingService(); var cache = new AutomaticMatchCache();
        service.RememberDetails(cache, Account, receipt, detail);
        service.MatchAll(cache, [receipt], Account, [order], [], true, details);
        cache.Snapshots = cache.Snapshots.Select(snapshot => snapshot with
            { AlgorithmVersion = "same-kyiv-day-active-unlinked-verified-discounts-v14" }).ToList();
        Check(!service.TryRestore(cache, [receipt], Account, [order], [], out _, details), "An old symbol-sensitive graph survived the new cleaning policy.");
        Check(service.TryGetDetails(cache, Account, receipt, out var cached) && cached.Items[0].Name == originalReceiptName,
            "Changing the comparison policy must reuse original cached details.");
        Equal(order.Key, service.RestoreKnownSuggestions(cache, [receipt], Account, [order], [], details)[receipt.Id].Order?.Key);
        cache.Links.Clear(); // A previously unmatched receipt has no durable automatic pair.
        var recomputed = service.MatchAll(cache, [receipt], Account, [order], [], true, details);
        Equal(order.Key, recomputed.Matches[receipt.Id].Order?.Key);
        Check(!recomputed.CacheHit, "The previously unmatched graph was not recomputed.");
        Equal(originalOrderName, order.Items[0].Name); Equal(originalReceiptName, detail.Items[0].Name);
    }

    private static void CompleteBasket()
    {
        var receipt = Receipt(); var order = Order(1, "Роз’єм XT60H-F") with
        { Items = [new("Роз’єм XT60H-F", "a", 40, 13, 520), new("Корпус «Чорний»", "b", 10, 13, 130)] };
        var details = new Dictionary<string, ReceiptDetails> { [receipt.Id] = Detail(receipt,
            new("Корпус Чорний", "x", 10, 13, 130), new("Роз?єм XT60HF", "y", 25, 13, 325), new("Розєм XT60H-F", "z", 15, 13, 195)) };
        Equal(order.Key, Match([receipt], [order], details)[receipt.Id].Order?.Key);
        var wrongQuantities = order with { Items = [order.Items[0] with { Quantity = 39, Total = 507 }, order.Items[1] with { Quantity = 11, Total = 143 }] };
        var mismatch = Match([receipt], [wrongQuantities], details)[receipt.Id];
        Equal(ProductComparison.Contradiction, mismatch.Products); Check(mismatch.Order is null, "Same total unit count hid per-name quantity mismatch.");
        var wrongName = order with { Items = [order.Items[0], order.Items[1] with { Name = "Корпус Білий" }] };
        Equal(ProductComparison.Contradiction, Match([receipt], [wrongName], details)[receipt.Id].Products);
        var oneUnit = Order(1, "Роз’єм XT60H-F") with { Items = [new("Роз’єм XT60H-F", "", 1, 650, 650)] };
        var fiftyDetails = new Dictionary<string, ReceiptDetails> { [receipt.Id] = Detail(receipt, new OrderItem("Роз?єм XT60HF", "", 50, 13, 650)) };
        Equal(ProductComparison.Contradiction, Match([receipt], [oneUnit], fiftyDetails)[receipt.Id].Products);
    }

    private static void Collisions()
    {
        var orders = new[] { Order(1, "Роз’єм XT60H-F"), Order(2, "Роз?єм XT60HF") with { Buyer = new("Synthetic other buyer") } };
        var receipts = new[] { Receipt(1), Receipt(2) };
        var details = receipts.ToDictionary(r => r.Id, r => Detail(r, new OrderItem("Розєм XT60HF", "", 50, 13, 650)));
        foreach (var os in new[] { orders, orders.Reverse().ToArray() })
        foreach (var rs in new[] { receipts, receipts.Reverse().ToArray(), receipts.Take(1).ToArray() })
        {
            var results = Match(rs, os, details);
            Check(results.Values.All(r => r.Order is null && r.Ambiguous), "Cleaning collision was allocated greedily.");
            Check(results.Values.All(r => r.CompetingOrderCount == 2), "Cleaning discarded a same-name competitor.");
        }
    }

    private static void InsufficientEvidence()
    {
        var receipt = Receipt(); var good = Order(1, "Роз’єм XT60H-F"); var other = Order(2, "Інший товар");
        var details = new Dictionary<string, ReceiptDetails> { [receipt.Id] = Detail(receipt, good.Items.ToArray()) };
        foreach (var insufficient in new[]
        {
            other with { Items = [other.Items[0] with { Name = " ? - © 🔌 " }] },
            other with { Items = [other.Items[0] with { Name = "" }] },
            other with { ItemListComplete = false },
            other with { Items = [other.Items[0] with { Name = good.Items[0].Name, Quantity = null, Total = null, UnitPrice = null }] }
        })
        {
            var result = Match([receipt], [good, insufficient], details)[receipt.Id];
            Check(result.Order is null && result.Ambiguous, "Unknown cleaned evidence was removed from the graph.");
        }
        details[receipt.Id] = Detail(receipt, new OrderItem("???", "", 50, 13, 650));
        var missingReceipt = Match([receipt], [good, other], details)[receipt.Id];
        Check(missingReceipt.Order is null && missingReceipt.Ambiguous, "Empty receipt name forced a product match or contradiction.");
        Equal(ProductComparison.Insufficient, missingReceipt.Products);
        Equal(ProductComparison.Insufficient, Compare("???", "!!!").Products);
        Equal(ProductComparison.Match, Compare("650", "650").Products);
    }

    private static void PaymentTieBreak()
    {
        var cod = Order(1, "Роз’єм XT60H-F") with { PaymentMethod = "Післяплата", DeliveryMethod = "Нова Пошта", Shipments = [new("nova_poshta", "")] };
        var online = Order(2, "Роз?єм XT60HF") with { PaymentMethod = "Пром-оплата" };
        var receipts = new[]
        {
            new ReceiptRecord { Id = Receipt(1).Id, Type = "SELL", Status = "DONE", FiscalDate = Day, TotalSumMinor = 65000, Payments = [new("CASHLESS", "Платіж NovaPay", 65000)] },
            new ReceiptRecord { Id = Receipt(2).Id, Type = "SELL", Status = "DONE", FiscalDate = Day, TotalSumMinor = 65000, Payments = [new("CASHLESS", "Платіж RozetkaPay", 65000)] }
        };
        var details = receipts.ToDictionary(r => r.Id, r => Detail(r, new OrderItem("Розєм XT60HF", "", 50, 13, 650)));
        var results = Match(receipts, [cod, online], details);
        Equal(cod.Key, results[receipts[0].Id].Order?.Key); Equal(online.Key, results[receipts[1].Id].Order?.Key);
        Check(results.Values.All(r => r.Basis == AutomaticLinkBasis.AmountAndPayment), "Payment failed to split identical cleaned baskets.");
        online = online with { Items = [online.Items[0] with { Name = "Корпус Чорний" }] };
        details[receipts[1].Id] = Detail(receipts[1], online.Items.ToArray());
        results = Match(receipts, [cod, online], details);
        Check(results.Values.All(r => r.Basis == AutomaticLinkBasis.AmountAndProducts), "Payment displaced full cleaned product evidence.");
    }

    private static void CacheMigration()
    {
        var receipt = Receipt(); var order = Order(1, "Роз’єм XT60H-F");
        var details = new Dictionary<string, ReceiptDetails> { [receipt.Id] = Detail(receipt, new OrderItem("Роз?єм XT60HF", "", 50, 13, 650)) };
        var service = new CachedReceiptOrderMatchingService(); var cache = new AutomaticMatchCache();
        service.RememberDetails(cache, Account, receipt, details[receipt.Id]);
        service.MatchAll(cache, [receipt], Account, [order], [], true, details);
        cache.Snapshots = cache.Snapshots.Select(s => s with { AlgorithmVersion = "same-kyiv-day-full-names-payment-v7" }).ToList();
        Check(!service.TryRestore(cache, [receipt], Account, [order], [], out _, details), "Strict v7 graph survived cleaned-name policy change.");
        Check(service.TryGetDetails(cache, Account, receipt, out _), "Version migration lost reusable details.");
        var recomputed = service.MatchAll(cache, [receipt], Account, [order], [], true, details);
        Equal(order.Key, recomputed.Matches[receipt.Id].Order?.Key);
        Check(service.TryRestore(cache, [receipt], Account, [order], [], out var restored, details), "Fresh cleaned-name graph cannot restore.");
        Equal(AutomaticLinkBasis.AmountAndProducts, restored[receipt.Id].Basis);
        var manualOrder = Order(2, "Інший товар");
        var manual = new ReceiptOrderDecision { AccountContext = Account, ReceiptId = receipt.Id, ConfirmedOrder = manualOrder.Key };
        var confirmed = service.MatchAll(cache, [receipt], Account, [order, manualOrder], [manual], true, details).Matches[receipt.Id];
        Equal(ReceiptLinkState.Manual, confirmed.State); Equal(manualOrder.Key, confirmed.Order?.Key);
        var suppressed = new ReceiptOrderDecision { AccountContext = Account, ReceiptId = receipt.Id, SuppressAutomatic = true };
        Check(service.MatchAll(cache, [receipt], Account, [order], [suppressed], true, details).Matches[receipt.Id].Order is null,
            "Name cleaning overrode explicitly suppressed automatic matching.");
    }
}
