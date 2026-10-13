using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public sealed class ReceiptDetailsService(CheckboxApiClient api, ISettingsService settings) : IReceiptDetailsService
{
    internal const string PositionDiscountIssue = "Знижка або повернення в позиціях: потрібна перевірка структури документа.";
    internal const string ReceiptDiscountIssue = "Знижка або округлення в чеку: склад загальної суми потребує перевірки.";
    internal const string PositionTotalIssue = "Сума позицій чека відрізняється від загальної: потрібна перевірка структури оплати.";
    internal static bool IsDiscountIssue(string issue) => issue is PositionDiscountIssue or ReceiptDiscountIssue;
    internal static bool NeedsDiscountRevalidation(ReceiptRecord receipt, ReceiptDetails details) =>
        details.AmountValidationVersion < ReceiptDiscounts.ValidationVersion &&
        (IsDiscountIssue(receipt.AmountComparisonIssue) || IsDiscountIssue(details.AmountComparisonIssue) || details.AmountComparisonIssue == PositionTotalIssue);
    public async Task<ReceiptDetails> GetAsync(string receiptId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(receiptId, out var id)) throw new ArgumentException("Некоректний UUID чека.");
        var config = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var json = await api.GetStringAsync($"{config.ApiBaseUrl.TrimEnd('/')}/api/v1/receipts/{id:D}",
            "receipt.details", cancellationToken).ConfigureAwait(false);
        return Parse(json, id.ToString("D"));
    }

    public static ReceiptDetails Parse(string json, string expectedId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var id = Text(root, "id");
        if (!Guid.TryParse(id, out var actual) || !Guid.TryParse(expectedId, out var expected) || actual != expected)
            throw new JsonException("API повернуло деталі іншого чека.");
        var items = new List<OrderItem>();
        var complete = false;
        var structural = false;
        var issue = ReadAmountIssue(root);
        if (root.TryGetProperty("goods", out var goods) && goods.ValueKind == JsonValueKind.Array)
        {
            complete = goods.GetArrayLength() > 0;
            structural = complete;
            foreach (var item in goods.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("good", out var good) || good.ValueKind != JsonValueKind.Object)
                { complete = false; structural = false; continue; }
                if (HasValues(item, "discounts") || (item.TryGetProperty("is_return", out var isReturn) && isReturn.ValueKind == JsonValueKind.True)) complete = false;
                items.Add(new(Text(good, "name"), Text(good, "code"), Number(item, "quantity") / 1000m,
                    Number(good, "price") / 100m, Number(item, "sum") / 100m));
            }
        }
        if (HasValues(root, "discounts") || HasValues(root, "pre_payment_relation_id") || Number(root, "round_sum") is not (null or 0)) complete = false;
        if (Number(root, "total_sum") is not { } totalMinor || items.Any(item => item.Total is null) ||
            items.Sum(item => item.Total ?? 0) != totalMinor / 100m) complete = false;
        if (structural && Number(root, "total_sum") is { } total && items.All(i => i.Total.HasValue) &&
            items.Sum(i => i.Total!.Value) != total / 100m)
            issue = PositionTotalIssue;
        ReceiptDiscounts.TryRead(root, out var discount);
        if (structural && discount is not null) { complete = true; issue = ""; }
        return new(id, items, Text(root, "related_receipt_id"), Text(root, "order_id"),
            root.TryGetProperty("context", out var context) && context.ValueKind == JsonValueKind.Object && context.EnumerateObject().Any(), complete)
            { ItemListComplete = structural, AmountComparisonIssue = issue,
                AmountValidationVersion = ReceiptDiscounts.ValidationVersion, DiscountReconciliation = discount };
    }

    internal static string ReadAmountIssue(JsonElement root)
    {
        if (HasValues(root, "pre_payment_relation_id")) return "Передоплата / часткова оплата: потрібен точний або ручний зв’язок.";
        if (HasValues(root, "discounts") || Number(root, "round_sum") is not (null or 0))
            return ReceiptDiscountIssue;
        if (root.TryGetProperty("goods", out var goods) && goods.ValueKind == JsonValueKind.Array &&
            goods.EnumerateArray().Any(g => g.ValueKind == JsonValueKind.Object && (HasValues(g, "discounts") ||
                g.TryGetProperty("is_return", out var returned) && returned.ValueKind == JsonValueKind.True)))
            return PositionDiscountIssue;
        return "";
    }

    private static string Text(JsonElement item, string field) => item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static decimal? Number(JsonElement item, string field) => item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : null;
    private static bool HasValues(JsonElement item, string field) => item.TryGetProperty(field, out var value) && (value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.Array => value.GetArrayLength() > 0,
        JsonValueKind.String => !string.IsNullOrEmpty(value.GetString()),
        _ => true
    });
}
