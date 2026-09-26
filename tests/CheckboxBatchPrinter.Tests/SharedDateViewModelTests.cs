using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task SharedDisplayDatesAsync()
    {
        var fixture = new Fixture();
        var first = Order() with { Number = "DAY-26", Total = 355,
            CreatedAt = new DateTimeOffset(2026, 9, 26, 8, 30, 0, TimeSpan.FromHours(3)) };
        var second = first with { Key = first.Key with { OrderId = "42" }, Number = "DAY-27", Total = 681,
            CreatedAt = first.CreatedAt!.Value.AddDays(1) };
        var undated = first with { Key = first.Key with { OrderId = "43" }, Number = "UNKNOWN-DATE", CreatedAt = null };
        fixture.Source.Orders = [first, second, undated];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 35500, 26), Receipt(ReceiptTwo, 2, 68100, 27)];
        var (main, workspace) = fixture.Create();
        main.DateFrom = new(2026, 9, 26); main.DateTo = new(2026, 9, 27);
        await main.RefreshAsync(); await OpenOrdersAsync(main, workspace);
        var early = main.Receipts.Single(r => r.Id == ReceiptOne);
        var later = main.Receipts.Single(r => r.Id == ReceiptTwo);
        True(early.OrderMatch!.Order is null && early.OrderMatch.CompetingOrderCount == 2,
            "An undated order hidden by the date view must still prevent false amount uniqueness.");
        Equal(second.Key, later.OrderMatch!.Order!.Key);
        Equal(2, workspace.Orders.Cast<object>().Count());
        Equal(3, fixture.Cache.Snapshot.Orders.Count);
        early.IsSelectedForOrders = true; later.IsSelectedForOrders = true;
        main.OrdersReceiptsTab.SelectedReceipt = early;
        workspace.SelectedOrder = workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Single(r => r.Key == first.Key);
        var fetchCalls = fixture.Source.FetchCalls;
        var detailCalls = fixture.Details.Calls;
        var receiptCalls = fixture.ReceiptSource.Calls;
        var linkSaves = fixture.Links.Saves;

        main.DateFrom = new(2026, 9, 27);
        True(main.AllReceiptsTab.View.Cast<ReceiptRowViewModel>().Select(r => r.Id).SequenceEqual([ReceiptTwo]));
        True(main.OrdersReceiptsTab.View.Cast<ReceiptRowViewModel>().Select(r => r.Id).SequenceEqual([ReceiptTwo]));
        Equal(second.Key, workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Single().Key);
        Equal(2, main.Receipts.Count);
        Equal(1, main.VisibleSelectedCount); Equal(1, main.HiddenSelectedCount);
        True(early.IsSelectedForOrders && main.OrdersReceiptsTab.SelectedReceipt is null && workspace.SelectedOrder is null);
        True(early.OrderMatch.Order is null && early.OrderMatch.CompetingOrderCount == 2);
        Equal(fetchCalls, fixture.Source.FetchCalls); Equal(detailCalls, fixture.Details.Calls);
        Equal(receiptCalls, fixture.ReceiptSource.Calls); Equal(linkSaves, fixture.Links.Saves);

        main.DateFrom = new(2026, 9, 26); main.DateTo = new(2026, 9, 26);
        True(main.AllReceiptsTab.View.Cast<ReceiptRowViewModel>().Select(r => r.Id).SequenceEqual([ReceiptOne]));
        Equal(first.Key, workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Single().Key);
        True(early.OrderMatch.Order is null && early.OrderMatch.CompetingOrderCount == 2);
        await workspace.AutoMatchAsync();
        True(early.OrderMatch.Order is null && early.OrderMatch.CompetingOrderCount == 2,
            "Rechecking while a competitor is invisible must use the full source scope.");
        Equal(0, fixture.Printer.Calls);
    }

    private static async Task SharedDisplayDateBoundariesAsync()
    {
        // Same UTC day, different Kyiv calendar dates: both panels must use Kyiv.
        var before = new DateTimeOffset(2026, 9, 26, 20, 59, 59, TimeSpan.Zero);
        var after = before.AddSeconds(1);
        var fixture = new Fixture();
        fixture.ReceiptSource.Rows =
        [
            new() { Id = ReceiptOne, Type = ReceiptTypes.Sell, Status = "DONE", FiscalDate = before },
            new() { Id = ReceiptTwo, Type = ReceiptTypes.Sell, Status = "DONE", FiscalDate = after },
            new() { Id = ReceiptThree, Type = ReceiptTypes.Sell, Status = "DONE" }
        ];
        fixture.Source.Orders =
        [
            Order() with { CreatedAt = before },
            Order() with { Key = Order().Key with { OrderId = "42" }, CreatedAt = after },
            Order() with { Key = Order().Key with { OrderId = "43" }, CreatedAt = null }
        ];
        var (main, workspace) = fixture.Create();
        main.DateFrom = new(2026, 9, 26); main.DateTo = new(2026, 9, 27);
        await main.RefreshAsync(); await OpenOrdersAsync(main, workspace);
        Equal(2, main.AllReceiptsTab.View.Cast<object>().Count()); Equal(2, workspace.Orders.Cast<object>().Count());
        main.Receipts.Single(r => r.Id == ReceiptOne).IsSelected = true;
        main.DateFrom = new(2026, 9, 27);
        Equal(ReceiptTwo, main.AllReceiptsTab.View.Cast<ReceiptRowViewModel>().Single().Id);
        Equal("42", workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Single().Key.OrderId);
        main.ClearDateCommand.Execute("from");
        main.DateTo = new(2026, 9, 26);
        Equal(ReceiptOne, main.AllReceiptsTab.View.Cast<ReceiptRowViewModel>().Single().Id);
        Equal("41", workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Single().Key.OrderId);
        main.ClearDateCommand.Execute("to");
        Equal(3, main.AllReceiptsTab.View.Cast<object>().Count());
        Equal(3, main.OrdersReceiptsTab.View.Cast<object>().Count()); Equal(3, workspace.Orders.Cast<object>().Count());
        True(main.Receipts.Single(r => r.Id == ReceiptOne).IsSelected);
        main.DateFrom = new(2026, 9, 27); main.DateTo = new(2026, 9, 26);
        Equal(0, main.AllReceiptsTab.View.Cast<object>().Count());
        Equal(0, main.OrdersReceiptsTab.View.Cast<object>().Count()); Equal(0, workspace.Orders.Cast<object>().Count());
        Equal(3, main.Receipts.Count); Equal(3, fixture.Cache.Snapshot.Orders.Count);
        Equal(1, fixture.ReceiptSource.Calls); Equal(1, fixture.Source.FetchCalls);

        // No Checkbox account: changing a display range must keep the already loaded
        // orders too, while an incomplete range still prevents an unbounded API query.
        var ordersOnly = new Fixture();
        ordersOnly.Settings.App.Login = ""; ordersOnly.Authentication.IsStored = false;
        ordersOnly.ReceiptSource.Rows = [];
        ordersOnly.Source.Orders = fixture.Source.Orders;
        var (orderMain, orderWorkspace) = ordersOnly.Create();
        orderMain.DateFrom = new(2026, 9, 26); orderMain.DateTo = new(2026, 9, 27);
        await OpenOrdersAsync(orderMain, orderWorkspace);
        orderMain.DateFrom = new(2026, 9, 27);
        Equal("42", orderWorkspace.Orders.Cast<MarketplaceOrderRowViewModel>().Single().Key.OrderId);
        orderMain.ClearDateCommand.Execute("from"); orderMain.ClearDateCommand.Execute("to");
        Equal(3, orderWorkspace.Orders.Cast<object>().Count());
        True(!orderWorkspace.SyncCommand.CanExecute(null));
        Equal(1, ordersOnly.Source.FetchCalls); Equal(0, ordersOnly.ReceiptSource.Calls);
        Equal(0, fixture.Printer.Calls); Equal(0, ordersOnly.Printer.Calls);
    }
}
