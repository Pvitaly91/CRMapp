using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class CanceledOrderMatchingTests
{
    private const string Account = "canceled-order-test-account";
    private static readonly DateTimeOffset Day = new(2026, 10, 5, 0, 0, 0, TimeSpan.FromHours(3));
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("canceled orders: canceled Honda competitor does not block the NovaPay receipt", Run(CanceledCompetitor)),
        ("canceled orders: exact status variants exclude canceled documents without guessing unknown statuses", Run(StatusVariants)),
        ("canceled orders: cancellation removes saved suggestions and releases their receipts", Run(CancellationAfterLink)),
        ("canceled orders: Rozetka cancellation releases saved fallback while retaining fiscal evidence", Run(RozetkaCancellationAfterLink)),
        ("canceled orders: manual and exact fiscal links retain priority", Run(AuthoritativeLinks))
    ];
    private static Func<Task> Run(Action action) => () => { action(); return Task.CompletedTask; };
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static ReceiptRecord Receipt() => new()
    {
        Id = "cd000000-0000-0000-0000-000000004669", Serial = 4669, Type = "SELL", Status = "DONE",
        FiscalDate = Day.AddHours(21).AddMinutes(51), TotalSumMinor = 40000,
        Payments = [new("CASHLESS", "Платіж NovaPay", 40000)]
    };
    private static MarketplaceOrder Order(string id = "431797758") => new()
    {
        Key = new(MarketplaceKind.Prom, "synthetic-canceled-store", id), Number = id,
        CreatedAt = Day.AddHours(21).AddMinutes(22), Total = 400, Currency = "UAH",
        Status = "Отримано", SourceStatus = "received", PaymentMethod = "Наложенный платеж",
        DeliveryMethod = "Нова Пошта", Shipments = [new("nova_poshta", "")],
        Items = [new("Ремкомплект петель дверей Honda Civic", "", 1, 400, 400)],
        ItemListComplete = true, ItemsComplete = true, RetrievedAtUtc = Day.AddHours(23)
    };
    private static Dictionary<string, ReceiptDetails> Details(ReceiptRecord receipt) =>
        new() { [receipt.Id] = new(receipt.Id, Order().Items) { ItemListComplete = true } };
    private static AutomaticMatchCache Restart(AutomaticMatchCache cache) =>
        JsonSerializer.Deserialize<AutomaticMatchCache>(JsonSerializer.Serialize(cache))!;
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Day.AddDays(1); }

    private static void CanceledCompetitor()
    {
        var receipt = Receipt(); var active = Order();
        var canceled = Order("431604463") with { CreatedAt = Day.AddHours(6).AddMinutes(22),
            SourceStatus = "canceled", Status = "Скасовано", DeliveryMethod = "Магазины Rozetka", Shipments = [] };
        var prepaid = Order("431668608") with { PaymentMethod = "Пром-оплата" };
        foreach (var reversed in new[] { false, true })
        {
            var orders = reversed ? new[] { prepaid, active, canceled } : new[] { canceled, active, prepaid };
            var match = new ReceiptOrderMatchingService().MatchAll([receipt], Account, orders, [], true, Details(receipt))[receipt.Id];
            Check(match.State == ReceiptLinkState.Suggested && match.Order?.Key == active.Key && !match.Ambiguous,
                "Canceled Honda order kept the real receipt ambiguous.");
            Check(match.Candidates.All(o => o.Key != canceled.Key) && match.GroupOrders.All(o => o.Key != canceled.Key),
                "Canceled order remained in automatic candidates.");
        }
        var alone = new ReceiptOrderMatchingService().MatchAll([receipt], Account, [canceled], [], true, Details(receipt))[receipt.Id];
        Check(alone.State == ReceiptLinkState.NotFound && alone.Candidates.Count == 0, "Canceled-only order received an automatic link.");
        var evidenceOnly = new ReceiptOrderMatchingService().Match(receipt, Account, [canceled], [], true);
        Check(evidenceOnly.Candidates.Count == 0, "Single-receipt amount candidates still include canceled orders.");
        var incompleteCanceled = canceled with { CreatedAt = null, Total = null, Items = [], ItemsComplete = false, ItemListComplete = false };
        var withMissing = new ReceiptOrderMatchingService().MatchAll([receipt], Account, [active, incompleteCanceled], [], true, Details(receipt))[receipt.Id];
        Check(withMissing.Order?.Key == active.Key, "Missing canceled-order data kept a competitor in the graph.");
    }
    private static void StatusVariants()
    {
        var receipt = Receipt(); var active = Order();
        foreach (var marketplace in new[] { MarketplaceKind.Prom, MarketplaceKind.Rozetka })
        foreach (var raw in new[] { true, false })
        foreach (var status in new[] { "canceled", " CANCELLED ", "Скасовано", "Скасоване", "Скасований", "Отменен", "Отменён" })
        {
            var canceled = Order("canceled") with { Key = new(marketplace, "synthetic-canceled-store", "canceled"),
                SourceStatus = raw ? status : "", Status = raw ? "" : status };
            var match = new ReceiptOrderMatchingService().MatchAll([receipt], Account, [active, canceled], [], true, Details(receipt))[receipt.Id];
            Check(match.Order?.Key == active.Key, $"Canceled status still competed: {marketplace}, {status}, raw={raw}.");
        }
        foreach (var status in new[] { "received", "completed", "not canceled", "cancel_pending", "10", "" })
        {
            var unknown = Order("unknown") with { SourceStatus = status, Status = status, PaymentStatus = "canceled" };
            var match = new ReceiptOrderMatchingService().MatchAll([receipt], Account, [active, unknown], [], true, Details(receipt))[receipt.Id];
            Check(match.Order is null && match.Ambiguous, $"Uncanceled or unknown status was silently dropped: {status}.");
        }
    }
    private static void CancellationAfterLink()
    {
        var receipt = Receipt(); var original = Order(); var replacement = Order("replacement");
        var service = new CachedReceiptOrderMatchingService(new Clock()); var cache = new AutomaticMatchCache();
        var saved = service.MatchAll(cache, [receipt], Account, [original], [], true, Details(receipt)).Matches;
        Check(saved[receipt.Id].Order?.Key == original.Key, "Original fixture did not get a saved automatic pair.");
        var completed = original with { SourceStatus = "completed", Status = "Завершено" };
        var retained = service.MatchAll(Restart(cache), [receipt], Account, [completed, replacement], [], true, Details(receipt)).Matches[receipt.Id];
        Check(retained.Order?.Key == original.Key, "An ordinary non-canceled status change discarded the saved link.");
        var canceled = original with { SourceStatus = "canceled", Status = "Скасовано" };
        var direct = new ReceiptOrderMatchingService().MatchAll([receipt], Account, [canceled, replacement], [], true,
            Details(receipt), existingLinks: saved)[receipt.Id];
        Check(direct.Order?.Key == replacement.Key, "Old match object retained a link despite the current canceled status.");
        var partialCache = Restart(cache);
        var partial = service.MatchAll(partialCache, [receipt], Account, [canceled, replacement], [], false, Details(receipt)).Matches[receipt.Id];
        Check(partial.Order is null && !partialCache.Links.ContainsKey(receipt.Id), "Partial refresh retained a canceled saved pair.");
        var restarted = Restart(cache);
        var match = service.MatchAll(restarted, [receipt], Account, [canceled, replacement], [], true, Details(receipt)).Matches[receipt.Id];
        Check(match.Order?.Key == replacement.Key && restarted.Links[receipt.Id].Match.Order == replacement.Key,
            "Cancellation did not release the receipt for its remaining active order.");
    }
    private static void AuthoritativeLinks()
    {
        var receipt = Receipt(); var canceled = Order() with { SourceStatus = "canceled", Status = "Скасовано" };
        var manual = new ReceiptOrderDecision { AccountContext = Account, ReceiptId = receipt.Id, ConfirmedOrder = canceled.Key };
        var matcher = new ReceiptOrderMatchingService();
        var confirmed = matcher.MatchAll([receipt], Account, [canceled], [manual], true, Details(receipt))[receipt.Id];
        Check(confirmed.State == ReceiptLinkState.Manual && confirmed.Order?.Key == canceled.Key, "Cancellation removed a manual historical link.");
        var exact = matcher.MatchAll([receipt], Account, [canceled with { ReceiptIds = [receipt.Id] }], [], true, Details(receipt))[receipt.Id];
        Check(exact.State == ReceiptLinkState.Exact && exact.Order?.Key == canceled.Key, "Cancellation removed a proven fiscal link.");
    }

    private static void RozetkaCancellationAfterLink()
    {
        var receipt = Receipt();
        var original = Order() with { Key = new(MarketplaceKind.Rozetka, "rozetka", "100"), SourceStatus = "6" };
        var replacement = Order("replacement");
        var service = new CachedReceiptOrderMatchingService(new Clock());
        var cache = new AutomaticMatchCache();
        var saved = service.MatchAll(cache, [receipt], Account, [original], [], true, Details(receipt)).Matches[receipt.Id];
        Check(saved.Order?.Key == original.Key && saved.State == ReceiptLinkState.Suggested && cache.Links.Count == 1,
            "The active keyless Rozetka order did not establish the fallback needed to test cancellation.");
        foreach (var canceled in new[]
        {
            original with { SourceStatus = "45", Status = "Скасовано покупцем" },
            original with { SourceStatus = "999", SourceStatusGroup = 3, Status = "Нова причина відмови" }
        })
        {
            var restored = Restart(cache);
            var match = service.MatchAll(restored, [receipt], Account, [canceled, replacement], [], true, Details(receipt)).Matches[receipt.Id];
            Check(match.Order?.Key == replacement.Key, "Canceled Rozetka order blocked the eligible Prom order.");
            var partial = Restart(cache);
            service.MatchAll(partial, [receipt], Account, [canceled], [], false, Details(receipt));
            Check(!partial.Links.ContainsKey(receipt.Id), "Partial refresh retained canceled Rozetka suggestion.");
            var exact = new ReceiptOrderMatchingService().MatchAll([receipt], Account,
                [canceled with { ReceiptIds = [receipt.Id] }], [], true, Details(receipt))[receipt.Id];
            Check(exact.State == ReceiptLinkState.Exact, "Rozetka cancellation removed historical fiscal evidence.");
        }
    }
}
