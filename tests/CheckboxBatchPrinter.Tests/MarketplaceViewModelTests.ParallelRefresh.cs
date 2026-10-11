using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task RefreshWaitUntilAsync(Func<bool> ready)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!ready())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("The expected parallel refresh state was not published.");
            await Task.Delay(1);
        }
    }

    private static Task AllRefreshProvidersEnteredAsync(Fixture fixture, RefreshAllWaybillSource carriers) =>
        Task.WhenAll(fixture.ReceiptSource.Entered.Task, fixture.Source.Entered.Task,
            fixture.Rozetka.Entered.Task, carriers.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));

    private static void BlockRefreshProviders(Fixture fixture)
    {
        fixture.ReceiptSource.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Source.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Rozetka.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static void ReleaseRefreshProviders(Fixture fixture, RefreshAllWaybillSource carriers)
    {
        fixture.ReceiptSource.Gate?.TrySetResult(true); fixture.Source.Gate?.TrySetResult(true);
        fixture.Rozetka.Gate?.TrySetResult(true); carriers.Gate?.TrySetResult(true);
    }

    private static async Task RefreshAllConcurrentStartsAsync()
    {
        var fixture = RefreshAllFixture(); BlockRefreshProviders(fixture);
        var carriers = new RefreshAllWaybillSource { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var waybills = new WaybillsViewModel(carriers);
        var (main, workspace) = fixture.Create(waybills: waybills);
        var refreshing = main.RefreshAllAsync();
        try
        {
            // These gates prove overlap without a speed threshold or real network.
            await AllRefreshProvidersEnteredAsync(fixture, carriers);
            True(!refreshing.IsCompleted && main.IsRefreshingAll && main.IsBusy);
            Equal(0, main.Receipts.Count); Equal(0, workspace.AllOrderRows.Count); Equal(0, waybills.Rows.Count);
            Equal(1, fixture.ReceiptSource.Calls); Equal(1, fixture.Source.FetchCalls);
            Equal(1, fixture.Rozetka.FetchCalls); Equal(1, carriers.Calls.Count);
            True(!main.RefreshAllCommand.CanExecute(null) && !workspace.SyncCommand.CanExecute(null) &&
                !waybills.RefreshCommand.CanExecute(null));
            ReleaseRefreshProviders(fixture, carriers);
            await refreshing.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(3, main.Receipts.Count); Equal(2, workspace.AllOrderRows.Count); Equal(2, waybills.Rows.Count);
            Equal(0, fixture.Printer.Calls); Equal(0, fixture.Links.Saves);
            True(!main.IsBusy && !workspace.IsWorkspaceRefreshing && !waybills.IsWorkspaceRefreshing);
        }
        finally { ReleaseRefreshProviders(fixture, carriers); main.CancelPendingOperations(); await refreshing; }
    }

    private static async Task RefreshAllLateExactIdsAsync()
    {
        var fixture = await SellerExactFixtureAsync();
        fixture.ReceiptSource.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var carriers = new RefreshAllWaybillSource();
        using var waybills = new WaybillsViewModel(carriers);
        var (main, workspace) = fixture.Create(autoLinkEnabled: true, waybills: waybills);
        var refreshing = main.RefreshAllAsync();
        try
        {
            await fixture.ReceiptSource.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Rozetka.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await carriers.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(0, main.Receipts.Count);
            True(main.IsBusy && !workspace.ReceiptCoverageFresh,
                "The old receipt universe was certified while Checkbox was still blocked.");
            fixture.ReceiptSource.Gate.TrySetResult(true);
            await refreshing.WaitAsync(TimeSpan.FromSeconds(5));
            var receipt = main.Receipts.Single(r => r.Id == ReceiptOne);
            Equal(ReceiptLinkState.Exact, receipt.OrderMatch!.State); Equal("100", receipt.OrderMatch.Order!.Number);
            True(workspace.AllOrderRows.Single(r => r.Number == "100").HasConfirmedLink);
            Equal(1, fixture.Rozetka.FetchCalls); // Equivalent connections share one account fetch; no second refresh.
            Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls);
        }
        finally { fixture.ReceiptSource.Gate.TrySetResult(true); main.CancelPendingOperations(); await refreshing; }
    }

    private static async Task RefreshAllLateBasketAsync()
    {
        var fixture = AutomaticBasketFixture();
        fixture.ReceiptSource.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var carriers = new RefreshAllWaybillSource();
        using var waybills = new WaybillsViewModel(carriers);
        var (main, workspace) = fixture.Create(autoLinkEnabled: true, waybills: waybills);
        var refreshing = main.RefreshAllAsync();
        try
        {
            await Task.WhenAll(fixture.ReceiptSource.Entered.Task, fixture.Source.Entered.Task, carriers.Entered.Task)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Equal(0, fixture.Details.Calls);
            fixture.ReceiptSource.Gate.TrySetResult(true);
            await refreshing.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(1, fixture.Source.FetchCalls); Equal(1, fixture.Details.Calls);
            Equal(ReceiptLinkState.Suggested, main.Receipts.Single(r => r.Id == ReceiptOne).OrderMatch!.State);
            Equal(BasketOrder().Key, main.Receipts.Single(r => r.Id == ReceiptOne).OrderMatch!.Order!.Key);
            True(workspace.AllOrderRows.Single().HasSuggestedLink);
            Equal(0, fixture.Printer.Calls); Equal(0, fixture.Links.Saves);
        }
        finally { fixture.ReceiptSource.Gate.TrySetResult(true); main.CancelPendingOperations(); await refreshing; }
    }

    private static async Task RefreshAllLateDateResultsAsync()
    {
        var fixture = RefreshAllFixture(); BlockRefreshProviders(fixture);
        fixture.ReceiptSource.IgnoreCancellation = fixture.Source.IgnoreCancellation = fixture.Rozetka.IgnoreCancellation = true;
        var carriers = new RefreshAllWaybillSource
            { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously), IgnoreCancellation = true };
        using var waybills = new WaybillsViewModel(carriers);
        var (main, workspace) = fixture.Create(waybills: waybills);
        var refreshing = main.RefreshAllAsync();
        try
        {
            await AllRefreshProvidersEnteredAsync(fixture, carriers);
            main.DateTo = new(2026, 9, 24);
            True(fixture.ReceiptSource.LastToken.IsCancellationRequested && fixture.Source.LastToken.IsCancellationRequested &&
                fixture.Rozetka.LastToken.IsCancellationRequested && carriers.Calls[0].Token.IsCancellationRequested);
            ReleaseRefreshProviders(fixture, carriers);
            await refreshing.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(0, main.Receipts.Count); Equal(0, workspace.AllOrderRows.Count); Equal(0, waybills.Rows.Count);
            Equal(0, fixture.Cache.Snapshot.Orders.Count);
            True(!workspace.ReceiptCoverageFresh && !main.IsBusy && main.RefreshAllCommand.CanExecute(null));
            Equal(0, fixture.Printer.Calls); Equal(0, fixture.Links.Saves);
        }
        finally { ReleaseRefreshProviders(fixture, carriers); main.CancelPendingOperations(); await refreshing; }
    }

    private static async Task RefreshAllConfigurationChangedAsync()
    {
        var fixture = RefreshAllFixture(); BlockRefreshProviders(fixture);
        fixture.ReceiptSource.IgnoreCancellation = fixture.Source.IgnoreCancellation = fixture.Rozetka.IgnoreCancellation = true;
        var carriers = new RefreshAllWaybillSource
            { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously), IgnoreCancellation = true };
        using var waybills = new WaybillsViewModel(carriers);
        var (main, workspace) = fixture.Create(autoLinkEnabled: true, waybills: waybills);
        var refreshing = main.RefreshAllAsync();
        try
        {
            await AllRefreshProvidersEnteredAsync(fixture, carriers);
            foreach (var connection in fixture.Settings.Market.Connections) connection.Enabled = false;
            workspace.InvalidateConfiguration();
            ReleaseRefreshProviders(fixture, carriers);
            await refreshing.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(3, main.Receipts.Count); Equal(2, waybills.Rows.Count);
            Equal(0, workspace.AllOrderRows.Count);
            True(main.Receipts.All(r => r.OrderMatch?.Order is null),
                "Old connections were applied to receipts after the marketplace configuration changed.");
            Equal(1, fixture.Source.FetchCalls); Equal(1, fixture.Rozetka.FetchCalls);
            True(!main.IsBusy && !workspace.IsWorkspaceRefreshing);
            Equal(0, fixture.Printer.Calls); Equal(0, fixture.Links.Saves);
        }
        finally { ReleaseRefreshProviders(fixture, carriers); main.CancelPendingOperations(); await refreshing; }
    }

    private static async Task RefreshAllOldBackgroundFlightAsync()
    {
        var fixture = RefreshAllFixture(); BlockRefreshProviders(fixture);
        var oldEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var freshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var freshRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        fixture.ReceiptSource.Read = async (_, _, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                oldEntered.TrySetResult(); await oldRelease.Task; // An already-dispatched timer ignores cancellation.
                return [Receipt(ReceiptTwo, 2, 10000)];
            }
            freshEntered.TrySetResult(); await freshRelease.Task.WaitAsync(token);
            return [Receipt(ReceiptOne, 1, 10000)];
        };
        var carriers = new RefreshAllWaybillSource { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var waybills = new WaybillsViewModel(carriers);
        var (main, workspace) = fixture.Create(autoLinkEnabled: true, waybills: waybills);
        var old = main.BackgroundRefreshAsync(new(2026, 9, 23), new(2026, 9, 23));
        Task? refreshing = null;
        try
        {
            await oldEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            refreshing = main.RefreshAllAsync();
            await Task.WhenAll(fixture.Source.Entered.Task, fixture.Rozetka.Entered.Task, carriers.Entered.Task)
                .WaitAsync(TimeSpan.FromSeconds(5));
            True(main.IsBusy && !workspace.ReceiptCoverageFresh);
            oldRelease.TrySetResult();
            try { await old.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (BackgroundSourceSkippedException) { }
            await freshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(0, main.Receipts.Count);
            True(!workspace.ReceiptCoverageFresh,
                "The old timer certified global receipt coverage before its own fresh request completed.");
            freshRelease.TrySetResult(); ReleaseRefreshProviders(fixture, carriers);
            await refreshing.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(2, fixture.ReceiptSource.Calls); Equal(1, main.Receipts.Count);
            Equal(ReceiptOne, main.Receipts.Single().Id);
            Equal(ReceiptLinkState.Exact, main.Receipts.Single().OrderMatch!.State);
            Equal(MarketplaceKind.Prom, main.Receipts.Single().OrderMatch!.Order!.Key.Marketplace);
            True(workspace.AllOrderRows.Single(r => r.Key.Marketplace == MarketplaceKind.Rozetka).LinkedReceipts.Count == 0);
            Equal(1, fixture.Source.FetchCalls); Equal(1, fixture.Rozetka.FetchCalls);
            Equal(0, fixture.Printer.Calls); Equal(0, fixture.Links.Saves);
        }
        finally
        {
            oldRelease.TrySetResult(); freshRelease.TrySetResult(); ReleaseRefreshProviders(fixture, carriers);
            main.CancelPendingOperations();
            try { await old; } catch (BackgroundSourceSkippedException) { }
            if (refreshing is not null) await refreshing;
        }
    }

    private static async Task RefreshAllAccountChangedAsync()
    {
        var fixture = RefreshAllFixture(); BlockRefreshProviders(fixture);
        fixture.ReceiptSource.IgnoreCancellation = fixture.Source.IgnoreCancellation = fixture.Rozetka.IgnoreCancellation = true;
        var carriers = new RefreshAllWaybillSource
            { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously), IgnoreCancellation = true };
        using var waybills = new WaybillsViewModel(carriers);
        var (main, workspace) = fixture.Create(autoLinkEnabled: true, waybills: waybills);
        var refreshing = main.RefreshAllAsync();
        try
        {
            await AllRefreshProvidersEnteredAsync(fixture, carriers);
            fixture.Settings.App.Login = "changed-synthetic-cashier";
            workspace.InvalidateAccount();
            ReleaseRefreshProviders(fixture, carriers);
            await refreshing.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(0, main.Receipts.Count); Equal(0, workspace.AllOrderRows.Count);
            Equal(2, waybills.Rows.Count); // Carrier credentials and their independently valid result did not change.
            True(!workspace.ReceiptCoverageFresh && !main.IsBusy);
            Equal(1, fixture.Source.FetchCalls); Equal(1, fixture.Rozetka.FetchCalls);
            Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);

            fixture.ReceiptSource.Gate = null; fixture.Source.Gate = null; fixture.Rozetka.Gate = null;
            await main.RefreshAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Equal(3, main.Receipts.Count); Equal(2, workspace.AllOrderRows.Count);
            Equal(ReceiptLinkState.Exact, main.Receipts.Single(r => r.Id == ReceiptOne).OrderMatch!.State);
            Equal(2, fixture.ReceiptSource.Calls); Equal(2, fixture.Source.FetchCalls); Equal(2, fixture.Rozetka.FetchCalls);
            Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
        }
        finally { ReleaseRefreshProviders(fixture, carriers); main.CancelPendingOperations(); await refreshing; }
    }

    private sealed class ParallelLabelPrinter : ILabelPrinter
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public LabelPrintBatch? Batch;
        public IReadOnlyList<string> Printers() => [ShippingLabelsTests.Settings.PrinterName];
        public IReadOnlyList<LabelDriverFormat> Formats(string printer) => [new("synthetic", 101.5, 101.5)];
        public LabelPrinterGeometry Inspect(LabelPrintSettings settings) => ShippingLabelsTests.Geometry;
        public async Task<int?> SubmitAsync(LabelPrintBatch batch, string printerName, Func<Task> before,
            Func<int, Task> created, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); await before(); Calls++; Batch = batch; Entered.TrySetResult();
            await Release.Task.WaitAsync(token); await created(42); return 42;
        }
    }

    private static async Task ParallelMarketplaceLabelPublicationAsync()
    {
        var (fixture, main, workspace) = await OrderSelectionFixtureAsync();
        var account = PrintAccountContext.Create(fixture.Settings.App);
        var first = workspace.AllOrderRows.Single(r => r.Number == "ORDER-41");
        first.IsSelectedForLabels = true;
        var printer = new ParallelLabelPrinter(); var history = new ShippingLabelsTests.History();
        var dialogs = new LabelDialogs();
        using var labels = new ShippingLabelsViewModel(workspace, new ShippingLabelsTests.Stores(), history,
            new ShippingLabelsTests.Source(), new ShippingLabelsTests.Renderer(), printer, dialogs);
        workspace.Labels = labels;
        var printing = labels.RunAsync(null, true);
        Task? refreshing = null; Task? selecting = null;
        BackgroundCandidateHistory? candidateHistory = null;
        try
        {
            await printer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            True(labels.IsBusy && !printing.IsCompleted);
            var calls = fixture.Source.FetchCalls;
            fixture.Source.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Source.Orders = fixture.Source.Orders.Select(order => order.Key == first.Key
                ? order with { Number = "BG-UPDATED-41", Shipments = [new("nova_poshta", "bg-replacement")] } : order).Append(
                    Order([ReceiptTwo]) with { Key = new(MarketplaceKind.Prom, "prom-test", "43"), Number = "BG-NEW-43",
                        Shipments = [new("nova_poshta", "bg-new-43")] }).ToArray();
            refreshing = workspace.SyncBackgroundAsync(Task.CompletedTask, account);
            await RefreshWaitUntilAsync(() => fixture.Source.FetchCalls == calls + 1);
            fixture.Source.Gate.TrySetResult(true);
            await RefreshWaitUntilAsync(() => fixture.Cache.Snapshot.Orders.Any(order => order.Number == "BG-UPDATED-41"));
            Equal("ORDER-41", first.Number); Equal("first", first.Model.Shipments.Single().TrackingNumber);
            True(workspace.AllOrderRows.All(row => row.Number != "BG-NEW-43") && !refreshing.IsCompleted,
                "A staged background refresh published a new order or TTN during the confirmed label submission.");
            Equal("first", printer.Batch!.Pages.Single().Shipment.TrackingNumber);
            printer.Release.TrySetResult();
            await Task.WhenAll(printing, refreshing).WaitAsync(TimeSpan.FromSeconds(5));
            Equal("BG-UPDATED-41", first.Number); Equal("bg-replacement", first.Model.Shipments.Single().TrackingNumber);
            True(workspace.AllOrderRows.Any(row => row.Number == "BG-NEW-43"));
            Equal(1, printer.Calls); Equal(0, dialogs.Errors.Count);
            Equal("first", history.Attempts.Single().Pages.Single().TrackingNumber);
            True(!first.IsSelectedForLabels && !first.HasPrintedLabel,
                "The completed old-TTN job was transferred to the replacement document.");

            candidateHistory = new BackgroundCandidateHistory();
            using var candidateLabels = new ShippingLabelsViewModel(workspace, new ShippingLabelsTests.Stores(), candidateHistory,
                new ShippingLabelsTests.Source(), new ShippingLabelsTests.Renderer(), new LabelPrinter(), new LabelDialogs());
            workspace.Labels = candidateLabels;
            selecting = ExecuteAsync(candidateLabels.SelectPrintCandidatesCommand);
            True(candidateLabels.IsBusy && !selecting.IsCompleted);
            fixture.Source.Orders = fixture.Source.Orders.Select(order => order.Key == first.Key
                ? order with { Number = "HISTORY-UPDATED-41", Shipments = [new("nova_poshta", "history-replacement")] } : order).ToArray();
            fixture.Source.Gate = null;
            await workspace.SyncBackgroundAsync(Task.CompletedTask, account).WaitAsync(TimeSpan.FromSeconds(5));
            Equal("HISTORY-UPDATED-41", first.Number); Equal("history-replacement", first.Model.Shipments.Single().TrackingNumber);
            True(candidateLabels.IsBusy && !selecting.IsCompleted,
                "Candidate-history reading was mistaken for an active label print and delayed updated rows.");
            candidateHistory.Gate.TrySetResult(true); await selecting.WaitAsync(TimeSpan.FromSeconds(5));
            True(first.IsSelectedForLabels); Equal(1, printer.Calls); Equal(0, fixture.Printer.Calls);
        }
        finally
        {
            printer.Release.TrySetResult(); fixture.Source.Gate?.TrySetResult(true); candidateHistory?.Gate.TrySetResult(true);
            main.CancelPendingOperations(); await printing;
            if (refreshing is not null) await refreshing;
            if (selecting is not null) await selecting;
        }
    }

    private static async Task RefreshAllOldBackgroundHistoryAsync()
    {
        var fixture = RefreshAllFixture();
        fixture.History.LoadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var freshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var freshRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        fixture.ReceiptSource.Read = async (_, _, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1) return [Receipt(ReceiptTwo, 2, 10000)];
            freshEntered.TrySetResult(); await freshRelease.Task.WaitAsync(token);
            return [Receipt(ReceiptOne, 1, 10000)];
        };
        var carriers = new RefreshAllWaybillSource();
        using var waybills = new WaybillsViewModel(carriers);
        var (main, workspace) = fixture.Create(autoLinkEnabled: true, waybills: waybills);
        var old = main.BackgroundRefreshAsync(new(2026, 9, 23), new(2026, 9, 23));
        Task? refreshing = null;
        try
        {
            await fixture.History.LoadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(1, fixture.ReceiptSource.Calls); Equal(0, main.Receipts.Count);
            refreshing = main.RefreshAllAsync();
            await Task.WhenAll(fixture.Source.Entered.Task, fixture.Rozetka.Entered.Task, carriers.Entered.Task)
                .WaitAsync(TimeSpan.FromSeconds(5));
            fixture.History.LoadGate.TrySetResult(true);
            try { await old.WaitAsync(TimeSpan.FromSeconds(5)); } catch (BackgroundSourceSkippedException) { }
            await freshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(0, main.Receipts.Count);
            True(!workspace.ReceiptCoverageFresh,
                "A timer already past the API check published its old rows after returning from print-history loading.");
            freshRelease.TrySetResult(); await refreshing.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(2, fixture.ReceiptSource.Calls); Equal(ReceiptOne, main.Receipts.Single().Id);
            Equal(ReceiptLinkState.Exact, main.Receipts.Single().OrderMatch!.State);
            Equal(MarketplaceKind.Prom, main.Receipts.Single().OrderMatch!.Order!.Key.Marketplace);
            Equal(0, fixture.Printer.Calls); Equal(0, fixture.Links.Saves); Equal(0, fixture.History.Saves);
        }
        finally
        {
            fixture.History.LoadGate.TrySetResult(true); freshRelease.TrySetResult(); main.CancelPendingOperations();
            try { await old; } catch (BackgroundSourceSkippedException) { }
            if (refreshing is not null) await refreshing;
        }
    }

    private static async Task RefreshAllSettingsFailureAsync()
    {
        var fixture = RefreshAllFixture(); fixture.Settings.ThrowOnAppLoad = true;
        fixture.Source.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Rozetka.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var carriers = new RefreshAllWaybillSource { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var waybills = new WaybillsViewModel(carriers);
        var (main, workspace) = fixture.Create(waybills: waybills);
        var refreshing = main.RefreshAllAsync();
        try
        {
            await Task.WhenAll(fixture.Source.Entered.Task, fixture.Rozetka.Entered.Task, carriers.Entered.Task)
                .WaitAsync(TimeSpan.FromSeconds(5));
            True(main.IsBusy && !refreshing.IsCompleted);
            Equal(0, fixture.ReceiptSource.Calls); Equal(0, main.Receipts.Count);
            ReleaseRefreshProviders(fixture, carriers);
            await refreshing.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(1, fixture.Source.FetchCalls); Equal(1, fixture.Rozetka.FetchCalls); Equal(1, carriers.Calls.Count);
            Equal(2, workspace.AllOrderRows.Count); Equal(2, waybills.Rows.Count);
            True(!workspace.ReceiptCoverageFresh && !main.IsBusy && main.RefreshAllCommand.CanExecute(null));
            Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls);
        }
        finally { ReleaseRefreshProviders(fixture, carriers); main.CancelPendingOperations(); await refreshing; }
    }

    private static async Task ParallelMarketplaceDateDuringPrintAsync()
    {
        var (fixture, main, workspace) = await OrderSelectionFixtureAsync();
        var first = workspace.AllOrderRows.Single(r => r.Number == "ORDER-41"); first.IsSelectedForLabels = true;
        var printer = new ParallelLabelPrinter(); var history = new ShippingLabelsTests.History(); var dialogs = new LabelDialogs();
        using var labels = new ShippingLabelsViewModel(workspace, new ShippingLabelsTests.Stores(), history,
            new ShippingLabelsTests.Source(), new ShippingLabelsTests.Renderer(), printer, dialogs);
        workspace.Labels = labels;
        var printing = labels.RunAsync(null, true);
        Task? refreshing = null;
        try
        {
            await printer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Source.Orders = fixture.Source.Orders.Select(order => order.Key == first.Key
                ? order with { Number = "STALE-DATE-41", Shipments = [new("nova_poshta", "stale-date-replacement")] } : order).Append(
                    Order([ReceiptTwo]) with { Key = new(MarketplaceKind.Prom, "prom-test", "43"), Number = "STALE-DATE-43",
                        Shipments = [new("nova_poshta", "stale-date-new-43")] }).ToArray();
            refreshing = workspace.SyncBackgroundAsync(Task.CompletedTask, PrintAccountContext.Create(fixture.Settings.App));
            await RefreshWaitUntilAsync(() => fixture.Cache.Snapshot.Orders.Any(order => order.Number == "STALE-DATE-41"));
            True(!refreshing.IsCompleted); Equal("ORDER-41", first.Number);
            main.DateTo = new(2026, 9, 24); main.DateFrom = new(2026, 9, 24);
            Equal(0, workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Count());
            printer.Release.TrySetResult(); await printing.WaitAsync(TimeSpan.FromSeconds(5));
            try { await refreshing.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("An outdated display scope claimed a completed background refresh."); }
            catch (BackgroundSourceSkippedException) { }
            Equal("ORDER-41", first.Number); Equal("first", first.Model.Shipments.Single().TrackingNumber);
            True(workspace.AllOrderRows.All(row => !row.Number.StartsWith("STALE-DATE", StringComparison.Ordinal)),
                "A response already fetched before printing ended was published after the shared display dates changed.");
            Equal(0, workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Count());
            Equal(1, printer.Calls); Equal("first", printer.Batch!.Pages.Single().Shipment.TrackingNumber);
            True(first.HasPrintedLabel && !first.IsSelectedForLabels); Equal(0, dialogs.Errors.Count);
            Equal(0, fixture.Printer.Calls); Equal(0, fixture.Links.Saves);
        }
        finally
        {
            printer.Release.TrySetResult(); main.CancelPendingOperations(); await printing;
            if (refreshing is not null)
                try { await refreshing; } catch (BackgroundSourceSkippedException) { }
        }
    }
}
