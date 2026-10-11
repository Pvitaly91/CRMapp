using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;
using CheckboxBatchPrinter.Views;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> OrderSelectionBackgroundScenarios =>
    [
        ("STA background marketplace refresh allows order header marks and retains them after row updates", () => StaAsync(OrderBackgroundHeaderAsync)),
        ("STA order candidates await history then use updated background rows links and tracking numbers", () => StaAsync(OrderBackgroundCandidatesLatestRowsAsync)),
        ("STA late order candidate history cannot change selection after account dates configuration or global refresh changes", () => StaAsync(OrderCandidatesChangedScopeAsync))
    ];

    private static async Task<(Fixture Fixture, MainViewModel Main, MarketplaceWorkspaceViewModel Workspace)> OrderSelectionFixtureAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders =
        [
            Order([ReceiptOne]) with { Shipments = [new("nova_poshta", "first")] },
            Order() with { Key = new(MarketplaceKind.Prom, "prom-test", "42"), Number = "UNLINKED-42",
                Total = 777777.77m, Shipments = [new("nova_poshta", "second")] }
        ];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await main.PrepareOrdersAsync();
        await workspace.SyncAsync();
        workspace.SetBackgroundRange(new(2026, 9, 23), new(2026, 9, 23));
        True(workspace.AllOrderRows.Single(r => r.Number == "ORDER-41").HasConfirmedLink);
        var unlinked = workspace.AllOrderRows.Single(r => r.Number == "UNLINKED-42");
        True(!unlinked.HasConfirmedLink && unlinked.LinkedReceipts.Count == 0,
            "The unrelated order must have neither confirmed nor suggested receipt links.");
        return (fixture, main, workspace);
    }

    private static async Task OrderBackgroundHeaderAsync()
    {
        var (fixture, main, workspace) = await OrderSelectionFixtureAsync();
        var printer = new LabelPrinter();
        using var labels = Labels(workspace, new(), new(), printer, new());
        var original = workspace.AllOrderRows.Single(r => r.Number == "ORDER-41");
        fixture.Source.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshing = workspace.SyncBackgroundAsync();
        try
        {
            True(workspace.IsBusy && !workspace.IsWorkspaceRefreshing);
            True(labels.CanToggleHeaderSelection && labels.ToggleHeaderSelectionCommand.CanExecute(null));
            labels.ToggleHeaderSelectionCommand.Execute(null);
            True(workspace.AllOrderRows.All(r => r.IsSelectedForLabels));
            labels.ToggleHeaderSelectionCommand.Execute(null);
            True(workspace.AllOrderRows.All(r => !r.IsSelectedForLabels));
            labels.ToggleHeaderSelectionCommand.Execute(null);
            fixture.Source.Orders = fixture.Source.Orders.Select(o => o.Key == original.Key
                ? o with { Number = "UPDATED-41", Shipments = [new("nova_poshta", "replacement")] } : o).ToArray();
        }
        finally { fixture.Source.Gate.TrySetResult(true); }
        await refreshing;
        try
        {
            True(ReferenceEquals(original, workspace.AllOrderRows.Single(r => r.Number == "UPDATED-41")),
                "Refreshing an existing full order key must retain its row and reversible marks.");
            True(workspace.AllOrderRows.All(r => r.IsSelectedForLabels));
            Equal<bool?>(true, labels.HeaderSelectionState);
            labels.ToggleHeaderSelectionCommand.Execute(null);
            True(workspace.AllOrderRows.All(r => !r.IsSelectedForLabels));
            Equal(0, printer.Calls); Equal(0, fixture.Printer.Calls);
        }
        finally { main.CancelPendingOperations(); }
    }

    private sealed class BackgroundCandidateHistory : ILabelHistoryStore
    {
        public TaskCompletionSource<bool> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<LabelPrintAttempt> Attempts = [];
        public int Loads;
        public async Task<IReadOnlyList<LabelPrintAttempt>> LoadAsync(CancellationToken cancellationToken = default)
        { Loads++; await Gate.Task.WaitAsync(cancellationToken); return Attempts; }
        public Task SaveAsync(LabelPrintAttempt attempt, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Selecting orders must not write print history.");
    }

    private static async Task OrderBackgroundCandidatesLatestRowsAsync()
    {
        var (fixture, main, workspace) = await OrderSelectionFixtureAsync();
        var first = workspace.AllOrderRows.Single(r => r.Number == "ORDER-41");
        var hidden = workspace.AllOrderRows.Single(r => r.Number == "UNLINKED-42");
        first.Update(first.Model with { Number = "VISIBLE-FIRST" }, first.LinkedReceipts, []);
        hidden.IsSelectedForLabels = true;
        workspace.OrderSearch = "VISIBLE";
        workspace.ShowCanceledOrders = true;
        var newUnknown = Order([ReceiptTwo]) with
        {
            Key = new(MarketplaceKind.Prom, "prom-test", "43"), Number = "VISIBLE-UNKNOWN", Total = 200,
            Shipments = [new("nova_poshta", "new-unknown")]
        };
        var newReady = Order([ReceiptThree]) with
        {
            Key = new(MarketplaceKind.Prom, "prom-test", "44"), Number = "VISIBLE-READY", Total = 300,
            Shipments = [new("nova_poshta", "new-ready")]
        };
        var history = new BackgroundCandidateHistory
        {
            Attempts = [CandidateAttempt(first, LabelSubmissionState.Submitted, "old-first-document"),
                CandidateAttempt(new MarketplaceOrderRowViewModel(newUnknown), LabelSubmissionState.SubmissionUnknown, "unknown-new-document")]
        };
        var printer = new LabelPrinter(); var dialogs = new LabelDialogs();
        using var labels = new ShippingLabelsViewModel(workspace, new ShippingLabelsTests.Stores(), history,
            new ShippingLabelsTests.Source(), new ShippingLabelsTests.Renderer(), printer, dialogs);
        fixture.Source.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshing = workspace.SyncBackgroundAsync();
        var selecting = ExecuteAsync(labels.SelectPrintCandidatesCommand);
        try
        {
            True(workspace.IsBusy && labels.IsBusy && history.Loads == 1);
            True(!first.IsSelectedForLabels && hidden.IsSelectedForLabels);
            fixture.Source.Orders =
            [
                first.Model with { Shipments = [new("nova_poshta", "new-first")] }, hidden.Model, newUnknown, newReady,
                Order() with { Key = new(MarketplaceKind.Prom, "prom-test", "45"), Number = "VISIBLE-NOLINK", Total = 777,
                    Shipments = [new("nova_poshta", "new-no-link")] },
                Order() with { Key = new(MarketplaceKind.Prom, "prom-test", "46"), Number = "VISIBLE-CANCELED", Total = 888,
                    SourceStatus = "canceled", Shipments = [new("nova_poshta", "new-canceled")] }
            ];
            fixture.Source.Gate.TrySetResult(true);
            await refreshing;
            True(!labels.SelectPrintCandidatesCommand.CanExecute(null), "A second click must not overlap its history read.");
            True(!first.IsSelectedForLabels && hidden.IsSelectedForLabels);
        }
        finally { fixture.Source.Gate.TrySetResult(true); history.Gate.TrySetResult(true); }
        await selecting;
        try
        {
            True(first.IsSelectedForLabels && !first.HasLabelSubmission,
                "An old printed TTN must not suppress its replacement loaded during history reading.");
            True(workspace.AllOrderRows.Single(r => r.Number == "VISIBLE-READY").IsSelectedForLabels,
                "A new linked order loaded while history was pending must be included.");
            var unknown = workspace.AllOrderRows.Single(r => r.Number == "VISIBLE-UNKNOWN");
            True(!unknown.IsSelectedForLabels && unknown.HasLabelSubmission,
                "History must be applied to newly loaded rows before selecting candidates.");
            True(workspace.AllOrderRows.Where(r => r.Number is "VISIBLE-NOLINK" or "VISIBLE-CANCELED").All(r => !r.IsSelectedForLabels));
            True(hidden.IsSelectedForLabels, "Candidate selection must preserve marks outside the current filter.");
            Equal(0, printer.Calls); Equal(0, fixture.Printer.Calls); Equal(0, dialogs.Previews);
        }
        finally { main.CancelPendingOperations(); }
    }

    private static async Task OrderCandidatesChangedScopeAsync()
    {
        foreach (var change in new[] { "account", "dates", "configuration", "global-refresh" })
        {
            var (fixture, main, workspace) = await OrderSelectionFixtureAsync();
            var history = new BackgroundCandidateHistory();
            using var labels = CandidateLabels(workspace, history, new());
            var originalFetchCalls = fixture.Source.FetchCalls;
            var selecting = ExecuteAsync(labels.SelectPrintCandidatesCommand);
            try
            {
                if (change == "account")
                {
                    const string account = "different-test-account";
                    var day = new DateOnly(2026, 9, 23);
                    workspace.SetReceiptScope(main.Receipts, account, day, day);
                    await workspace.AttachAsync(main.Receipts, account, day, day);
                }
                else if (change == "dates") workspace.SetDisplayDates(null, null);
                else if (change == "configuration") workspace.InvalidateConfiguration();
                else workspace.IsWorkspaceRefreshing = true;
                foreach (var row in workspace.AllOrderRows)
                {
                    AttachCandidateReceipt(row);
                    row.IsSelectedForLabels = row.Number == "UNLINKED-42";
                }
                var marks = workspace.AllOrderRows.ToDictionary(r => r.Key, r => r.IsSelectedForLabels);
                True(marks.Count > 0);
                history.Gate.TrySetResult(true);
                await selecting;
                True(workspace.AllOrderRows.All(r => marks[r.Key] == r.IsSelectedForLabels),
                    $"Late candidate history changed marks after {change} changed the selection scope.");
                Equal(originalFetchCalls, fixture.Source.FetchCalls); Equal(0, fixture.Printer.Calls);
            }
            finally { history.Gate.TrySetResult(true); workspace.IsWorkspaceRefreshing = false; main.CancelPendingOperations(); }
        }
    }

    private static async Task OrderSelectionBackgroundDashboardAsync()
    {
        _xamlCheckpoint = "order selection during background updates";
        var (fixture, main, workspace) = await OrderSelectionFixtureAsync();
        var printer = new LabelPrinter();
        using var labels = Labels(workspace, new(), new(), printer, new());
        workspace.Labels = labels;
        main.SelectedTabIndex = 1;
        var window = new MainWindow { DataContext = main };
        var failures = new BindingErrorListener();
        var oldLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Listeners.Add(failures);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        fixture.Source.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshing = workspace.SyncBackgroundAsync();
        Task? global = null;
        try
        {
            LayoutContent(window); await DrainDispatcherAsync();
            var header = (CheckBox)window.FindName("OrdersHeaderToggle");
            var candidates = (Button)window.FindName("OrdersPrintCandidatesButton");
            True(workspace.IsBusy && header.IsEnabled && candidates.IsEnabled,
                "The real order header checkbox and candidate icon must remain enabled while only marketplace data is updating.");
            ClickCheckBox(header); await DrainDispatcherAsync();
            True(workspace.AllOrderRows.All(r => r.IsSelectedForLabels) && header.IsChecked == true);
            ClickCheckBox(header); await DrainDispatcherAsync();
            True(workspace.AllOrderRows.All(r => !r.IsSelectedForLabels) && header.IsChecked == false);
            var unlinkedBefore = workspace.AllOrderRows.Single(r => r.Number == "UNLINKED-42");
            True(!unlinkedBefore.HasConfirmedLink && unlinkedBefore.LinkedReceipts.Count == 0,
                $"The unlinked fixture became eligible before the candidate click: confirmed={unlinkedBefore.HasConfirmedLink}, " +
                $"receipts={unlinkedBefore.LinkedReceipts.Count}, link={unlinkedBefore.LinkStatus}.");
            True(ReferenceEquals(candidates.Command, labels.SelectPrintCandidatesCommand),
                "The candidate icon resolved a different workspace command.");
            ClickCandidateButton(candidates); await DrainDispatcherAsync();
            True(workspace.AllOrderRows.Single(r => r.Number == "ORDER-41").IsSelectedForLabels);
            var unlinkedAfter = workspace.AllOrderRows.Single(r => r.Number == "UNLINKED-42");
            True(!unlinkedAfter.IsSelectedForLabels,
                $"Unlinked row marked after candidate click: confirmed={unlinkedAfter.HasConfirmedLink}, receipts={unlinkedAfter.LinkedReceipts.Count}, " +
                $"link={unlinkedAfter.LinkStatus}, status={labels.Status}.");
            RenderSyntheticContent(window, "order-selection-background.png");
            fixture.Source.Gate.TrySetResult(true); await refreshing; await DrainDispatcherAsync();
            True(header.IsEnabled && candidates.IsEnabled);
            True(workspace.AllOrderRows.Single(r => r.Number == "ORDER-41").IsSelectedForLabels);
            var beforeGlobal = workspace.AllOrderRows.ToDictionary(r => r.Key, r => r.IsSelectedForLabels);
            fixture.ReceiptSource.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            global = main.RefreshAllAsync(); await DrainDispatcherAsync();
            True(workspace.IsWorkspaceRefreshing && !header.IsEnabled && !candidates.IsEnabled,
                "The explicit all-data refresh must continue to hold order selection disabled.");
            ClickCheckBox(header); ClickCandidateButton(candidates); await DrainDispatcherAsync();
            True(workspace.AllOrderRows.All(r => beforeGlobal[r.Key] == r.IsSelectedForLabels));
            fixture.ReceiptSource.Gate.TrySetResult(true); await global; await DrainDispatcherAsync();
            True(header.IsEnabled && candidates.IsEnabled);
            Equal(0, printer.Calls); Equal(0, fixture.Printer.Calls);
            True(failures.Messages.Count == 0, "Background order selection binding errors: " + string.Join("\n", failures.Messages));
        }
        finally
        {
            fixture.Source.Gate.TrySetResult(true); fixture.ReceiptSource.Gate?.TrySetResult(true);
            await refreshing;
            if (global is not null) await global;
            main.CancelPendingOperations(); window.Close(); await DrainDispatcherAsync();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(failures);
            PresentationTraceSources.DataBindingSource.Switch.Level = oldLevel;
        }
    }
}
