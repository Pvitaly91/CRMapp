using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class ProblemOrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 21, 0, 0, TimeSpan.FromHours(3));
    private static MarketplaceOrder Order => new() { Key = new(MarketplaceKind.Prom, "shop", "1"), Number = "1",
        CreatedAt = Now.AddHours(-25), Status = "Прийнято" };
    private static ShipmentTrackingStatus Waiting => new(ShippingCarrier.NovaPoshta, "20400554610503", "Прибув у відділення", "Нова пошта", Now)
        { StatusCode = "7", ArrivedAt = new(2026, 10, 14, 17, 58, 10, TimeSpan.FromHours(3)), PaidStorageStartsOn = new(2026, 10, 20) };
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("problem orders use strict 24-hour age and either missing receipt or missing TTN", () => Run(MissingDocuments)),
        ("problem orders warn one Kyiv day before paid storage and from five pickup days", () => Run(Storage)),
        ("problem orders exclude cancellations received parcels and completed orders", () => Run(Completed)),
        ("problem orders do not invent arrival or storage dates and handle multiple parcels", () => Run(UnknownAndMultiple)),
        ("problem orders use elapsed UTC for age and Kyiv calendar across DST for storage", () => Run(TimeZones))
    ];
    private static Task Run(Action action) { action(); return Task.CompletedTask; }
    private static void Check(bool value) { if (!value) throw new Exception("Problem order rule assertion failed."); }
    private static IReadOnlyList<OrderProblem> Evaluate(MarketplaceOrder? order = null, bool receipt = false, params ShipmentTrackingStatus[] statuses) =>
        ProblemOrderRules.Evaluate(order ?? Order with { Shipments = statuses.Select(s => new OrderShipment("nova_poshta", s.TrackingNumber)).ToArray() }, receipt, statuses, Now);

    private static void MissingDocuments()
    {
        Check(Evaluate().Single().Reason.Contains("чека і ТТН"));
        Check(Evaluate(receipt: true).Single().Reason.EndsWith("ТТН"));
        var withTtn = Order with { Shipments = [new("nova_poshta", Waiting.TrackingNumber)] };
        Check(Evaluate(withTtn).Single().Reason.EndsWith("чека"));
        Check(Evaluate(withTtn, true).Count == 0);
        foreach (var age in new[] { TimeSpan.FromHours(24), TimeSpan.FromHours(23), TimeSpan.FromHours(-1) })
            Check(Evaluate(Order with { CreatedAt = Now - age }).Count == 0);
        Check(Evaluate(Order with { CreatedAt = null }).Count == 0);
        Check(Evaluate(Order with { Status = "Нове", PaymentStatus = "paid" }).Count == 1);
        Check(Evaluate(Order with { Key = Order.Key with { Marketplace = MarketplaceKind.Rozetka }, SourceStatusGroup = 1 }).Count == 1);
    }
    private static void Storage()
    {
        var problems = Evaluate(receipt: true, statuses: Waiting);
        Check(problems.Count == 2 && problems[0].Priority == 1 && problems[0].Reason.Contains("20.10.2026") && problems[1].Reason.Contains("5 дн."));
        Check(Evaluate(receipt: true, statuses: Waiting with { PaidStorageStartsOn = new(2026, 10, 21), ArrivedAt = Now.AddDays(-4) }).Count == 0);
        Check(Evaluate(receipt: true, statuses: Waiting with { PaidStorageStartsOn = new(2026, 10, 19), ArrivedAt = null }).Single().Reason.StartsWith("ТТН"));
        Check(Evaluate(receipt: true, statuses: Waiting with { StatusCode = "8" }).Count == 2);
        Check(Evaluate(receipt: true, statuses: Waiting with { StatusCode = "4", Status = "Відправлення прямує до м. Київ" }).Count == 0);
    }
    private static void Completed()
    {
        Check(Evaluate(Order with { Status = "canceled" }, statuses: Waiting).Count == 0);
        Check(Evaluate(Order with { Key = Order.Key with { Marketplace = MarketplaceKind.Rozetka }, SourceStatusGroup = 3 }, statuses: Waiting).Count == 0);
        foreach (var code in new[] { "9", "10", "11", "102", "103" })
            Check(Evaluate(statuses: Waiting with { StatusCode = code, Status = "Отримано" }).Count == 0);
        Check(Evaluate(Order with { Status = "delivered" }).Count == 0);
        Check(Evaluate(statuses: Waiting with { StatusCode = "4", Status = "Відправлення прямує до м. Київ" }).Count == 0);
        Check(Evaluate(statuses: Waiting with { Status = "Відмова від отримання" }).Count == 0);
    }
    private static void UnknownAndMultiple()
    {
        Check(Evaluate(receipt: true, statuses: Waiting with { ArrivedAt = null, PaidStorageStartsOn = null }).Count == 0);
        Check(Evaluate(receipt: true, statuses: Waiting with { ArrivedAt = Now.AddDays(1), PaidStorageStartsOn = null }).Count == 0);
        Check(Evaluate(receipt: true, statuses: [Waiting, Waiting with { TrackingNumber = "20400554484754" }]).Count == 4);
        var rozetka = Waiting with { Carrier = ShippingCarrier.RozetkaDelivery, StatusCode = "", Status = "Готово до видачі", PaidStorageStartsOn = null };
        Check(Evaluate(receipt: true, statuses: rozetka).Single().Priority == 2);
        Check(Evaluate(receipt: true, statuses: Waiting with { StatusCode = "future-status", Status = "Невідомо" }).Count == 0);
    }
    private static void TimeZones()
    {
        var utc = new DateTimeOffset(2026, 10, 19, 21, 5, 0, TimeSpan.Zero); // Oct 20 Kyiv
        Check(ProblemOrderRules.Evaluate(Order, true, [Waiting with { ArrivedAt = null }], utc).Single().Reason.Contains("платне зберігання"));
        var dst = new DateTimeOffset(2026, 10, 25, 2, 5, 0, TimeSpan.Zero);
        Check(ProblemOrderRules.Evaluate(Order with { CreatedAt = dst.AddHours(-24) }, false, [], dst).Count == 0);
        Check(ProblemOrderRules.Evaluate(Order with { CreatedAt = dst.AddHours(-24).AddSeconds(-1) }, false, [], dst).Count == 1);
    }
}
