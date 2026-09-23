using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public static class MarketplaceFiscalEvidence
{
    // Fiscal documents are immutable evidence. Missing/null detail fields must not erase it.
    // Differing assertions for one document survive as a conflict, never silently replace each other.
    public static MarketplaceOrder Preserve(MarketplaceOrder current, MarketplaceOrder? previous)
    {
        if (previous is null || previous.Key != current.Key) return current;
        return current with
        {
            // Legacy DTO semantics are retained for existing adapters/clients. New fiscal evidence is typed below.
            ReceiptIds = current.ReceiptIds.Count > 0 ? current.ReceiptIds : previous.ReceiptIds,
            FiscalReceiptNumbers = current.FiscalReceiptNumbers.Concat(previous.FiscalReceiptNumbers).Distinct(StringComparer.Ordinal).ToArray(),
            FiscalReceiptUrls = current.FiscalReceiptUrls.Concat(previous.FiscalReceiptUrls).Distinct(StringComparer.Ordinal).ToArray(),
            FiscalReferences = current.FiscalReferences.Concat(previous.FiscalReferences)
                .Select(r => r with { VerifiedReceiptId = "", VerifiedAccountContext = "" }).Distinct().ToArray()
        };
    }
}
