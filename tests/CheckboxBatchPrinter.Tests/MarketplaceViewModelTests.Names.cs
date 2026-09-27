using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task PromUkrainianNamesUiAsync()
    {
        var fixture = new Fixture(); var cache = new AutomaticCache();
        var oldOrders = (await PromOrdersTests.ReadLanguageOrdersAsync(false))
            .Select(o => o with { Key = o.Key with { ConnectionId = "prom-test" } }).ToArray();
        var ukrainianOrders = (await PromOrdersTests.ReadLanguageOrdersAsync(true))
            .Select(o => o with { Key = o.Key with { ConnectionId = "prom-test" } }).ToArray();
        fixture.Source.Orders = oldOrders;
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 20100), Receipt(ReceiptTwo, 2, 20100)];
        fixture.Details.Values[ReceiptOne] = new(ReceiptOne, ukrainianOrders[0].Items) { ItemListComplete = true };
        fixture.Details.Values[ReceiptTwo] = new(ReceiptTwo, ukrainianOrders[1].Items) { ItemListComplete = true };
        var (main, workspace) = fixture.Create(automaticCache: cache);
        await main.RefreshAsync(); await OpenOrdersAsync(main, workspace);
        True(main.Receipts.All(r => r.OrderMatch!.Ambiguous && r.OrderMatch.Order is null));
        main.Receipts[0].IsSelectedForOrders = true;
        fixture.Source.Orders = ukrainianOrders;
        await ExecuteAsync(workspace.AutoMatchCommand);
        foreach (var row in main.Receipts)
        {
            Equal("Автозв’язок: сума й товари", row.LinkStatus);
            Equal(row.Id == ReceiptOne ? ukrainianOrders[0].Key : ukrainianOrders[1].Key, row.OrderMatch!.Order!.Key);
        }
        True(workspace.Orders.Cast<MarketplaceOrderRowViewModel>().All(o => o.LinkStatus == "Автозв’язок: сума й товари"));
        True(main.Receipts[0].IsSelectedForOrders);
        Equal(2, fixture.Details.Calls);

        // Restart from serialized caches; no live refresh or repeated receipt details needed.
        fixture.Source.Fail = true;
        var fetches = fixture.Source.FetchCalls;
        var (restarted, restored) = fixture.Create(automaticCache: cache);
        await restarted.RefreshAsync(); restarted.SelectedTabIndex = 1; await restarted.PrepareOrdersAsync();
        True(restarted.Receipts.All(r => r.OrderMatch?.Basis == AutomaticLinkBasis.AmountAndProducts));
        True(restored.Orders.Cast<MarketplaceOrderRowViewModel>().All(o => o.HasSuggestedLink));
        True(fixture.Cache.Snapshot.Orders.All(o => o.Items[0].Name.StartsWith("Кухонний рушник", StringComparison.Ordinal)));
        Equal(fetches, fixture.Source.FetchCalls); Equal(2, fixture.Details.Calls);
        Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
    }

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
