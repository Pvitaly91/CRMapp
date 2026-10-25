using System.Collections.ObjectModel;
using System.ComponentModel;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static ReceiptRecord TotalsReceipt(string id, long amount, string type = "SELL", int day = 23,
        bool known = true) => new()
    {
        Id = id, Type = type, Status = "DONE", TotalSumMinor = amount, TotalKnown = known,
        FiscalDate = new DateTimeOffset(2026, 9, day, 12, 0, 0, TimeSpan.FromHours(3))
    };

    private static string ReceiptTotalsText(int count, decimal? amount, int unknown = 0) =>
        $"Чеків: {count} · Сума за фільтром: {(amount is { } total ? total.ToString("N2") + " грн" : "—")}" +
        (unknown > 0 ? $" · Без суми: {unknown}" : "");

    private static string OrderTotalsText(int count, string amounts, int unknown = 0) =>
        $"Замовлень: {count} · Сума за фільтром: {amounts}" + (unknown > 0 ? $" · Без суми: {unknown}" : "");

    private static async Task ReceiptFilteredAmountTotalsAsync()
    {
        var fixture = new Fixture();
        fixture.ReceiptSource.Rows = [TotalsReceipt("alpha", 10000), TotalsReceipt("beta", 2500, "RETURN"),
            TotalsReceipt("missing", 99999, known: false), TotalsReceipt("tomorrow", 50000, day: 24)];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        main.Receipts.Single(row => row.Id == "alpha").OrderMatch = new(ReceiptLinkState.Manual, Order(), "Synthetic", []);
        var rz = Order() with { Key = new(MarketplaceKind.Rozetka, "rz-test", "rz"), Number = "RZ" };
        main.Receipts.Single(row => row.Id == "beta").OrderMatch = new(ReceiptLinkState.Manual, rz, "Synthetic", []);
        main.OrdersReceiptsTab.View.Refresh();

        Equal(ReceiptTotalsText(3, 125m, 1), main.AllReceiptsTab.AmountSummary);
        Equal(ReceiptTotalsText(3, 125m, 1), main.OrdersReceiptsTab.AmountSummary);
        workspace.Filter = "Prom";
        Equal(ReceiptTotalsText(1, 100m), main.OrdersReceiptsTab.AmountSummary);
        Equal(ReceiptTotalsText(3, 125m, 1), main.AllReceiptsTab.AmountSummary);
        main.AllReceiptsTab.SelectedType = main.ReceiptTypes.Single(type => type.Value == "RETURN");
        Equal(ReceiptTotalsText(1, 25m), main.AllReceiptsTab.AmountSummary);
        Equal(ReceiptTotalsText(1, 100m), main.OrdersReceiptsTab.AmountSummary);
        main.AllReceiptsTab.SelectedType = main.ReceiptTypes[0];
        main.AllReceiptsTab.SearchText = "100,00";
        Equal(ReceiptTotalsText(1, 100m), main.AllReceiptsTab.AmountSummary);
        main.AllReceiptsTab.SearchText = "missing";
        Equal(ReceiptTotalsText(1, null, 1), main.AllReceiptsTab.AmountSummary);
        main.AllReceiptsTab.SearchText = "not-present";
        Equal(ReceiptTotalsText(0, 0m), main.AllReceiptsTab.AmountSummary);
        main.AllReceiptsTab.SearchText = "";
        main.AllReceiptsTab.SetDisplayDates(new(2026, 9, 24), new(2026, 9, 24));
        Equal(ReceiptTotalsText(1, 500m), main.AllReceiptsTab.AmountSummary);
        Equal(ReceiptTotalsText(1, 100m), main.OrdersReceiptsTab.AmountSummary);
        Equal(0, fixture.Source.FetchCalls);
        Equal(0, fixture.Printer.Calls);
    }

    private static Task ReceiptAmountTotalsUpdatesAsync()
    {
        var row = new ReceiptRowViewModel(TotalsReceipt("mutable", 10000));
        var rows = new ObservableCollection<ReceiptRowViewModel> { row };
        using var tab = new ReceiptTabViewModel(rows, new("", "Усі типи"));
        var notifications = 0;
        tab.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(tab.AmountSummary)) notifications++; };
        Equal(ReceiptTotalsText(1, 100m), tab.AmountSummary);
        row.IsSelected = true;
        tab.View.SortDescriptions.Clear();
        tab.View.SortDescriptions.Add(new(nameof(ReceiptRowViewModel.Total), ListSortDirection.Ascending));
        Equal(ReceiptTotalsText(1, 100m), tab.AmountSummary);

        var before = notifications;
        row.Update(TotalsReceipt(row.Id, 17555));
        True(notifications > before, "Replacing the amount on the same receipt row did not notify its total.");
        Equal(ReceiptTotalsText(1, 175.55m), tab.AmountSummary);
        before = notifications;
        row.Update(TotalsReceipt(row.Id, 999999, known: false));
        True(notifications > before, "An unknown amount did not invalidate the previously displayed sum.");
        Equal(ReceiptTotalsText(1, null, 1), tab.AmountSummary);
        row.Update(TotalsReceipt(row.Id, 0));
        Equal(ReceiptTotalsText(1, 0m), tab.AmountSummary);
        var second = new ReceiptRowViewModel(TotalsReceipt("second", 2500, "RETURN"));
        rows.Add(second);
        Equal(ReceiptTotalsText(2, 25m), tab.AmountSummary);
        rows.Remove(row);
        Equal(ReceiptTotalsText(1, 25m), tab.AmountSummary);
        rows.Clear();
        Equal(ReceiptTotalsText(0, 0m), tab.AmountSummary);
        return Task.CompletedTask;
    }

    private static async Task OrderFilteredAmountTotalsAsync()
    {
        var fixture = new Fixture();
        fixture.Settings.Market.Connections.Add(new()
            { Id = "rz-test", Marketplace = MarketplaceKind.Rozetka, Enabled = true, Name = "Synthetic Rozetka" });
        var active = Order([ReceiptOne]) with { Number = "MATCH-ACTIVE", SourceStatus = "received" };
        var canceled = active with { Key = active.Key with { OrderId = "cancel" }, Number = "MATCH-CANCELED",
            Total = 200m, SourceStatus = "canceled", ReceiptIds = [] };
        var usd = active with { Key = active.Key with { OrderId = "usd" }, Number = "MATCH-USD", Total = 10m,
            Currency = " usd ", ReceiptIds = [] };
        var missing = active with { Key = active.Key with { OrderId = "missing" }, Number = "MATCH-MISSING",
            Total = null, ReceiptIds = [] };
        var tomorrow = active with { Key = active.Key with { OrderId = "tomorrow" }, Number = "NEXT-DAY",
            Total = 500m, CreatedAt = active.CreatedAt!.Value.AddDays(1), ReceiptIds = [] };
        var rz = active with { Key = new(MarketplaceKind.Rozetka, "rz-test", "rz"), Number = "MATCH-RZ",
            Total = 40m, Currency = "", ReceiptIds = [] };
        fixture.Source.Orders = [active, canceled, usd, missing, tomorrow];
        fixture.Rozetka.Orders = [rz];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 10000)];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);

        Equal(OrderTotalsText(4, $"{100m:N2} UAH; {10m:N2} USD; {40m:N2} (валюта не надана)", 1), workspace.OrderAmountSummary);
        var calls = fixture.Source.FetchCalls + fixture.Rozetka.FetchCalls;
        workspace.OrderFilter = "Prom";
        Equal(OrderTotalsText(3, $"{100m:N2} UAH; {10m:N2} USD", 1), workspace.OrderAmountSummary);
        workspace.ShowCanceledOrders = true;
        Equal(OrderTotalsText(4, $"{300m:N2} UAH; {10m:N2} USD", 1), workspace.OrderAmountSummary);
        workspace.OrderSearch = "MATCH-MISSING";
        Equal(OrderTotalsText(1, "—", 1), workspace.OrderAmountSummary);
        workspace.OrderSearch = "not-present";
        Equal(OrderTotalsText(0, $"{0m:N2}"), workspace.OrderAmountSummary);
        workspace.OrderSearch = "MATCH";
        workspace.OrderFilter = "Є чек";
        Equal(OrderTotalsText(1, $"{100m:N2} UAH"), workspace.OrderAmountSummary);
        workspace.OrderFilter = "Rozetka";
        Equal(OrderTotalsText(1, $"{40m:N2} (валюта не надана)"), workspace.OrderAmountSummary);
        workspace.OrderSearch = "";
        workspace.OrderFilter = "Усі";
        workspace.SetDisplayDates(new(2026, 9, 24), new(2026, 9, 24));
        Equal(OrderTotalsText(1, $"{500m:N2} UAH"), workspace.OrderAmountSummary);
        Equal(ReceiptTotalsText(1, 100m), main.AllReceiptsTab.AmountSummary);
        Equal(calls, fixture.Source.FetchCalls + fixture.Rozetka.FetchCalls);
        Equal(0, fixture.Printer.Calls);
    }

    private static async Task OrderAmountTotalsRefreshAsync()
    {
        var fixture = new Fixture();
        var order = Order() with { SourceStatus = "received" };
        fixture.Source.Orders = [order];
        fixture.ReceiptSource.Rows = [];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        var row = workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Single();
        var notifications = 0;
        workspace.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(workspace.OrderAmountSummary)) notifications++; };
        row.IsSelectedForLabels = true;
        Equal(OrderTotalsText(1, $"{100m:N2} UAH"), workspace.OrderAmountSummary);
        fixture.Source.Orders = [order with { Total = 175.55m, Currency = "EUR" }];
        await workspace.SyncAsync();
        True(ReferenceEquals(row, workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Single()),
            "The source refresh no longer exercises an existing order row.");
        True(row.IsSelectedForLabels);
        True(notifications > 0, "Updating an existing order amount did not notify the total binding.");
        Equal(OrderTotalsText(1, $"{175.55m:N2} EUR"), workspace.OrderAmountSummary);
        fixture.Source.Orders = [order with { Total = null }];
        await workspace.SyncAsync();
        Equal(OrderTotalsText(1, "—", 1), workspace.OrderAmountSummary);
        Equal(0, fixture.Printer.Calls);
        Equal(0, fixture.Settings.AppSaves);
        Equal(0, fixture.Settings.MarketplaceSaves);
    }
}
