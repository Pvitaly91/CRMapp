using System.ComponentModel;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task CanceledOrdersDefaultVisibilityAsync()
    {
        var fixture = new Fixture();
        fixture.Settings.Market.Connections.Add(new()
            { Id = "rz-test", Marketplace = MarketplaceKind.Rozetka, Enabled = true, Name = "Synthetic Rozetka" });
        var active = Order() with { Number = "ACTIVE", SourceStatus = "received" };
        var promRaw = active with
            { Key = active.Key with { OrderId = "canceled-raw" }, Number = "PROM-RAW", SourceStatus = " canceled " };
        var promLegacy = active with
            { Key = active.Key with { OrderId = "canceled-legacy" }, Number = "PROM-LEGACY", SourceStatus = "", Status = "Отменено" };
        var paymentCanceled = active with
            { Key = active.Key with { OrderId = "payment-only" }, Number = "PAYMENT-ONLY", PaymentStatus = "canceled" };
        var rzRaw = active with
            { Key = new(MarketplaceKind.Rozetka, "rz-test", "raw"), Number = "RZ-RAW", SourceStatus = "cancelled" };
        var rzLegacy = active with
            { Key = new(MarketplaceKind.Rozetka, "rz-test", "legacy"), Number = "RZ-LEGACY", SourceStatus = "17", Status = "Скасовано" };
        var rzUnknown = active with
            { Key = new(MarketplaceKind.Rozetka, "rz-test", "unknown"), Number = "RZ-UNKNOWN", SourceStatus = "17", Status = "17" };
        fixture.Source.Orders = [active, promRaw, promLegacy, paymentCanceled];
        fixture.Rozetka.Orders = [rzRaw, rzLegacy, rzUnknown];
        fixture.ReceiptSource.Rows = [];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);

        True(!workspace.ShowCanceledOrders);
        Equal(7, workspace.AllOrderRows.Count);
        var visible = workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Select(row => row.Key).ToHashSet();
        True(visible.SetEquals([active.Key, paymentCanceled.Key, rzUnknown.Key]),
            "Only explicit order cancellation statuses may hide orders; payment and unknown numeric statuses stay visible.");
        AssertDashboardIndices(workspace);
        True(workspace.AllOrderRows.Where(row => row.Model.IsCanceled).All(row => row.DisplayIndex == 0));
        var calls = fixture.Source.FetchCalls + fixture.Rozetka.FetchCalls;
        workspace.ShowCanceledOrders = true;
        Equal(7, workspace.Orders.Cast<object>().Count());
        AssertDashboardIndices(workspace);
        workspace.ShowCanceledOrders = false;
        Equal(3, workspace.Orders.Cast<object>().Count());
        Equal(calls, fixture.Source.FetchCalls + fixture.Rozetka.FetchCalls);
        Equal(7, fixture.Cache.Snapshot.Orders.Count);

        // Showing the archive is temporary: a new workspace starts with it hidden,
        // while all canceled orders still survive in the serialized source cache.
        workspace.ShowCanceledOrders = true;
        var (restarted, restored) = fixture.Create();
        await restarted.RefreshAsync();
        restarted.SelectedTabIndex = 1;
        await restarted.PrepareOrdersAsync();
        True(!restored.ShowCanceledOrders);
        Equal(7, restored.AllOrderRows.Count);
        Equal(3, restored.Orders.Cast<object>().Count());
        Equal(0, fixture.Printer.Calls);
        Equal(0, fixture.Settings.AppSaves);
        Equal(0, fixture.Settings.MarketplaceSaves);
    }

    private static async Task CanceledOrdersCombinedFiltersAsync()
    {
        var fixture = new Fixture();
        fixture.Settings.Market.Connections.Add(new()
            { Id = "rz-test", Marketplace = MarketplaceKind.Rozetka, Enabled = true, Name = "Synthetic Rozetka" });
        var canceled = Order() with { Number = "MATCH-PROM-CANCELED", SourceStatus = "canceled", Status = "Скасовано" };
        var active = Order([ReceiptTwo]) with
            { Key = canceled.Key with { OrderId = "active" }, Number = "MATCH-PROM-ACTIVE" };
        var other = Order() with
            { Key = canceled.Key with { OrderId = "unlinked" }, Number = "MATCH-PROM-OTHER" };
        var historical = canceled with
        {
            Key = canceled.Key with { OrderId = "old" }, Number = "MATCH-PROM-OLD",
            CreatedAt = canceled.CreatedAt!.Value.AddDays(-1)
        };
        var rozetka = Order([ReceiptThree]) with
        {
            Key = new(MarketplaceKind.Rozetka, "rz-test", "canceled"), Number = "MATCH-RZ-CANCELED",
            SourceStatus = "cancelled", Status = "Скасовано"
        };
        fixture.Source.Orders = [canceled, active, other, historical];
        fixture.Rozetka.Orders = [rozetka];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 10000), Receipt(ReceiptTwo, 2, 10000), Receipt(ReceiptThree, 3, 10000)];
        await fixture.Links.SaveDecisionAsync(new()
        {
            AccountContext = PrintAccountContext.Create(fixture.Settings.App), ReceiptId = ReceiptOne,
            ConfirmedOrder = canceled.Key, UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        var allRows = workspace.AllOrderRows.ToArray();
        var canceledRow = allRows.Single(row => row.Key == canceled.Key);
        var canceledReceipt = main.Receipts.Single(row => row.Id == ReceiptOne);
        canceledRow.IsSelectedForLabels = true;
        canceledReceipt.IsSelectedForOrders = true;
        Equal(ReceiptLinkState.Manual, canceledReceipt.OrderMatch!.State);
        True(canceledRow.HasConfirmedLink);
        Equal(5, allRows.Length);

        workspace.OrderFilter = "Prom";
        workspace.OrderSearch = "MATCH";
        Equal(2, workspace.Orders.Cast<object>().Count());
        workspace.ShowCanceledOrders = true;
        Equal(3, workspace.Orders.Cast<object>().Count());
        True(!workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Any(row => row.Key == historical.Key),
            "Showing cancellations must not bypass the shared date filter.");
        workspace.OrderFilter = "Є чек";
        workspace.OrderSearch = "MATCH-PROM";
        Equal(2, workspace.Orders.Cast<object>().Count());
        workspace.SelectedOrder = canceledRow;
        workspace.ShowCanceledOrders = false;
        Equal(active.Key, workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Single().Key);
        True(workspace.SelectedOrder is null);
        workspace.ShowCanceledOrders = true;
        workspace.OrderSearch = "not-found";
        Equal(0, workspace.Orders.Cast<object>().Count());
        workspace.OrderSearch = "MATCH";
        Equal(3, workspace.Orders.Cast<object>().Count());
        main.DateFrom = new(2026, 9, 24);
        main.DateTo = new(2026, 9, 24);
        Equal(0, workspace.Orders.Cast<object>().Count());
        main.DateFrom = new(2026, 9, 23);
        main.DateTo = new(2026, 9, 23);
        Equal(3, workspace.Orders.Cast<object>().Count());
        AssertDashboardIndices(workspace);

        True(workspace.AllOrderRows.SequenceEqual(allRows), "View filtering must retain the matching universe and row instances.");
        True(canceledRow.IsSelectedForLabels && canceledReceipt.IsSelectedForOrders);
        Equal(canceled.Key, canceledReceipt.OrderMatch!.Order!.Key);
        Equal(ReceiptLinkState.Manual, canceledReceipt.OrderMatch.State);
        Equal(rozetka.Key, main.Receipts.Single(row => row.Id == ReceiptThree).OrderMatch!.Order!.Key);
        Equal(ReceiptLinkState.Exact, main.Receipts.Single(row => row.Id == ReceiptThree).OrderMatch!.State);
        Equal(1, fixture.Links.Saves);
        Equal(5, fixture.Cache.Snapshot.Orders.Count);
        Equal(0, fixture.Printer.Calls);
        Equal(0, fixture.History.Saves);
    }

    private static async Task RefreshedCancellationHidesOrderAsync()
    {
        var fixture = new Fixture();
        var first = Order([ReceiptOne]) with { Number = "A-ORDER", SourceStatus = "received" };
        var second = Order() with { Key = first.Key with { OrderId = "42" }, Number = "B-ORDER" };
        fixture.Source.Orders = [first, second];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 10000)];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        workspace.Orders.SortDescriptions.Clear();
        workspace.Orders.SortDescriptions.Add(new(nameof(MarketplaceOrderRowViewModel.Number), ListSortDirection.Ascending));
        var firstRow = workspace.AllOrderRows.Single(row => row.Key == first.Key);
        firstRow.IsSelectedForLabels = true;
        workspace.SelectedOrder = firstRow;
        var receipt = main.Receipts.Single();
        receipt.IsSelectedForOrders = true;
        Equal(1, firstRow.DisplayIndex);
        fixture.Source.Orders = [first with { SourceStatus = "canceled", Status = "Скасовано" }, second];
        await workspace.SyncAsync();

        True(ReferenceEquals(firstRow, workspace.AllOrderRows.Single(row => row.Key == first.Key)));
        True(firstRow.Model.IsCanceled);
        Equal(second.Key, workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Single().Key);
        Equal(0, firstRow.DisplayIndex);
        AssertDashboardIndices(workspace);
        True(workspace.SelectedOrder is null, "A canceled row hidden by sync must not remain an actionable current order.");
        True(firstRow.IsSelectedForLabels && receipt.IsSelectedForOrders);
        True(firstRow.HasConfirmedLink);
        Equal(ReceiptLinkState.Exact, receipt.OrderMatch!.State);
        Equal(first.Key, receipt.OrderMatch.Order!.Key);
        True(fixture.Cache.Snapshot.Orders.Single(order => order.Key == first.Key).IsCanceled);
        workspace.ShowCanceledOrders = true;
        Equal(2, workspace.Orders.Cast<object>().Count());
        Equal(1, firstRow.DisplayIndex);
        AssertDashboardIndices(workspace);
        True(firstRow.IsSelectedForLabels && receipt.IsSelectedForOrders);
        Equal(first.Key, receipt.OrderMatch.Order!.Key);
        Equal(0, fixture.Links.Saves);
        Equal(0, fixture.Printer.Calls);
        Equal(0, fixture.History.Saves);
    }
}
