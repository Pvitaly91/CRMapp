namespace CheckboxBatchPrinter.ViewModels;

internal static class DashboardTotals
{
    public static string Receipts(IEnumerable<ReceiptRowViewModel> rows)
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
        var amount = count > 0 && count == unknown ? "—" : $"{sum:N2} грн";
        return $"Чеків: {count} · Сума за фільтром: {amount}" + Unknown(unknown);
    }

    public static string Orders(IEnumerable<MarketplaceOrderRowViewModel> rows)
    {
        var count = 0;
        var unknown = 0;
        var sums = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            count++;
            if (row.Model.Total is not { } total) { unknown++; continue; }
            var currency = row.Model.Currency.Trim().ToUpperInvariant();
            sums[currency] = sums.GetValueOrDefault(currency) + total;
        }
        var amounts = sums.Where(pair => pair.Key.Length > 0)
            .Select(pair => $"{pair.Value:N2} {pair.Key}").ToList();
        if (sums.TryGetValue("", out var unspecified)) amounts.Add($"{unspecified:N2} (валюта не надана)");
        var amount = amounts.Count > 0 ? string.Join("; ", amounts) : count == 0 ? $"{0m:N2}" : "—";
        return $"Замовлень: {count} · Сума за фільтром: {amount}" + Unknown(unknown);
    }

    private static string Unknown(int count) => count > 0 ? $" · Без суми: {count}" : "";
}
