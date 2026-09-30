using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Services;

internal static class ReceiptPrintHeader
{
    public static string TrackingFor(MarketplaceOrder? order) => order is null ? "" : string.Join("; ",
        ShippingCarrierNames.ForOrder(order)
            .Where(s => s.Carrier is ShippingCarrier.NovaPoshta or ShippingCarrier.RozetkaDelivery)
            .Select(s => (s.Carrier, Number: Clean(s.TrackingNumber)))
            .Where(s => s.Number.Length > 0).Distinct()
            .Select(s => $"{(s.Carrier == ShippingCarrier.NovaPoshta ? "НП" : "Rozetka Delivery")}: {s.Number}"));

    public static string Text(string orderNumber, string trackingText)
    {
        var cleanedNumber = Clean(orderNumber);
        if (cleanedNumber.Length == 0) return "";
        var number = $"Замовлення №{cleanedNumber}";
        return string.IsNullOrWhiteSpace(trackingText) ? number : $"{number} · ТТН {Clean(trackingText)}";
    }

    private static string Clean(string text) => new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
}
