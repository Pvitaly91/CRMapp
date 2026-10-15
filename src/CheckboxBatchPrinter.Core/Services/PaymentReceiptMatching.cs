using System.Text;
using System.Text.RegularExpressions;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

/// <summary>Bounded payment evidence for eligible order baskets, never an inference of the receipt's marketplace.</summary>
internal static class PaymentReceiptMatching
{
    internal sealed record Check(PaymentComparison State, string Reason = "");
    private enum Provider { Unknown, NovaPay, RozetkaPay }
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);
    private static string Normalize(string? text) => Spaces.Replace((text ?? "").Normalize(NormalizationForm.FormKC)
        .Trim().ToUpperInvariant().Replace('–', '-').Replace('—', '-'), " ");

    internal static Check Compare(MarketplaceOrder order, ReceiptRecord receipt)
    {
        var expected = OrderProvider(order); var actual = ReceiptProvider(receipt);
        if (expected == Provider.Unknown || actual == Provider.Unknown) return new(PaymentComparison.Insufficient);
        var marketplace = order.Key.Marketplace == MarketplaceKind.Rozetka ? "Rozetka" : "Prom";
        return expected == actual ? new(PaymentComparison.Match,
            $"Оплата узгоджена: «{order.PaymentMethod}» у {marketplace} і {actual} у чеку.") : new(PaymentComparison.Contradiction,
            $"Оплата відрізняється: «{order.PaymentMethod}» у {marketplace} відповідає {expected}; у чеку {actual}. Це не визначає маркетплейс чека.");
    }

    private static Provider OrderProvider(MarketplaceOrder order)
    {
        if (!AutomaticReceiptLinkPolicy.CanUseAmountFallback(order)) return Provider.Unknown;
        var method = Normalize(order.PaymentMethod);
        if (order.Key.Marketplace == MarketplaceKind.Prom && method is "ПРОМ-ОПЛАТА" or "ПРОМ ОПЛАТА" or "ПРОМОПЛАТА")
            return Provider.RozetkaPay;
        if (method is "ROZETKAPAY" or "ROZETKA PAY" or
            "ПЛАТІЖ ROZETKAPAY" or "ПЛАТЕЖ ROZETKAPAY") return Provider.RozetkaPay;
        if (method is "NOVAPAY" or "NOVA PAY" or "ПЛАТІЖ NOVAPAY" or "ПЛАТЕЖ NOVAPAY") return Provider.NovaPay;
        // Generic COD is not always NovaPay: e.g. Rozetka Delivery has its own COD.
        // Require an explicit, unopposed Nova Poshta carrier; an address/TTN is not a payment key.
        if (method is not ("НАЛОЖЕННЫЙ ПЛАТЕЖ" or "НАЛОЖЕННЫЙ ПЛАТЁЖ" or "ПІСЛЯПЛАТА" or "НАКЛАДЕНИЙ ПЛАТІЖ"))
            return Provider.Unknown;
        var carriers = order.Shipments.Select(s => Normalize(s.Carrier)).Where(c => c.Length > 0).ToArray();
        var delivery = Normalize(order.DeliveryMethod);
        if (ShippingCarrierNames.FromDocumentedDeliveryField(delivery) == ShippingCarrier.RozetkaDelivery) return Provider.Unknown;
        if (carriers.Length > 0 ? carriers.All(IsNovaPoshta) : IsNovaPoshta(delivery)) return Provider.NovaPay;
        return Provider.Unknown;
    }

    private static bool IsNovaPoshta(string value) => value is "NOVA_POSHTA" or "NOVA POSHTA" or "НОВА ПОШТА" or "НОВАЯ ПОЧТА";

    private static Provider ReceiptProvider(ReceiptRecord receipt)
    {
        if (!receipt.TotalKnown || receipt.TotalSumMinor <= 0 || receipt.Payments.Count == 0) return Provider.Unknown;
        Provider result = Provider.Unknown; long sum = 0;
        try
        {
            foreach (var payment in receipt.Payments)
            {
                if (payment.ValueMinor <= 0 || Normalize(payment.Type) != "CASHLESS") return Provider.Unknown;
                var provider = Normalize(payment.Label) switch
                {
                    "NOVAPAY" or "NOVA PAY" or "ПЛАТІЖ NOVAPAY" or "ПЛАТЕЖ NOVAPAY" => Provider.NovaPay,
                    "ROZETKAPAY" or "ROZETKA PAY" or "ПЛАТІЖ ROZETKAPAY" or "ПЛАТЕЖ ROZETKAPAY" => Provider.RozetkaPay,
                    _ => Provider.Unknown
                };
                if (provider == Provider.Unknown || result != Provider.Unknown && result != provider) return Provider.Unknown;
                result = provider; sum = checked(sum + payment.ValueMinor);
            }
        }
        catch (OverflowException) { return Provider.Unknown; }
        return sum == receipt.TotalSumMinor ? result : Provider.Unknown;
    }
}
