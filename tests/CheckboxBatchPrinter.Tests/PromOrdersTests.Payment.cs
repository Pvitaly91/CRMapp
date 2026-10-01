using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static partial class PromOrdersTests
{
    private static async Task PaymentMatchingAsync()
    {
        var calls = 0;
        using var http = Http((request, _) =>
        {
            calls++;
            var id = request.RequestUri!.AbsolutePath.EndsWith("501", StringComparison.Ordinal) ? 501 : 502;
            return Task.FromResult(Json(JsonSerializer.Serialize(new { order = new
            {
                id, date_created = "2026-09-23T07:00:00Z", price = "280 грн",
                payment_option = new { name = id == 501 ? "Наложенный платеж" : "Пром-оплата" },
                payment_data = new { status = id == 501 ? "unpaid" : "paid" }, delivery_option = new { name = "Нова Пошта" },
                delivery_provider_data = new { provider = "nova_poshta", declaration_number = "SYNTHETIC" },
                products = new[] { new { name = "Запасна кришка", quantity = 1, price = "280 грн", total_price = "280 грн" } }
            } })));
        });
        var client = Client(http); var orders = new[] { (await client.GetOrderAsync(Connection, Credentials, "501"))!, (await client.GetOrderAsync(Connection, Credentials, "502"))! };
        var receipts = Enumerable.Range(0, 2).Select(i => new ReceiptRecord
        {
            Id = $"e7300000-0000-0000-0000-{i + 1:D12}", Type = "SELL", Status = "DONE", FiscalDate = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.FromHours(3)),
            TotalSumMinor = 28000, Payments = [new("CASHLESS", i == 0 ? "Платіж NovaPay" : "Платіж RozetkaPay", 28000)]
        }).ToArray();
        var details = receipts.Select((r, i) => new ReceiptDetails(r.Id, orders[i].Items) { ItemListComplete = true }).ToDictionary(d => d.Id);
        var result = new ReceiptOrderMatchingService().MatchAll(receipts, "payment-api-test", orders, [], true, details);
        Assert(orders[0].PaymentStatus == "unpaid" && orders[1].PaymentStatus == "paid", "Payment status was changed or treated as provider.");
        for (var i = 0; i < 2; i++) Assert(result[receipts[i].Id].Order?.Key == orders[i].Key && result[receipts[i].Id].Basis == AutomaticLinkBasis.AmountAndPayment,
            "Prom payment fields did not reach the matcher.");
        Assert(calls == 2, "Payment criterion introduced an additional API call.");
    }
}
