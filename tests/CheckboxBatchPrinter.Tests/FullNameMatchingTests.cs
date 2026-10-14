using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class FullNameMatchingTests
{
    private const string Account = "full-name-synthetic-account";
    private static readonly DateTimeOffset Day = new(2026, 10, 1, 16, 0, 0, TimeSpan.FromHours(3));
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("full names: arbitrary categories and plural kits resolve both marketplaces in every input order", Run(ArbitraryNames)),
        ("full names: cleaning removes symbols without deleting words digits or translating", Run(Normalization)),
        ("full names: entire multibasket names and aggregated quantities are required", Run(CompleteBasket)),
        ("full names: absent names incomplete lists and unknown same-name quantities preserve competitors", Run(IncompleteEvidence)),
        ("full names: v6 graphs expire but details and manual decisions survive", Run(CacheMigration))
    ];
    private static Func<Task> Run(Action test) => () => { test(); return Task.CompletedTask; };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
    private static ReceiptRecord Receipt(int n) => new()
    { Id = $"e7400000-0000-0000-0000-{n:D12}", Type = "SELL", Status = "DONE", FiscalDate = Day, TotalSumMinor = 28000 };
    private static MarketplaceOrder Order(int n, string name, MarketplaceKind marketplace = MarketplaceKind.Prom) => new()
    {
        Key = new(marketplace, "full-name-synthetic-shop", n.ToString()), Number = $"SYNTHETIC-{n}",
        CreatedAt = Day.AddHours(-1), Total = 280, Currency = "UAH", ItemListComplete = true, ItemsComplete = true,
        Items = [new(name, "unrelated-marketplace-code", 1, 280, 280)]
    };
    private static ReceiptDetails Detail(ReceiptRecord r, params OrderItem[] items) => new(r.Id, items) { ItemListComplete = true };
    private static IReadOnlyDictionary<string, ReceiptOrderMatch> Match(ReceiptRecord[] rs, MarketplaceOrder[] os,
        IReadOnlyDictionary<string, ReceiptDetails> details) => new ReceiptOrderMatchingService().MatchAll(rs, Account, os, [], true, details);

    private static void ArbitraryNames()
    {
        foreach (var (left, right) in new[]
        {
            ("Подарункова листівка Весняний ранок", "Подарункова листівка Зимовий вечір"),
            ("Набір тримачів та гачків 10 шт", "Набір тримачів та гачків 12 шт"),
            ("Кухонний рушник", "Запасна кришка"), ("Товар X", "Товар Y")
        })
        foreach (var marketplace in new[] { MarketplaceKind.Prom, MarketplaceKind.Rozetka })
        {
            var orders = new[] { Order(1, left, marketplace), Order(2, right, marketplace) };
            var receipts = new[] { Receipt(1), Receipt(2) };
            var details = receipts.Select((r, i) => Detail(r, orders[i].Items.ToArray())).ToDictionary(d => d.Id);
            foreach (var os in new[] { orders, orders.Reverse().ToArray() })
            foreach (var rs in new[] { receipts, receipts.Reverse().ToArray() })
            {
                var result = Match(rs, os, details);
                for (var i = 0; i < 2; i++)
                {
                    Equal(orders[i].Key, result[receipts[i].Id].Order?.Key);
                    Equal(AutomaticLinkBasis.AmountAndProducts, result[receipts[i].Id].Basis);
                    Check(result[receipts[i].Id].Explanation.Contains("Повні назви"), "No concrete full-name exclusion reason.");
                }
            }
        }
    }

    private static ReceiptOrderMatch Compare(string left, string right)
    {
        var r = Receipt(1); var o = Order(1, left);
        return Match([r], [o], new Dictionary<string, ReceiptDetails>
            { [r.Id] = Detail(r, new OrderItem(right, "unrelated-receipt-code", 1, 280, 280)) })[r.Id];
    }

    private static void Normalization()
    {
        Equal(ProductComparison.Match, Compare("Подарунковий набір DC-12 \"Весна\" O'Ring",
            "  ПОДАРУНКОВИЙ\u00a0набір\tDC–12 «Весна» O’Ring  ").Products);
        // The explicitly requested cleaned-name policy now discards punctuation/symbols.
        foreach (var (left, right) in new[]
        {
            ("Набір DC-12", "Набір DC12"), ("Набір \"А\"", "Набір А"),
            ("Роз’єм XT60", "Роз?єм XT60"), ("Зарядка 2×USB", "Зарядка 2?USB"),
            ("Перехідник USB→DC", "Перехідник USB?DC"), ("Модуль 4/8Ω", "Модуль 4/8?")
        }) Equal(ProductComparison.Match, Compare(left, right).Products);
        foreach (var (left, right) in new[]
        {
            ("Кабель живлення", "Кабель питания"), ("Червоний кабель USB", "Кабель USB червоний"),
            ("Набір 12 В 24 В", "Набір 24 В 12 В"), ("Товар USB", "Товар USВ"),
            ("Кабель живлення USB", "Каб. USB"), ("Кабель USB", "Кабель USB подовжувач"),
            ("Модуль 4/8Ω", "Модуль 8/16?"),
            ("Вимикач маси", "Вимикач маси, латунний контакт")
        })
        {
            var result = Compare(left, right);
            Equal(ProductComparison.Contradiction, result.Products);
            Check(result.Order is null && result.Explanation.Contains("Повні назви"), "Different complete names were accepted.");
            Equal(result.Products, Compare(right, left).Products);
        }
    }

    private static void CompleteBasket()
    {
        var r = Receipt(1); var order = Order(1, "A") with
        { Items = [new("Запасна кришка", "a", 2, 100, 200), new("Подарункова листівка", "b", 1, 80, 80)] };
        var split = Detail(r, new("Подарункова листівка", "x", 1, 80, 80),
            new("ЗАПАСНА кришка", "y", 1, 100, 100), new("Запасна кришка", "z", 1, 100, 100));
        var details = new Dictionary<string, ReceiptDetails> { [r.Id] = split };
        Equal(ProductComparison.Match, Match([r], [order], details)[r.Id].Products);
        // Same number of lines/units/total is insufficient: the quantity of each exact name matters.
        var swapped = order with { Key = order.Key with { OrderId = "2" },
            Items = [new("Запасна кришка", "a", 1, 80, 80), new("Подарункова листівка", "b", 2, 100, 200)] };
        Equal(order.Key, Match([r], [swapped, order], details)[r.Id].Order?.Key);
        var quantityMismatch = Match([r], [swapped], details)[r.Id];
        Equal(ProductComparison.Contradiction, quantityMismatch.Products);
        Check(quantityMismatch.Explanation.Contains("Кількість"), "Per-name quantity mismatch lacks explanation.");
        var wrongName = order with { Items = [order.Items[0], order.Items[1] with { Name = "Інша листівка" }] };
        Equal(ProductComparison.Contradiction, Match([r], [wrongName], details)[r.Id].Products);
        var duplicate = order with { Key = order.Key with { OrderId = "3" }, Buyer = new("Synthetic second buyer") };
        Check(Match([r], [order, duplicate], details)[r.Id].Ambiguous, "Equal named baskets must not choose between buyers.");
    }

    private static void IncompleteEvidence()
    {
        var r = Receipt(1); var good = Order(1, "Подарункова листівка"); var other = Order(2, "Інша листівка");
        var details = new Dictionary<string, ReceiptDetails> { [r.Id] = Detail(r, good.Items.ToArray()) };
        foreach (var missing in new[]
        {
            other with { Items = [] }, other with { ItemListComplete = false },
            other with { Items = [other.Items[0] with { Name = " " }] },
            other with { Items = [good.Items[0] with { Quantity = null, Total = null, UnitPrice = null }] }
        })
        {
            var result = Match([r], [good, missing], details)[r.Id];
            Check(result.Order is null && result.Ambiguous, "Missing name/list/quantity was excluded from competing graph.");
        }
        foreach (var missing in new[]
        {
            Detail(r, good.Items.ToArray()) with { ItemListComplete = false }, Detail(r, new OrderItem("", "", 1, 280, 280)),
            Detail(r, new OrderItem(good.Items[0].Name, "", null, null, null))
        })
        {
            var result = Match([r], [good, other], new Dictionary<string, ReceiptDetails> { [r.Id] = missing });
            // Quantity is unavailable for an equal name, so product equality is not invented.
            Equal(ProductComparison.Insufficient, result[r.Id].Products);
        }
    }

    private static void CacheMigration()
    {
        var r = Receipt(1); var orders = new[] { Order(1, "Запасна кришка"), Order(2, "Кухонний рушник") };
        var details = new Dictionary<string, ReceiptDetails> { [r.Id] = Detail(r, orders[0].Items.ToArray()) };
        var service = new CachedReceiptOrderMatchingService(); var cache = new AutomaticMatchCache();
        service.RememberDetails(cache, Account, r, details[r.Id]);
        service.MatchAll(cache, [r], Account, orders, [], true, details);
        cache.Snapshots = cache.Snapshots.Select(s => s with { AlgorithmVersion = "same-kyiv-day-products-first-payment-v6" }).ToList();
        Check(!service.TryRestore(cache, [r], Account, orders, [], out _, details), "v6 name graph was reused.");
        Check(service.TryGetDetails(cache, Account, r, out _), "Receipt details were unnecessarily discarded.");
        var recomputed = service.MatchAll(cache, [r], Account, orders, [], true, details);
        Equal(orders[0].Key, recomputed.Matches[r.Id].Order?.Key);
        var manual = new ReceiptOrderDecision { AccountContext = Account, ReceiptId = r.Id, ConfirmedOrder = orders[1].Key };
        var confirmed = service.MatchAll(cache, [r], Account, orders, [manual], true, details).Matches[r.Id];
        Equal(ReceiptLinkState.Manual, confirmed.State); Equal(orders[1].Key, confirmed.Order?.Key);
    }
}
