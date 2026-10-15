using System.Net.Http;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static partial class RozetkaOrdersTests
{
    private static Dictionary<string, object?> AmountRow() => new()
    {
        ["id"] = 100, ["created"] = "2026-09-23 10:00:00", ["amount"] = "390.00", ["amount_with_discount"] = "390.00",
        ["cost"] = "439.00", ["cost_with_discount"] = "439.00", ["delivery"] = new { cost = 49 },
        ["user"] = new { contact_fio = "Synthetic buyer" }, ["prro"] = new { prro_receipt_status = 0 },
        ["purchases"] = new[] { new { item_name = "Synthetic cable", quantity = 1, price_with_discount = "390.00", cost_with_discount = "390.00" } }
    };

    private static async Task<MarketplaceOrder> ParseAmountRowAsync(Dictionary<string, object?> row, bool list = false)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++; Equal(list ? "/orders/search" : "/orders/100", request.RequestUri!.AbsolutePath);
            return Task.FromResult(list ? Page(1, 1, row) : Json(new { success = true, content = row }));
        }));
        var client = new RozetkaOrdersClient(new MarketplaceHttpTransport(http));
        var order = list ? (await client.FetchAsync(Connection, Credentials, Range)).Orders.Single()
            : (await client.GetOrderAsync(Connection, Credentials, "100"))!;
        Equal(1, calls); return order;
    }

    private static async Task MerchandiseAmountAsync()
    {
        foreach (var list in new[] { false, true })
        {
            var row = AmountRow();
            var order = await ParseAmountRowAsync(row, list);
            Equal(390m, order.Total); Equal("390.00", order.RawTotal); Equal(49m, order.DeliveryCost); Equal(0m, order.Discount);
            Equal(390m, order.Items.Single().Total); Equal("", order.Currency); Equal("", order.AmountComparisonIssue);
            row["amount"] = "450.00"; row["cost"] = "499.00";
            var discounted = await ParseAmountRowAsync(row, list);
            Equal(390m, discounted.Total); Equal(60m, discounted.Discount); Equal(49m, discounted.DeliveryCost);
        }
        var goods = await ParseAmountRowAsync(AmountRow());
        var receipt = new ReceiptRecord { Id = "76000000-0000-0000-0000-000000000001", Type = ReceiptTypes.Sell,
            Status = "DONE", TotalSumMinor = 39000, FiscalDate = goods.CreatedAt!.Value.AddHours(1) };
        var details = new Dictionary<string, ReceiptDetails> { [receipt.Id] = new(receipt.Id, [new("Synthetic cable", "", 1, 390, 390)])
            { ItemsComplete = true, ItemListComplete = true } };
        var match = new ReceiptOrderMatchingService().MatchAll([receipt], "synthetic-account", [goods], [], true, details)[receipt.Id];
        True(match.Order is null); True(match.State != ReceiptLinkState.Suggested);
        Equal(AutomaticLinkBasis.None, match.Basis);
    }

    private static async Task MerchandiseAmountPriorityAsync()
    {
        foreach (var missing in new object?[] { null, "", " " })
        {
            var row = AmountRow(); row["amount_with_discount"] = missing;
            var order = await ParseAmountRowAsync(row); Equal(390m, order.Total); Equal("390.00", order.RawTotal);
        }
        var absent = AmountRow(); absent.Remove("amount_with_discount");
        Equal(390m, (await ParseAmountRowAsync(absent)).Total);
        var numeric = AmountRow(); numeric["amount_with_discount"] = 390.25m;
        Equal(390.25m, (await ParseAmountRowAsync(numeric)).Total);
        var zero = AmountRow(); zero["amount_with_discount"] = "0.00";
        Equal(0m, (await ParseAmountRowAsync(zero)).Total);
        foreach (var invalid in new object[] { "bad amount", "390,00", "-1.00", true, new { unexpected = 390 } })
        {
            var row = AmountRow(); row["amount_with_discount"] = invalid;
            var order = await ParseAmountRowAsync(row);
            True(order.Total is null); True(order.AmountComparisonIssue.Length > 0);
            True(order.RawTotal != "439.00" && order.RawTotal != "390.00");
        }
        var malformedAmount = AmountRow(); malformedAmount.Remove("amount_with_discount"); malformedAmount["amount"] = "bad amount";
        var unknown = await ParseAmountRowAsync(malformedAmount);
        True(unknown.Total is null); True(unknown.AmountComparisonIssue.Length > 0);
    }

    private static async Task CheckoutAmountGuardAsync()
    {
        var row = AmountRow(); row.Remove("amount"); row.Remove("amount_with_discount");
        var order = await ParseAmountRowAsync(row);
        Equal(439m, order.Total); Equal("439.00", order.RawTotal); Equal(49m, order.DeliveryCost);
        True(order.AmountComparisonIssue.Length > 0);
        var receipt = new ReceiptRecord { Id = "76000000-0000-0000-0000-000000000002", Type = ReceiptTypes.Sell,
            Status = "DONE", TotalSumMinor = 43900, FiscalDate = order.CreatedAt!.Value.AddHours(1) };
        // Even a matching basket cannot turn checkout cost into proven merchandise value.
        var candidate = order with { Items = [new("Synthetic cable", "", 1, 439, 439)] };
        var details = new Dictionary<string, ReceiptDetails> { [receipt.Id] = new(receipt.Id, [new("Synthetic cable", "", 1, 439, 439)])
            { ItemsComplete = true, ItemListComplete = true } };
        var match = new ReceiptOrderMatchingService().MatchAll([receipt], "synthetic-account", [candidate], [], true, details)[receipt.Id];
        True(match.Order is null); True(match.State != ReceiptLinkState.Suggested);
        row["delivery"] = new { cost = 0 };
        var legacy = await ParseAmountRowAsync(row); Equal(439m, legacy.Total); Equal("", legacy.AmountComparisonIssue);
        row.Remove("delivery"); row.Remove("cost_with_discount");
        Equal(439m, (await ParseAmountRowAsync(row)).Total);
        row.Remove("cost");
        True((await ParseAmountRowAsync(row)).Total is null);
    }
}
