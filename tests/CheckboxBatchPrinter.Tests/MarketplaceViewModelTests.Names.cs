using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task ContactUnconnectedMarketplaceUiAsync()
    {
        const string contacts = "Клеми кільце та вилка без ізоляції, набір 320 шт, мідні наконечники для проводів";
        const string toggle = "Вимикач маси АКБ 12/24V 500A універсальний, відсікач живлення акумулятора, сріблясті клеми";
        var fixture = new Fixture(); var cache = new AutomaticCache();
        var order = Order() with { Total = 270, Items = [new(contacts, "", 1, 270, 270)], ItemsComplete = true, ItemListComplete = true };
        fixture.Source.Orders = [order];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 27000), Receipt(ReceiptTwo, 2, 27000)];
        fixture.Details.Values[ReceiptOne] = new(ReceiptOne, order.Items) { ItemListComplete = true };
        fixture.Details.Values[ReceiptTwo] = new(ReceiptTwo, [new(toggle, "", 1, 270, 270)]) { ItemListComplete = true };
        var (main, workspace) = fixture.Create(automaticCache: cache);
        await main.RefreshAsync(); main.Receipts[1].IsSelected = true; main.Receipts[0].IsSelectedForOrders = true;
        await OpenOrdersAsync(main, workspace);
        var matching = main.Receipts.Single(r => r.Id == ReceiptOne);
        var unrelated = main.Receipts.Single(r => r.Id == ReceiptTwo);
        Equal("Автозв’язок: сума й товари", matching.LinkStatus); Equal(order.Key, matching.OrderMatch!.Order!.Key);
        True(unrelated.OrderMatch!.Order is null); Equal(ProductComparison.Contradiction, unrelated.OrderMatch.Products);
        var displayedOrder = workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Single();
        Equal(matching, displayedOrder.LinkedReceipts.Single()); Equal(matching.LinkStatus, displayedOrder.LinkStatus);
        True(displayedOrder.HasSuggestedLink && !displayedOrder.HasConfirmedLink);
        Equal(2, main.AllReceiptsTab.View.Cast<ReceiptRowViewModel>().Count());
        True(unrelated.IsSelected && matching.IsSelectedForOrders);
        Equal(0, fixture.Rozetka.FetchCalls); Equal(2, fixture.Details.Calls);
        fixture.Source.Fail = true; var fetches = fixture.Source.FetchCalls;
        var (restarted, restored) = fixture.Create(automaticCache: cache);
        await restarted.RefreshAsync(); restarted.SelectedTabIndex = 1; await restarted.PrepareOrdersAsync();
        Equal(order.Key, restarted.Receipts.Single(r => r.Id == ReceiptOne).OrderMatch!.Order!.Key);
        True(restarted.Receipts.Single(r => r.Id == ReceiptTwo).OrderMatch!.Order is null);
        True(restored.Orders.Cast<MarketplaceOrderRowViewModel>().Single().HasSuggestedLink);
        Equal(fetches, fixture.Source.FetchCalls); Equal(2, fixture.Details.Calls);
        Equal(0, fixture.Rozetka.FetchCalls); Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
    }

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
        True(main.Receipts.All(r => r.OrderMatch!.Products == ProductComparison.Contradiction && r.OrderMatch.Order is null));
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
        // Fifty units with a damaged apostrophe exercise cleaned full names through tables,
        // preserved source titles, serialized caches, and a later hidden-name collision.
        var cable = Order() with { Total = 650, Items = [new("Роз’єм XT60H-F", "", 50, 13, 650)], ItemListComplete = true };
        var sensor = cable with { Key = cable.Key with { OrderId = "42" }, Number = "ORDER-42",
            Items = [new("Запасна кришка для контейнера", "", 50, 13, 650)] };
        fixture.Source.Orders = [cable, sensor];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 65000), Receipt(ReceiptTwo, 2, 65000)];
        fixture.Details.Values[ReceiptOne] = new(ReceiptOne, [new("  РОЗ?ЄМ\u00a0XT60HF ", "", 50, 13, 650)]) { ItemListComplete = true };
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
        Equal("Роз’єм XT60H-F", fixture.Cache.Snapshot.Orders.Single(o => o.Key == cable.Key).Items[0].Name);
        Equal("  РОЗ?ЄМ\u00a0XT60HF ", fixture.Details.Values[ReceiptOne].Items[0].Name);
        Equal(2, fixture.Details.Calls);
        var (restarted, restored) = fixture.Create(automaticCache: cache);
        await restarted.RefreshAsync(); restarted.SelectedTabIndex = 1; await restarted.PrepareOrdersAsync();
        True(restarted.Receipts.All(r => r.OrderMatch?.Basis == AutomaticLinkBasis.AmountAndProducts));
        Equal(2, fixture.Details.Calls);

        // A different raw spelling with an identical cleaned name remains a competitor behind search.
        // Cached probable pairs must not reserve its receipt.
        fixture.Source.Orders = [sensor, cable, cable with { Key = cable.Key with { OrderId = "43" }, Number = "HIDDEN",
            Items = [new("Розєм XT60HF", "", 50, 13, 650)] }];
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
