using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

internal static class ReceiptDiscounts
{
    internal const int ValidationVersion = 1;

    // Checkbox's calculated discount sum is signed minor units. Use its declared
    // amount and verify the entire document; never infer a discount from a gap alone.
    internal static bool TryRead(JsonElement root, out ReceiptDiscountReconciliation? evidence)
    {
        evidence = null;
        try
        {
            if (Text(root, "type") != "SELL" || Text(root, "status") != "DONE" ||
                Number(root, "total_sum") is not { } total || total <= 0 || decimal.Truncate(total) != total ||
                root.TryGetProperty("round_sum", out var rounding) && rounding.ValueKind != JsonValueKind.Null && Number(root, "round_sum") != 0 ||
                Present(root, "pre_payment_relation_id") ||
                !root.TryGetProperty("goods", out var goods) || goods.ValueKind != JsonValueKind.Array || goods.GetArrayLength() == 0)
                return false;
            decimal gross = 0, itemDiscount = 0;
            var discounts = new List<decimal>();
            foreach (var item in goods.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    item.TryGetProperty("is_return", out var returned) && returned.ValueKind is not (JsonValueKind.False or JsonValueKind.Null) ||
                    !item.TryGetProperty("good", out var good) || good.ValueKind != JsonValueKind.Object ||
                    Number(good, "price") is not { } price || price < 0 || decimal.Truncate(price) != price ||
                    Number(item, "quantity") is not { } quantity || quantity <= 0 || decimal.Truncate(quantity) != quantity ||
                    Number(item, "sum") is not { } lineSum || lineSum < 0 || decimal.Truncate(lineSum) != lineSum ||
                    checked(price * quantity / 1000m) != lineSum ||
                    !ReadReductions(item, lineSum, out var reduction)) return false;
                gross = checked(gross + lineSum); itemDiscount = checked(itemDiscount + reduction);
                discounts.Add(reduction / 100m);
            }
            if (!ReadReductions(root, gross - itemDiscount, out var receiptDiscount)) return false;
            var discount = checked(itemDiscount + receiptDiscount);
            if (discount <= 0 || checked(gross - discount) != total) return false;
            evidence = new(gross / 100m, discount / 100m, total / 100m, discounts, receiptDiscount / 100m);
            return true;
        }
        catch (OverflowException) { return false; }
    }

    private static bool ReadReductions(JsonElement owner, decimal baseMinor, out decimal reduction)
    {
        reduction = 0;
        if (!owner.TryGetProperty("discounts", out var entries) || entries.ValueKind == JsonValueKind.Null) return true;
        if (entries.ValueKind != JsonValueKind.Array) return false;
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || Text(entry, "type") != "DISCOUNT" ||
                Number(entry, "value") is not { } value || value <= 0 ||
                Number(entry, "sum") is not { } sum || sum >= 0 || decimal.Truncate(sum) != sum)
                return false;
            var amount = -sum;
            var remaining = checked(baseMinor - reduction);
            if (amount > remaining) return false;
            switch (Text(entry, "mode"))
            {
                case "VALUE" when decimal.Truncate(value) == value && value == amount: break;
                case "PERCENT" when value <= 100 && Math.Abs(checked(remaining * value / 100m) - amount) <= 0.5m: break;
                default: return false;
            }
            reduction = checked(reduction + amount);
        }
        return true;
    }

    internal static string MatchingExplanation(ReceiptRecord receipt, MarketplaceOrder order, ReceiptDetails? details,
        ProductComparison products)
    {
        if (products != ProductComparison.Match || details is not { AmountValidationVersion: ValidationVersion,
                ItemsComplete: true, DiscountReconciliation: { Schema: 1 } discount } ||
            BasketReceiptMatching.NormalizeId(details.Id) != BasketReceiptMatching.NormalizeId(receipt.Id) ||
            !receipt.TotalKnown || discount.NetTotal != receipt.TotalSum || order.Total != receipt.TotalSum ||
            receipt.AmountComparisonIssue.Length > 0 && !ReceiptDetailsService.IsDiscountIssue(receipt.AmountComparisonIssue) ||
            details.AmountComparisonIssue.Length > 0 ||
            order.AmountComparisonIssue.Length > 0 && order.AmountComparisonIssue != "Знижки Prom: склад загальної суми потребує перевірки." ||
            order.Discount is { } stated && stated != 0 && stated != discount.DiscountTotal ||
            !(order.ItemListComplete ?? order.ItemsComplete) || discount.ItemDiscounts is null ||
            discount.ItemDiscounts.Count != details.Items.Count || discount.ReceiptDiscount < 0 || discount.DiscountTotal <= 0)
            return "";
        try
        {
            var receiptGross = GrossByProduct(details.Items, requireLineTotals: true);
            var orderGross = GrossByProduct(order.Items, requireLineTotals: false);
            if (receiptGross is null || orderGross is null || !receiptGross.Keys.ToHashSet().SetEquals(orderGross.Keys) ||
                receiptGross.Any(pair => orderGross[pair.Key] != pair.Value) ||
                receiptGross.Values.Sum() != discount.GrossTotal ||
                discount.ItemDiscounts.Any(value => value < 0) ||
                discount.ItemDiscounts.Select((value, index) => value > details.Items[index].Total).Any(value => value) ||
                checked(discount.ItemDiscounts.Sum() + discount.ReceiptDiscount) != discount.DiscountTotal ||
                checked(discount.GrossTotal - discount.DiscountTotal) != discount.NetTotal)
                return "";
            return $"Знижку враховано: {discount.GrossTotal:0.##} − {discount.DiscountTotal:0.##} = {discount.NetTotal:0.##} грн. " +
                "Суми товарів до знижки, повні назви та кількості збігаються; розрахована знижка підтверджена Checkbox. ";
        }
        catch (OverflowException) { return ""; }
    }

    private static Dictionary<string, decimal>? GrossByProduct(IReadOnlyList<OrderItem> items, bool requireLineTotals)
    {
        if (items.Count == 0) return null;
        var groups = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var name = ProductNameIdentity.Normalize(item.Name);
            if (name.Length == 0 || item.Quantity is not > 0 || item.UnitPrice is not >= 0) return null;
            var gross = checked(item.Quantity.Value * item.UnitPrice.Value);
            if (requireLineTotals && !item.Total.HasValue || item.Total is { } total && total != gross) return null;
            groups[name] = checked(groups.GetValueOrDefault(name) + gross);
        }
        return groups;
    }

    private static decimal? Number(JsonElement owner, string name) => owner.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : null;
    private static string Text(JsonElement owner, string name) => owner.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool Present(JsonElement owner, string name) => owner.TryGetProperty(name, out var value) &&
        value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) &&
        !(value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()));
}
