using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static string ExpandedOrderSum(string prefix, string amounts, int unknown = 0) =>
        prefix + amounts + (unknown > 0 ? $" · Без суми: {unknown}" : "");

    private static string ReturnTotalsText(int count, decimal? sum, int unknown = 0) =>
        $"Повернення ({count}): {(sum is { } total ? $"{total:N2} грн" : "—")}" +
        (unknown > 0 ? $" · Без суми: {unknown}" : "");

    private static async Task ExpandedOrderCurrencyTotalsAsync()
    {
        var fixture = new Fixture();
        fixture.Settings.Market.Connections.Add(new()
            { Id = "rz-test", Marketplace = MarketplaceKind.Rozetka, Enabled = true, Name = "Synthetic Rozetka" });
        var prom = Order() with { Number = "PROM-UAH", SourceStatus = "received" };
        var usd = prom with { Key = prom.Key with { OrderId = "usd" }, Number = "PROM-USD", Total = 10m, Currency = " usd " };
        var missingCurrency = prom with { Key = prom.Key with { OrderId = "no-currency" }, Number = "PROM-UNSPECIFIED", Total = 7m, Currency = "" };
        var missingAmount = prom with { Key = prom.Key with { OrderId = "no-amount" }, Number = "PROM-MISSING", Total = null };
        var rz = prom with { Key = new(MarketplaceKind.Rozetka, "rz-test", "rz"), Number = "ROZETKA-UAH", Total = 40m, Currency = "" };
        var eur = rz with { Key = rz.Key with { OrderId = "eur" }, Number = "ROZETKA-EUR", Total = 3m, Currency = "EUR" };
        fixture.Source.Orders = [prom, usd, missingCurrency, missingAmount];
        fixture.Rozetka.Orders = [rz, eur];
        fixture.ReceiptSource.Rows = [];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);

        Equal(ExpandedOrderSum("Prom + Rozetka: ", $"{3m:N2} EUR; {140m:N2} грн; {10m:N2} USD; {7m:N2} (валюта не надана)", 1),
            workspace.CombinedOrderAmountSummary);
        Equal(OrderTotalsText(6, $"{3m:N2} EUR; {100m:N2} UAH; {10m:N2} USD; {47m:N2} (валюта не надана)", 1),
            workspace.OrderAmountSummary);
        var rzRow = workspace.AllOrderRows.Single(row => row.Key == rz.Key);
        Equal("", rzRow.Model.Currency);
        Equal("", fixture.Cache.Snapshot.Orders.Single(order => order.Key == rz.Key).Currency);

        var notifications = 0;
        workspace.PropertyChanged += (_, args) =>
        { if (args.PropertyName == nameof(workspace.CombinedOrderAmountSummary)) notifications++; };
        fixture.Rozetka.Orders = [rz with { Total = 50m }, eur];
        await workspace.SyncAsync();
        True(ReferenceEquals(rzRow, workspace.AllOrderRows.Single(row => row.Key == rz.Key)));
        True(notifications > 0, "A same-row refresh must notify the combined amount binding.");
        Equal(ExpandedOrderSum("Prom + Rozetka: ", $"{3m:N2} EUR; {150m:N2} грн; {10m:N2} USD; {7m:N2} (валюта не надана)", 1),
            workspace.CombinedOrderAmountSummary);
        Equal("", rzRow.Model.Currency);
        workspace.OrderSearch = "PROM-MISSING";
        Equal(ExpandedOrderSum("Prom + Rozetka: ", "—", 1), workspace.CombinedOrderAmountSummary);
        workspace.OrderSearch = "not-present";
        Equal($"Prom + Rozetka: {0m:N2} грн", workspace.CombinedOrderAmountSummary);
        Equal(0, fixture.Printer.Calls);
        Equal(0, fixture.Settings.AppSaves);
        Equal(0, fixture.Settings.MarketplaceSaves);
    }

    private static async Task CanceledOrderScopeTotalsAsync()
    {
        var fixture = new Fixture();
        fixture.Settings.Market.Connections.Add(new()
            { Id = "rz-test", Marketplace = MarketplaceKind.Rozetka, Enabled = true, Name = "Synthetic Rozetka" });
        var active = Order() with { Number = "ACTIVE", SourceStatus = "received" };
        var canceled = active with { Key = active.Key with { OrderId = "cancel" }, Number = "CANCEL-PROM", Total = 200m, SourceStatus = "canceled" };
        var linked = canceled with { Key = canceled.Key with { OrderId = "linked" }, Number = "CANCEL-LINKED", Total = 50m };
        var missing = canceled with { Key = canceled.Key with { OrderId = "missing" }, Number = "CANCEL-MISSING", Total = null };
        var old = canceled with { Key = canceled.Key with { OrderId = "old" }, Number = "CANCEL-OLD", Total = 900m,
            CreatedAt = canceled.CreatedAt!.Value.AddDays(-1) };
        var next = canceled with { Key = canceled.Key with { OrderId = "next" }, Number = "CANCEL-NEXT", Total = 300m,
            CreatedAt = canceled.CreatedAt!.Value.AddDays(1) };
        var rz = canceled with { Key = new(MarketplaceKind.Rozetka, "rz-test", "rz"), Number = "CANCEL-RZ", Total = 40m, Currency = "" };
        fixture.Source.Orders = [active, canceled, linked, missing, old, next];
        fixture.Rozetka.Orders = [rz];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 5000)];
        await fixture.Links.SaveDecisionAsync(new()
        {
            AccountContext = PrintAccountContext.Create(fixture.Settings.App), ReceiptId = ReceiptOne,
            ConfirmedOrder = linked.Key, UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        True(!workspace.ShowCanceledOrders);
        Equal($"Prom + Rozetka: {100m:N2} грн", workspace.CombinedOrderAmountSummary);
        Equal(ExpandedOrderSum("Скасовані (4): ", $"{290m:N2} грн", 1), workspace.CanceledOrderAmountSummary);
        var calls = fixture.Source.FetchCalls + fixture.Rozetka.FetchCalls;

        workspace.OrderFilter = "Prom";
        Equal(ExpandedOrderSum("Скасовані (3): ", $"{250m:N2} грн", 1), workspace.CanceledOrderAmountSummary);
        workspace.OrderFilter = "Є чек";
        Equal($"Скасовані (1): {50m:N2} грн", workspace.CanceledOrderAmountSummary);
        Equal($"Prom + Rozetka: {0m:N2} грн", workspace.CombinedOrderAmountSummary);
        workspace.ShowCanceledOrders = true;
        Equal($"Prom + Rozetka: {50m:N2} грн", workspace.CombinedOrderAmountSummary);
        Equal($"Скасовані (1): {50m:N2} грн", workspace.CanceledOrderAmountSummary);
        workspace.ShowCanceledOrders = false;
        workspace.OrderFilter = "Без чека";
        Equal(ExpandedOrderSum("Скасовані (3): ", $"{240m:N2} грн", 1), workspace.CanceledOrderAmountSummary);
        workspace.OrderFilter = "Rozetka";
        Equal($"Скасовані (1): {40m:N2} грн", workspace.CanceledOrderAmountSummary);
        workspace.OrderFilter = "Усі";
        workspace.OrderSearch = "CANCEL-RZ";
        Equal($"Скасовані (1): {40m:N2} грн", workspace.CanceledOrderAmountSummary);
        workspace.OrderSearch = "not-present";
        Equal($"Скасовані (0): {0m:N2} грн", workspace.CanceledOrderAmountSummary);
        workspace.OrderSearch = "";
        workspace.SetDisplayDates(new(2026, 9, 24), new(2026, 9, 24));
        Equal($"Скасовані (1): {300m:N2} грн", workspace.CanceledOrderAmountSummary);
        Equal(calls, fixture.Source.FetchCalls + fixture.Rozetka.FetchCalls);

        workspace.SetDisplayDates(new(2026, 9, 23), new(2026, 9, 23));
        var notifications = 0;
        workspace.PropertyChanged += (_, args) =>
        { if (args.PropertyName == nameof(workspace.CanceledOrderAmountSummary)) notifications++; };
        fixture.Source.Orders = [active, canceled with { SourceStatus = "received", Total = 250m }, linked, missing, old, next];
        await workspace.SyncAsync();
        True(notifications > 0, "A reactivated order must refresh the canceled amount binding.");
        Equal(ExpandedOrderSum("Скасовані (3): ", $"{90m:N2} грн", 1), workspace.CanceledOrderAmountSummary);
        Equal($"Prom + Rozetka: {350m:N2} грн", workspace.CombinedOrderAmountSummary);
        Equal(0, fixture.Printer.Calls);
    }

    private static async Task ReturnReceiptSubtotalsAsync()
    {
        var fixture = new Fixture();
        fixture.ReceiptSource.Rows = [TotalsReceipt("sale", 10000), TotalsReceipt("refund", 2500, "RETURN"),
            TotalsReceipt("negative", -500, "return"), TotalsReceipt("unknown-refund", 99999, "RETURN", known: false),
            TotalsReceipt("next-refund", 9000, "RETURN", day: 24)];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        var refund = main.Receipts.Single(row => row.Id == "refund");
        refund.OrderMatch = new(ReceiptLinkState.Manual, Order(), "Synthetic", []);
        var negative = main.Receipts.Single(row => row.Id == "negative");
        negative.OrderMatch = new(ReceiptLinkState.Manual,
            Order() with { Key = new(MarketplaceKind.Rozetka, "rz-test", "rz") }, "Synthetic", []);
        main.OrdersReceiptsTab.View.Refresh();
        Equal(ReturnTotalsText(3, 20m, 1), main.AllReceiptsTab.ReturnAmountSummary);
        Equal(ReceiptTotalsText(4, 120m, 1), main.AllReceiptsTab.AmountSummary);
        workspace.Filter = "Prom";
        Equal(ReturnTotalsText(1, 25m), main.OrdersReceiptsTab.ReturnAmountSummary);
        Equal(ReturnTotalsText(3, 20m, 1), main.AllReceiptsTab.ReturnAmountSummary);
        main.AllReceiptsTab.SelectedType = main.ReceiptTypes.Single(type => type.Value == "SELL");
        Equal(ReturnTotalsText(0, 0m), main.AllReceiptsTab.ReturnAmountSummary);
        Equal(ReturnTotalsText(1, 25m), main.OrdersReceiptsTab.ReturnAmountSummary);
        main.AllReceiptsTab.SelectedType = main.ReceiptTypes[0];
        main.AllReceiptsTab.SearchText = "unknown-refund";
        Equal(ReturnTotalsText(1, null, 1), main.AllReceiptsTab.ReturnAmountSummary);
        main.AllReceiptsTab.SearchText = "-5,00";
        Equal(ReturnTotalsText(1, -5m), main.AllReceiptsTab.ReturnAmountSummary);
        main.AllReceiptsTab.SearchText = "";
        main.AllReceiptsTab.SetDisplayDates(new(2026, 9, 24), new(2026, 9, 24));
        Equal(ReturnTotalsText(1, 90m), main.AllReceiptsTab.ReturnAmountSummary);
        Equal(ReturnTotalsText(1, 25m), main.OrdersReceiptsTab.ReturnAmountSummary);
        main.AllReceiptsTab.SetDisplayDates(new(2026, 9, 23), new(2026, 9, 23));

        var allNotifications = 0;
        var orderNotifications = 0;
        main.AllReceiptsTab.PropertyChanged += (_, args) =>
        { if (args.PropertyName == nameof(ReceiptTabViewModel.ReturnAmountSummary)) allNotifications++; };
        main.OrdersReceiptsTab.PropertyChanged += (_, args) =>
        { if (args.PropertyName == nameof(ReceiptTabViewModel.ReturnAmountSummary)) orderNotifications++; };
        refund.Update(TotalsReceipt(refund.Id, 7000));
        True(allNotifications > 0 && orderNotifications > 0, "A type/amount update on the same receipt must notify both return summaries.");
        Equal(ReturnTotalsText(2, -5m, 1), main.AllReceiptsTab.ReturnAmountSummary);
        Equal(ReturnTotalsText(0, 0m), main.OrdersReceiptsTab.ReturnAmountSummary);
        refund.Update(TotalsReceipt(refund.Id, 123456, "RETURN", known: false));
        Equal(ReturnTotalsText(3, -5m, 2), main.AllReceiptsTab.ReturnAmountSummary);
        Equal(ReturnTotalsText(1, null, 1), main.OrdersReceiptsTab.ReturnAmountSummary);
        main.Receipts.Remove(main.Receipts.Single(row => row.Id == "unknown-refund"));
        Equal(ReturnTotalsText(2, -5m, 1), main.AllReceiptsTab.ReturnAmountSummary);
        main.Receipts.Clear();
        Equal(ReturnTotalsText(0, 0m), main.AllReceiptsTab.ReturnAmountSummary);
        Equal(ReturnTotalsText(0, 0m), main.OrdersReceiptsTab.ReturnAmountSummary);
        Equal(0, fixture.Source.FetchCalls);
        Equal(0, fixture.Printer.Calls);
    }
}
