using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task QuantityAutomaticUiAsync()
    {
        var fixture = new Fixture(); var cache = new AutomaticCache();
        var first = Order() with { Total = 201, Items = [new("Кабель живлення", "", 1, 201, 201)], ItemListComplete = true };
        var second = first with { Key = first.Key with { OrderId = "42" }, Number = "ORDER-42",
            Items = [new("Кабель живлення", "", 1, 100, 100), new("Адаптер живлення", "", 1, 101, 101)] };
        fixture.Source.Orders = [first, second];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 20100), Receipt(ReceiptTwo, 2, 20100)];
        fixture.Details.Values[ReceiptOne] = new(ReceiptOne, [new("Кабель питания", "", 1, 201, 201)]) { ItemListComplete = true };
        fixture.Details.Values[ReceiptTwo] = new(ReceiptTwo, [new("Кабель питания", "", 1, 100, 100), new("Адаптер питания", "", 1, 101, 101)]) { ItemListComplete = true };
        var (main, workspace) = fixture.Create(automaticCache: cache);
        await main.RefreshAsync(); await OpenOrdersAsync(main, workspace);
        foreach (var row in main.Receipts)
        {
            Equal(ReceiptLinkState.Suggested, row.OrderMatch!.State);
            Equal(AutomaticLinkBasis.AmountAndQuantity, row.OrderMatch.Basis);
            Equal(row.Id == ReceiptOne ? first.Key : second.Key, row.OrderMatch.Order!.Key);
            Equal("Автозв’язок: сума й кількість товарів", row.LinkStatus);
        }
        True(workspace.Orders.Cast<MarketplaceOrderRowViewModel>().All(o => o.HasSuggestedLink && o.LinkStatus.Contains("кількість")));
        Equal(2, fixture.Details.Calls);
        var (restarted, restored) = fixture.Create(automaticCache: cache);
        await restarted.RefreshAsync(); restarted.SelectedTabIndex = 1; await restarted.PrepareOrdersAsync();
        True(restarted.Receipts.All(r => r.OrderMatch?.Basis == AutomaticLinkBasis.AmountAndQuantity));
        Equal(2, fixture.Details.Calls);
        // A third order with the same amount AND count defeats the old unique-count link,
        // even while hidden in the order table. The unrelated two-unit pair survives.
        fixture.Source.Orders = [first, second, first with { Key = first.Key with { OrderId = "43" }, Number = "HIDDEN" }];
        restored.OrderSearch = "ORDER";
        await ExecuteAsync(restored.AutoMatchCommand);
        True(restarted.Receipts.Single(r => r.Id == ReceiptOne).OrderMatch!.Order is null);
        Equal(second.Key, restarted.Receipts.Single(r => r.Id == ReceiptTwo).OrderMatch!.Order!.Key);
        Equal(2, fixture.Details.Calls); Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls);
    }
}
