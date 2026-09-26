using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task NameAutomaticUiAsync()
    {
        var fixture = new Fixture(); var cache = new AutomaticCache();
        var cable = Order() with { Total = 201, Items = [new("Червоний кабель USB", "", 1, 201, 201)], ItemListComplete = true };
        var sensor = cable with { Key = cable.Key with { OrderId = "42" }, Number = "ORDER-42",
            Items = [new("Датчик руху PIR", "", 1, 201, 201)] };
        fixture.Source.Orders = [cable, sensor];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 20100), Receipt(ReceiptTwo, 2, 20100)];
        fixture.Details.Values[ReceiptOne] = new(ReceiptOne, [new("Кабель USB червоний", "", 1, 201, 201)]) { ItemListComplete = true };
        fixture.Details.Values[ReceiptTwo] = new(ReceiptTwo, sensor.Items) { ItemListComplete = true };
        var (main, workspace) = fixture.Create(automaticCache: cache);
        await main.RefreshAsync(); await OpenOrdersAsync(main, workspace);
        foreach (var row in main.Receipts)
        {
            Equal(ReceiptLinkState.Suggested, row.OrderMatch!.State);
            Equal(AutomaticLinkBasis.AmountAndProducts, row.OrderMatch.Basis);
            Equal(row.Id == ReceiptOne ? cable.Key : sensor.Key, row.OrderMatch.Order!.Key);
            Equal("Автозв’язок: сума й товари", row.LinkStatus);
        }
        True(workspace.Orders.Cast<MarketplaceOrderRowViewModel>().All(o => o.HasSuggestedLink && o.LinkStatus == "Автозв’язок: сума й товари"));
        Equal(2, fixture.Details.Calls);
        var (restarted, restored) = fixture.Create(automaticCache: cache);
        await restarted.RefreshAsync(); restarted.SelectedTabIndex = 1; await restarted.PrepareOrdersAsync();
        True(restarted.Receipts.All(r => r.OrderMatch?.Basis == AutomaticLinkBasis.AmountAndProducts));
        Equal(2, fixture.Details.Calls);

        // A differently spelled, potentially identical product stays a competitor even
        // behind the table search. Cached probable pairs must not reserve its receipt.
        fixture.Source.Orders = [sensor, cable, cable with { Key = cable.Key with { OrderId = "43" }, Number = "HIDDEN",
            Items = [new("Кабель питания USB", "", 1, 201, 201)] }];
        restored.OrderSearch = "ORDER";
        await ExecuteAsync(restored.AutoMatchCommand);
        var ambiguous = restarted.Receipts.Single(r => r.Id == ReceiptOne);
        True(ambiguous.OrderMatch!.Order is null && ambiguous.OrderMatch.Ambiguous);
        Equal(2, ambiguous.OrderMatch.CompetingOrderCount);
        True(!ambiguous.LinkStatus.StartsWith("Автозв’язок"));
        Equal(sensor.Key, restarted.Receipts.Single(r => r.Id == ReceiptTwo).OrderMatch!.Order!.Key);
        Equal(2, fixture.Details.Calls); Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls);
    }
}
