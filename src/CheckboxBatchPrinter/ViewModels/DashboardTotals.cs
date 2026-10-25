using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.ViewModels;

internal static class DashboardTotals
{
    public static string Receipts(IEnumerable<ReceiptRowViewModel> rows)
    {
        var (count, unknown, sum) = ReceiptAmounts(rows);
        return $"Чеків: {count} · Сума за фільтром: {ReceiptAmount(count, unknown, sum)}" + Unknown(unknown);
    }

    public static string ReceiptReturns(IEnumerable<ReceiptRowViewModel> rows)
    {
        var (count, unknown, sum) = ReceiptAmounts(rows.Where(row =>
            string.Equals(row.RawType, ReceiptTypes.Return, StringComparison.OrdinalIgnoreCase)));
        return $"Повернення ({count}): {ReceiptAmount(count, unknown, sum)}" + Unknown(unknown);
    }

    private static (int Count, int Unknown, decimal Sum) ReceiptAmounts(IEnumerable<ReceiptRowViewModel> rows)
    {
        var count = 0;
        var unknown = 0;
        var sum = 0m;
        foreach (var row in rows)
        {
            count++;
            if (row.Model.TotalKnown) sum += row.Total;
            else unknown++;
        }
        return (count, unknown, sum);
    }

    public static string Orders(IEnumerable<MarketplaceOrderRowViewModel> rows)
    {
        var (count, unknown, amount) = OrderAmounts(rows, combineRozetkaHryvnia: false);
        return $"Замовлень: {count} · Сума за фільтром: {amount}" + Unknown(unknown);
    }

    public static string CombinedOrders(IEnumerable<MarketplaceOrderRowViewModel> rows)
    {
        var (_, unknown, amount) = OrderAmounts(rows.Where(row => !row.Model.IsCanceled), combineRozetkaHryvnia: true);
        return $"Prom + Rozetka: {amount}" + Unknown(unknown);
    }

    public static string CanceledOrders(IEnumerable<MarketplaceOrderRowViewModel> rows)
    {
        var (count, unknown, amount) = OrderAmounts(rows, combineRozetkaHryvnia: true);
        return $"Скасовані ({count}): {amount}" + Unknown(unknown);
    }

    private static (int Count, int Unknown, string Amount) OrderAmounts(
        IEnumerable<MarketplaceOrderRowViewModel> rows, bool combineRozetkaHryvnia)
    {
        var count = 0;
        var unknown = 0;
        var sums = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            count++;
            if (row.Model.Total is not { } total) { unknown++; continue; }
            var currency = row.Model.Currency.Trim().ToUpperInvariant();
            // The requested combined display treats Seller's missing currency as hryvnia.
            // Keep this inference out of the order model, cache and receipt matching.
            if (combineRozetkaHryvnia && currency.Length == 0 && row.Key.Marketplace == MarketplaceKind.Rozetka)
                currency = "UAH";
            sums[currency] = sums.GetValueOrDefault(currency) + total;
        }
        var amounts = sums.Where(pair => pair.Key.Length > 0)
            .Select(pair => $"{pair.Value:N2} {(combineRozetkaHryvnia && pair.Key == "UAH" ? "грн" : pair.Key)}").ToList();
        if (sums.TryGetValue("", out var unspecified)) amounts.Add($"{unspecified:N2} (валюта не надана)");
        var amount = amounts.Count > 0 ? string.Join("; ", amounts) : count == 0
            ? $"{0m:N2}" + (combineRozetkaHryvnia ? " грн" : "") : "—";
        return (count, unknown, amount);
    }

    private static string ReceiptAmount(int count, int unknown, decimal sum) =>
        count > 0 && count == unknown ? "—" : $"{sum:N2} грн";

    private static string Unknown(int count) => count > 0 ? $" · Без суми: {count}" : "";
}
