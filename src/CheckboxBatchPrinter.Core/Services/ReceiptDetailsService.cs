using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public sealed class ReceiptDetailsService(CheckboxApiClient api, ISettingsService settings) : IReceiptDetailsService
{
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
        if (root.TryGetProperty("goods", out var goods) && goods.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in goods.EnumerateArray())
            {
                if (!item.TryGetProperty("good", out var good) || good.ValueKind != JsonValueKind.Object) continue;
                items.Add(new(Text(good, "name"), Text(good, "code"), Number(item, "quantity") / 1000m,
                    Number(good, "price") / 100m, Number(item, "sum") / 100m));
            }
        }
        return new(id, items, Text(root, "related_receipt_id"), Text(root, "order_id"),
            root.TryGetProperty("context", out var context) && context.ValueKind == JsonValueKind.Object && context.EnumerateObject().Any());
    }

    private static string Text(JsonElement item, string field) => item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static decimal? Number(JsonElement item, string field) => item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : null;
}
