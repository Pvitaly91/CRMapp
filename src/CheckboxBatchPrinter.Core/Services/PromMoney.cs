using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

// Prom returns localized major-unit amounts, e.g. "300 грн". Recognize a
// bounded grammar, not arbitrary removal of everything except digits.
internal static class PromMoney
{
    private static readonly Regex Format = new(
        @"\A(?<amount>[+-]?(?:[0-9]+|[0-9]{1,3}(?:[ \u00a0\u202f][0-9]{3})+)(?:[.,][0-9]{1,2})?)[ \u00a0\u202f]*(?<currency>грн\.?|UAH|₴)?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static (decimal? Amount, string Currency) Parse(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDecimal(out var amount) => (amount, ""),
        JsonValueKind.String => Parse(value.GetString()),
        _ => (null, "")
    };

    internal static (decimal? Amount, string Currency) Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl)) return (null, "");
        var match = Format.Match(value.Trim());
        if (!match.Success) return (null, "");
        var number = match.Groups["amount"].Value.Replace(" ", "").Replace("\u00a0", "")
            .Replace("\u202f", "").Replace(',', '.');
        if (!decimal.TryParse(number, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var amount)) return (null, "");
        return (amount, match.Groups["currency"].Success ? "UAH" : "");
    }

    // Old cache retained RawTotal, but not raw item prices. Recover only known
    // totals in memory, without fabricating products, freshness or fiscal proof.
    internal static MarketplaceOrder RestoreCachedTotal(MarketplaceOrder order)
    {
        if (order.Key.Marketplace != MarketplaceKind.Prom || order.Total.HasValue) return order;
        var parsed = Parse(order.RawTotal);
        if (!parsed.Amount.HasValue || (parsed.Currency.Length > 0 && order.Currency.Length > 0 &&
            !string.Equals(order.Currency, parsed.Currency, StringComparison.OrdinalIgnoreCase))) return order;
        return order with { Total = parsed.Amount, Currency = order.Currency.Length > 0 ? order.Currency : parsed.Currency };
    }
}
