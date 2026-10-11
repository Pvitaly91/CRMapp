using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> RefreshAllScenarios =>
    [
        ("STA unified refresh updates receipts both marketplaces and carriers from every tab", () => StaAsync(RefreshAllServicesAsync)),
        ("STA unified refresh starts Checkbox Prom Rozetka and carrier reads before any provider completes", () => StaAsync(RefreshAllConcurrentStartsAsync)),
        ("STA parallel refresh applies exact Rozetka UUID after late Checkbox completion without a second marketplace request", () => StaAsync(RefreshAllLateExactIdsAsync)),
        ("STA parallel refresh performs final complete basket matching when orders finish before Checkbox", () => StaAsync(RefreshAllLateBasketAsync)),
        ("STA parallel refresh discards all late uncancelable provider results after the shared dates change", () => StaAsync(RefreshAllLateDateResultsAsync)),
        ("STA parallel refresh cannot apply old marketplace configuration after its connections are disabled", () => StaAsync(RefreshAllConfigurationChangedAsync)),
        ("STA an older same-period background receipt flight cannot certify or replace the global refresh", () => StaAsync(RefreshAllOldBackgroundFlightAsync)),
        ("STA parallel refresh rejects late old-cashier matches and a subsequent new-cashier refresh succeeds", () => StaAsync(RefreshAllAccountChangedAsync)),
        ("STA staged marketplace publication waits for label submission but never for candidate-history selection", () => StaAsync(ParallelMarketplaceLabelPublicationAsync)),
        ("STA old background receipt history completion cannot publish into a global refresh", () => StaAsync(RefreshAllOldBackgroundHistoryAsync)),
        ("STA unavailable local Checkbox settings do not abort independent global marketplace and carrier reads", () => StaAsync(RefreshAllSettingsFailureAsync)),
        ("STA changed shared dates during a staged label-print wait cannot publish old marketplace rows or TTNs", () => StaAsync(ParallelMarketplaceDateDuringPrintAsync)),
        ("STA unified refresh recomputes enabled automatic links while the orders tab is inactive", () => StaAsync(RefreshAllInactiveMatchingAsync)),
        ("STA missing Checkbox credentials or failed receipt API do not skip independent stores and carriers", () => StaAsync(RefreshAllReceiptFailureAsync)),
        ("STA failed Checkbox refresh cannot create a new automatic pair from stale receipt coverage and retry restores matching", () => StaAsync(RefreshAllStaleReceiptCoverageAsync)),
        ("STA unified refresh continues carrier loading after marketplace API failure", () => StaAsync(RefreshAllMarketplaceFailureAsync)),
        ("STA unified refresh rejects incomplete or reversed shared dates before any service call", () => StaAsync(RefreshAllInvalidDatesAsync)),
        ("STA repeated unified refresh shares one flight and blocks printing throughout all stages", () => StaAsync(RefreshAllSingleFlightAsync)),
        ("STA changing shared dates cancels all already-started unified refresh providers", () => StaAsync(RefreshAllDateCancellationAsync)),
        ("STA explicitly requested carrier stage survives leaving its tab", () => StaAsync(RefreshAllWaybillTabExitAsync)),
        ("STA unified refresh cannot start while a frozen receipt job is being submitted", () => StaAsync(RefreshAllPrintGuardAsync))
    ];

    private static Fixture RefreshAllFixture()
    {
        var fixture = new Fixture();
        fixture.Settings.Market.Connections.Add(new()
            { Id = "rz-test", Marketplace = MarketplaceKind.Rozetka, Enabled = true, Name = "Rozetka synthetic shop" });
        fixture.Source.Orders = [Order(receiptIds: [ReceiptOne])];
        fixture.Rozetka.Orders = [Order(receiptIds: [ReceiptTwo]) with
            { Key = new(MarketplaceKind.Rozetka, "rz-test", "42"), Number = "RZ-42" }];
        return fixture;
    }

    private static async Task RefreshAllServicesAsync()
    {
        foreach (var tab in new[] { 0, 1, 2 })
        {
            var fixture = RefreshAllFixture();
            Equal(0, fixture.Settings.Market.HistoryDays);
            var carrierSource = new RefreshAllWaybillSource();
            using var waybills = new WaybillsViewModel(carrierSource);
            var (main, workspace) = fixture.Create(waybills: waybills);
            main.DateTo = new(2026, 9, 24);
            main.SelectedTabIndex = tab;
            try
            {
                await ExecuteAsync(main.RefreshAllCommand);
                Equal(1, fixture.ReceiptSource.Calls);
                Equal(1, fixture.Source.FetchCalls); Equal(1, fixture.Rozetka.FetchCalls);
                Equal(1, carrierSource.Calls.Count);
                Equal(new DateOnly(2026, 9, 23), fixture.ReceiptSource.LastFrom);
                Equal(new DateOnly(2026, 9, 24), fixture.ReceiptSource.LastTo);
                Equal((fixture.ReceiptSource.LastFrom, fixture.ReceiptSource.LastTo),
                    (carrierSource.Calls[0].From, carrierSource.Calls[0].To));
                var expectedRange = MarketplaceSyncService.BuildRange(fixture.ReceiptSource.LastFrom,
                    fixture.ReceiptSource.LastTo, 0);
                Equal(expectedRange, fixture.Source.LastRange); Equal(expectedRange, fixture.Rozetka.LastRange);
                Equal(3, main.Receipts.Count); Equal(2, workspace.AllOrderRows.Count); Equal(2, waybills.Rows.Count);
                Equal(tab, main.SelectedTabIndex);
                True(!workspace.AutoLinkEnabled, "A global refresh must preserve the user's automatic-linking preference.");
                True(!main.IsBusy && !workspace.IsWorkspaceRefreshing && !waybills.IsWorkspaceRefreshing);
                Equal(0, fixture.Printer.Calls); Equal(0, fixture.Links.Saves);
            }
            finally { main.CancelPendingOperations(); }
        }
    }

    private static async Task RefreshAllInactiveMatchingAsync()
    {
        var fixture = AutomaticBasketFixture();
        using var waybills = new WaybillsViewModel(new RefreshAllWaybillSource());
        var (main, workspace) = fixture.Create(autoLinkEnabled: true, waybills: waybills);
        try
        {
            await main.RefreshAllAsync();
            Equal(0, main.SelectedTabIndex);
            Equal(1, fixture.Source.FetchCalls); Equal(1, fixture.Details.Calls);
            var receipt = main.Receipts.Single(row => row.Id == ReceiptOne);
            Equal(ReceiptLinkState.Suggested, receipt.OrderMatch!.State);
            Equal(BasketOrder().Key, receipt.OrderMatch.Order!.Key);
            True(workspace.AllOrderRows.Single().HasSuggestedLink);
            Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls);
        }
        finally { main.CancelPendingOperations(); }
    }

    private static async Task RefreshAllReceiptFailureAsync()
    {
        foreach (var missingCredentials in new[] { true, false })
        {
            var fixture = RefreshAllFixture();
            fixture.Authentication.IsStored = !missingCredentials;
            fixture.ReceiptSource.Fail = !missingCredentials;
            var carrierSource = new RefreshAllWaybillSource();
            using var waybills = new WaybillsViewModel(carrierSource);
            var (main, workspace) = fixture.Create(waybills: waybills);
            main.DateFrom = new(2026, 9, 24); main.DateTo = new(2026, 9, 25);
            try
            {
                await main.RefreshAllAsync();
                Equal(missingCredentials ? 0 : 1, fixture.ReceiptSource.Calls);
                Equal(1, fixture.Source.FetchCalls); Equal(1, fixture.Rozetka.FetchCalls);
                Equal(1, carrierSource.Calls.Count);
                Equal(new DateOnly(2026, 9, 24), carrierSource.Calls[0].From);
                Equal(new DateOnly(2026, 9, 25), carrierSource.Calls[0].To);
                var range = MarketplaceSyncService.BuildRange(new(2026, 9, 24), new(2026, 9, 25), fixture.Settings.Market.HistoryDays);
                Equal(range, fixture.Source.LastRange);
                Equal(0, main.Receipts.Count); Equal(2, workspace.AllOrderRows.Count); Equal(2, waybills.Rows.Count);
                True(!main.IsBusy && main.RefreshAllCommand.CanExecute(null));
                Equal(0, fixture.Printer.Calls);
            }
            finally { main.CancelPendingOperations(); }
        }
    }

    private static async Task RefreshAllMarketplaceFailureAsync()
    {
        var fixture = RefreshAllFixture();
        fixture.Source.Fail = true;
        var carrierSource = new RefreshAllWaybillSource();
        using var waybills = new WaybillsViewModel(carrierSource);
        var (main, workspace) = fixture.Create(waybills: waybills);
        try
        {
            await main.RefreshAllAsync();
            Equal(1, fixture.ReceiptSource.Calls); Equal(1, fixture.Source.FetchCalls); Equal(1, fixture.Rozetka.FetchCalls);
            Equal(1, carrierSource.Calls.Count); Equal(2, waybills.Rows.Count);
            True(workspace.AllOrderRows.Any(row => row.Key.Marketplace == MarketplaceKind.Rozetka));
            True(!main.IsBusy && main.RefreshAllCommand.CanExecute(null));
            Equal(0, fixture.Printer.Calls);
        }
        finally { main.CancelPendingOperations(); }
    }

    private static async Task RefreshAllStaleReceiptCoverageAsync()
    {
        var fixture = AutomaticBasketFixture();
        var carrierSource = new RefreshAllWaybillSource();
        using var waybills = new WaybillsViewModel(carrierSource);
        var (main, workspace) = fixture.Create(autoLinkEnabled: true, waybills: waybills);
        try
        {
            // The last receipt list covered this same day, but it is not proof that
            // no competing receipt has appeared since an unavailable API refresh.
            await main.RefreshAsync();
            var oldReceipt = main.Receipts.Single(row => row.Id == ReceiptOne);
            fixture.ReceiptSource.Fail = true;
            await main.RefreshAllAsync();
            True(ReferenceEquals(oldReceipt, main.Receipts.Single(row => row.Id == ReceiptOne)));
            Equal(1, fixture.Source.FetchCalls); Equal(1, carrierSource.Calls.Count);
            True(oldReceipt.OrderMatch?.State != ReceiptLinkState.Suggested,
                "A newly fetched order acquired an automatic link from stale receipt coverage after Checkbox failed.");
            True(!workspace.AllOrderRows.Single().HasSuggestedLink);

            await workspace.SyncAsync();
            True(oldReceipt.OrderMatch?.State != ReceiptLinkState.Suggested,
                "A later marketplace check treated the failed Checkbox check as fresh coverage.");

            fixture.ReceiptSource.Fail = false;
            await main.RefreshAllAsync();
            Equal(3, fixture.Source.FetchCalls); Equal(2, carrierSource.Calls.Count);
            Equal(ReceiptLinkState.Suggested, oldReceipt.OrderMatch!.State);
            Equal(BasketOrder().Key, oldReceipt.OrderMatch.Order!.Key);
            True(workspace.AllOrderRows.Single().HasSuggestedLink);
            Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls);
        }
        finally { main.CancelPendingOperations(); }
    }

    private static async Task RefreshAllInvalidDatesAsync()
    {
        foreach (var invalid in new[] { "from", "to", "reverse" })
        {
            var fixture = RefreshAllFixture(); var carrierSource = new RefreshAllWaybillSource();
            using var waybills = new WaybillsViewModel(carrierSource);
            var (main, workspace) = fixture.Create(waybills: waybills);
            if (invalid == "from") main.DateFrom = null;
            if (invalid == "to") main.DateTo = null;
            if (invalid == "reverse") main.DateFrom = main.DateTo!.Value.AddDays(1);
            try
            {
                await main.RefreshAllAsync();
                Equal(0, fixture.ReceiptSource.Calls); Equal(0, fixture.Source.FetchCalls); Equal(0, fixture.Rozetka.FetchCalls);
                Equal(0, carrierSource.Calls.Count);
                True(fixture.Dialogs.Errors.Count > 0, "Invalid dates need visible feedback before any API request.");
                True(!main.IsBusy && !workspace.IsWorkspaceRefreshing && !waybills.IsWorkspaceRefreshing);
            }
            finally { main.CancelPendingOperations(); }
        }
    }

    private static async Task RefreshAllSingleFlightAsync()
    {
        var fixture = RefreshAllFixture();
        fixture.ReceiptSource.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var carrierSource = new RefreshAllWaybillSource { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var waybills = new WaybillsViewModel(carrierSource);
        var (main, workspace) = fixture.Create(waybills: waybills);
        var first = main.RefreshAllAsync();
        try
        {
            await fixture.ReceiptSource.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = main.RefreshAllAsync();
            True(main.IsBusy && !main.RefreshAllCommand.CanExecute(null) && !main.SettingsCommand.CanExecute(null),
                "Global refresh/settings commands remained enabled during receipt loading.");
            True(workspace.IsWorkspaceRefreshing && waybills.IsWorkspaceRefreshing,
                "One of the optional modules is not held busy during the shared refresh.");
            var backgroundSkipped = false;
            try
            {
                await main.BackgroundRefreshAsync(new(2026, 9, 25), new(2026, 9, 25))
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (BackgroundSourceSkippedException) { backgroundSkipped = true; }
            True(backgroundSkipped, "A timer fetching another period must not replace the global refresh's receipt freshness.");
            Equal(1, fixture.ReceiptSource.Calls);
            main.RefreshAllCommand.Execute(null); // A stale queued click cannot spawn another refresh.
            Equal(1, fixture.ReceiptSource.Calls);
            fixture.ReceiptSource.Gate.SetResult(true);
            await carrierSource.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await RefreshWaitUntilAsync(() => main.Receipts.Count > 0);
            main.Receipts[0].IsSelected = true;
            True(main.IsBusy && !main.PrintSelectedCommand.CanExecute(null) && !main.PreviewCommand.CanExecute(null),
                "Receipt printing/preview became available before the carrier stage completed.");
            True(!workspace.SyncCommand.CanExecute(null), "Independent marketplace refresh is enabled during the global flight.");
            True(!waybills.RefreshCommand.CanExecute(null), "Independent carrier refresh is enabled during the global flight.");
            carrierSource.Gate.SetResult(true);
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Equal(1, fixture.Source.FetchCalls); Equal(1, fixture.Rozetka.FetchCalls); Equal(1, carrierSource.Calls.Count);
            True(!main.IsBusy && main.PrintSelectedCommand.CanExecute(null), "Main busy state/print did not recover after all stages.");
            True(!workspace.IsWorkspaceRefreshing && !waybills.IsWorkspaceRefreshing, "Optional-module global guard did not clear.");
            Equal(0, fixture.Printer.Calls);
        }
        finally
        {
            fixture.ReceiptSource.Gate.TrySetResult(true); carrierSource.Gate.TrySetResult(true);
            main.CancelPendingOperations();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task RefreshAllDateCancellationAsync()
    {
        var fixture = RefreshAllFixture();
        fixture.ReceiptSource.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Source.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Rozetka.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var carrierSource = new RefreshAllWaybillSource { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var waybills = new WaybillsViewModel(carrierSource);
        var (main, workspace) = fixture.Create(waybills: waybills);
        var old = main.RefreshAllAsync();
        try
        {
            await AllRefreshProvidersEnteredAsync(fixture, carrierSource);
            main.DateTo = new(2026, 9, 24);
            await old.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(1, fixture.Source.FetchCalls); Equal(1, fixture.Rozetka.FetchCalls); Equal(1, carrierSource.Calls.Count);
            True(fixture.ReceiptSource.LastToken.IsCancellationRequested && fixture.Source.LastToken.IsCancellationRequested &&
                fixture.Rozetka.LastToken.IsCancellationRequested && carrierSource.Calls[0].Token.IsCancellationRequested,
                "Changing the date did not cancel every already-started provider.");
            Equal(0, main.Receipts.Count); Equal(0, workspace.AllOrderRows.Count); Equal(0, waybills.Rows.Count);
            True(!main.IsBusy && !workspace.IsWorkspaceRefreshing && !waybills.IsWorkspaceRefreshing);
            fixture.ReceiptSource.Gate = null; fixture.Source.Gate = null; fixture.Rozetka.Gate = null; carrierSource.Gate = null;
            await main.RefreshAllAsync();
            Equal(2, fixture.ReceiptSource.Calls); Equal(2, fixture.Source.FetchCalls); Equal(2, carrierSource.Calls.Count);
            Equal(new DateOnly(2026, 9, 24), carrierSource.Calls[1].To);
        }
        finally
        {
            fixture.ReceiptSource.Gate?.TrySetResult(true); fixture.Source.Gate?.TrySetResult(true);
            fixture.Rozetka.Gate?.TrySetResult(true); carrierSource.Gate?.TrySetResult(true);
            main.CancelPendingOperations(); await old.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task RefreshAllWaybillTabExitAsync()
    {
        var fixture = RefreshAllFixture();
        var carrierSource = new RefreshAllWaybillSource { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var waybills = new WaybillsViewModel(carrierSource);
        var (main, _) = fixture.Create(waybills: waybills);
        main.SelectedTabIndex = 2;
        var refreshing = main.RefreshAllAsync();
        try
        {
            await carrierSource.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            main.SelectedTabIndex = 0;
            True(!carrierSource.Calls[0].Token.IsCancellationRequested,
                "Leaving the carrier tab must not cancel the explicitly requested global refresh.");
            carrierSource.Gate.SetResult(true);
            await refreshing.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(2, waybills.Rows.Count); Equal(1, carrierSource.Calls.Count); Equal(0, main.SelectedTabIndex);
            True(!main.IsBusy);
        }
        finally { carrierSource.Gate.TrySetResult(true); main.CancelPendingOperations(); await refreshing; }
    }

    private static async Task RefreshAllPrintGuardAsync()
    {
        var fixture = RefreshAllFixture(); var carrierSource = new RefreshAllWaybillSource();
        using var waybills = new WaybillsViewModel(carrierSource);
        var (main, _) = fixture.Create(waybills: waybills);
        await main.RefreshAsync();
        main.Receipts[0].IsSelected = true;
        fixture.Printer.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var printing = ExecuteAsync(main.PrintSelectedCommand, () => !main.IsPrinting);
        try
        {
            await fixture.Printer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            True(main.IsPrinting && !main.RefreshAllCommand.CanExecute(null));
            await main.RefreshAllAsync();
            Equal(1, fixture.ReceiptSource.Calls); Equal(0, fixture.Source.FetchCalls); Equal(0, carrierSource.Calls.Count);
        }
        finally { fixture.Printer.Gate.TrySetResult(true); await printing; main.CancelPendingOperations(); }
    }

    private sealed class RefreshAllWaybillSource : IWaybillService
    {
        public List<(DateOnly From, DateOnly To, CancellationToken Token)> Calls { get; } = [];
        public TaskCompletionSource<bool>? Gate;
        public bool IgnoreCancellation;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<WaybillFetchResult> FetchAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
        {
            Calls.Add((from, to, cancellationToken)); Entered.TrySetResult();
            if (Gate is not null) await Gate.Task.WaitAsync(IgnoreCancellation ? CancellationToken.None : cancellationToken);
            return new([
                new(ShippingCarrier.NovaPoshta, "np-test", "Synthetic NP", "np-doc", "NP-REFRESH", from.ToDateTime(new(12, 0)),
                    "Створено", "Synthetic recipient", "Київ", 100, 1),
                new(ShippingCarrier.RozetkaDelivery, "rd-test", "Synthetic RD", "rd-doc", "RD-REFRESH", to.ToDateTime(new(12, 0)),
                    "Створено", "Synthetic recipient", "Львів", 100, 1)
            ], [new(ShippingCarrier.NovaPoshta, "Synthetic NP", true, 1, ""),
                new(ShippingCarrier.RozetkaDelivery, "Synthetic RD", true, 1, "")]);
        }
    }
}
