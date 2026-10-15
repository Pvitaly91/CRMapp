using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

/// <summary>Marketplace eligibility for probable links; date, amount and ambiguity guards apply separately.</summary>
public static class AutomaticReceiptLinkPolicy
{
    public static bool CanUseAmountFallback(MarketplaceOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return order.Key.Marketplace == MarketplaceKind.Prom ||
            order.Key.Marketplace == MarketplaceKind.Rozetka && !HasFiscalKey(order);
    }

    /// <summary>
    /// An unresolved, malformed or foreign-context assertion still needs fiscal verification.
    /// Only absent/blank key values permit Rozetka fallback; failure to resolve a present key is not absence.
    /// </summary>
    public static bool HasFiscalKey(MarketplaceOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return order.ReceiptIds.Any(HasValue) || order.FiscalReceiptNumbers.Any(HasValue) ||
            order.FiscalReceiptUrls.Any(HasValue) ||
            order.FiscalReferences.Any(reference => HasValue(reference.Value) || HasValue(reference.VerifiedReceiptId));
    }

    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);
}
