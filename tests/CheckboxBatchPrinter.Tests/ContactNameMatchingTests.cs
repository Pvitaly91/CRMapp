using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class ContactNameMatchingTests
{
    private const string Contacts = "Клеми кільце та вилка без ізоляції, набір 320 шт, мідні наконечники для проводів";
    private const string Switch = "Вимикач маси АКБ 12/24V 500A універсальний, відсікач живлення акумулятора, сріблясті клеми";
    private const string Account = "synthetic-contact-account";
    private static readonly DateTimeOffset Day = new(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(3));
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("contacts: one Prom order resolves matching receipt despite an unconnected marketplace receipt at equal price and quantity", Run(OneOrderTwoReceipts)),
        ("contacts: homogeneous terminal sets and switch contact descriptions identify different main products", Run(ContactVariants)),
        ("contacts: complete mixed kit names separate while missing data and identical receipts stay ambiguous", Run(UncertainNames)),
        ("contacts: old name graph expires while cached details recompute the correct pair", Run(CacheVersion))
    ];
    private static Func<Task> Run(Action test) => () => { test(); return Task.CompletedTask; };
    private static ReceiptRecord Receipt(int n) => new()
    {
        Id = $"e7100000-0000-0000-0000-{n:D12}", Serial = 4558 + n,
        Type = ReceiptTypes.Sell, Status = "DONE", FiscalDate = Day.AddHours(13).AddMinutes(n == 1 ? 40 : 58),
        TotalSumMinor = 27000
    };
    private static MarketplaceOrder Order(string name = Contacts) => new()
    {
        Key = new(MarketplaceKind.Prom, "synthetic-contact-shop", "TEST-CONTACTS"), Number = "TEST-CONTACTS",
        CreatedAt = Day.AddHours(13).AddMinutes(35), Total = 270m, Currency = "UAH",
        Items = [new(name, "", 1m, 270m, 270m)], ItemsComplete = true, ItemListComplete = true
    };
    private static ReceiptDetails Detail(ReceiptRecord r, string name) =>
        new(r.Id, [new(name, "", 1m, 270m, 270m)]) { ItemListComplete = true };
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
    private static void Check(bool ok, string reason)
    { if (!ok) throw new Exception(reason); }

    private static void OneOrderTwoReceipts()
    {
        var receipts = new[] { Receipt(1), Receipt(2) };
        var order = Order();
        var details = new Dictionary<string, ReceiptDetails>
        { [receipts[0].Id] = Detail(receipts[0], Contacts), [receipts[1].Id] = Detail(receipts[1], Switch) };
        foreach (var input in new[] { receipts, receipts.Reverse().ToArray() })
        {
            var matches = new ReceiptOrderMatchingService().MatchAll(input, Account, [order], [], true, details);
            var correct = matches[receipts[0].Id]; var unrelated = matches[receipts[1].Id];
            Console.WriteLine($"  Contacts270: matching={correct.State}; unrelated={unrelated.State}/{unrelated.Products}");
            Equal(ReceiptLinkState.Suggested, correct.State); Equal(order.Key, correct.Order!.Key);
            Equal(ProductComparison.Match, correct.Products); Equal(AutomaticLinkBasis.AmountAndProducts, correct.Basis);
            Check(!correct.Ambiguous && correct.CompetingReceiptIds.SequenceEqual([receipts[0].Id]), "Unrelated receipt still competes.");
            Equal(ProductComparison.Contradiction, unrelated.Products);
            Check(unrelated.Order is null && unrelated.State != ReceiptLinkState.Suggested, "Unrelated marketplace receipt acquired the Prom order.");
            Check(unrelated.Explanation.Contains("Повні назви"), "Exclusion lacks a concrete full-name reason.");
        }
        // Identical extra basket lines must not hide the differing main product.
        var extra = new OrderItem("Кабель USB", "", 1, 15, 15);
        var basketOrder = order with { Total = 285, Items = [order.Items[0], extra] };
        var basketReceipts = receipts.Select(r => new ReceiptRecord
        { Id = r.Id, Type = r.Type, Status = r.Status, FiscalDate = r.FiscalDate, Serial = r.Serial, TotalSumMinor = 28500 }).ToArray();
        var baskets = details.ToDictionary(p => p.Key, p => p.Value with { Items = [.. p.Value.Items, extra] });
        var result = new ReceiptOrderMatchingService().MatchAll(basketReceipts, Account, [basketOrder], [], true, baskets);
        Equal(basketOrder.Key, result[receipts[0].Id].Order!.Key);
        Check(result[receipts[1].Id].Order is null, "A shared extra line masked the different switch.");
    }

    private static ProductComparison Compare(string left, string right)
    {
        var receipt = Receipt(1);
        return new ReceiptOrderMatchingService().MatchAll([receipt], Account, [Order(left)], [], true,
            new Dictionary<string, ReceiptDetails> { [receipt.Id] = Detail(receipt, right) })[receipt.Id].Products;
    }
    private static void ContactVariants()
    {
        foreach (var contacts in new[]
        {
            Contacts, "Набір клем кільце та вилка 320 шт", "Мідні клеми, набір 100 шт",
            "Клеммы кольцо и вилка, набор 320 шт", "Набор клемм 100 шт", "Клеми кільцеві"
        })
        foreach (var toggle in new[] { Switch, "Вимикач маси АКБ", "Выключатель массы АКБ, серебристые клеммы" })
        {
            Equal(ProductComparison.Contradiction, Compare(contacts, toggle));
            Equal(ProductComparison.Contradiction, Compare(toggle, contacts));
        }
        Equal(ProductComparison.Match, Compare(Contacts, Contacts));
        Equal(ProductComparison.Contradiction, Compare("Клеми кільцеві", "Клеммы кольцевые"));
        Equal(ProductComparison.Contradiction, Compare("Клеми кільцеві 12 В", "Клеми кільцеві 24 В"));
    }
    private static void UncertainNames()
    {
        var receipts = new[] { Receipt(1), Receipt(2) }; var order = Order();
        foreach (var uncertain in new[]
        {
            "Набір клем і вимикач маси", "Вимикач та клеми", "Вимикач, клеми",
            "Клеми для вимикача", "Чохол для клем", "Набір різних товарів",
            "Кабель, набір 320 шт", "Клеми, набір кабель USB", Contacts
        })
        {
            var details = new Dictionary<string, ReceiptDetails>
            { [receipts[0].Id] = Detail(receipts[0], Contacts), [receipts[1].Id] = Detail(receipts[1], uncertain) };
            var match = new ReceiptOrderMatchingService().MatchAll(receipts, Account, [order], [], true, details)[receipts[0].Id];
            if (uncertain == Contacts)
                Check(match.Order is null && match.Ambiguous, "Identical receipt was incorrectly excluded.");
            else
            {
                Equal(order.Key, match.Order!.Key);
                Equal(AutomaticLinkBasis.AmountAndProducts, match.Basis);
            }
        }
        var partial = new Dictionary<string, ReceiptDetails>
        { [receipts[0].Id] = Detail(receipts[0], Contacts), [receipts[1].Id] = Detail(receipts[1], Switch) with { ItemListComplete = false } };
        Check(new ReceiptOrderMatchingService().MatchAll(receipts, Account, [order], [], true, partial)[receipts[0].Id].Order is null,
            "Incomplete unrelated receipt was discarded.");
    }
    private static void CacheVersion()
    {
        var receipts = new[] { Receipt(1), Receipt(2) }; var orders = new[] { Order() };
        var details = new Dictionary<string, ReceiptDetails>
        { [receipts[0].Id] = Detail(receipts[0], Contacts), [receipts[1].Id] = Detail(receipts[1], Switch) };
        var cache = new AutomaticMatchCache(); var service = new CachedReceiptOrderMatchingService();
        foreach (var r in receipts) service.RememberDetails(cache, Account, r, details[r.Id]);
        service.MatchAll(cache, receipts, Account, orders, [], true, details);
        cache.Snapshots = cache.Snapshots.Select(s => s with { AlgorithmVersion = "same-kyiv-day-product-names-v3" }).ToList();
        Check(!service.TryRestore(cache, receipts, Account, orders, [], out _, details), "Old name-matching graph was reused after the algorithm changed.");
        Check(receipts.All(r => service.TryGetDetails(cache, Account, r, out _)), "Reusable receipt details were lost.");
        var result = service.MatchAll(cache, receipts, Account, orders, [], true, details);
        Check(!result.CacheHit, "Old ambiguity was replayed from cache.");
        Equal(orders[0].Key, result.Matches[receipts[0].Id].Order!.Key);
        Check(result.Matches[receipts[1].Id].Order is null, "Unrelated receipt was linked during cache refresh.");
    }
}
