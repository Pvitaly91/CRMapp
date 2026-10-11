using System.Collections.ObjectModel;
using System.ComponentModel;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> HeaderToggleScenarios =>
    [
        ("STA receipt header marks partial visible rows then clears all without crossing tab scopes", () => StaAsync(ReceiptHeaderStatesAsync)),
        ("STA receipt header follows search type dates sort and clears hidden-only marks", () => StaAsync(ReceiptHeaderFiltersAsync)),
        ("STA receipt header observes refreshed rows and detaches removed rows", () => StaAsync(ReceiptHeaderRowsAsync)),
        ("STA receipt headers disable during a frozen print and follow submitted mark clearing", () => StaAsync(ReceiptHeaderPrintingAsync)),
        ("STA order header excludes missing TTNs and default-hidden cancellations", () => StaAsync(OrderHeaderEligibilityAsync)),
        ("STA order header follows filters dates sort and clears hidden label marks", () => StaAsync(OrderHeaderFiltersAsync)),
        ("STA order header remains usable during marketplace refresh and observes changed shipments", () => StaAsync(OrderHeaderRefreshAsync)),
        ("STA order header disables throughout fake label submission and follows cleared marks", () => StaAsync(OrderHeaderPrintingAsync)),
        ("STA waybill header selects eligible visible documents and clears hidden marks", () => StaAsync(WaybillHeaderFiltersAsync)),
        ("STA waybill header disables during refresh and handles deleted replaced and empty rows", () => StaAsync(WaybillHeaderRefreshAsync)),
        ("STA waybill header disables during fake submission and retains hidden document marks", () => StaAsync(WaybillHeaderPrintingAsync))
    ];

    private static ObservableCollection<ReceiptRowViewModel> HeaderReceipts() =>
    [
        new(new ReceiptRecord { Id = ReceiptOne, Serial = 1, FiscalCode = "FIRST", Type = ReceiptTypes.Sell, Status = "DONE", TotalSumMinor = 10000,
            FiscalDate = new DateTimeOffset(2026, 9, 23, 11, 0, 0, TimeSpan.FromHours(3)) }),
        new(new ReceiptRecord { Id = ReceiptTwo, Serial = 2, FiscalCode = "SECOND", Type = ReceiptTypes.Return, Status = "DONE", TotalSumMinor = 20000,
            FiscalDate = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.FromHours(3)) }),
        new(new ReceiptRecord { Id = ReceiptThree, Serial = 3, FiscalCode = "THIRD", Type = ReceiptTypes.Sell, Status = "DONE", TotalSumMinor = 30000,
            FiscalDate = new DateTimeOffset(2026, 9, 24, 13, 0, 0, TimeSpan.FromHours(3)) })
    ];

    private static Task ReceiptHeaderStatesAsync()
    {
        var rows = HeaderReceipts();
        using var all = new ReceiptTabViewModel(rows, new("", "Усі типи"));
        using var orders = new ReceiptTabViewModel(rows, new("", "Усі типи"), usesOrders: true);
        var updates = new List<string?>();
        var commandUpdates = 0;
        all.PropertyChanged += (_, e) => updates.Add(e.PropertyName);
        all.ToggleHeaderSelectionCommand.CanExecuteChanged += (_, _) => commandUpdates++;
        Equal<bool?>(false, all.HeaderSelectionState);
        True(all.CanToggleHeaderSelection && all.ToggleHeaderSelectionCommand.CanExecute(null));
        rows[0].IsSelected = true;
        Equal<bool?>(null, all.HeaderSelectionState);
        Equal<bool?>(false, orders.HeaderSelectionState);
        all.ToggleHeaderSelectionCommand.Execute(null);
        Equal<bool?>(true, all.HeaderSelectionState);
        True(rows.All(r => r.IsSelected && !r.IsSelectedForOrders));
        orders.ToggleHeaderSelectionCommand.Execute(null);
        Equal<bool?>(true, orders.HeaderSelectionState);
        all.ToggleHeaderSelectionCommand.Execute(null);
        Equal<bool?>(false, all.HeaderSelectionState);
        True(rows.All(r => !r.IsSelected && r.IsSelectedForOrders));
        rows[1].IsSelectedForOrders = false;
        Equal<bool?>(null, orders.HeaderSelectionState);
        orders.ToggleHeaderSelectionCommand.Execute(null);
        Equal<bool?>(true, orders.HeaderSelectionState);
        orders.ToggleHeaderSelectionCommand.Execute(null);
        True(rows.All(r => !r.IsSelected && !r.IsSelectedForOrders));
        True(updates.Contains(nameof(all.HeaderSelectionState)) && commandUpdates > 0,
            "Manual marks must notify the header binding and command.");
        return Task.CompletedTask;
    }

    private static Task ReceiptHeaderFiltersAsync()
    {
        var rows = HeaderReceipts();
        using var tab = new ReceiptTabViewModel(rows, new("", "Усі типи"));
        rows[0].IsSelected = true;
        tab.SearchText = "FIRST";
        Equal<bool?>(true, tab.HeaderSelectionState);
        tab.SearchText = "";
        tab.SetDisplayDates(new(2026, 9, 24), new(2026, 9, 24));
        Equal<bool?>(null, tab.HeaderSelectionState);
        tab.ToggleHeaderSelectionCommand.Execute(null);
        True(rows[0].IsSelected && !rows[1].IsSelected && rows[2].IsSelected,
            "Selecting visible dates must preserve existing hidden marks.");
        Equal<bool?>(true, tab.HeaderSelectionState);
        tab.ToggleHeaderSelectionCommand.Execute(null);
        True(rows.All(r => !r.IsSelected), "Clearing a fully marked view must also clear hidden dates.");
        tab.SetDisplayDates(null, null);
        tab.SelectedType = new(ReceiptTypes.Return, "Повернення");
        tab.ToggleHeaderSelectionCommand.Execute(null);
        True(!rows[0].IsSelected && rows[1].IsSelected && !rows[2].IsSelected);
        tab.SelectedType = new(ReceiptTypes.Sell, "Продаж");
        Equal<bool?>(null, tab.HeaderSelectionState);
        tab.View.SortDescriptions.Clear();
        tab.View.SortDescriptions.Add(new(nameof(ReceiptRowViewModel.Serial), ListSortDirection.Ascending));
        Equal<bool?>(null, tab.HeaderSelectionState);
        tab.ToggleHeaderSelectionCommand.Execute(null);
        True(rows.All(r => r.IsSelected));
        tab.SearchText = "NO-MATCH";
        Equal<bool?>(null, tab.HeaderSelectionState);
        True(tab.CanToggleHeaderSelection, "Hidden-only marks must still be clearable.");
        tab.ToggleHeaderSelectionCommand.Execute(null);
        Equal<bool?>(false, tab.HeaderSelectionState);
        True(!tab.CanToggleHeaderSelection && !tab.ToggleHeaderSelectionCommand.CanExecute(null));
        tab.ToggleHeaderSelectionCommand.Execute(null);
        True(rows.All(r => !r.IsSelected), "An empty disabled header must not select hidden rows.");
        tab.SearchText = "";
        Equal<bool?>(false, tab.HeaderSelectionState);
        True(tab.CanToggleHeaderSelection);
        return Task.CompletedTask;
    }

    private static Task ReceiptHeaderRowsAsync()
    {
        var rows = new ObservableCollection<ReceiptRowViewModel>();
        using var tab = new ReceiptTabViewModel(rows, new("", "Усі типи"));
        True(!tab.CanToggleHeaderSelection);
        var original = new ReceiptRowViewModel(Receipt(ReceiptOne, 1, 10000));
        rows.Add(original);
        True(tab.CanToggleHeaderSelection);
        tab.ToggleHeaderSelectionCommand.Execute(null);
        Equal<bool?>(true, tab.HeaderSelectionState);
        var added = new ReceiptRowViewModel(Receipt(ReceiptTwo, 2, 20000));
        rows.Add(added);
        Equal<bool?>(null, tab.HeaderSelectionState);
        added.IsSelected = true;
        Equal<bool?>(true, tab.HeaderSelectionState);
        rows.Remove(original);
        var changes = 0;
        tab.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(tab.HeaderSelectionState)) changes++; };
        original.IsSelected = false;
        Equal(0, changes);
        Equal<bool?>(true, tab.HeaderSelectionState);
        rows.Clear();
        Equal<bool?>(false, tab.HeaderSelectionState);
        True(!tab.CanToggleHeaderSelection);
        tab.Dispose();
        rows.Add(new(Receipt(ReceiptThree, 3, 30000)));
        True(!tab.CanToggleHeaderSelection && !tab.ToggleHeaderSelectionCommand.CanExecute(null));
        return Task.CompletedTask;
    }

    private static async Task ReceiptHeaderPrintingAsync()
    {
        var fixture = new Fixture();
        var (main, _) = fixture.Create();
        await main.RefreshAsync();
        var row = main.Receipts.Single(r => r.Id == ReceiptOne);
        row.IsSelected = true; row.IsSelectedForOrders = true;
        fixture.Printer.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var printing = ExecuteAsync(main.PrintSelectedCommand, () => !main.IsBusy);
        try
        {
            await fixture.Printer.Entered.Task;
            True(main.IsBusy);
            True(!main.AllReceiptsTab.CanToggleHeaderSelection && !main.OrdersReceiptsTab.CanToggleHeaderSelection);
            True(!main.AllReceiptsTab.ToggleHeaderSelectionCommand.CanExecute(null));
            main.AllReceiptsTab.ToggleHeaderSelectionCommand.Execute(null);
            True(main.Receipts.Count(r => r.IsSelected) == 1 && row.IsSelectedForOrders,
                "A busy header must not modify the frozen print selection.");
        }
        finally { fixture.Printer.Gate.TrySetResult(true); }
        await printing;
        Equal<bool?>(false, main.AllReceiptsTab.HeaderSelectionState);
        Equal<bool?>(null, main.OrdersReceiptsTab.HeaderSelectionState);
        True(row.IsSelectedForOrders && main.AllReceiptsTab.CanToggleHeaderSelection);
        Equal(1, fixture.Printer.Calls);
        main.CancelPendingOperations();
    }

    private static async Task OrderHeaderEligibilityAsync()
    {
        var (workspace, fixture) = await LabelWorkspace();
        var active = fixture.Source.Orders[0];
        var noTracking = active with { Key = active.Key with { OrderId = "no-tracking" }, Number = "NO-TTN", Shipments = [] };
        var whitespace = active with { Key = active.Key with { OrderId = "blank-tracking" }, Number = "BLANK-TTN", Shipments = [new("nova_poshta", "  ")] };
        var canceled = active with { Key = active.Key with { OrderId = "canceled" }, Number = "CANCELED", SourceStatus = "canceled" };
        fixture.Source.Orders = [.. fixture.Source.Orders, noTracking, whitespace, canceled];
        await workspace.SyncAsync();
        var printer = new LabelPrinter();
        using var labels = Labels(workspace, new(), new(), printer, new());
        Equal(5, workspace.AllOrderRows.Count);
        labels.ToggleHeaderSelectionCommand.Execute(null);
        Equal<bool?>(true, labels.HeaderSelectionState);
        True(workspace.AllOrderRows.Where(r => r.Number.StartsWith("ORDER-", StringComparison.Ordinal)).All(r => r.IsSelectedForLabels));
        True(workspace.AllOrderRows.Where(r => r.Number is "NO-TTN" or "BLANK-TTN" or "CANCELED").All(r => !r.IsSelectedForLabels),
            "Missing/blank TTNs and hidden cancellations must not be marked by the header.");
        workspace.ShowCanceledOrders = true;
        Equal<bool?>(null, labels.HeaderSelectionState);
        labels.ToggleHeaderSelectionCommand.Execute(null);
        True(workspace.AllOrderRows.Single(r => r.Number == "CANCELED").IsSelectedForLabels);
        Equal<bool?>(true, labels.HeaderSelectionState);
        workspace.ShowCanceledOrders = false;
        labels.ToggleHeaderSelectionCommand.Execute(null);
        True(workspace.AllOrderRows.All(r => !r.IsSelectedForLabels), "Clearing must include a now-hidden canceled order.");
        Equal<bool?>(false, labels.HeaderSelectionState);
        Equal(0, printer.Calls); Equal(0, fixture.Printer.Calls);
    }

    private static async Task OrderHeaderFiltersAsync()
    {
        var (workspace, fixture) = await LabelWorkspace();
        var printer = new LabelPrinter();
        using var labels = Labels(workspace, new(), new(), printer, new());
        var first = workspace.AllOrderRows.Single(r => r.Number == "ORDER-41");
        first.IsSelectedForLabels = true;
        Equal<bool?>(null, labels.HeaderSelectionState);
        workspace.OrderSearch = "ORDER-41";
        Equal<bool?>(true, labels.HeaderSelectionState);
        workspace.OrderSearch = "ORDER-42";
        Equal<bool?>(null, labels.HeaderSelectionState);
        labels.ToggleHeaderSelectionCommand.Execute(null);
        Equal<bool?>(true, labels.HeaderSelectionState);
        True(workspace.AllOrderRows.All(r => r.IsSelectedForLabels));
        workspace.Orders.SortDescriptions.Clear();
        workspace.Orders.SortDescriptions.Add(new(nameof(MarketplaceOrderRowViewModel.Number), ListSortDirection.Ascending));
        Equal<bool?>(true, labels.HeaderSelectionState);
        workspace.OrderFilter = "Rozetka";
        Equal<bool?>(null, labels.HeaderSelectionState);
        True(labels.CanToggleHeaderSelection);
        labels.ToggleHeaderSelectionCommand.Execute(null);
        True(workspace.AllOrderRows.All(r => !r.IsSelectedForLabels));
        True(!labels.CanToggleHeaderSelection);
        workspace.OrderFilter = "Усі";
        workspace.OrderSearch = "";
        labels.ToggleHeaderSelectionCommand.Execute(null);
        workspace.SetDisplayDates(new(2026, 9, 24), new(2026, 9, 24));
        Equal<bool?>(null, labels.HeaderSelectionState);
        labels.ToggleHeaderSelectionCommand.Execute(null);
        Equal<bool?>(false, labels.HeaderSelectionState);
        True(!labels.CanToggleHeaderSelection);
        workspace.SetDisplayDates(new(2026, 9, 23), new(2026, 9, 23));
        True(labels.CanToggleHeaderSelection);
        Equal(0, printer.Calls); Equal(0, fixture.Printer.Calls);
    }

    private static async Task OrderHeaderRefreshAsync()
    {
        var (workspace, fixture) = await LabelWorkspace();
        using var labels = Labels(workspace, new(), new(), new(), new());
        labels.ToggleHeaderSelectionCommand.Execute(null);
        fixture.Source.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshing = workspace.SyncAsync();
        try
        {
            True(workspace.IsBusy && labels.CanToggleHeaderSelection);
            labels.ToggleHeaderSelectionCommand.Execute(null);
            True(workspace.AllOrderRows.All(r => !r.IsSelectedForLabels),
                "A marketplace-only refresh must not prevent clearing reversible label marks.");
            labels.ToggleHeaderSelectionCommand.Execute(null);
            True(workspace.AllOrderRows.All(r => r.IsSelectedForLabels));
        }
        finally { fixture.Source.Gate.TrySetResult(true); }
        await refreshing;
        True(labels.CanToggleHeaderSelection);
        Equal<bool?>(true, labels.HeaderSelectionState);
        labels.ToggleHeaderSelectionCommand.Execute(null);
        var first = workspace.AllOrderRows.Single(r => r.Number == "ORDER-41");
        first.Update(first.Model with { Shipments = [] }, [], []);
        workspace.OrderSearch = "ORDER-41";
        Equal<bool?>(false, labels.HeaderSelectionState);
        True(!labels.CanToggleHeaderSelection, "A visible order without a TTN is not eligible.");
        var stateChanges = 0;
        labels.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(labels.HeaderSelectionState)) stateChanges++; };
        first.Update(first.Model with { Shipments = [new("nova_poshta", "replacement")] }, [], []);
        True(labels.CanToggleHeaderSelection && stateChanges > 0,
            "Receiving a TTN on the existing row must update header eligibility.");
        labels.ToggleHeaderSelectionCommand.Execute(null);
        True(first.IsSelectedForLabels);
        Equal<bool?>(true, labels.HeaderSelectionState);
        first.Update(first.Model with { Shipments = [] }, [], []);
        Equal<bool?>(null, labels.HeaderSelectionState);
        labels.ToggleHeaderSelectionCommand.Execute(null);
        True(!first.IsSelectedForLabels && !labels.CanToggleHeaderSelection);
        Equal(0, fixture.Printer.Calls);
    }

    private static async Task OrderHeaderPrintingAsync()
    {
        var (workspace, fixture) = await LabelWorkspace();
        var source = new ShippingLabelsTests.Source();
        var printer = new LabelPrinter(); var dialogs = new LabelDialogs();
        using var labels = Labels(workspace, new(), source, printer, dialogs);
        labels.ToggleHeaderSelectionCommand.Execute(null);
        source.OnLoad = dialogs.OnPreview = () =>
        {
            True(labels.IsBusy && !labels.CanToggleHeaderSelection && !labels.ToggleHeaderSelectionCommand.CanExecute(null));
            True(!labels.CanSelectPrintCandidates && !labels.SelectPrintCandidatesCommand.CanExecute(null));
            labels.ToggleHeaderSelectionCommand.Execute(null);
            labels.SelectPrintCandidatesCommand.Execute(null);
            True(workspace.AllOrderRows.All(r => r.IsSelectedForLabels));
        };
        await labels.RunAsync(null, true);
        Equal<bool?>(false, labels.HeaderSelectionState);
        True(labels.CanToggleHeaderSelection && workspace.AllOrderRows.All(r => !r.IsSelectedForLabels));
        Equal(1, printer.Calls); Equal(0, fixture.Printer.Calls);
    }

    private static readonly DateOnly HeaderWaybillDay = new(2026, 10, 9);
    private static WaybillRecord HeaderWaybill(string tracking, ShippingCarrier carrier = ShippingCarrier.NovaPoshta, bool deleted = false) =>
        new(carrier, carrier == ShippingCarrier.NovaPoshta ? "np-test" : "rd-test", "Synthetic account", "doc-" + tracking, tracking,
            HeaderWaybillDay.ToDateTime(new(12, 0)), deleted ? "Видалено" : "Створено", "Тестовий отримувач", "Київ", 125, 2, deleted);

    private sealed class HeaderWaybillSource : IWaybillService
    {
        public WaybillRecord[] Documents = [HeaderWaybill("NP-ONE"), HeaderWaybill("RD-TWO", ShippingCarrier.RozetkaDelivery), HeaderWaybill("DELETED", deleted: true)];
        public TaskCompletionSource<bool>? Gate;
        public async Task<WaybillFetchResult> FetchAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
        {
            if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
            return new(Documents, []);
        }
    }

    private static async Task WaybillHeaderFiltersAsync()
    {
        var source = new HeaderWaybillSource();
        using var waybills = new WaybillsViewModel(source);
        waybills.SetDisplayDates(HeaderWaybillDay, HeaderWaybillDay);
        await waybills.RefreshAsync();
        Equal<bool?>(false, waybills.HeaderSelectionState);
        var first = waybills.Rows.Single(r => r.TrackingNumber == "NP-ONE");
        var second = waybills.Rows.Single(r => r.TrackingNumber == "RD-TWO");
        var deleted = waybills.Rows.Single(r => r.IsDeleted);
        first.IsSelected = true;
        Equal<bool?>(null, waybills.HeaderSelectionState);
        waybills.ToggleHeaderSelectionCommand.Execute(null);
        Equal<bool?>(true, waybills.HeaderSelectionState);
        True(first.IsSelected && second.IsSelected && !deleted.IsSelected);
        waybills.SelectedCarrier = waybills.Carriers.Single(c => c.Value == ShippingCarrier.NovaPoshta);
        Equal<bool?>(true, waybills.HeaderSelectionState);
        waybills.View.SortDescriptions.Clear();
        waybills.View.SortDescriptions.Add(new(nameof(WaybillRowViewModel.TrackingNumber), ListSortDirection.Ascending));
        Equal<bool?>(true, waybills.HeaderSelectionState);
        waybills.ToggleHeaderSelectionCommand.Execute(null);
        True(waybills.Rows.All(r => !r.IsSelected), "Clearing NP must also clear hidden RD marks.");
        first.IsSelected = true;
        waybills.SearchText = "DELETED";
        Equal<bool?>(null, waybills.HeaderSelectionState);
        True(waybills.CanToggleHeaderSelection, "A deleted-only view must permit clearing hidden marks.");
        waybills.ToggleHeaderSelectionCommand.Execute(null);
        Equal<bool?>(false, waybills.HeaderSelectionState);
        True(!waybills.CanToggleHeaderSelection);
        deleted.IsSelected = true;
        True(!deleted.IsSelected);
        waybills.SelectedCarrier = waybills.Carriers[0];
        waybills.SearchText = "MISSING";
        True(!waybills.CanToggleHeaderSelection);
        second.IsSelected = true;
        Equal<bool?>(null, waybills.HeaderSelectionState);
        waybills.ToggleHeaderSelectionCommand.Execute(null);
        True(waybills.Rows.All(r => !r.IsSelected));
    }

    private static async Task WaybillHeaderRefreshAsync()
    {
        var source = new HeaderWaybillSource();
        using var waybills = new WaybillsViewModel(source);
        waybills.SetDisplayDates(HeaderWaybillDay, HeaderWaybillDay);
        True(!waybills.CanToggleHeaderSelection);
        await waybills.RefreshAsync();
        var old = waybills.Rows.Single(r => r.TrackingNumber == "NP-ONE");
        old.IsSelected = true;
        source.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshing = waybills.RefreshAsync();
        try
        {
            True(waybills.IsBusy && !waybills.CanToggleHeaderSelection);
            waybills.ToggleHeaderSelectionCommand.Execute(null);
            True(old.IsSelected && waybills.Rows.Count(r => r.IsSelected) == 1);
        }
        finally { source.Gate.TrySetResult(true); }
        await refreshing;
        Equal<bool?>(null, waybills.HeaderSelectionState);
        True(waybills.CanToggleHeaderSelection && waybills.Rows.Single(r => r.TrackingNumber == "NP-ONE").IsSelected);
        var changes = 0;
        waybills.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(waybills.HeaderSelectionState)) changes++; };
        old.IsSelected = false;
        Equal(0, changes);
        waybills.ToggleHeaderSelectionCommand.Execute(null);
        source.Documents = [source.Documents[0] with { IsDeleted = true }, source.Documents[1]];
        await waybills.RefreshAsync();
        Equal<bool?>(true, waybills.HeaderSelectionState);
        True(waybills.Rows.Single(r => r.IsDeleted).IsSelected == false);
        waybills.ToggleHeaderSelectionCommand.Execute(null);
        Equal<bool?>(false, waybills.HeaderSelectionState);
        source.Documents = [];
        await waybills.RefreshAsync();
        True(!waybills.CanToggleHeaderSelection && !waybills.ToggleHeaderSelectionCommand.CanExecute(null));
        waybills.SetDisplayDates(null, HeaderWaybillDay);
        await waybills.RefreshAsync();
        Equal<bool?>(false, waybills.HeaderSelectionState);
        True(!waybills.CanToggleHeaderSelection);
    }

    private sealed class HeaderWaybillDialogs : ILabelDialogs
    {
        public Action? OnPreview;
        public bool Preview(LabelPrintBatch batch, LabelPrinterGeometry geometry, IReadOnlyList<LabelPrintAttempt> previous, bool allowPrint)
        { OnPreview?.Invoke(); return true; }
        public Task SettingsAsync() => Task.CompletedTask;
        public void Error(string message) => throw new InvalidOperationException("Unexpected synthetic dialog: " + message);
    }

    private static async Task WaybillHeaderPrintingAsync()
    {
        var source = new HeaderWaybillSource();
        var printer = new DashboardWaybillPrinter(); var dialogs = new HeaderWaybillDialogs();
        using var waybills = new WaybillsViewModel(source, new DashboardWaybillPreparation(), new DashboardWaybillSettings(),
            new DashboardWaybillHistory(), printer, dialogs);
        waybills.SetDisplayDates(HeaderWaybillDay, HeaderWaybillDay);
        await waybills.RefreshAsync();
        waybills.ToggleHeaderSelectionCommand.Execute(null);
        waybills.SearchText = "NP-ONE";
        Equal<bool?>(true, waybills.HeaderSelectionState);
        dialogs.OnPreview = () =>
        {
            True(waybills.IsPrintingBusy && !waybills.CanToggleHeaderSelection && !waybills.ToggleHeaderSelectionCommand.CanExecute(null));
            waybills.ToggleHeaderSelectionCommand.Execute(null);
            True(waybills.Rows.Count(r => r.IsSelected) == 2);
            Equal(0, printer.Calls);
        };
        await waybills.RunAsync(null, true);
        Equal(1, printer.Calls);
        True(waybills.Rows.Single(r => r.TrackingNumber == "NP-ONE").HasPrintedLabel);
        True(!waybills.Rows.Single(r => r.TrackingNumber == "NP-ONE").IsSelected);
        True(waybills.Rows.Single(r => r.TrackingNumber == "RD-TWO").IsSelected);
        Equal<bool?>(null, waybills.HeaderSelectionState);
        True(waybills.CanToggleHeaderSelection);
        waybills.ToggleHeaderSelectionCommand.Execute(null);
        Equal<bool?>(true, waybills.HeaderSelectionState);
        waybills.ToggleHeaderSelectionCommand.Execute(null);
        Equal<bool?>(false, waybills.HeaderSelectionState);
        True(waybills.Rows.All(r => !r.IsSelected));
    }
}
