using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;
using CheckboxBatchPrinter.Views;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> PrintCandidateScenarios =>
    [
        ("STA receipt candidates require an active linked order and an unprinted ready receipt", () => StaAsync(ReceiptCandidatesEligibilityAsync)),
        ("STA receipt candidates preserve hidden marks and the other receipt tab", () => StaAsync(ReceiptCandidatesFiltersAsync)),
        ("STA receipt candidates notify changed data and refuse busy or disposed selection", () => StaAsync(ReceiptCandidatesLifecycleAsync)),
        ("STA order candidates include manual exact automatic and persisted links but exclude cancellations", () => StaAsync(OrderCandidatesEligibilityAsync)),
        ("STA order candidates reload partial unknown and failed submission history across restart", () => StaAsync(OrderCandidatesHistoryAsync)),
        ("STA order candidate history loading freezes actions and selects the current filtered view", () => StaAsync(OrderCandidatesLoadingAsync)),
        ("STA unreadable order candidate history preserves every existing mark", () => StaAsync(OrderCandidatesHistoryFailureAsync)),
        ("STA order candidates refuse workspace refresh and disposal", () => StaAsync(OrderCandidatesLifecycleAsync))
    ];

    private static ReceiptRowViewModel CandidateReceipt(string id, MarketplaceOrder? order,
        ReceiptLinkState state = ReceiptLinkState.Exact, PrintItemStatus printStatus = PrintItemStatus.Waiting,
        string fiscalStatus = "DONE", int day = 23)
    {
        var row = new ReceiptRowViewModel(new ReceiptRecord
        {
            Id = id, Serial = 1, FiscalCode = id, Status = fiscalStatus, Type = ReceiptTypes.Sell,
            TotalSumMinor = 10000, FiscalDate = new DateTimeOffset(2026, 9, day, 12, 0, 0, TimeSpan.FromHours(3))
        }) { PrintStatus = printStatus };
        row.OrderMatch = new(state, order, "Synthetic candidate fixture", order is null ? [Order()] : []);
        return row;
    }

    private static Task ReceiptCandidatesEligibilityAsync()
    {
        var active = Order();
        ObservableCollection<ReceiptRowViewModel> rows =
        [
            CandidateReceipt("manual", active, ReceiptLinkState.Manual),
            CandidateReceipt("exact", active),
            CandidateReceipt("automatic-error", active, ReceiptLinkState.Suggested, PrintItemStatus.Error),
            CandidateReceipt("printed", active, printStatus: PrintItemStatus.Done),
            CandidateReceipt("printing", active, printStatus: PrintItemStatus.Printing),
            CandidateReceipt("downloading", active, printStatus: PrintItemStatus.Downloading),
            CandidateReceipt("candidate-only", null, ReceiptLinkState.Candidates),
            CandidateReceipt("canceled", active with { SourceStatus = "canceled" }),
            CandidateReceipt("not-fiscalized", active, fiscalStatus: "CREATED")
        ];
        using var tab = new ReceiptTabViewModel(rows, new("", "Усі типи"));
        foreach (var row in rows) { row.IsSelected = true; row.IsSelectedForOrders = true; }
        tab.SelectPrintCandidatesCommand.Execute(null);
        True(rows.Take(3).All(r => r.IsSelected));
        True(rows.Skip(3).All(r => !r.IsSelected));
        True(rows.All(r => r.IsSelectedForOrders), "Receipt candidate selection must not change the other tab's marks.");
        tab.SelectPrintCandidatesCommand.Execute(null);
        True(rows.Take(3).All(r => r.IsSelected) && rows.Skip(3).All(r => !r.IsSelected),
            "A second candidate click must retain the same selection, not toggle it off.");
        return Task.CompletedTask;
    }

    private static Task ReceiptCandidatesFiltersAsync()
    {
        var active = Order();
        ObservableCollection<ReceiptRowViewModel> rows =
        [CandidateReceipt("VISIBLE-GOOD", active), CandidateReceipt("VISIBLE-BAD", null),
            CandidateReceipt("HIDDEN-GOOD", active, day: 24), CandidateReceipt("HIDDEN-BAD", null, day: 24)];
        using var all = new ReceiptTabViewModel(rows, new("", "Усі типи"));
        using var orders = new ReceiptTabViewModel(rows, new("", "Усі типи"), usesOrders: true);
        rows[1].IsSelectedForOrders = rows[2].IsSelectedForOrders = rows[3].IsSelectedForOrders = true;
        rows[1].IsSelected = true;
        orders.SetDisplayDates(new(2026, 9, 23), new(2026, 9, 23));
        orders.View.SortDescriptions.Clear();
        orders.View.SortDescriptions.Add(new(nameof(ReceiptRowViewModel.FiscalCode), ListSortDirection.Ascending));
        orders.SelectPrintCandidatesCommand.Execute(null);
        True(rows[0].IsSelectedForOrders && !rows[1].IsSelectedForOrders);
        True(rows[2].IsSelectedForOrders && rows[3].IsSelectedForOrders,
            "Candidate selection must leave marks outside the visible date range untouched.");
        True(rows[1].IsSelected && rows.Where(r => r != rows[1]).All(r => !r.IsSelected));
        all.SearchText = "VISIBLE";
        all.SelectPrintCandidatesCommand.Execute(null);
        True(rows[0].IsSelected && !rows[1].IsSelected);
        all.SearchText = "NO-MATCH";
        True(!all.CanSelectPrintCandidates && !all.SelectPrintCandidatesCommand.CanExecute(null));
        all.SelectPrintCandidatesCommand.Execute(null);
        True(rows[0].IsSelected && rows[3].IsSelectedForOrders);
        return Task.CompletedTask;
    }

    private static Task ReceiptCandidatesLifecycleAsync()
    {
        ObservableCollection<ReceiptRowViewModel> rows = [CandidateReceipt("CHANGING", null)];
        using var tab = new ReceiptTabViewModel(rows, new("", "Усі типи"));
        var notifications = 0;
        tab.SelectPrintCandidatesCommand.CanExecuteChanged += (_, _) => notifications++;
        rows[0].OrderMatch = new(ReceiptLinkState.Manual, Order(), "Synthetic linked order", []);
        True(notifications > 0);
        tab.SelectPrintCandidatesCommand.Execute(null); True(rows[0].IsSelected);
        rows[0].PrintStatus = PrintItemStatus.Done;
        tab.SelectPrintCandidatesCommand.Execute(null); True(!rows[0].IsSelected);
        rows[0].PrintStatus = PrintItemStatus.Waiting;
        tab.IsHeaderSelectionBusy = true;
        True(!tab.CanSelectPrintCandidates && !tab.SelectPrintCandidatesCommand.CanExecute(null));
        tab.SelectPrintCandidatesCommand.Execute(null); True(!rows[0].IsSelected);
        tab.IsHeaderSelectionBusy = false;
        tab.SelectPrintCandidatesCommand.Execute(null); True(rows[0].IsSelected);
        tab.Dispose();
        rows[0].PrintStatus = PrintItemStatus.Done;
        True(!tab.CanSelectPrintCandidates && !tab.SelectPrintCandidatesCommand.CanExecute(null));
        tab.SelectPrintCandidatesCommand.Execute(null); True(rows[0].IsSelected);
        return Task.CompletedTask;
    }

    private static void AttachCandidateReceipt(MarketplaceOrderRowViewModel row,
        ReceiptLinkState state = ReceiptLinkState.Exact, PrintItemStatus printStatus = PrintItemStatus.Waiting)
    {
        var receipt = CandidateReceipt("linked-" + row.Number, row.Model, state, printStatus);
        row.Update(row.Model, [receipt], []);
    }

    private static async Task OrderCandidatesEligibilityAsync()
    {
        var (workspace, fixture) = await LabelWorkspace();
        fixture.Source.Orders = Enumerable.Range(0, 7).Select(i => fixture.Source.Orders[0] with
        {
            Key = new(MarketplaceKind.Prom, "prom-test", "candidate-" + i), Number = "CANDIDATE-" + i,
            SourceStatus = i == 5 ? "canceled" : "", Shipments = i == 3 ? [] : [new("nova_poshta", "candidate-ttn-" + i)]
        }).ToArray();
        await workspace.SyncAsync(); workspace.ShowCanceledOrders = true;
        var rows = workspace.AllOrderRows.Where(r => r.Number.StartsWith("CANDIDATE-", StringComparison.Ordinal))
            .OrderBy(r => r.Number).ToArray();
        AttachCandidateReceipt(rows[0], ReceiptLinkState.Manual, PrintItemStatus.Done);
        AttachCandidateReceipt(rows[1], ReceiptLinkState.Exact);
        AttachCandidateReceipt(rows[2], ReceiptLinkState.Suggested);
        AttachCandidateReceipt(rows[3]); // No extra TTN condition was requested for this shortcut.
        rows[4].Update(rows[4].Model, [], [new() { ReceiptId = "outside-loaded-receipts", ConfirmedOrder = rows[4].Key }]);
        AttachCandidateReceipt(rows[5]);
        var candidateOnly = CandidateReceipt("candidate-list-only", null, ReceiptLinkState.Candidates);
        candidateOnly.OrderMatch = new(ReceiptLinkState.Candidates, null, "Synthetic ambiguity", [rows[6].Model]);
        rows[6].Update(rows[6].Model, [candidateOnly], []);
        foreach (var row in rows) row.IsSelectedForLabels = true;
        var printer = new LabelPrinter(); var dialogs = new LabelDialogs();
        using var labels = Labels(workspace, new(), new(), printer, dialogs);
        await ExecuteAsync(labels.SelectPrintCandidatesCommand);
        True(rows.Take(5).All(r => r.IsSelectedForLabels),
            "Manual, exact, automatic, persisted links and a linked row without a TTN remain candidates.");
        True(rows.Skip(5).All(r => !r.IsSelectedForLabels),
            "Canceled orders and candidate-list-only ambiguity are not linked print candidates.");
        True(rows[4].HasConfirmedLink && rows[4].LinkedReceipts.Count == 0);
        True(rows[0].LinkedReceipts.Single().PrintStatus == PrintItemStatus.Done,
            "A printed fiscal receipt does not mean its shipping label was printed.");
        await ExecuteAsync(labels.SelectPrintCandidatesCommand);
        True(rows.Take(5).All(r => r.IsSelectedForLabels));
        Equal(0, printer.Calls); Equal(0, fixture.Printer.Calls); Equal(0, dialogs.Previews);
    }

    private static LabelPrintAttempt CandidateAttempt(MarketplaceOrderRowViewModel row, LabelSubmissionState state,
        string id, int shipment = 0)
    {
        var reference = ShippingCarrierNames.ForOrder(row.Model)[shipment];
        return new(id, "synthetic-job", "synthetic-printer", DateTimeOffset.UtcNow, state,
            [new(reference.Carrier, "synthetic", reference.TrackingNumber, "synthetic-doc", 1, "synthetic-hash")], 42);
    }

    private static async Task OrderCandidatesHistoryAsync()
    {
        var (workspace, fixture) = await LabelWorkspace();
        var first = workspace.AllOrderRows.Single(r => r.Number == "ORDER-41");
        var second = workspace.AllOrderRows.Single(r => r.Number == "ORDER-42");
        first.Update(first.Model with { Shipments = [new("nova_poshta", "first"), new("nova_poshta", "unprinted-second-place")] }, [], []);
        AttachCandidateReceipt(first); AttachCandidateReceipt(second);
        var history = new ShippingLabelsTests.History(); var printer = new LabelPrinter();
        using var labels = Labels(workspace, history, new(), printer, new());
        await ExecuteAsync(labels.SelectPrintCandidatesCommand);
        True(first.IsSelectedForLabels && second.IsSelectedForLabels);
        await history.SaveAsync(CandidateAttempt(first, LabelSubmissionState.Submitted, "partial"));
        await history.SaveAsync(CandidateAttempt(second, LabelSubmissionState.SubmissionUnknown, "unknown"));
        await history.SaveAsync(CandidateAttempt(second, LabelSubmissionState.NotSubmitted, "failed-retry"));
        await ExecuteAsync(labels.SelectPrintCandidatesCommand);
        True(!first.IsSelectedForLabels && !second.IsSelectedForLabels);
        True(first.HasLabelSubmission && !first.HasPrintedLabel,
            "Partial submission must exclude a candidate even when the full order is not marked printed.");
        True(second.HasLabelSubmission && !second.HasPrintedLabel,
            "An unknown submission must not be silently scheduled again after a failed retry.");
        using var reopened = Labels(workspace, history, new(), printer, new());
        await ExecuteAsync(reopened.SelectPrintCandidatesCommand);
        True(!first.IsSelectedForLabels && !second.IsSelectedForLabels);
        first.Update(first.Model with { Shipments = [new("nova_poshta", "replacement-new-document")] }, first.LinkedReceipts, []);
        True(!first.HasLabelSubmission);
        await ExecuteAsync(reopened.SelectPrintCandidatesCommand);
        True(first.IsSelectedForLabels && !second.IsSelectedForLabels,
            "History for a replaced TTN must not suppress the new document.");
        history.Attempts.Clear();
        await history.SaveAsync(CandidateAttempt(second, LabelSubmissionState.NotSubmitted, "never-submitted"));
        await ExecuteAsync(reopened.SelectPrintCandidatesCommand);
        True(first.IsSelectedForLabels && second.IsSelectedForLabels);
        Equal(0, printer.Calls); Equal(0, fixture.Printer.Calls);
    }

    private sealed class CandidateHistory : ILabelHistoryStore
    {
        public TaskCompletionSource<bool>? Gate;
        public bool Fail;
        public int Loads;
        public async Task<IReadOnlyList<LabelPrintAttempt>> LoadAsync(CancellationToken cancellationToken = default)
        {
            Loads++;
            if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
            if (Fail) throw new IOException("Synthetic unreadable label journal");
            return [];
        }
        public Task SaveAsync(LabelPrintAttempt attempt, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Selecting candidates must not write print history.");
    }

    private static ShippingLabelsViewModel CandidateLabels(MarketplaceWorkspaceViewModel workspace,
        ILabelHistoryStore history, LabelDialogs dialogs) => new(workspace, new ShippingLabelsTests.Stores(),
            history, new ShippingLabelsTests.Source(), new ShippingLabelsTests.Renderer(), new LabelPrinter(), dialogs);

    private static async Task OrderCandidatesLoadingAsync()
    {
        var (workspace, fixture) = await LabelWorkspace();
        foreach (var row in workspace.AllOrderRows) AttachCandidateReceipt(row);
        var first = workspace.AllOrderRows.Single(r => r.Number == "ORDER-41");
        var second = workspace.AllOrderRows.Single(r => r.Number == "ORDER-42");
        first.IsSelectedForLabels = true;
        var history = new CandidateHistory { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var labels = CandidateLabels(workspace, history, new());
        var selecting = ExecuteAsync(labels.SelectPrintCandidatesCommand);
        try
        {
            True(labels.IsBusy && !labels.CanSelectPrintCandidates && !labels.SelectPrintCandidatesCommand.CanExecute(null));
            True(!labels.PrintCommand.CanExecute(null) && !labels.ToggleHeaderSelectionCommand.CanExecute(null));
            labels.SelectPrintCandidatesCommand.Execute(null); Equal(1, history.Loads);
            workspace.OrderSearch = "ORDER-42";
            True(first.IsSelectedForLabels && !second.IsSelectedForLabels);
        }
        finally { history.Gate.TrySetResult(true); }
        await selecting;
        True(first.IsSelectedForLabels && second.IsSelectedForLabels,
            "Selection must use the current view and preserve marks hidden while history was loading.");
        second.Update(second.Model, [], []);
        await ExecuteAsync(labels.SelectPrintCandidatesCommand);
        True(first.IsSelectedForLabels && !second.IsSelectedForLabels);
        workspace.OrderSearch = "NO-MATCH";
        True(!labels.CanSelectPrintCandidates && !labels.SelectPrintCandidatesCommand.CanExecute(null));
        labels.SelectPrintCandidatesCommand.Execute(null); Equal(2, history.Loads);
        True(first.IsSelectedForLabels); Equal(0, fixture.Printer.Calls);
    }

    private static async Task OrderCandidatesHistoryFailureAsync()
    {
        var (workspace, fixture) = await LabelWorkspace();
        foreach (var row in workspace.AllOrderRows) AttachCandidateReceipt(row);
        var first = workspace.AllOrderRows.Single(r => r.Number == "ORDER-41");
        first.IsSelectedForLabels = true;
        var history = new CandidateHistory { Fail = true }; var dialogs = new LabelDialogs();
        using var labels = CandidateLabels(workspace, history, dialogs);
        await ExecuteAsync(labels.SelectPrintCandidatesCommand);
        True(first.IsSelectedForLabels && !workspace.AllOrderRows.Single(r => r.Number == "ORDER-42").IsSelectedForLabels,
            "A failed history read must preserve the entire prior selection.");
        True(!labels.IsBusy && labels.CanSelectPrintCandidates);
        True(dialogs.Errors.Count == 1 || !string.IsNullOrWhiteSpace(labels.Status));
        Equal(0, dialogs.Previews); Equal(0, fixture.Printer.Calls);
    }

    private static async Task OrderCandidatesLifecycleAsync()
    {
        var (workspace, fixture) = await LabelWorkspace();
        var history = new CandidateHistory();
        using var labels = CandidateLabels(workspace, history, new());
        fixture.Source.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshing = workspace.SyncAsync();
        try
        {
            True(!labels.CanSelectPrintCandidates && !labels.SelectPrintCandidatesCommand.CanExecute(null));
            labels.SelectPrintCandidatesCommand.Execute(null); Equal(0, history.Loads);
        }
        finally { fixture.Source.Gate.TrySetResult(true); }
        await refreshing;
        foreach (var row in workspace.AllOrderRows) AttachCandidateReceipt(row);
        await ExecuteAsync(labels.SelectPrintCandidatesCommand);
        True(workspace.AllOrderRows.All(r => r.IsSelectedForLabels));
        labels.Dispose();
        True(!labels.CanSelectPrintCandidates && !labels.SelectPrintCandidatesCommand.CanExecute(null));
        labels.SelectPrintCandidatesCommand.Execute(null); Equal(1, history.Loads);
        var lateHistory = new CandidateHistory { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var disposedWhileLoading = CandidateLabels(workspace, lateHistory, new());
        workspace.AllOrderRows.First().IsSelectedForLabels = false;
        var original = workspace.AllOrderRows.Select(r => r.IsSelectedForLabels).ToArray();
        var lateSelection = ExecuteAsync(disposedWhileLoading.SelectPrintCandidatesCommand);
        disposedWhileLoading.Dispose();
        lateHistory.Gate.TrySetResult(true); await lateSelection;
        True(original.SequenceEqual(workspace.AllOrderRows.Select(r => r.IsSelectedForLabels)),
            "A history load completed after disposal must not change marks.");
        Equal(0, fixture.Printer.Calls);
    }

    private static async Task PrintCandidatesDashboardAsync()
    {
        _xamlCheckpoint = "print candidate icon headers";
        var fixture = new Fixture(); fixture.Source.Orders = [Order(receiptIds: [ReceiptOne]) with { Shipments = [new("nova_poshta", "candidate-ttn")] }];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync(); await main.PrepareOrdersAsync(); await workspace.SyncAsync();
        using var labels = Labels(workspace, new(), new(), new(), new()); workspace.Labels = labels;
        var window = new MainWindow { DataContext = main };
        var failures = new BindingErrorListener();
        var oldLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Listeners.Add(failures);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            main.SelectedTabIndex = 0; LayoutContent(window); await DrainDispatcherAsync();
            var all = (Button)window.FindName("AllReceiptsPrintCandidatesButton");
            var orderReceipts = (Button)window.FindName("OrdersReceiptsPrintCandidatesButton");
            var orders = (Button)window.FindName("OrdersPrintCandidatesButton");
            True(ReferenceEquals(all.Command, main.AllReceiptsTab.SelectPrintCandidatesCommand));
            True(ReferenceEquals(orderReceipts.Command, main.OrdersReceiptsTab.SelectPrintCandidatesCommand));
            True(ReferenceEquals(orders.Command, labels.SelectPrintCandidatesCommand));
            foreach (var button in new[] { all, orderReceipts, orders })
                True(button.ToolTip is not null && button.ContentTemplate is not null,
                    "Each print candidate header must be an icon with an explanation.");
            True(VisualChildren<System.Windows.Shapes.Path>(all).Any(), "Receipt candidate icon was not rendered.");
            foreach (var row in main.Receipts) row.IsSelected = true;
            ClickCandidateButton(all); await DrainDispatcherAsync();
            True(main.Receipts.Single(r => r.Id == ReceiptOne).IsSelected);
            True(main.Receipts.Where(r => r.Id != ReceiptOne).All(r => !r.IsSelected));
            RenderSyntheticContent(window, "receipt-print-candidates.png");
            main.SelectedTabIndex = 1; LayoutContent(window); await DrainDispatcherAsync();
            True(VisualChildren<System.Windows.Shapes.Path>(orderReceipts).Any() &&
                VisualChildren<System.Windows.Shapes.Path>(orders).Any(), "Order dashboard candidate icons were not rendered.");
            ClickCandidateButton(orderReceipts); await DrainDispatcherAsync();
            True(main.Receipts.Single(r => r.Id == ReceiptOne).IsSelectedForOrders);
            True(main.Receipts.Where(r => r.Id != ReceiptOne).All(r => !r.IsSelectedForOrders));
            ClickCandidateButton(orders); await DrainDispatcherAsync();
            True(workspace.AllOrderRows.Single().IsSelectedForLabels);
            RenderSyntheticContent(window, "order-print-candidates.png");
            main.AllReceiptsTab.IsHeaderSelectionBusy = true; await DrainDispatcherAsync();
            True(!all.IsEnabled);
            Equal(0, fixture.Printer.Calls);
            True(failures.Messages.Count == 0, "Print candidate header binding errors: " + string.Join("\n", failures.Messages));
        }
        finally
        {
            main.CancelPendingOperations(); window.Close(); await DrainDispatcherAsync();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(failures);
            PresentationTraceSources.DataBindingSource.Switch.Level = oldLevel;
        }
    }

    private static void ClickCandidateButton(Button button) => typeof(Button)
        .GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(button, null);
}
