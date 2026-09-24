using System.Text;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

/// <summary>Deliberately conservative, local-only comparison. It never manufactures fiscal identity.</summary>
internal static class BasketReceiptMatching
{
    private sealed record Line(string Name, decimal Quantity, decimal Price, decimal Total);

    internal static MarketplaceOrder? TrySuggest(ReceiptRecord receipt, string account,
        IReadOnlyList<MarketplaceOrder> orders, IReadOnlyList<ReceiptOrderDecision> decisions,
        IReadOnlySet<OrderKey> rejected, ReceiptDetails? details, IReadOnlyList<ReceiptRecord>? receiptScope,
        IReadOnlyDictionary<string, ReceiptDetails>? detailsScope)
    {
        if (receipt.Type != ReceiptTypes.Sell || receipt.Status != "DONE" || receiptScope is null || detailsScope is null ||
            !receiptScope.Any(r => SameId(r.Id, receipt.Id))) return null;
        details ??= FindDetails(detailsScope, receipt.Id);
        if (details is null || !SameId(details.Id, receipt.Id) || !details.ItemsComplete ||
            !TryBasket(details.Items, receipt.TotalSum, requireExplicitLineTotal: true, out var receiptBasket)) return null;

        // Count before applying rejections or fiscal exclusions: dismissing evidence is not uniqueness.
        if (orders.Any(order => !order.Total.HasValue && IsCompatibleCurrency(order) &&
                (order.CreatedAt is null || receipt.DisplayDate is { } date &&
                    order.CreatedAt <= date && order.CreatedAt >= date.AddDays(-30)))) return null;
        if (orders.Any(order => order.Total == receipt.TotalSum && !order.CreatedAt.HasValue)) return null;
        // An incomplete same-amount/date order could contain this basket; do not silently discard it.
        if (orders.Any(order => IsCandidate(receipt, order) && (!order.ItemsComplete ||
                !TryBasket(order.Items, order.Total!.Value, requireExplicitLineTotal: false, out _)))) return null;
        var matchingOrders = orders.Where(order => IsCandidate(receipt, order) &&
            TryBasket(order.Items, order.Total!.Value, requireExplicitLineTotal: false, out var orderBasket) &&
            receiptBasket.SequenceEqual(orderBasket)).ToArray();
        if (matchingOrders.Length != 1) return null;
        var selected = matchingOrders[0];
        if (rejected.Contains(selected.Key) || HasFiscalEvidence(selected) || selected.Discount is not (null or 0m)) return null;
        if (receiptScope.Any(r => r.Type == ReceiptTypes.Sell && r.TotalSum == selected.Total && !r.DisplayDate.HasValue)) return null;

        // A user's confirmed link anywhere in this account cannot be replaced by a hypothesis.
        var currentDecisions = decisions.Where(d => d.AccountContext == account)
            .GroupBy(d => NormalizeId(d.ReceiptId), StringComparer.Ordinal)
            .Select(g => g.MaxBy(d => d.UpdatedAtUtc)!);
        if (currentDecisions.Any(d => d.ConfirmedOrder == selected.Key && !SameId(d.ReceiptId, receipt.Id))) return null;

        // Fail closed when another same-amount, eligible sale lacks complete item details. This
        // prevents the first HTTP result from winning a race against a competing receipt's details.
        foreach (var competing in receiptScope.Where(r => !SameId(r.Id, receipt.Id) && IsCandidate(r, selected))
                     .DistinctBy(r => NormalizeId(r.Id)))
        {
            var competingDetails = FindDetails(detailsScope, competing.Id);
            if (competingDetails is null || !competingDetails.ItemsComplete ||
                !TryBasket(competingDetails.Items, competing.TotalSum, requireExplicitLineTotal: true, out var basket)) return null;
            if (receiptBasket.SequenceEqual(basket)) return null;
        }
        return selected;
    }

    private static bool IsCandidate(ReceiptRecord receipt, MarketplaceOrder order) =>
        receipt.Type == ReceiptTypes.Sell && receipt.TotalSum > 0 && order.Total == receipt.TotalSum &&
        IsCompatibleCurrency(order) &&
        receipt.DisplayDate is { } printed && order.CreatedAt is { } created &&
        created <= printed && created >= printed.AddDays(-30);

    private static bool IsCompatibleCurrency(MarketplaceOrder order) => string.IsNullOrWhiteSpace(order.Currency) ||
        string.Equals(order.Currency, "UAH", StringComparison.OrdinalIgnoreCase);

    private static bool HasFiscalEvidence(MarketplaceOrder order) => order.ReceiptIds.Count > 0 ||
        order.FiscalReferences.Count > 0 || order.FiscalReceiptNumbers.Count > 0 || order.FiscalReceiptUrls.Count > 0;

    private static ReceiptDetails? FindDetails(IReadOnlyDictionary<string, ReceiptDetails> details, string id) =>
        details.Values.FirstOrDefault(d => SameId(d.Id, id));

    private static bool TryBasket(IReadOnlyList<OrderItem> items, decimal total, bool requireExplicitLineTotal,
        out Line[] lines)
    {
        lines = [];
        if (items.Count == 0 || total <= 0) return false;
        var result = new List<Line>(items.Count);
        try
        {
            foreach (var item in items)
            {
                var name = NormalizeName(item.Name);
                if (name.Length == 0 || item.Quantity is not { } quantity || quantity <= 0 ||
                    item.UnitPrice is not { } price || price < 0 || requireExplicitLineTotal && !item.Total.HasValue) return false;
                var lineTotal = item.Total ?? checked(quantity * price);
                // No discount/allocation/rounding guesses, no removing unknown or delivery rows.
                if (lineTotal < 0 || lineTotal != checked(quantity * price)) return false;
                result.Add(new(name, quantity, price, lineTotal));
            }
            if (result.Sum(line => line.Total) != total) return false;
            // APIs may split identical units into separate positions. Aggregate only identical
            // full names AND unit prices; quantities and totals are still compared exactly.
            lines = result.GroupBy(line => (line.Name, line.Price))
                .Select(group => new Line(group.Key.Name, group.Sum(line => line.Quantity),
                    group.Key.Price, group.Sum(line => line.Total)))
                .OrderBy(line => line.Name, StringComparer.Ordinal).ThenBy(line => line.Price)
                .ThenBy(line => line.Quantity).ThenBy(line => line.Total).ToArray();
        }
        catch (OverflowException) { return false; }
        return true;
    }

    // Presentation-only normalization. Do not strip punctuation, numbers or model codes, guess
    // translations, compare partial words, or assume SKU namespaces are shared between stores.
    private static string NormalizeName(string name)
    {
        var normalized = new string(name.Normalize(NormalizationForm.FormC).Select(character => character switch
        {
            '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' => '-',
            '\u2018' or '\u2019' or '\u201a' or '\u201b' => '\'',
            '\u201c' or '\u201d' or '\u201e' or '\u201f' or '\u00ab' or '\u00bb' => '"',
            _ => character
        }).ToArray());
        return string.Join(" ", normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }
    private static string NormalizeId(string value) => Guid.TryParse(value, out var id) ? id.ToString("D") : value;
    private static bool SameId(string left, string right) => NormalizeId(left) == NormalizeId(right);
}
