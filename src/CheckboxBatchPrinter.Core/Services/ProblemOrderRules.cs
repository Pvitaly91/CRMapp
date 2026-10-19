using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public sealed record OrderProblem(string Reason, int Priority);

// Advisory only: these rules never change an order, receipt link or print history.
public static class ProblemOrderRules
{
    public const int PickupDays = 5;

    public static IReadOnlyList<OrderProblem> Evaluate(MarketplaceOrder order, bool hasReceipt,
        IReadOnlyList<ShipmentTrackingStatus> tracking, DateTimeOffset now)
    {
        if (order.IsCanceled) return [];
        var result = new List<OrderProblem>();
        var today = DateRangeBuilder.KyivDate(now);
        foreach (var status in tracking.Where(IsWaitingForPickup))
        {
            if (status.PaidStorageStartsOn is { } paid && paid <= today.AddDays(1))
                result.Add(new($"ТТН {status.TrackingNumber}: " + (paid > today
                    ? $"останній день безоплатного зберігання; платне з {paid:dd.MM.yyyy}"
                    : $"платне зберігання з {paid:dd.MM.yyyy}"), 1));
            if (status.ArrivedAt is { } arrived && arrived <= now)
            {
                var days = today.DayNumber - DateRangeBuilder.KyivDate(arrived).DayNumber;
                if (days >= PickupDays)
                    result.Add(new($"ТТН {status.TrackingNumber}: не забирають {days} дн. (прибуло {DateRangeBuilder.KyivDate(arrived):dd.MM.yyyy})", 2));
            }
        }
        // A shipping label is not evidence of dispatch. Use the carrier's status.
        var dispatched = tracking.Any(s => ShipmentTrackingProgress.IsDispatched(s.Carrier, s.Status) || IsTerminal(s));
        if (!dispatched && !IsCompletedOrder(order) && order.CreatedAt is { } created && now - created > TimeSpan.FromHours(24))
        {
            var missing = new List<string>();
            if (!hasReceipt) missing.Add("чека");
            if (!order.Shipments.Any(s => !string.IsNullOrWhiteSpace(s.TrackingNumber))) missing.Add("ТТН");
            if (missing.Count > 0) result.Add(new($"Понад 24 год без {string.Join(" і ", missing)}", 3));
        }
        return result;
    }

    public static bool IsCompletedOrder(MarketplaceOrder order) =>
        order.Key.Marketplace == MarketplaceKind.Rozetka && order.SourceStatusGroup == 2 ||
        new[] { order.Status, order.SourceStatus }.Any(s => Normalize(s) is
            "delivered" or "completed" or "виконано" or "завершено" or "отримано" or "выполнен" or "выполнено" or "получено");

    public static bool IsTerminal(ShipmentTrackingStatus status)
    {
        var text = Normalize(status.Status);
        return status.Carrier == ShippingCarrier.NovaPoshta && status.StatusCode is "9" or "10" or "11" or "102" or "103" ||
            new[] { "отримано", "відправлення отримано", "получено", "отправление получено", "видано", "замовлення видано",
                "повернен", "поверта", "відмов", "отказ", "возврат", "скасован", "отмен", "gaveout" }.Any(text.StartsWith);
    }

    public static bool IsWaitingForPickup(ShipmentTrackingStatus status)
    {
        if (IsTerminal(status)) return false;
        if (status.Carrier == ShippingCarrier.NovaPoshta && status.StatusCode.Length > 0)
            return status.StatusCode is "7" or "8";
        var text = Normalize(status.Status);
        return new[] { "прибув у відділення", "прибуло у відділення", "відправлення прибуло у відділення",
            "готово до видачі", "готовий до видачі", "відправлення у відділенні", "прибыло в отделение",
            "готово к выдаче", "readytoissueoctopus", "placedinstore" }.Any(text.StartsWith);
    }

    private static string Normalize(string text) => text.Trim().ToLowerInvariant();
}
