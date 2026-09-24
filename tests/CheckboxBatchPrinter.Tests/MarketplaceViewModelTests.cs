using System.Text.Json;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Reflection;
using System.Diagnostics;
using System.Windows.Media.Imaging;
using System.IO;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Services;
using CheckboxBatchPrinter.ViewModels;
using CheckboxBatchPrinter.Views;

namespace CheckboxBatchPrinter.Tests;

internal static class MarketplaceViewModelTests
{
    private const string ReceiptOne = "00000000-0000-0000-0000-000000000001";
    private const string ReceiptTwo = "00000000-0000-0000-0000-000000000002";
    private const string ReceiptThree = "00000000-0000-0000-0000-000000000003";
    private static volatile string _xamlCheckpoint = "not started";

    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("STA selected Prom hidden by Rozetka filter never enters printer backend", () => StaAsync(FilteredPrintAsync)),
        ("STA clear all selection includes hidden rows and hidden-only selection disables print", () => StaAsync(HiddenSelectionAsync)),
        ("STA TTN and receipt type filters exclude hidden selections from print", () => StaAsync(SearchPrintAsync)),
        ("STA table receipt number, date and order sorting determine backend order", () => StaAsync(SortedPrintAsync)),
        ("STA marketplace sync inside confirmation cannot change immutable batch", () => StaAsync(ConfirmationSyncAsync)),
        ("STA unlinked receipts print and confirmation count matches backend", () => StaAsync(UnlinkedPrintAsync)),
        ("STA failed PNG preparation never submits a smaller confirmed batch", () => StaAsync(ImageFailureAsync)),
        ("STA cancelled confirmation never submits or changes print history", () => StaAsync(CancelPrintAsync)),
        ("STA marketplace sync preserves selection, confirmed print batch and existing print history", () => StaAsync(ConcurrentPrintAsync)),
        ("STA manual link survives real MainViewModel F5 and viewmodel restart", () => StaAsync(ManualLinkAsync)),
        ("STA unavailable marketplace does not block printing loaded Checkbox receipts", () => StaAsync(FailedMarketplaceAsync)),
        ("STA settings Cancel still invalidates marketplace data after cashier credentials changed", () => StaAsync(SettingsCancelAsync)),
        ("STA stale receipt detail continuation cannot enter another account cache", () => StaAsync(StaleDetailsAsync)),
        ("STA corrupt marketplace settings do not block or overwrite Checkbox and printer settings", () => StaAsync(CorruptSettingsAsync)),
        ("STA Rozetka credential drafts merge and saved account identity cannot change", () => StaAsync(CredentialDraftAsync)),
        ("Checkbox details preserve units and do not infer marketplace IDs from opaque context", ReceiptDetailsAsync),
        ("STA default All Receipts tab works without integrations including preview and print", () => StaAsync(NoIntegrationsAsync)),
        ("STA Checkbox F5 never opens marketplace settings, secrets, cache or APIs", () => StaAsync(CheckboxRefreshIsolationAsync)),
        ("STA All Receipts includes linked Prom, Rozetka and unlinked receipts", () => StaAsync(AllCategoriesAsync)),
        ("STA tab searches, sort, current row and check marks are independent", () => StaAsync(IndependentTabsAsync)),
        ("STA slow marketplace API cannot block Checkbox F5, preview or printing", () => StaAsync(SlowMarketplaceAsync)),
        ("STA damaged marketplace settings cache and links cannot block base workflow", () => StaAsync(CorruptModuleAsync)),
        ("STA disabling integrations preserves receipts history decisions cache and secrets", () => StaAsync(DisabledIntegrationsAsync)),
        ("STA print and preview commands use only active tab selection", () => StaAsync(ActiveTabCommandsAsync)),
        ("STA changing tabs during confirmation and backend preserves one frozen job", () => StaAsync(SwitchDuringPrintAsync)),
        ("STA real Rozetka adapter fiscal URL auto-fills row without manual link and does not gate base print", () => StaAsync(FiscalAutomaticUiAsync)),
        ("STA all marketplace orders load without Checkbox credentials or receipt rows", () => StaAsync(OrdersWithoutCheckboxAsync)),
        ("STA no-Checkbox order refresh uses changed calendar dates and rejects incomplete ranges", () => StaAsync(OrderCalendarWithoutCheckboxAsync)),
        ("STA marketplace order filters search and full order keys are independent of receipt tabs", () => StaAsync(IndependentOrderPanelAsync)),
        ("STA selected order and receipt require explicit confirmation and survive F5 restart", () => StaAsync(SelectedOrderManualAsync)),
        ("STA unique complete basket suggests a link without writing manual decisions", () => StaAsync(BasketSuggestedUiAsync)),
        ("STA duplicate orders or competing receipts cannot create basket suggestions", () => StaAsync(BasketAmbiguousUiAsync)),
        ("STA cached and partial marketplace orders remain visible without claiming checked links", () => StaAsync(PartialOrderPanelAsync)),
        ("STA old account basket detail completion cannot populate the new account", () => StaAsync(StaleBasketDetailsAsync)),
        ("STA basket continuation reaches unattempted receipts after one hundred detail failures", () => StaAsync(BasketContinuationAfterFailuresAsync)),
        ("STA automatic orders activation suggests without buttons and restores after Checkbox F5", () => StaAsync(AutomaticBasketActivationAsync)),
        ("STA enabled automatic linking never contacts optional stores from All Receipts refresh", () => StaAsync(AutomaticBasketAllTabIsolationAsync)),
        ("STA repeated automatic tab preparations share one in-flight refresh", () => StaAsync(AutomaticBasketDeduplicationAsync)),
        ("STA tab exit and automatic toggle cancel background linking without retries while inactive", () => StaAsync(AutomaticBasketCancellationAsync)),
        ("STA automatic linking with absent or disabled integrations never reads secrets or APIs", () => StaAsync(AutomaticBasketNoIntegrationsAsync)),
        ("STA automatic old-account item completion cannot seed a new account", () => StaAsync(AutomaticBasketAccountChangeAsync)),
        ("STA quick automatic off-on waits for cancelled details and starts one fresh check", () => StaAsync(AutomaticBasketRapidToggleAsync)),
        ("STA explicitly started marketplace sync survives tab exit and automatic cancellation", () => StaAsync(AutomaticBasketManualSyncAsync)),
        ("STA invalid no-Checkbox dates cancel automatic refresh and restored dates allow one new attempt", () => StaAsync(AutomaticBasketInvalidDatesAsync)),
        ("STA Main Settings and OrderLink XAML initialize without showing windows", XamlSmokeProcessAsync)
    ];

    public static void RunIsolatedXamlSmoke()
    {
        True(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA);
        // WPF owns the child process main thread. Do not tear down a secondary COM
        // apartment and Join it while Application and theme resources still exist.
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        Exception? failure = null;
        var finished = false;
        dispatcher.UnhandledException += (_, args) =>
        {
            failure = failure is null ? args.Exception : new AggregateException(failure, args.Exception);
            args.Handled = true;
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        };
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await XamlSmokeAsync();
                Console.WriteLine("XAML assertions and binding cleanup completed.");
                await DrainDispatcherAsync();
                Console.WriteLine("XAML final dispatcher drain completed.");
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                finished = true;
                dispatcher.BeginInvokeShutdown(DispatcherPriority.ContextIdle);
            }
        }));
        Dispatcher.Run();
        Console.WriteLine("XAML main dispatcher shut down.");
        if (failure is not null) throw new InvalidOperationException("XAML checkpoint: " + _xamlCheckpoint, failure);
        True(finished, "XAML dispatcher exited before assertions completed. " + _xamlCheckpoint);
    }

    private static async Task XamlSmokeProcessAsync()
    {
        // WPF Application/theme caches are process-wide. Give compiled window tests
        // their own main STA thread and generic test Application (never production App).
        // A fresh child is isolation, not a retry: all assertions run exactly once.
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate test executable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(MarketplaceViewModelTests).Assembly.Location);
        start.ArgumentList.Add("--xaml-smoke");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start isolated XAML test.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true); // Only our own synthetic child.
            await process.WaitForExitAsync();
            throw new InvalidOperationException("Isolated XAML test timed out. " + await standardOutput + await standardError);
        }
        var output = await standardOutput;
        var error = await standardError;
        True(process.ExitCode == 0, $"Isolated XAML test failed ({process.ExitCode}): {output}{error}");
        True(output.Contains("PASS isolated compiled XAML tabs, bindings and one-click marks", StringComparison.Ordinal),
            "Isolated XAML process exited without its completed-assertions marker.");
    }

    private static async Task FilteredPrintAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [Order(receiptIds: [ReceiptOne])];
        fixture.Settings.Market.Connections.Add(new() { Id = "rz-test", Marketplace = MarketplaceKind.Rozetka, Enabled = true, Name = "Test Rozetka" });
        fixture.Rozetka.Orders = [new() { Key = new(MarketplaceKind.Rozetka, "rz-test", "42"), Number = "RZ-42", ReceiptIds = [ReceiptTwo] }];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        workspace.Filter = "Prom";
        main.SelectAllCommand.Execute(null);
        workspace.Filter = "Rozetka";
        main.SelectAllCommand.Execute(null);
        await ExecuteAsync(main.PrintSelectedCommand);
        True(fixture.Printer.BatchIds.SequenceEqual([ReceiptTwo]),
            $"Expected only visible Rozetka {ReceiptTwo}; backend received: {string.Join(", ", fixture.Printer.BatchIds)}");
        AssertConfirmation(fixture, [ReceiptTwo]);
        Equal(1, fixture.Dialogs.Confirmation!.HiddenSelectedCount);
        Equal("Rozetka", fixture.Dialogs.Confirmation.Items[0].Marketplace);
        Equal("RZ-42", fixture.Dialogs.Confirmation.Items[0].OrderNumber);
        Equal(1, main.VisibleSelectedCount);
        Equal(1, main.HiddenSelectedCount);
    }

    private static async Task HiddenSelectionAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [Order(receiptIds: [ReceiptOne])];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        workspace.Filter = "Prom";
        main.SelectAllCommand.Execute(null);
        Equal(1, main.SelectedCount);
        var changes = new List<string?>();
        main.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        var commandChanges = 0;
        main.PrintSelectedCommand.CanExecuteChanged += (_, _) => commandChanges++;
        workspace.Filter = "Rozetka";
        Equal(0, main.VisibleSelectedCount); Equal(1, main.HiddenSelectedCount);
        True(!main.PrintSelectedCommand.CanExecute(null));
        True(changes.Contains(nameof(main.VisibleSelectedCount)) && changes.Contains(nameof(main.HiddenSelectedCount)));
        True(commandChanges > 0);
        True(main.ClearSelectionCommand.CanExecute(null));
        main.ClearSelectionCommand.Execute(null);
        Equal(0, main.SelectedCount);
        workspace.Filter = "Prom";
        True(main.Receipts.All(r => !r.IsSelectedForOrders));
        Equal(0, fixture.Printer.Calls);
    }

    private static async Task SearchPrintAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [Order(receiptIds: [ReceiptOne])];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        main.SelectAllCommand.Execute(null);
        main.SearchText = "TEST-TTN";
        Equal(1, main.VisibleSelectedCount); Equal(2, main.HiddenSelectedCount);
        await ExecuteAsync(main.PrintSelectedCommand);
        AssertConfirmation(fixture, [ReceiptOne]);
        Equal(2, fixture.Dialogs.Confirmation!.HiddenSelectedCount);
        main.SelectedType = main.ReceiptTypes.Single(t => t.Value == ReceiptTypes.Return);
        Equal(0, main.VisibleSelectedCount);
        True(!main.PrintSelectedCommand.CanExecute(null));
    }

    private static async Task SortedPrintAsync()
    {
        foreach (var sort in new[]
        {
            (Property: "Serial", Direction: ListSortDirection.Ascending, Ids: new[] { ReceiptOne, ReceiptTwo, ReceiptThree }),
            (Property: "LocalDate", Direction: ListSortDirection.Descending, Ids: new[] { ReceiptOne, ReceiptThree, ReceiptTwo }),
            (Property: "OrderNumber", Direction: ListSortDirection.Descending, Ids: new[] { ReceiptOne, ReceiptTwo, ReceiptThree })
        })
        {
            var fixture = new Fixture();
            fixture.ReceiptSource.Rows = [Receipt(ReceiptTwo, 2, 10000, 21), Receipt(ReceiptOne, 1, 10000, 23), Receipt(ReceiptThree, 3, 99900, 22)];
            fixture.Source.Orders =
            [
                new() { Key = new(MarketplaceKind.Prom, "prom-test", "Z"), Number = "Z-2", ReceiptIds = [ReceiptOne] },
                new() { Key = new(MarketplaceKind.Prom, "prom-test", "A"), Number = "A-1", ReceiptIds = [ReceiptTwo] }
            ];
            var (main, workspace) = fixture.Create();
            await main.RefreshAsync();
            await OpenOrdersAsync(main, workspace);
            using (main.ReceiptsView.DeferRefresh())
            {
                main.ReceiptsView.SortDescriptions.Clear();
                main.ReceiptsView.SortDescriptions.Add(new SortDescription(sort.Property, sort.Direction));
            }
            main.SelectAllCommand.Execute(null);
            True(main.ReceiptsView.Cast<ReceiptRowViewModel>().Select(r => r.Id).SequenceEqual(sort.Ids));
            await ExecuteAsync(main.PrintSelectedCommand);
            AssertConfirmation(fixture, sort.Ids);
        }
    }

    private static async Task ConfirmationSyncAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [Order(receiptIds: [ReceiptOne, ReceiptTwo])];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        workspace.Filter = "Prom";
        main.SelectAllCommand.Execute(null);
        fixture.Dialogs.DuringConfirmation = () =>
        {
            True(main.IsBusy && !main.RefreshCommand.CanExecute(null));
            fixture.Source.Orders = [new() { Key = Order().Key, Number = "CHANGED", ReceiptIds = [ReceiptThree] }];
            // A real modal WPF dialog pumps the dispatcher while async sync completes.
            var frame = new DispatcherFrame();
            Exception? failure = null;
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
            {
                try { await workspace.SyncAsync(); }
                catch (Exception ex) { failure = ex; }
                finally { frame.Continue = false; }
            }));
            Dispatcher.PushFrame(frame);
            if (failure is not null) throw failure;
            True(main.ReceiptsView.Cast<ReceiptRowViewModel>().Select(r => r.Id).SequenceEqual([ReceiptThree]));
            Equal(0, main.VisibleSelectedCount);
            True(fixture.Dialogs.Confirmation!.Items.All(i => i.OrderNumber == "ORDER-41"));
        };
        await ExecuteAsync(main.PrintSelectedCommand, () => !main.IsBusy);
        AssertConfirmation(fixture, [ReceiptTwo, ReceiptOne]);
        True(fixture.Dialogs.Confirmation!.Items.All(i => i.OrderNumber == "ORDER-41"));
        Equal(0, fixture.Dialogs.Errors.Count);
        True(!main.PrintSelectedCommand.CanExecute(null));
    }

    private static async Task UnlinkedPrintAsync()
    {
        var fixture = new Fixture();
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        main.SelectAllCommand.Execute(null);
        await ExecuteAsync(main.PrintSelectedCommand);
        AssertConfirmation(fixture, [ReceiptThree, ReceiptTwo, ReceiptOne]);
        True(fixture.Dialogs.Confirmation!.Items.All(i => i.Marketplace == "" && i.OrderNumber == ""));
        Equal(1, fixture.Printer.Calls); // Existing continuous batch backend, not separate jobs.
    }

    private static async Task ImageFailureAsync()
    {
        var fixture = new Fixture();
        fixture.Images.FailId = ReceiptOne;
        var (main, _) = fixture.Create();
        await main.RefreshAsync();
        main.SelectAllCommand.Execute(null);
        await ExecuteAsync(main.PrintSelectedCommand);
        Equal(3, fixture.Dialogs.Confirmation!.Count);
        Equal(0, fixture.Printer.Calls);
        Equal(0, fixture.History.Saves);
        Equal(1, fixture.Dialogs.Errors.Count);
        True(main.Receipts.All(r => r.PrintStatus is not (PrintItemStatus.Downloading or PrintItemStatus.Printing)));
    }

    private static async Task CancelPrintAsync()
    {
        var fixture = new Fixture();
        fixture.Dialogs.AcceptPrint = false;
        var (main, _) = fixture.Create();
        await main.RefreshAsync();
        main.SelectAllCommand.Execute(null);
        await ExecuteAsync(main.PrintSelectedCommand);
        Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
        Equal(PrintItemStatus.Done, main.Receipts.Single(r => r.Id == ReceiptThree).PrintStatus);
        True(!main.IsBusy);
    }

    private static void AssertConfirmation(Fixture fixture, string[] expectedIds)
    {
        var confirmation = fixture.Dialogs.Confirmation!;
        True(confirmation is not null);
        Equal("mock-printer", confirmation!.PrinterName);
        Equal(expectedIds.Length, confirmation.Count);
        Equal(confirmation.Count, fixture.Printer.BatchIds.Length);
        True(confirmation.Items.Select(i => i.ReceiptId).SequenceEqual(expectedIds));
        True(confirmation.Items.Select(i => i.Position).SequenceEqual(Enumerable.Range(1, expectedIds.Length)));
        True(fixture.Printer.BatchIds.SequenceEqual(expectedIds));
    }

    private static async Task ConcurrentPrintAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [Order(receiptIds: [ReceiptTwo])];
        fixture.Source.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Printer.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        main.SelectedTabIndex = 1;
        await main.PrepareOrdersAsync();
        var sync = workspace.SyncAsync();
        await fixture.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        True(workspace.IsBusy && !main.IsBusy, "Marketplace loading must not own Main.IsBusy.");
        Equal(3, main.Receipts.Count);
        main.Receipts[0].IsSelectedForOrders = true;
        main.Receipts[1].IsSelectedForOrders = true;
        Equal(2, main.SelectedCount);
        Equal(PrintItemStatus.Done, main.Receipts[2].PrintStatus);
        True(main.PrintSelectedCommand.CanExecute(null));
        var duplicateSync = workspace.SyncAsync();
        Equal(1, fixture.Source.FetchCalls); // A second user action cannot duplicate the active sync.

        var rows = main.Receipts.ToArray();
        var expectedIds = new[] { ReceiptTwo, ReceiptOne };
        var print = ExecuteAsync(main.PrintSelectedCommand);
        await fixture.Printer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        True(fixture.Printer.BatchIds.SequenceEqual(expectedIds));
        Equal(0, fixture.History.Saves);
        fixture.Source.Gate.SetResult(true);
        await sync;
        await duplicateSync;
        True(rows.SequenceEqual(main.Receipts));
        Equal(2, main.SelectedCount);
        Equal("ORDER-41", main.Receipts[0].OrderNumber);
        Equal(ReceiptLinkState.Exact, main.Receipts[0].OrderMatch!.State);
        Equal(PrintItemStatus.Printing, main.Receipts[0].PrintStatus);
        Equal(PrintItemStatus.Done, main.Receipts[2].PrintStatus);
        True(fixture.Printer.BatchIds.SequenceEqual(expectedIds), "Order enrichment changed the confirmed print batch.");
        Equal(0, fixture.History.Saves);
        fixture.Printer.Gate.SetResult(true);
        await print;
        True(main.Receipts.All(row => row.PrintStatus == PrintItemStatus.Done));
        Equal(1, fixture.History.Saves);
        Equal(3, fixture.History.Records.Count);
        Equal(0, fixture.Dialogs.Errors.Count);

        fixture.Source.Gate = null;
        var fetchesBeforeRefresh = fixture.Source.FetchCalls;
        await main.RefreshAsync();
        await main.PrepareOrdersAsync();
        Equal(fetchesBeforeRefresh, fixture.Source.FetchCalls);
        await workspace.SyncAsync(); // Fresh range coverage is explicit; cached automatic evidence is incomplete.
        Equal(2, main.SelectedCount);
        True(main.Receipts.All(row => row.PrintStatus == PrintItemStatus.Done));
        Equal("ORDER-41", main.Receipts[0].OrderNumber);
        True(fixture.Printer.BatchIds.SequenceEqual(expectedIds));
    }

    private static async Task ManualLinkAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [Order()];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        var row = main.Receipts[0];
        row.IsSelectedForOrders = true;
        Equal(ReceiptLinkState.Candidates, row.OrderMatch!.State);
        Equal("", row.OrderNumber); // A candidate is not a confirmed table value.
        fixture.Dialogs.Choice = new(Order().Key, false);
        workspace.SelectedReceipt = row;
        await ExecuteAsync(workspace.LinkCommand);
        Equal(1, fixture.Links.Saves);
        Equal(ReceiptLinkState.Manual, row.OrderMatch!.State);
        Equal("ORDER-41", row.OrderNumber);
        main.SearchText = "TEST-TTN";
        Equal(1, main.ReceiptsView.Cast<ReceiptRowViewModel>().Count());
        workspace.CopyTrackingCommand.Execute(null);
        Equal("TEST-TTN", fixture.Dialogs.Copied);

        main.SearchText = "";
        await main.RefreshAsync();
        await main.PrepareOrdersAsync();
        Equal(ReceiptLinkState.Manual, main.Receipts[0].OrderMatch!.State);
        Equal("ORDER-41", main.Receipts[0].OrderNumber);
        True(main.Receipts[0].IsSelectedForOrders);
        Equal(PrintItemStatus.Done, main.Receipts[2].PrintStatus);
        var (restarted, _) = fixture.Create(); // Fresh VMs + deserialized stored decisions/cache.
        await restarted.RefreshAsync();
        restarted.SelectedTabIndex = 1;
        await restarted.PrepareOrdersAsync();
        Equal(ReceiptLinkState.Manual, restarted.Receipts[0].OrderMatch!.State);
        Equal("ORDER-41", restarted.Receipts[0].OrderNumber);
        Equal(0, restarted.SelectedCount);
        Equal(PrintItemStatus.Done, restarted.Receipts[2].PrintStatus);
        Equal(0, fixture.Printer.Calls);
        Equal(0, fixture.History.Saves);
        Equal(0, fixture.Dialogs.Errors.Count);
    }

    private static async Task FailedMarketplaceAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Fail = true;
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        Equal(3, main.Receipts.Count);
        True(!main.IsBusy && !workspace.IsBusy);
        True(main.Receipts.All(row => row.OrderMatch?.State == ReceiptLinkState.Incomplete));
        main.SelectedTabIndex = 0;
        Equal(3, main.ReceiptsView.Cast<ReceiptRowViewModel>().Count());
        main.Receipts[0].IsSelected = true;
        True(main.PrintSelectedCommand.CanExecute(null));
        await ExecuteAsync(main.PrintSelectedCommand);
        Equal(1, fixture.Printer.Calls);
        Equal(PrintItemStatus.Done, main.Receipts[0].PrintStatus);
        Equal(PrintItemStatus.Done, main.Receipts[2].PrintStatus);
        Equal(0, fixture.Dialogs.Errors.Count);
        True(fixture.Cache.Snapshot.States.Single().LastSuccessUtc is null);
    }

    private static Task ReceiptDetailsAsync()
    {
        const string json = """
        {"id":"00000000-0000-0000-0000-000000000001","order_id":"11111111-1111-1111-1111-111111111111",
         "context":{"marketplace":"prom","order_id":"41","receipt_id":"00000000-0000-0000-0000-000000000001"},
         "related_receipt_id":null,"goods":[
           {"good":{"name":"Товар","code":"SKU","price":12345},"quantity":1500,"sum":18518},
           {"good":{"name":"Невідомо","code":"EMPTY","price":null},"quantity":null,"sum":null}
         ]}
        """;
        var detail = ReceiptDetailsService.Parse(json, ReceiptOne);
        Equal(1.5m, detail.Items[0].Quantity); Equal(123.45m, detail.Items[0].UnitPrice); Equal(185.18m, detail.Items[0].Total);
        True(detail.Items[1].Quantity is null && detail.Items[1].UnitPrice is null && detail.Items[1].Total is null);
        Equal("11111111-1111-1111-1111-111111111111", detail.CheckboxOrderId);
        True(detail.HasUnmappedContext);
        var match = new ReceiptOrderMatchingService().Match(Receipt(ReceiptOne, 1, 10000), "account", [Order()], [], true, detail);
        Equal(ReceiptLinkState.Candidates, match.State);
        True(match.Order is null, "Opaque context created an inferred marketplace match.");
        try { ReceiptDetailsService.Parse(json, ReceiptTwo); throw new InvalidOperationException("Wrong receipt detail accepted."); }
        catch (JsonException) { }
        return Task.CompletedTask;
    }

    private static async Task SettingsCancelAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [Order(receiptIds: [ReceiptTwo])];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        main.Receipts[0].IsSelectedForOrders = true;
        Equal("Тестовий покупець", main.Receipts[0].OrderBuyer);
        // Test connection inside Settings has already persisted a new cashier, then user presses Cancel.
        fixture.Dialogs.AfterSettings = () => fixture.Settings.App.Login = "other-cashier";
        await ExecuteAsync(main.SettingsCommand);
        True(main.Receipts.All(row => row.OrderMatch is null));
        True(workspace.SelectedReceipt is null);
        Equal("", main.Receipts[0].OrderBuyer);
        Equal(0, fixture.Printer.Calls);
        True(main.Receipts[0].IsSelectedForOrders);
        await ExecuteAsync(main.PrintSelectedCommand);
        Equal(0, fixture.Printer.Calls); // Existing receipts cannot be printed under a different account.
        Equal(1, fixture.Dialogs.Errors.Count);
    }

    private static async Task StaleDetailsAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [Order()];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        fixture.Details.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.SelectedReceipt = main.Receipts[0];
        workspace.LinkCommand.Execute(null);
        await fixture.Details.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        workspace.InvalidateAccount();
        await workspace.AttachAsync(main.Receipts.ToArray(), "new-account-context", new(2026, 9, 23), new(2026, 9, 23));
        var oldFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finished(object? sender, EventArgs args) => oldFinished.TrySetResult();
        workspace.LinkCommand.CanExecuteChanged += Finished;
        fixture.Details.Gate.SetResult(true);
        await oldFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        workspace.LinkCommand.CanExecuteChanged -= Finished;
        Equal(0, fixture.Dialogs.ChoiceCalls); Equal(0, fixture.Links.Saves);
        fixture.Details.Gate = null;
        fixture.Dialogs.Choice = new(Order().Key, false);
        workspace.SelectedReceipt = main.Receipts[0]; // Current row is tab-local; attachment no longer silently selects one.
        await ExecuteAsync(workspace.LinkCommand);
        Equal(2, fixture.Details.Calls); // Fetch again: old continuation must not populate this context.
        Equal(1, fixture.Links.Saves);
    }

    private static async Task CorruptSettingsAsync()
    {
        var fixture = new Fixture();
        fixture.Settings.ThrowOnMarketplaceLoad = true;
        var sync = new MarketplaceSyncService([fixture.Source], fixture.Settings, fixture.Cache);
        var marketplace = new MarketplaceSettingsViewModel(fixture.Settings, fixture.Settings, sync);
        var settings = new SettingsViewModel(fixture.Settings, new Authentication(), new Images(), fixture.Printer, new Logger(), marketplace);
        await settings.LoadAsync();
        Equal(0, fixture.Settings.MarketplaceLoads);
        await settings.EnsureMarketplaceLoadedAsync();
        True(!marketplace.CanEdit && !marketplace.AddPromCommand.CanExecute(null));
        Equal("mock-printer", settings.SelectedPrinter);
        Equal("test-cashier", settings.Login);
        settings.SeparatePrintJobPerReceipt = false;
        await settings.SaveAsync();
        True(!fixture.Settings.App.SeparatePrintJobPerReceipt);
        Equal("test-cashier", fixture.Settings.App.Login);
        Equal(1, fixture.Settings.AppSaves);
        Equal(0, fixture.Settings.MarketplaceSaves);
        Equal(0, fixture.Settings.SecretSaves);
    }

    private static async Task CredentialDraftAsync()
    {
        var fixture = new Fixture();
        var first = new MarketplaceConnection { Id = "rz-A", Marketplace = MarketplaceKind.Rozetka, Name = "A", Enabled = true };
        var second = new MarketplaceConnection { Id = "rz-B", Marketplace = MarketplaceKind.Rozetka, Name = "B", Enabled = true };
        fixture.Settings.Market.Connections = [first, second];
        fixture.Settings.Secrets[first.Id] = new(Login: "seller-A", Password: "old-synthetic-A");
        fixture.Settings.Secrets[second.Id] = new(Login: "seller-B", Password: "old-synthetic-B");
        var sync = new MarketplaceSyncService([], fixture.Settings, fixture.Cache);
        var vm = new MarketplaceSettingsViewModel(fixture.Settings, fixture.Settings, sync);
        await vm.LoadAsync();
        vm.Password = "new-synthetic-A";
        vm.Selected = second; // Stage the password, then clear visible secret fields.
        vm.Selected = first;
        vm.Login = "seller-A"; // Editing only login must merge with the staged password.
        await vm.SaveAsync();
        Equal("new-synthetic-A", fixture.Settings.Secrets[first.Id].Password);
        Equal("seller-A", fixture.Settings.Secrets[first.Id].Login);
        Equal("old-synthetic-B", fixture.Settings.Secrets[second.Id].Password);
        Equal(1, fixture.Settings.SecretSaves);
        Equal("", vm.Password); Equal("", vm.Login);

        vm.Login = "different-seller";
        vm.Password = "different-password";
        var rejected = false;
        try { await vm.SaveAsync(); }
        catch (InvalidOperationException) { rejected = true; }
        True(rejected, "A different Rozetka login reused the saved account key.");
        Equal("seller-A", fixture.Settings.Secrets[first.Id].Login);
        Equal("new-synthetic-A", fixture.Settings.Secrets[first.Id].Password);
        Equal(1, fixture.Settings.SecretSaves);
        Equal(1, fixture.Settings.MarketplaceSaves);
    }

    private static async Task OpenOrdersAsync(MainViewModel main, MarketplaceWorkspaceViewModel workspace)
    {
        main.SelectedTabIndex = 1;
        await main.PrepareOrdersAsync();
        await workspace.SyncAsync();
    }

    private static async Task NoIntegrationsAsync()
    {
        var fixture = new Fixture();
        fixture.Settings.Market.Connections.Clear();
        fixture.Authentication.IsStored = false;
        fixture.Dialogs.AfterSettings = () => fixture.Authentication.IsStored = true;
        var (main, workspace) = fixture.Create();
        Equal(0, main.SelectedTabIndex);
        await main.InitializeAsync(); // Simulate first startup: configure Checkbox only, never marketplaces.
        Equal(false, fixture.Dialogs.SettingsOpenedForMarketplace);
        Equal(3, main.ReceiptsView.Cast<ReceiptRowViewModel>().Count());
        True(main.Receipts.All(r => r.OrderMatch is null));
        main.AllReceiptsTab.SelectedReceipt = main.Receipts.Single(r => r.Id == ReceiptOne);
        await ExecuteAsync(main.PreviewCommand);
        Equal(ReceiptOne, fixture.Dialogs.PreviewId);
        main.SelectAllCommand.Execute(null);
        await ExecuteAsync(main.PrintSelectedCommand);
        AssertConfirmation(fixture, [ReceiptThree, ReceiptTwo, ReceiptOne]);
        Equal(0, fixture.Source.FetchCalls); Equal(0, fixture.Rozetka.FetchCalls);
        Equal(0, fixture.Settings.MarketplaceLoads); Equal(0, fixture.Settings.SecretLoads);
        Equal(0, fixture.Cache.Loads); Equal(0, fixture.Links.Loads);
        main.SelectedTabIndex = 1;
        await main.PrepareOrdersAsync();
        True(workspace.Status.Contains("Інтеграції не налаштовані"));
        Equal(3, main.ReceiptsView.Cast<ReceiptRowViewModel>().Count());
        Equal(0, main.SelectedCount); // Shared print history never becomes selection.
        True(main.Receipts.All(r => r.PrintStatus == PrintItemStatus.Done));
    }

    private static async Task CheckboxRefreshIsolationAsync()
    {
        var fixture = new Fixture();
        fixture.Settings.ThrowOnMarketplaceLoad = true;
        fixture.Cache.ThrowOnLoad = true; fixture.Links.ThrowOnLoad = true;
        var (main, _) = fixture.Create();
        await main.RefreshAsync();
        main.Receipts[0].IsSelected = true;
        await ExecuteAsync(main.RefreshCommand);
        main.DateFrom = new(2026, 9, 22);
        await main.RefreshAsync();
        Equal(3, fixture.ReceiptSource.Calls);
        Equal(3, main.ReceiptsView.Cast<ReceiptRowViewModel>().Count());
        Equal(1, main.SelectedCount);
        Equal(0, fixture.Settings.MarketplaceLoads); Equal(0, fixture.Settings.SecretLoads);
        Equal(0, fixture.Cache.Loads); Equal(0, fixture.Links.Loads);
        Equal(0, fixture.Source.FetchCalls); Equal(0, fixture.Rozetka.FetchCalls);
        Equal(PrintItemStatus.Done, main.Receipts.Single(r => r.Id == ReceiptThree).PrintStatus);
    }

    private static async Task AllCategoriesAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [Order(receiptIds: [ReceiptOne])];
        fixture.Settings.Market.Connections.Add(new() { Id = "rz-test", Marketplace = MarketplaceKind.Rozetka, Enabled = true });
        fixture.Rozetka.Orders = [new() { Key = new(MarketplaceKind.Rozetka, "rz-test", "42"), Number = "RZ-42", ReceiptIds = [ReceiptTwo] }];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        Equal("Prom", main.Receipts.Single(r => r.Id == ReceiptOne).Marketplace);
        Equal("Rozetka", main.Receipts.Single(r => r.Id == ReceiptTwo).Marketplace);
        True(main.Receipts.Single(r => r.Id == ReceiptThree).OrderMatch?.Order is null);
        workspace.Filter = "Rozetka";
        True(main.OrdersReceiptsTab.View.Cast<ReceiptRowViewModel>().Select(r => r.Id).SequenceEqual([ReceiptTwo]));
        True(main.AllReceiptsTab.View.Cast<ReceiptRowViewModel>().Select(r => r.Id).SequenceEqual([ReceiptThree, ReceiptTwo, ReceiptOne]));
        main.SelectedTabIndex = 0;
        Equal(3, main.ReceiptsView.Cast<ReceiptRowViewModel>().Count());
        main.SelectAllCommand.Execute(null);
        await ExecuteAsync(main.PrintSelectedCommand);
        AssertConfirmation(fixture, [ReceiptThree, ReceiptTwo, ReceiptOne]);
        Equal(0, fixture.Dialogs.Confirmation!.HiddenSelectedCount);
    }

    private static async Task IndependentTabsAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [Order(receiptIds: [ReceiptOne])];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        main.AllReceiptsTab.SearchText = "2";
        main.AllReceiptsTab.SelectedReceipt = main.Receipts.Single(r => r.Id == ReceiptTwo);
        using (main.AllReceiptsTab.View.DeferRefresh())
        {
            main.AllReceiptsTab.View.SortDescriptions.Clear();
            main.AllReceiptsTab.View.SortDescriptions.Add(new("Serial", ListSortDirection.Ascending));
        }
        main.SelectAllCommand.Execute(null);
        await OpenOrdersAsync(main, workspace);
        main.SearchText = "TEST-TTN";
        main.OrdersReceiptsTab.SelectedReceipt = main.Receipts.Single(r => r.Id == ReceiptOne);
        main.SelectAllCommand.Execute(null);
        True(!ReferenceEquals(main.AllReceiptsTab.View, main.OrdersReceiptsTab.View));
        Equal("2", main.AllReceiptsTab.SearchText); Equal("TEST-TTN", main.OrdersReceiptsTab.SearchText);
        Equal("Serial", main.AllReceiptsTab.View.SortDescriptions[0].PropertyName);
        Equal("LocalDate", main.OrdersReceiptsTab.View.SortDescriptions[0].PropertyName);
        Equal(ReceiptTwo, main.AllReceiptsTab.SelectedReceipt!.Id); Equal(ReceiptOne, main.OrdersReceiptsTab.SelectedReceipt!.Id);
        True(main.Receipts.Single(r => r.Id == ReceiptTwo).IsSelected);
        True(!main.Receipts.Single(r => r.Id == ReceiptTwo).IsSelectedForOrders);
        True(main.Receipts.Single(r => r.Id == ReceiptOne).IsSelectedForOrders);
        True(!main.Receipts.Single(r => r.Id == ReceiptOne).IsSelected);
        main.SelectedType = main.ReceiptTypes.Single(t => t.Value == ReceiptTypes.Return);
        Equal(0, main.VisibleSelectedCount); Equal(1, main.HiddenSelectedCount);
        Equal(1, main.AllReceiptsTab.View.Cast<ReceiptRowViewModel>().Count());
        main.ClearSelectionCommand.Execute(null);
        True(main.Receipts.Single(r => r.Id == ReceiptTwo).IsSelected);
        main.SelectedTabIndex = 0;
        Equal(1, main.VisibleSelectedCount); Equal(0, main.HiddenSelectedCount);
        True(main.PrintSelectedCommand.CanExecute(null));
        main.SearchText = "TEST-TTN";
        Equal(0, main.ReceiptsView.Cast<ReceiptRowViewModel>().Count()); // Base search never reads order fields.
        main.SearchText = "2";
        await main.RefreshAsync();
        Equal("2", main.AllReceiptsTab.SearchText); Equal("TEST-TTN", main.OrdersReceiptsTab.SearchText);
        True(main.Receipts.Single(r => r.Id == ReceiptTwo).IsSelected);
        True(main.Receipts.All(r => !r.IsSelectedForOrders));
    }

    private static async Task SlowMarketplaceAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        main.SelectedTabIndex = 1;
        await main.PrepareOrdersAsync();
        var sync = workspace.SyncAsync();
        await fixture.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        True(workspace.IsBusy);
        main.SelectedTabIndex = 0;
        await main.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Equal(3, main.ReceiptsView.Cast<ReceiptRowViewModel>().Count());
        main.AllReceiptsTab.SelectedReceipt = main.Receipts[0];
        await ExecuteAsync(main.PreviewCommand);
        Equal(ReceiptTwo, fixture.Dialogs.PreviewId);
        main.SelectAllCommand.Execute(null);
        await ExecuteAsync(main.PrintSelectedCommand);
        Equal(1, fixture.Printer.Calls);
        Equal(2, fixture.ReceiptSource.Calls);
        Equal(1, fixture.Source.FetchCalls);
        if (workspace.CancelCommand.CanExecute(null)) workspace.CancelCommand.Execute(null);
        fixture.Source.Gate.TrySetResult(true);
        await sync;
        True(main.Receipts.All(r => r.PrintStatus == PrintItemStatus.Done));
    }

    private static async Task CorruptModuleAsync()
    {
        foreach (var damaged in new[] { "settings", "cache", "links" })
        {
            var fixture = new Fixture();
            fixture.Settings.ThrowOnMarketplaceLoad = damaged == "settings";
            fixture.Cache.ThrowOnLoad = damaged == "cache";
            fixture.Links.ThrowOnLoad = damaged == "links";
            var (main, workspace) = fixture.Create();
            await main.RefreshAsync();
            Equal(3, main.ReceiptsView.Cast<ReceiptRowViewModel>().Count());
            main.SelectedTabIndex = 1;
            await main.PrepareOrdersAsync();
            True(workspace.Status.Contains("Не вдалося"));
            main.SelectedTabIndex = 0;
            await main.RefreshAsync();
            main.SelectAllCommand.Execute(null);
            await ExecuteAsync(main.PrintSelectedCommand);
            AssertConfirmation(fixture, [ReceiptThree, ReceiptTwo, ReceiptOne]);
            Equal(0, fixture.Source.FetchCalls); Equal(0, fixture.Dialogs.Errors.Count);
            Equal(0, fixture.Settings.MarketplaceSaves); Equal(0, fixture.Settings.SecretSaves); Equal(0, fixture.Links.Saves);
        }
    }

    private static async Task DisabledIntegrationsAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [Order()];
        fixture.Settings.Secrets["prom-test"] = new(Token: "synthetic-preserved-token");
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        workspace.SelectedReceipt = main.Receipts[0];
        fixture.Dialogs.Choice = new(Order().Key, false);
        await ExecuteAsync(workspace.LinkCommand);
        var account = PrintAccountContext.Create(fixture.Settings.App);
        var saved = (await fixture.Links.LoadAsync(account)).Single();
        var cacheCount = fixture.Cache.Snapshot.Orders.Count;
        var fetches = fixture.Source.FetchCalls;
        var cacheReads = fixture.Cache.Loads;
        var linkReads = fixture.Links.Loads;
        var secretReads = fixture.Settings.SecretLoads;
        fixture.Dialogs.AfterSettings = () => fixture.Settings.Market.Connections.Single().Enabled = false;
        await ExecuteAsync(main.SettingsCommand);
        await workspace.SyncAsync();
        Equal(fetches, fixture.Source.FetchCalls);
        Equal(cacheReads, fixture.Cache.Loads); Equal(linkReads, fixture.Links.Loads); Equal(secretReads, fixture.Settings.SecretLoads);
        Equal(cacheCount, fixture.Cache.Snapshot.Orders.Count);
        Equal(saved.ConfirmedOrder, (await fixture.Links.LoadAsync(account)).Single().ConfirmedOrder);
        Equal("synthetic-preserved-token", fixture.Settings.Secrets["prom-test"].Token);
        main.SelectedTabIndex = 0;
        await main.RefreshAsync();
        Equal(3, main.ReceiptsView.Cast<ReceiptRowViewModel>().Count());
        Equal(PrintItemStatus.Done, main.Receipts.Single(r => r.Id == ReceiptThree).PrintStatus);
        main.SelectAllCommand.Execute(null);
        await ExecuteAsync(main.PrintSelectedCommand);
        Equal(3, fixture.History.Records.Count);
        Equal(1, fixture.Links.Saves);
        Equal(0, fixture.Settings.SecretSaves);
    }

    private static async Task ActiveTabCommandsAsync()
    {
        var fixture = new Fixture();
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        main.AllReceiptsTab.SearchText = "2";
        main.AllReceiptsTab.SelectedReceipt = main.Receipts.Single(r => r.Id == ReceiptTwo);
        main.SelectAllCommand.Execute(null);
        await OpenOrdersAsync(main, workspace);
        main.SearchText = "1";
        main.OrdersReceiptsTab.SelectedReceipt = main.Receipts.Single(r => r.Id == ReceiptOne);
        Equal(0, main.SelectedCount); True(!main.PrintSelectedCommand.CanExecute(null));
        main.SelectAllCommand.Execute(null);
        await ExecuteAsync(main.PreviewCommand);
        Equal(ReceiptOne, fixture.Dialogs.PreviewId);
        await ExecuteAsync(main.PrintSelectedCommand);
        AssertConfirmation(fixture, [ReceiptOne]);
        main.SelectedTabIndex = 0;
        Equal(1, main.SelectedCount);
        await ExecuteAsync(main.PreviewCommand);
        Equal(ReceiptTwo, fixture.Dialogs.PreviewId);
        await ExecuteAsync(main.PrintSelectedCommand);
        AssertConfirmation(fixture, [ReceiptTwo]);
        Equal(2, fixture.Printer.Calls);
        True(main.AllReceiptsTab.View.Cast<ReceiptRowViewModel>().All(r => r.PrintStatus == PrintItemStatus.Done));
        True(main.OrdersReceiptsTab.View.Cast<ReceiptRowViewModel>().All(r => r.PrintStatus == PrintItemStatus.Done));
    }

    private static async Task SwitchDuringPrintAsync()
    {
        var fixture = new Fixture();
        fixture.Printer.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        main.Receipts.Single(r => r.Id == ReceiptOne).IsSelectedForOrders = true;
        main.SelectedTabIndex = 0;
        main.Receipts.Single(r => r.Id == ReceiptTwo).IsSelected = true;
        fixture.Dialogs.DuringConfirmation = () =>
        {
            main.SelectedTabIndex = 1;
            True(main.IsBusy && !main.PrintSelectedCommand.CanExecute(null));
            True(!main.RetryFailedCommand.CanExecute(null));
            main.PrintSelectedCommand.Execute(null); // Async command guard prevents a second job.
            fixture.Settings.App.PrinterName = "changed-after-snapshot";
        };
        var print = ExecuteAsync(main.PrintSelectedCommand, () => !main.IsBusy);
        await fixture.Printer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Equal(1, fixture.Printer.Calls);
        Equal("mock-printer", fixture.Printer.PrinterName);
        True(fixture.Printer.BatchIds.SequenceEqual([ReceiptTwo]));
        main.SelectedTabIndex = 0;
        main.SelectedTabIndex = 1;
        True(!main.PrintSelectedCommand.CanExecute(null));
        main.PrintSelectedCommand.Execute(null);
        Equal(1, fixture.Printer.Calls);
        fixture.Printer.Gate.SetResult(true);
        await print;
        AssertConfirmation(fixture, [ReceiptTwo]);
        Equal(1, fixture.Printer.Calls);
        Equal(PrintItemStatus.Waiting, main.Receipts.Single(r => r.Id == ReceiptOne).PrintStatus);
        Equal(PrintItemStatus.Done, main.Receipts.Single(r => r.Id == ReceiptTwo).PrintStatus);
        True(main.Receipts.Single(r => r.Id == ReceiptOne).IsSelectedForOrders);
        True(!main.Receipts.Single(r => r.Id == ReceiptTwo).IsSelectedForOrders);
    }

    private static async Task FiscalAutomaticUiAsync()
    {
        var fixture = new Fixture();
        // Normalize an actual API-shaped response through RozetkaOrdersClient first, not a synthetic ReceiptIds list.
        var order = await FiscalLinkTests.Fetch($"https://api.checkbox.ua/api/v1/receipts/{ReceiptOne}/html?simple=true", "FN-1");
        fixture.Settings.Market.Connections.Clear();
        fixture.Settings.Market.Connections.Add(new() { Id = order.Key.ConnectionId, Marketplace = MarketplaceKind.Rozetka, Enabled = true });
        fixture.Rozetka.Orders = [order];
        fixture.ReceiptSource.Rows = [new ReceiptRecord { Id = ReceiptOne, FiscalCode = "FN-1", Serial = 1, Status = "DONE",
            Type = ReceiptTypes.Sell, TotalSumMinor = 10000, FiscalDate = new DateTimeOffset(2026,9,24,12,0,0,TimeSpan.FromHours(3)) },
            Receipt(ReceiptTwo, 2, 10000), Receipt(ReceiptThree, 3, 99900)];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        var row = main.Receipts.Single(r => r.Id == ReceiptOne);
        // Base Checkbox parsing provides fiscal_code; make the fixture agree with the issued document.
        Equal("FN-1", row.FiscalCode);
        await OpenOrdersAsync(main, workspace);
        Equal(ReceiptLinkState.Exact, row.OrderMatch!.State);
        Equal("100", row.OrderNumber); Equal("Rozetka", row.Marketplace);
        Equal("Synthetic", row.OrderBuyer); Equal("SYNTHETIC-TTN", row.OrderTracking);
        Equal("За посиланням на чек", row.LinkStatus);
        Equal(0, fixture.Dialogs.ChoiceCalls); Equal(0, fixture.Links.Saves);
        workspace.Filter = "Rozetka"; Equal(1, main.ReceiptsView.Cast<object>().Count());
        main.SelectedTabIndex = 0; Equal(3, main.ReceiptsView.Cast<object>().Count());
        main.Receipts.Single(r => r.Id == ReceiptTwo).IsSelected = true;
        await ExecuteAsync(main.PrintSelectedCommand);
        AssertConfirmation(fixture, [ReceiptTwo]);
        True(!row.IsSelected && !row.IsSelectedForOrders);
        await main.RefreshAsync(); // No marketplace fetch from F5; cache is re-evaluated on explicit sync.
        Equal(1, fixture.Rozetka.FetchCalls);
        await OpenOrdersAsync(main, workspace);
        Equal(ReceiptLinkState.Exact, main.Receipts.Single(r => r.Id == ReceiptOne).OrderMatch!.State);
    }

    private static async Task OrdersWithoutCheckboxAsync()
    {
        var fixture = new Fixture();
        fixture.Settings.App.Login = "";
        fixture.Authentication.IsStored = false;
        fixture.ReceiptSource.Rows = [];
        fixture.Source.Orders = [Order()];
        fixture.Settings.Market.Connections.Add(new() { Id = "rz-test", Marketplace = MarketplaceKind.Rozetka, Enabled = true });
        fixture.Rozetka.Orders = [Order() with { Key = new(MarketplaceKind.Rozetka, "rz-test", "41"), Number = "RZ-41" }];
        var (main, workspace) = fixture.Create();
        workspace.SetOrderDatesWithoutReceipts(new(2026, 9, 23), new(2026, 9, 24));
        main.SelectedTabIndex = 1;
        await main.PrepareOrdersAsync();
        Equal(0, fixture.Source.FetchCalls); // Entering the tab remains local-only.
        True(workspace.SyncCommand.CanExecute(null));
        await workspace.SyncAsync();
        Equal(2, workspace.Orders.Cast<object>().Count());
        Equal(0, main.Receipts.Count); Equal(0, fixture.ReceiptSource.Calls); Equal(0, fixture.Details.Calls);
        workspace.SelectedOrder = workspace.Orders.Cast<MarketplaceOrderRowViewModel>().First();
        True(!workspace.LinkSelectedOrderCommand.CanExecute(null));
        True(!main.PrintSelectedCommand.CanExecute(null));
        Equal(0, fixture.Links.Loads); Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls);

        // A successful empty Checkbox response is also allowed: orders do not depend on a receipt join.
        fixture.Settings.App.Login = "test-cashier";
        await main.RefreshAsync();
        await main.PrepareOrdersAsync();
        Equal(2, workspace.Orders.Cast<object>().Count());
        Equal(0, main.Receipts.Count);
    }

    private static async Task OrderCalendarWithoutCheckboxAsync()
    {
        var fixture = new Fixture();
        fixture.Settings.App.Login = "";
        fixture.Authentication.IsStored = false;
        fixture.ReceiptSource.Rows = [];
        fixture.Source.Orders = [Order()];
        var (main, workspace) = fixture.Create();
        main.SelectedTabIndex = 1;
        await main.PrepareOrdersAsync();
        await workspace.SyncAsync();
        Equal(1, fixture.Source.FetchCalls);
        var priorRange = fixture.Source.LastRange;
        var settingsReads = fixture.Settings.MarketplaceLoads;
        var cacheReads = fixture.Cache.Loads;
        var secretReads = fixture.Settings.SecretLoads;

        // Stay on the open orders tab: no tab switch, successful Checkbox refresh or explicit PrepareOrders call.
        main.DateFrom = new(2026, 10, 2);
        main.DateTo = new(2026, 10, 3);
        Equal(1, main.SelectedTabIndex);
        Equal(settingsReads, fixture.Settings.MarketplaceLoads);
        Equal(cacheReads, fixture.Cache.Loads); Equal(secretReads, fixture.Settings.SecretLoads);
        Equal(1, fixture.Source.FetchCalls); Equal(0, fixture.ReceiptSource.Calls);
        True(workspace.SyncCommand.CanExecute(null));
        await workspace.SyncAsync();
        Equal(2, fixture.Source.FetchCalls);
        Equal(MarketplaceSyncService.BuildRange(new(2026, 10, 2), new(2026, 10, 3), fixture.Settings.Market.HistoryDays), fixture.Source.LastRange);
        True(priorRange != fixture.Source.LastRange, "Changing the visible calendar must change the actual marketplace query range.");

        main.DateFrom = null;
        True(!workspace.SyncCommand.CanExecute(null));
        main.DateFrom = new(2026, 10, 5); // Reversed compared with the existing October 3 end date.
        True(!workspace.SyncCommand.CanExecute(null));
        main.DateFrom = new(2026, 10, 2);
        True(workspace.SyncCommand.CanExecute(null));
        main.DateTo = null;
        True(!workspace.SyncCommand.CanExecute(null));
        main.DateTo = new(2026, 10, 3);
        True(workspace.SyncCommand.CanExecute(null));
        Equal(2, fixture.Source.FetchCalls); Equal(0, fixture.ReceiptSource.Calls);
        Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls);
    }

    private static async Task IndependentOrderPanelAsync()
    {
        var fixture = new Fixture();
        fixture.Settings.Market.Connections.Add(new() { Id = "prom-other", Marketplace = MarketplaceKind.Prom, Enabled = true, Name = "Second shop" });
        fixture.Settings.Market.Connections.Add(new() { Id = "rz-test", Marketplace = MarketplaceKind.Rozetka, Enabled = true });
        fixture.Source.Orders = [Order(), Order() with { Key = new(MarketplaceKind.Prom, "prom-other", "41"), StoreName = "Second shop" }];
        fixture.Rozetka.Orders = [Order() with { Key = new(MarketplaceKind.Rozetka, "rz-test", "41"), Number = "RZ-41" }];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        Equal(3, workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Select(r => r.Key).Distinct().Count());
        True(workspace.FiscalSummary.Contains("однакові API-ID замовлень у різних підключеннях", StringComparison.Ordinal));
        True(main.Receipts.All(row => row.OrderMatch?.State is not (ReceiptLinkState.Suggested or ReceiptLinkState.Exact)));
        True(workspace.Orders.Cast<MarketplaceOrderRowViewModel>().All(row => !row.HasSuggestedLink && !row.HasConfirmedLink));
        main.AllReceiptsTab.SearchText = "999";
        main.OrdersReceiptsTab.SearchText = "FN-only";
        workspace.Filter = "Без зв’язку";
        workspace.OrderFilter = "Prom";
        Equal(2, workspace.Orders.Cast<object>().Count());
        workspace.SelectedOrder = workspace.Orders.Cast<MarketplaceOrderRowViewModel>().First();
        workspace.OrderFilter = "Rozetka";
        Equal(1, workspace.Orders.Cast<object>().Count());
        True(workspace.SelectedOrder is null, "A now-hidden order must not remain an actionable selection.");
        workspace.OrderSearch = "RZ-41";
        Equal("rz-test", workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Single().Key.ConnectionId);
        workspace.OrderSearch = "does-not-exist";
        Equal(0, workspace.Orders.Cast<object>().Count());
        workspace.OrderSearch = ""; workspace.OrderFilter = "Усі";
        Equal(3, workspace.Orders.Cast<object>().Count());
        Equal("999", main.AllReceiptsTab.SearchText); Equal("FN-only", main.OrdersReceiptsTab.SearchText);
        Equal("Без зв’язку", workspace.Filter); Equal(0, fixture.Links.Saves);
        True(!ReferenceEquals(workspace.Orders, main.AllReceiptsTab.View) && !ReferenceEquals(workspace.Orders, main.OrdersReceiptsTab.View));
    }

    private static async Task SelectedOrderManualAsync()
    {
        var fixture = new Fixture();
        var wanted = Order() with { Key = new(MarketplaceKind.Prom, "prom-test", "42"), Number = "ORDER-42" };
        fixture.Source.Orders = [Order(), wanted];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        var receipt = main.Receipts.Single(r => r.Id == ReceiptOne);
        workspace.SelectedReceipt = receipt;
        workspace.SelectedOrder = workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Single(r => r.Key == wanted.Key);
        fixture.Dialogs.Choice = null;
        await ExecuteAsync(workspace.LinkSelectedOrderCommand);
        Equal(0, fixture.Links.Saves); Equal(1, fixture.Dialogs.ChoiceCalls);
        Equal(ReceiptOne, fixture.Dialogs.ChoiceReceiptId);
        True(fixture.Dialogs.ChoiceOrderKeys.SequenceEqual([wanted.Key]), "The confirmation must contain only the explicitly selected order.");
        fixture.Dialogs.Choice = new(wanted.Key, false);
        await ExecuteAsync(workspace.LinkSelectedOrderCommand);
        Equal(1, fixture.Links.Saves); Equal(ReceiptLinkState.Manual, receipt.OrderMatch!.State);
        Equal(wanted.Key, receipt.OrderMatch.Order!.Key);
        True(workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Single(r => r.Key == wanted.Key).HasConfirmedLink);
        True(!receipt.IsSelected && !receipt.IsSelectedForOrders, "Linking is not a print selection.");

        await main.RefreshAsync();
        await main.PrepareOrdersAsync();
        Equal(ReceiptLinkState.Manual, main.Receipts.Single(r => r.Id == ReceiptOne).OrderMatch!.State);
        True(workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Single(r => r.Key == wanted.Key).HasConfirmedLink);
        var (restarted, restartedWorkspace) = fixture.Create();
        await restarted.RefreshAsync();
        restarted.SelectedTabIndex = 1;
        await restarted.PrepareOrdersAsync();
        Equal(wanted.Key, restarted.Receipts.Single(r => r.Id == ReceiptOne).OrderMatch!.Order!.Key);
        True(restartedWorkspace.Orders.Cast<MarketplaceOrderRowViewModel>().Single(r => r.Key == wanted.Key).HasConfirmedLink);
        Equal(1, fixture.Source.FetchCalls); Equal(1, fixture.Links.Saves);
        Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
    }

    private static MarketplaceOrder BasketOrder() => Order() with { Items = [new("Товар", "SKU", 1m, 100m, 100m)], ItemsComplete = true };

    private static ReceiptDetails BasketDetails(string id) => ReceiptDetailsService.Parse($$"""
        {"id":"{{id}}","type":"SELL","status":"DONE","total_sum":10000,"round_sum":0,"discounts":[],
         "goods":[{"good":{"name":"Товар","code":"SKU","price":10000},"quantity":1000,"sum":10000,"is_return":false,"discounts":[]}]}
        """, id);

    private static async Task BasketSuggestedUiAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [BasketOrder()];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 10000), Receipt(ReceiptThree, 3, 99900)];
        fixture.Details.Values[ReceiptOne] = BasketDetails(ReceiptOne);
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        var receipt = main.Receipts.Single(r => r.Id == ReceiptOne);
        Equal(ReceiptLinkState.Suggested, receipt.OrderMatch!.State);
        Equal(BasketOrder().Key, receipt.OrderMatch.Order!.Key);
        Equal(1, fixture.Details.Calls);
        var order = workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Single();
        True(order.HasSuggestedLink && !order.HasConfirmedLink);
        True(order.LinkStatus.Contains("Ймовір", StringComparison.OrdinalIgnoreCase));
        Equal(0, fixture.Dialogs.ChoiceCalls); Equal(0, fixture.Links.Saves);
        await workspace.AutoMatchAsync();
        Equal(ReceiptLinkState.Suggested, receipt.OrderMatch.State); Equal(0, fixture.Links.Saves);
        main.SelectedTabIndex = 0;
        Equal(2, main.ReceiptsView.Cast<object>().Count());
        True(!receipt.IsSelected && !receipt.IsSelectedForOrders);
        Equal(PrintItemStatus.Done, main.Receipts.Single(r => r.Id == ReceiptThree).PrintStatus);
        Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
    }

    private static async Task BasketAmbiguousUiAsync()
    {
        foreach (var duplicateOrders in new[] { true, false })
        {
            var fixture = new Fixture();
            fixture.Source.Orders = duplicateOrders
                ? [BasketOrder(), BasketOrder() with { Key = new(MarketplaceKind.Prom, "prom-test", "42"), Number = "ORDER-42" }]
                : [BasketOrder()];
            fixture.ReceiptSource.Rows = duplicateOrders ? [Receipt(ReceiptOne, 1, 10000)]
                : [Receipt(ReceiptOne, 1, 10000), Receipt(ReceiptTwo, 2, 10000)];
            fixture.Details.Values[ReceiptOne] = BasketDetails(ReceiptOne);
            fixture.Details.Values[ReceiptTwo] = BasketDetails(ReceiptTwo);
            var (main, workspace) = fixture.Create();
            await main.RefreshAsync();
            await OpenOrdersAsync(main, workspace);
            True(main.Receipts.All(r => r.OrderMatch?.State != ReceiptLinkState.Suggested && r.OrderMatch?.Order is null));
            True(workspace.Orders.Cast<MarketplaceOrderRowViewModel>().All(r => !r.HasSuggestedLink && !r.HasConfirmedLink));
            Equal(duplicateOrders ? 1 : 2, fixture.Details.Calls);
            Equal(0, fixture.Links.Saves);
        }
    }

    private static async Task PartialOrderPanelAsync()
    {
        var fixture = new Fixture();
        var cached = BasketOrder();
        var fresh = BasketOrder() with { Key = new(MarketplaceKind.Prom, "prom-test", "42"), Number = "ORDER-42" };
        await fixture.Cache.SaveAsync(new([cached], []));
        fixture.Source.Orders = [fresh]; fixture.Source.Complete = false;
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 10000)];
        fixture.Details.Values[ReceiptOne] = BasketDetails(ReceiptOne);
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        main.SelectedTabIndex = 1;
        await main.PrepareOrdersAsync();
        Equal(cached.Key, workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Single().Key);
        Equal(0, fixture.Source.FetchCalls);
        await workspace.SyncAsync();
        Equal(2, workspace.Orders.Cast<object>().Count());
        True(main.Receipts.All(r => r.OrderMatch?.State is not (ReceiptLinkState.Exact or ReceiptLinkState.Suggested)));
        True(!workspace.AutoMatchCommand.CanExecute(null));
        True(fixture.Cache.Snapshot.States.All(s => !s.Complete));
        Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls);
    }

    private static async Task StaleBasketDetailsAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [BasketOrder()];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 10000)];
        fixture.Details.Values[ReceiptOne] = BasketDetails(ReceiptOne);
        fixture.Details.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Details.IgnoreCancellation = true;
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        main.SelectedTabIndex = 1;
        await main.PrepareOrdersAsync();
        var pending = workspace.SyncAsync();
        await fixture.Details.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        True(!main.IsBusy, "Optional basket detail reads must not block the Checkbox tab.");
        workspace.InvalidateAccount();
        await workspace.AttachAsync(main.Receipts.ToArray(), "new-account-context", new(2026, 9, 23), new(2026, 9, 23));
        fixture.Details.Gate.SetResult(true);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        True(main.Receipts.All(r => r.OrderMatch?.State != ReceiptLinkState.Suggested));
        Equal(0, fixture.Links.Saves); Equal(0, fixture.Dialogs.ChoiceCalls);
        fixture.Details.Gate = null;
        await workspace.SyncAsync();
        Equal(2, fixture.Details.Calls); // Old-account completion did not seed the new account's detail cache.
        Equal(ReceiptLinkState.Suggested, main.Receipts.Single().OrderMatch!.State);
        Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
    }

    private static async Task BasketContinuationAfterFailuresAsync()
    {
        var fixture = new Fixture();
        var ids = Enumerable.Range(1, 101).Select(i => "20000000-0000-0000-0000-" + i.ToString("D12")).ToArray();
        fixture.Source.Orders = [BasketOrder()];
        fixture.ReceiptSource.Rows = ids.Select(id => Receipt(id, 1, 10000)).ToArray();
        foreach (var id in ids.Take(100)) fixture.Details.FailIds.Add(id);
        fixture.Details.Values[ids[^1]] = BasketDetails(ids[^1]);
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        Equal(100, fixture.Details.Calls);
        True(fixture.Details.RequestedIds.SequenceEqual(ids.Take(100)));
        True(!fixture.Details.RequestedIds.Contains(ids[^1]));
        True(workspace.AutoMatchCommand.CanExecute(null));
        await workspace.AutoMatchAsync();
        True(fixture.Details.Calls > 100 && fixture.Details.Calls <= 200, "Each action must keep its own one-hundred-request bound.");
        Equal(ids[^1], fixture.Details.RequestedIds[100]);
        Equal(1, fixture.Details.RequestedIds.Count(id => id == ids[^1]));
        // The accessible receipt is inspected but not assumed unique: one hundred competing baskets remain unknown.
        True(main.Receipts.All(row => row.OrderMatch?.State != ReceiptLinkState.Suggested && row.OrderMatch?.Order is null));
        True(workspace.Orders.Cast<MarketplaceOrderRowViewModel>().All(row => !row.HasSuggestedLink && !row.HasConfirmedLink));
        Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
    }

    private static Fixture AutomaticBasketFixture()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [BasketOrder()];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 10000), Receipt(ReceiptThree, 3, 99900)];
        fixture.Details.Values[ReceiptOne] = BasketDetails(ReceiptOne);
        return fixture;
    }

    private static async Task AutomaticBasketActivationAsync()
    {
        var fixture = AutomaticBasketFixture();
        var (main, workspace) = fixture.Create(autoLinkEnabled: true);
        True(workspace.AutoLinkEnabled);
        await main.RefreshAsync();
        Equal(0, fixture.Source.FetchCalls); Equal(0, fixture.Details.Calls);

        // Only activate the tab: no Sync, AutoMatch or manual-link command is invoked.
        main.SelectedTabIndex = 1;
        await main.PrepareOrdersAsync();
        var beforeRefresh = main.Receipts.Single(r => r.Id == ReceiptOne);
        Equal(ReceiptLinkState.Suggested, beforeRefresh.OrderMatch!.State);
        Equal(BasketOrder().Key, beforeRefresh.OrderMatch.Order!.Key);
        Equal(1, fixture.Source.FetchCalls); Equal(1, fixture.Details.Calls);
        True(workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Single().HasSuggestedLink);
        beforeRefresh.IsSelectedForOrders = true;

        await main.RefreshAsync();
        await main.PrepareOrdersAsync(); // Await the automatic operation started by the open tab's F5.
        var afterRefresh = main.Receipts.Single(r => r.Id == ReceiptOne);
        True(!ReferenceEquals(beforeRefresh, afterRefresh), "F5 must exercise replacement receipt rows.");
        Equal(ReceiptLinkState.Suggested, afterRefresh.OrderMatch!.State);
        Equal(BasketOrder().Key, afterRefresh.OrderMatch.Order!.Key);
        True(afterRefresh.IsSelectedForOrders, "Automatic linking must not clear receipt check marks.");
        Equal(2, fixture.Source.FetchCalls);
        Equal(1, fixture.Details.Calls); // Completed immutable details remain scoped to this cashier.
        Equal(PrintItemStatus.Done, main.Receipts.Single(r => r.Id == ReceiptThree).PrintStatus);
        Equal(0, fixture.Links.Saves); Equal(0, fixture.Dialogs.ChoiceCalls);
        Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
    }

    private static async Task AutomaticBasketAllTabIsolationAsync()
    {
        var fixture = AutomaticBasketFixture();
        fixture.Settings.ThrowOnMarketplaceLoad = true;
        fixture.Cache.ThrowOnLoad = true; fixture.Links.ThrowOnLoad = true;
        var (main, workspace) = fixture.Create(autoLinkEnabled: true);
        True(workspace.AutoLinkEnabled); Equal(0, main.SelectedTabIndex);
        await main.RefreshAsync();
        await ExecuteAsync(main.RefreshCommand);
        main.DateFrom = new(2026, 9, 22);
        await main.RefreshAsync();
        Equal(3, fixture.ReceiptSource.Calls);
        Equal(0, fixture.Settings.MarketplaceLoads); Equal(0, fixture.Settings.SecretLoads);
        Equal(0, fixture.Cache.Loads); Equal(0, fixture.Links.Loads);
        Equal(0, fixture.Source.FetchCalls); Equal(0, fixture.Rozetka.FetchCalls); Equal(0, fixture.Details.Calls);
        main.AllReceiptsTab.SelectedReceipt = main.Receipts.Single(r => r.Id == ReceiptOne);
        await ExecuteAsync(main.PreviewCommand);
        Equal(ReceiptOne, fixture.Dialogs.PreviewId);
        main.Receipts.Single(r => r.Id == ReceiptOne).IsSelected = true;
        await ExecuteAsync(main.PrintSelectedCommand);
        AssertConfirmation(fixture, [ReceiptOne]);
        Equal(1, fixture.Printer.Calls);
        Equal(0, fixture.Settings.MarketplaceLoads); Equal(0, fixture.Source.FetchCalls);
    }

    private static async Task AutomaticBasketDeduplicationAsync()
    {
        var fixture = AutomaticBasketFixture();
        fixture.Source.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var (main, workspace) = fixture.Create(autoLinkEnabled: true);
        await main.RefreshAsync();
        main.SelectedTabIndex = 1;
        await fixture.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = main.PrepareOrdersAsync();
        var second = main.PrepareOrdersAsync();
        var third = workspace.OpenAsync();
        Equal(1, fixture.Source.FetchCalls);
        True(!main.IsBusy, "An automatic optional refresh cannot make Checkbox busy.");
        fixture.Source.Gate.SetResult(true);
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(5));
        Equal(ReceiptLinkState.Suggested, main.Receipts.Single(r => r.Id == ReceiptOne).OrderMatch!.State);
        Equal(1, fixture.Source.FetchCalls); Equal(1, fixture.Details.Calls);
        main.SelectedTabIndex = 0;
        main.SelectedTabIndex = 1;
        await main.PrepareOrdersAsync();
        Equal(1, fixture.Source.FetchCalls); Equal(1, fixture.Details.Calls);
        Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls);
    }

    private static async Task AutomaticBasketCancellationAsync()
    {
        foreach (var leaveTab in new[] { true, false })
        {
            var fixture = AutomaticBasketFixture();
            fixture.Source.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var (main, workspace) = fixture.Create(autoLinkEnabled: true);
            await main.RefreshAsync();
            main.SelectedTabIndex = 1;
            await fixture.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = main.PrepareOrdersAsync();
            if (leaveTab) main.SelectedTabIndex = 0;
            else workspace.AutoLinkEnabled = false;
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            True(!workspace.IsBusy, "Leaving the tab or disabling automatic linking must cancel its pending API.");
            Equal(1, fixture.Source.FetchCalls); Equal(0, fixture.Details.Calls);
            fixture.Source.Gate.SetResult(true);
            await DrainDispatcherAsync();
            await main.PrepareOrdersAsync();
            await main.PrepareOrdersAsync();
            Equal(1, fixture.Source.FetchCalls);
            True(main.Receipts.All(r => r.OrderMatch?.State != ReceiptLinkState.Suggested));
            if (!leaveTab)
            {
                workspace.AutoLinkEnabled = true;
                await main.PrepareOrdersAsync();
                Equal(2, fixture.Source.FetchCalls);
                Equal(ReceiptLinkState.Suggested, main.Receipts.Single(r => r.Id == ReceiptOne).OrderMatch!.State);
            }
            Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls);
        }

        // A failed API must not turn repeated binding/tab preparation calls into an automatic retry loop.
        var failed = AutomaticBasketFixture();
        failed.Source.Fail = true;
        var (failedMain, failedWorkspace) = failed.Create(autoLinkEnabled: true);
        await failedMain.RefreshAsync();
        failedMain.SelectedTabIndex = 1;
        await failedMain.PrepareOrdersAsync();
        await failedMain.PrepareOrdersAsync();
        await failedWorkspace.OpenAsync();
        Equal(1, failed.Source.FetchCalls); Equal(0, failed.Details.Calls);
        True(!failedWorkspace.IsBusy);
        True(failedMain.Receipts.All(r => r.OrderMatch?.State != ReceiptLinkState.Suggested));
        Equal(0, failed.Links.Saves); Equal(0, failed.Printer.Calls);
    }

    private static async Task AutomaticBasketNoIntegrationsAsync()
    {
        foreach (var disabled in new[] { false, true })
        {
            var fixture = AutomaticBasketFixture();
            if (disabled) fixture.Settings.Market.Connections.Single().Enabled = false;
            else fixture.Settings.Market.Connections.Clear();
            var (main, workspace) = fixture.Create(autoLinkEnabled: true);
            await main.RefreshAsync();
            main.SelectedTabIndex = 1;
            await main.PrepareOrdersAsync();
            await main.PrepareOrdersAsync();
            True(workspace.ShowSetupHint);
            Equal(0, fixture.Settings.SecretLoads); Equal(0, fixture.Cache.Loads); Equal(0, fixture.Links.Loads);
            Equal(0, fixture.Source.FetchCalls); Equal(0, fixture.Rozetka.FetchCalls); Equal(0, fixture.Details.Calls);
            True(!workspace.IsBusy); Equal(0, fixture.Printer.Calls); Equal(0, fixture.Links.Saves);
        }
    }

    private static async Task AutomaticBasketAccountChangeAsync()
    {
        var fixture = AutomaticBasketFixture();
        fixture.Details.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Details.IgnoreCancellation = true;
        var (main, workspace) = fixture.Create(autoLinkEnabled: true);
        await main.RefreshAsync();
        main.SelectedTabIndex = 1;
        await fixture.Details.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = main.PrepareOrdersAsync();
        workspace.InvalidateAccount();
        await workspace.AttachAsync(main.Receipts.ToArray(), "new-account-context", new(2026, 9, 23), new(2026, 9, 23));
        fixture.Details.Gate.SetResult(true);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        True(main.Receipts.All(r => r.OrderMatch?.State != ReceiptLinkState.Suggested));
        Equal(1, fixture.Details.Calls);
        fixture.Details.Gate = null;
        await workspace.OpenAsync();
        Equal(2, fixture.Details.Calls); // A stale completion cannot populate a different account's details.
        Equal(ReceiptLinkState.Suggested, main.Receipts.Single(r => r.Id == ReceiptOne).OrderMatch!.State);
        Equal(0, fixture.Links.Saves); Equal(0, fixture.Dialogs.ChoiceCalls);
        Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
    }

    private static async Task AutomaticBasketRapidToggleAsync()
    {
        var fixture = AutomaticBasketFixture();
        fixture.Details.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Details.IgnoreCancellation = true;
        var (main, workspace) = fixture.Create(autoLinkEnabled: true);
        await main.RefreshAsync();
        main.SelectedTabIndex = 1;
        await fixture.Details.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancelled = main.PrepareOrdersAsync();
        workspace.AutoLinkEnabled = false;
        workspace.AutoLinkEnabled = true; // No dispatcher yield: the first cancelled backend still owns its task.
        var restarted = main.PrepareOrdersAsync();
        var duplicate = workspace.OpenAsync();
        Equal(1, fixture.Source.FetchCalls); Equal(1, fixture.Details.Calls);
        True(!cancelled.IsCompleted && !restarted.IsCompleted);
        fixture.Details.Gate.SetResult(true);
        await Task.WhenAll(cancelled, restarted, duplicate).WaitAsync(TimeSpan.FromSeconds(5));
        Equal(2, fixture.Source.FetchCalls); Equal(2, fixture.Details.Calls);
        Equal(ReceiptLinkState.Suggested, main.Receipts.Single(r => r.Id == ReceiptOne).OrderMatch!.State);
        await main.PrepareOrdersAsync();
        Equal(2, fixture.Source.FetchCalls); Equal(2, fixture.Details.Calls);
        True(!workspace.IsBusy);
        Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
    }

    private static async Task AutomaticBasketManualSyncAsync()
    {
        var fixture = AutomaticBasketFixture();
        fixture.Source.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var (main, workspace) = fixture.Create(autoLinkEnabled: true);
        workspace.AutoLinkEnabled = false;
        await main.RefreshAsync();
        main.SelectedTabIndex = 1;
        await main.PrepareOrdersAsync();
        Equal(0, fixture.Source.FetchCalls);
        var manual = workspace.SyncAsync();
        await fixture.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Enabling the automatic wrapper must not transfer ownership of an existing explicit refresh.
        workspace.AutoLinkEnabled = true;
        var automaticWrapper = main.PrepareOrdersAsync();
        main.SelectedTabIndex = 0;
        workspace.AutoLinkEnabled = false;
        await DrainDispatcherAsync();
        Equal(1, fixture.Source.FetchCalls);
        True(!manual.IsCompleted && workspace.IsBusy, "A tab switch must not cancel a manually started marketplace refresh.");
        True(!main.IsBusy);
        fixture.Source.Gate.SetResult(true);
        await Task.WhenAll(manual, automaticWrapper).WaitAsync(TimeSpan.FromSeconds(5));
        Equal(1, fixture.Source.FetchCalls); Equal(1, fixture.Details.Calls);
        Equal(ReceiptLinkState.Suggested, main.Receipts.Single(r => r.Id == ReceiptOne).OrderMatch!.State);
        True(!workspace.IsBusy); Equal(0, main.SelectedTabIndex);
        Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
    }

    private static async Task AutomaticBasketInvalidDatesAsync()
    {
        var fixture = AutomaticBasketFixture();
        fixture.Authentication.IsStored = false;
        fixture.Source.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var (main, workspace) = fixture.Create(autoLinkEnabled: true);
        Equal(0, main.Receipts.Count); // Marketplace orders need not wait for Checkbox login.
        main.SelectedTabIndex = 1;
        await fixture.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = main.PrepareOrdersAsync();
        main.DateTo = null;
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        True(!workspace.IsBusy);
        True(!workspace.SyncCommand.CanExecute(null));
        Equal(0, workspace.Orders.Cast<object>().Count());
        fixture.Source.Gate.SetResult(true);
        await main.PrepareOrdersAsync();
        await main.PrepareOrdersAsync();
        Equal(1, fixture.Source.FetchCalls);

        // Restore the original dates, not a different range: invalid preparations must not consume its automatic attempt.
        main.DateTo = new(2026, 9, 23);
        await main.PrepareOrdersAsync();
        Equal(2, fixture.Source.FetchCalls);
        Equal(1, workspace.Orders.Cast<object>().Count());
        True(workspace.SyncCommand.CanExecute(null));
        Equal(0, fixture.ReceiptSource.Calls); Equal(0, fixture.Details.Calls);
        Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls);
    }

    private static async Task XamlSmokeAsync()
    {
        _xamlCheckpoint = "initialize application";
        // Never construct the production App: WPF schedules Startup even without Run.
        // Generic Application shares only the exact compiled production resource dictionary.
        var application = new SyntheticApplication();
        application.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
            new Uri("/CheckboxBatchPrinter;component/Resources/AppResources.xaml", UriKind.Relative)));
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        True(application.Resources["BoolToVisibility"] is BooleanToVisibilityConverter);
        var fixture = new Fixture();
        await fixture.Cache.SaveAsync(new([Order()], []));
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        var mainWindow = new MainWindow { DataContext = main };
        var settingsWindow = new SettingsWindow();
        var orderWindow = new OrderLinkWindow(new OrderLinkViewModel(new(Receipt(ReceiptOne, 1, 10000)), null, [Order()], [Order()]));
        var printWindow = new PrintConfirmationWindow(new("mock-printer", 2, [new(1, ReceiptOne, "1", "Prom", "ORDER-41")]));
        var bindingFailures = new BindingErrorListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingFailures);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            foreach (var window in new Window[] { mainWindow, settingsWindow, orderWindow, printWindow })
            {
                _xamlCheckpoint = "layout " + window.GetType().Name;
                LayoutContent(window);
                True(window.Content is not null && !window.IsVisible);
            }
            _xamlCheckpoint = "drain initial layout";
            await DrainDispatcherAsync(); // Run real deferred binding/layout work before declaring the smoke test successful.
            Equal(1, application.InterceptedStartupCalls);
            var tabs = (TabControl)mainWindow.FindName("ReceiptTabs");
            var allGrid = (DataGrid)mainWindow.FindName("AllReceiptsGrid");
            var ordersGrid = (DataGrid)mainWindow.FindName("OrdersReceiptsGrid");
            var marketplaceOrdersGrid = (DataGrid)mainWindow.FindName("MarketplaceOrdersGrid");
            True(tabs is not null && allGrid is not null && ordersGrid is not null && marketplaceOrdersGrid is not null);
            Equal(2, tabs!.Items.Count); Equal(0, tabs.SelectedIndex); Equal(0, main.SelectedTabIndex);
            Equal("Усі чеки", ((TabItem)tabs.Items[0]).Header?.ToString());
            Equal("Чеки та замовлення", ((TabItem)tabs.Items[1]).Header?.ToString());
            True(ReferenceEquals(main.AllReceiptsTab.View, allGrid!.ItemsSource));
            True(ReferenceEquals(main.OrdersReceiptsTab.View, ordersGrid!.ItemsSource));
            True(ReferenceEquals(workspace.Orders, marketplaceOrdersGrid!.ItemsSource));
            True(!ReferenceEquals(allGrid.ItemsSource, ordersGrid.ItemsSource));
            Equal(3, allGrid.Items.Count);
            var first = main.Receipts.Single(r => r.Id == ReceiptOne);
            var second = main.Receipts.Single(r => r.Id == ReceiptTwo);
            allGrid.SelectedItem = second;
            _xamlCheckpoint = "drain all selection";
            await DrainDispatcherAsync();
            True(ReferenceEquals(second, main.AllReceiptsTab.SelectedReceipt));
            _xamlCheckpoint = "all ScrollIntoView";
            allGrid.ScrollIntoView(second);
            _xamlCheckpoint = "all UpdateLayout";
            allGrid.UpdateLayout();
            var allRow = (DataGridRow)allGrid.ItemContainerGenerator.ContainerFromItem(second);
            var allCheckBox = VisualChildren<CheckBox>(allRow).First();
            _xamlCheckpoint = "all OnClick";
            ClickCheckBox(allCheckBox);
            _xamlCheckpoint = "drain all click";
            await DrainDispatcherAsync();
            True(second.IsSelected && !second.IsSelectedForOrders, "One checkbox toggle must change only the All Receipts mark.");
            RenderSyntheticContent(mainWindow, "all-receipts.png");
            tabs.SelectedIndex = 1;
            _xamlCheckpoint = "attach orders";
            await main.PrepareOrdersAsync();
            _xamlCheckpoint = "layout orders";
            LayoutContent(mainWindow);
            _xamlCheckpoint = "drain orders layout";
            await DrainDispatcherAsync();
            Equal(1, main.SelectedTabIndex);
            var split = (Grid)mainWindow.FindName("OrdersSplitLayout");
            var leftPane = (Grid)mainWindow.FindName("MarketplaceOrdersPane");
            var rightPane = (Grid)mainWindow.FindName("CheckboxReceiptsPane");
            var splitter = (GridSplitter)mainWindow.FindName("OrdersVerticalSplitter");
            Equal(3, split.ColumnDefinitions.Count);
            Equal(GridResizeDirection.Columns, splitter.ResizeDirection);
            Equal(GridResizeBehavior.PreviousAndNext, splitter.ResizeBehavior);
            var leftOrigin = leftPane.TranslatePoint(new Point(), split);
            var rightOrigin = rightPane.TranslatePoint(new Point(), split);
            True(rightOrigin.X >= leftOrigin.X + leftPane.ActualWidth && Math.Abs(leftOrigin.Y - rightOrigin.Y) < 1,
                "Orders and receipts must be side by side, not stacked.");
            var leftWidth = leftPane.ActualWidth;
            var rightWidth = rightPane.ActualWidth;
            DragSplitter(splitter, 40);
            LayoutContent(mainWindow);
            True(leftPane.ActualWidth > leftWidth + 20 && rightPane.ActualWidth < rightWidth - 20,
                "Dragging the real vertical splitter must resize both table panes horizontally.");
            DragSplitter(splitter, -40);
            LayoutContent(mainWindow);
            Equal(1, marketplaceOrdersGrid.Items.Count);
            var displayedOrder = workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Single();
            marketplaceOrdersGrid.SelectedItem = displayedOrder;
            await DrainDispatcherAsync();
            True(ReferenceEquals(displayedOrder, workspace.SelectedOrder));
            marketplaceOrdersGrid.ScrollIntoView(displayedOrder); marketplaceOrdersGrid.UpdateLayout();
            var marketplaceOrderRow = (DataGridRow)marketplaceOrdersGrid.ItemContainerGenerator.ContainerFromItem(displayedOrder);
            True(VisualChildren<TextBlock>(marketplaceOrderRow).Any(text => text.Text == "ORDER-41"), "The real orders grid must render the unlinked cached order number.");
            Equal(0, main.SelectedCount); True(!main.PrintSelectedCommand.CanExecute(null));
            ordersGrid.SelectedItem = first;
            _xamlCheckpoint = "drain orders selection";
            await DrainDispatcherAsync();
            True(ReferenceEquals(first, main.OrdersReceiptsTab.SelectedReceipt));
            True(ReferenceEquals(second, main.AllReceiptsTab.SelectedReceipt));
            _xamlCheckpoint = "orders ScrollIntoView";
            ordersGrid.ScrollIntoView(first);
            _xamlCheckpoint = "orders UpdateLayout";
            ordersGrid.UpdateLayout();
            var ordersRow = (DataGridRow)ordersGrid.ItemContainerGenerator.ContainerFromItem(first);
            var ordersCheckBox = VisualChildren<CheckBox>(ordersRow).First();
            _xamlCheckpoint = "orders OnClick";
            ClickCheckBox(ordersCheckBox);
            _xamlCheckpoint = "drain orders click";
            await DrainDispatcherAsync();
            True(first.IsSelectedForOrders && !first.IsSelected);
            RenderSyntheticContent(mainWindow, "receipts-and-orders.png");
            Equal(1, main.SelectedCount); True(main.PrintSelectedCommand.CanExecute(null));
            tabs.SelectedIndex = 0;
            _xamlCheckpoint = "drain return to all";
            await DrainDispatcherAsync();
            Equal(0, main.SelectedTabIndex); Equal(1, main.SelectedCount);
            True(ReferenceEquals(second, allGrid.SelectedItem));
            _xamlCheckpoint = "automatic checkbox binding";
            var automaticLinks = (CheckBox)mainWindow.FindName("AutomaticOrderLinks");
            True(automaticLinks is not null, "Compiled XAML must expose the automatic-linking switch.");
            var automaticBinding = BindingOperations.GetBinding(automaticLinks!, CheckBox.IsCheckedProperty);
            True(automaticBinding is not null);
            Equal("Marketplace.AutoLinkEnabled", automaticBinding!.Path.Path);
            Equal(BindingMode.TwoWay, automaticBinding.Mode);
            Equal(UpdateSourceTrigger.PropertyChanged, automaticBinding.UpdateSourceTrigger);
            Equal<bool?>(false, automaticLinks!.IsChecked);
            workspace.AutoLinkEnabled = true; // Changing the hidden optional control never activates the All tab's APIs.
            await DrainDispatcherAsync();
            Equal<bool?>(true, automaticLinks.IsChecked);
            ClickCheckBox(automaticLinks);
            await DrainDispatcherAsync();
            True(!workspace.AutoLinkEnabled, "One actual checkbox click must disable automatic linking through the compiled binding.");
            Equal<bool?>(false, automaticLinks.IsChecked);
            Equal(0, fixture.Source.FetchCalls);
            Equal(0, fixture.Printer.Calls);
            True(bindingFailures.Messages.Count == 0, "Compiled XAML binding errors: " + string.Join("\n", bindingFailures.Messages));
        }
        finally
        {
            _xamlCheckpoint = "detach trace listener";
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingFailures);
            foreach (var window in new Window[] { mainWindow, settingsWindow, orderWindow, printWindow })
            {
                _xamlCheckpoint = "detach/close " + window.GetType().Name;
                window.DataContext = null;
                BindingOperations.ClearAllBindings(window);
                window.Content = null;
                window.Close();
            }
            _xamlCheckpoint = "drain cleanup";
            await DrainDispatcherAsync();
            _xamlCheckpoint = "finished body";
            // The isolated child owns dispatcher shutdown after this binding cleanup.
        }
    }

    private sealed class SyntheticApplication : Application
    {
        public int InterceptedStartupCalls { get; private set; }
        protected override void OnStartup(StartupEventArgs e)
        {
            InterceptedStartupCalls++;
            // The real App type is never constructed and none of its bootstrap is inherited.
        }
    }

    private static void LayoutContent(Window window)
    {
        // Hidden Windows skip their own layout; directly measure the real compiled content tree.
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1280, 720));
        content.Arrange(new Rect(0, 0, 1280, 720));
        content.UpdateLayout();
    }

    private static void ClickCheckBox(CheckBox checkBox)
    {
        // Run one actual WPF click/toggle handler in-process. Avoid creating an external
        // UI Automation provider for a deliberately hidden test window.
        var click = typeof(CheckBox).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
        click.Invoke(checkBox, null);
    }

    private static void DragSplitter(GridSplitter splitter, double horizontalChange)
    {
        splitter.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0)
            { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragStartedEvent });
        splitter.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(horizontalChange, 0)
            { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent });
        splitter.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(horizontalChange, 0, false)
            { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent });
    }

    private static void RenderSyntheticContent(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("CHECKBOX_TEST_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render((Visual)window.Content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name));
        encoder.Save(output);
    }

    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is null) throw new InvalidOperationException("Expected a realized WPF row.");
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T result) yield return result;
            foreach (var nested in VisualChildren<T>(child)) yield return nested;
        }
    }

    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrEmpty(message)) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }

    private static Task ExecuteAsync(ICommand command, Func<bool>? finished = null)
    {
        if (!command.CanExecute(null)) throw new InvalidOperationException("Expected enabled command.");
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedBusy = false;
        void Changed(object? sender, EventArgs args)
        {
            if (!(finished?.Invoke() ?? command.CanExecute(null))) { observedBusy = true; return; }
            if (!observedBusy) return;
            command.CanExecuteChanged -= Changed;
            completed.TrySetResult();
        }
        command.CanExecuteChanged += Changed;
        command.Execute(null);
        return completed.Task.WaitAsync(TimeSpan.FromSeconds(8));
    }

    private static async Task StaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher? testDispatcher = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            testDispatcher = dispatcher;
            Exception? failure = null;
            var actionFinished = false;
            void Record(Exception exception) => failure = failure is null ? exception : new AggregateException(failure, exception);
            dispatcher.UnhandledException += (_, args) =>
            {
                Record(args.Exception);
                args.Handled = true; // Report it through the test result; never emit a false PASS or crash the next test.
                if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); }
                catch (Exception exception) { Record(exception); }
                finally
                {
                    try { if (!dispatcher.HasShutdownStarted) await DrainDispatcherAsync(); }
                    catch (Exception exception) { Record(exception); }
                    actionFinished = true;
                    if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvokeShutdown(DispatcherPriority.ContextIdle);
                }
            }));
            try { Dispatcher.Run(); }
            catch (Exception exception) { Record(exception); }
            finally
            {
                if (!actionFinished && failure is null) Record(new InvalidOperationException("STA dispatcher exited before the test action finished."));
                // Completion happens after deferred bindings and dispatcher teardown, not merely after the assertion body.
                if (failure is null) completion.TrySetResult();
                else completion.TrySetException(failure);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
        finally
        {
            if (testDispatcher is { HasShutdownStarted: false }) testDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            if (!thread.Join(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("STA test dispatcher did not terminate cleanly. XAML checkpoint: " + _xamlCheckpoint);
        }
    }

    private static async Task DrainDispatcherAsync()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }

    private static MarketplaceOrder Order(IReadOnlyList<string>? receiptIds = null) => new()
    {
        Key = new(MarketplaceKind.Prom, "prom-test", "41"), Number = "ORDER-41", StoreName = "Test shop",
        CreatedAt = new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.FromHours(3)), Total = 100m, Currency = "UAH",
        Status = "Отримано", Buyer = new("Тестовий покупець"), Shipments = [new("Test carrier", "TEST-TTN")], ReceiptIds = receiptIds ?? []
    };
    private static ReceiptRecord Receipt(string id, int serial, long amount, int day = 23) => new()
    {
        Id = id, Serial = serial, Status = "DONE", Type = ReceiptTypes.Sell, TotalSumMinor = amount,
        FiscalDate = new DateTimeOffset(2026, 9, day, 10 + serial, 0, 0, TimeSpan.FromHours(3))
    };
    private static void True(bool value, string message = "Assertion failed.") { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }

    private sealed class Fixture
    {
        public Settings Settings { get; } = new();
        public Source Source { get; } = new();
        public Source Rozetka { get; } = new(MarketplaceKind.Rozetka);
        public Cache Cache { get; } = new();
        public Links Links { get; } = new();
        public PrintHistory History { get; } = new();
        public Printer Printer { get; } = new();
        public Dialogs Dialogs { get; } = new();
        public Details Details { get; } = new();
        public Images Images { get; } = new();
        public Authentication Authentication { get; } = new();
        public Receipts ReceiptSource { get; } = new();
        public Fixture()
        {
            var account = PrintAccountContext.Create(Settings.App);
            History.Records[ReceiptThree] = new(account, ReceiptThree, "mock-printer", DateTimeOffset.UtcNow);
        }
        public (MainViewModel Main, MarketplaceWorkspaceViewModel Workspace) Create(bool autoLinkEnabled = false)
        {
            var sync = new MarketplaceSyncService([Source, Rozetka], Settings, Cache);
            // Existing scenarios explicitly use manual refresh mode; separate activation scenarios cover the production default.
            var workspace = new MarketplaceWorkspaceViewModel(Settings, sync, Links, Details, Dialogs, autoLinkEnabled: autoLinkEnabled);
            var main = new MainViewModel(ReceiptSource, Images, Settings, Authentication, History,
                Printer, Dialogs, new Logger(), workspace) { DateFrom = new(2026, 9, 23), DateTo = new(2026, 9, 23) };
            return (main, workspace);
        }
    }
    private sealed class Settings : ISettingsService, IMarketplaceSettingsStore, IMarketplaceSecretStore
    {
        public AppSettings App { get; } = new() { Login = "test-cashier", PrinterName = "mock-printer" };
        public MarketplaceSettings Market { get; } = new()
            { Connections = [new() { Id = "prom-test", Marketplace = MarketplaceKind.Prom, Enabled = true, Name = "Test shop" }] };
        public Dictionary<string, MarketplaceCredentials> Secrets { get; } = [];
        public bool ThrowOnMarketplaceLoad;
        public int AppSaves, MarketplaceSaves, SecretSaves, MarketplaceLoads, SecretLoads;
        Task<AppSettings> ISettingsService.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(App);
        Task<MarketplaceSettings> IMarketplaceSettingsStore.LoadAsync(CancellationToken cancellationToken)
        {
            MarketplaceLoads++;
            return ThrowOnMarketplaceLoad ? Task.FromException<MarketplaceSettings>(new JsonException("Synthetic damaged settings.")) : Task.FromResult(Market);
        }
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) { AppSaves++; return Task.CompletedTask; }
        public Task SaveAsync(MarketplaceSettings settings, CancellationToken cancellationToken = default) { MarketplaceSaves++; return Task.CompletedTask; }
        public Task<MarketplaceCredentials?> LoadAsync(string id, CancellationToken cancellationToken = default)
        { SecretLoads++; return Task.FromResult<MarketplaceCredentials?>(Secrets.GetValueOrDefault(id) ?? new(Token: "synthetic")); }
        public Task SaveAsync(string id, MarketplaceCredentials value, CancellationToken cancellationToken = default)
        { SecretSaves++; Secrets[id] = value; return Task.CompletedTask; }
    }
    private sealed class Source(MarketplaceKind kind = MarketplaceKind.Prom) : IMarketplaceOrdersClient
    {
        public MarketplaceKind Marketplace => kind;
        public IReadOnlyList<MarketplaceOrder> Orders = [];
        public bool Fail;
        public bool Complete = true;
        public int FetchCalls;
        public MarketplaceRange? LastRange;
        public TaskCompletionSource<bool>? Gate;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task TestConnectionAsync(MarketplaceConnection c, MarketplaceCredentials s, CancellationToken ct = default) => Task.CompletedTask;
        public async Task<OrdersFetchResult> FetchAsync(MarketplaceConnection c, MarketplaceCredentials s, MarketplaceRange range, CancellationToken ct = default)
        {
            FetchCalls++; LastRange = range; Entered.TrySetResult();
            if (Gate is not null) await Gate.Task.WaitAsync(ct);
            if (Fail) throw new MarketplaceApiException("API unavailable (test).");
            return new(Orders, Complete, Complete ? "" : "Перевірка неповна (synthetic test).");
        }
        public Task<MarketplaceOrder?> GetOrderAsync(MarketplaceConnection c, MarketplaceCredentials s, string id, CancellationToken ct = default)
            => Task.FromResult(Orders.FirstOrDefault(o => o.Key.OrderId == id));
    }
    private sealed class Cache : IMarketplaceCacheStore
    {
        private string _json = JsonSerializer.Serialize(new MarketplaceSnapshot([], []));
        public int Loads;
        public bool ThrowOnLoad;
        public MarketplaceSnapshot Snapshot => JsonSerializer.Deserialize<MarketplaceSnapshot>(_json)!;
        public Task<MarketplaceSnapshot> LoadAsync(int days, CancellationToken ct = default)
        { Loads++; return ThrowOnLoad ? Task.FromException<MarketplaceSnapshot>(new JsonException("Synthetic damaged cache.")) : Task.FromResult(Snapshot); }
        public Task SaveAsync(MarketplaceSnapshot value, CancellationToken ct = default) { _json = JsonSerializer.Serialize(value); return Task.CompletedTask; }
    }
    private sealed class Links : IReceiptOrderLinkStore
    {
        private string _json = "[]";
        public int Saves, Loads;
        public bool ThrowOnLoad;
        public Task<IReadOnlyList<ReceiptOrderDecision>> LoadAsync(string account, CancellationToken ct = default)
        {
            Loads++;
            return ThrowOnLoad ? Task.FromException<IReadOnlyList<ReceiptOrderDecision>>(new JsonException("Synthetic damaged links.")) :
                Task.FromResult<IReadOnlyList<ReceiptOrderDecision>>(JsonSerializer.Deserialize<List<ReceiptOrderDecision>>(_json)!.Where(d => d.AccountContext == account).ToArray());
        }
        public Task SaveDecisionAsync(ReceiptOrderDecision decision, CancellationToken ct = default)
        {
            Saves++;
            var records = JsonSerializer.Deserialize<List<ReceiptOrderDecision>>(_json)!;
            records.RemoveAll(d => d.AccountContext == decision.AccountContext && d.ReceiptId == decision.ReceiptId);
            records.Add(decision); _json = JsonSerializer.Serialize(records); return Task.CompletedTask;
        }
    }
    private sealed class PrintHistory : IPrintHistoryStore
    {
        public Dictionary<string, PrintedReceiptRecord> Records { get; } = [];
        public int Saves;
        public Task<IReadOnlyDictionary<string, PrintedReceiptRecord>> LoadAsync(string account, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, PrintedReceiptRecord>>(Records.Where(p => p.Value.AccountContext == account).ToDictionary(p => p.Key, p => p.Value));
        public Task MarkPrintedAsync(string account, IReadOnlyCollection<string> ids, string printer, CancellationToken ct = default)
        {
            Saves++; foreach (var id in ids) Records[id] = new(account, id, printer, DateTimeOffset.UtcNow); return Task.CompletedTask;
        }
    }
    private sealed class Printer : IPrintService
    {
        public int Calls;
        public string PrinterName = "";
        public string[] BatchIds = [];
        public TaskCompletionSource<bool>? Gate;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<string> GetInstalledPrinters() => ["mock-printer"];
        public bool PrinterExists(string name) => name == "mock-printer";
        public Task PrintReceiptAsync(byte[] png, string id, AppSettings settings, CancellationToken ct = default) =>
            PrintReceiptsAsSingleJobAsync([(png, id)], settings, ct);
        public async Task PrintReceiptsAsSingleJobAsync(IReadOnlyList<(byte[] Png, string ReceiptId)> receipts, AppSettings settings, CancellationToken ct = default)
        {
            Calls++; PrinterName = settings.PrinterName; BatchIds = receipts.Select(r => r.ReceiptId).ToArray(); Entered.TrySetResult();
            if (Gate is not null) await Gate.Task.WaitAsync(ct);
        }
        public Task PrintTestAsync(AppSettings settings, CancellationToken ct = default) => throw new InvalidOperationException("Physical test forbidden in VM scenario.");
    }
    private sealed class Receipts : IReceiptService
    {
        public int Calls;
        public IReadOnlyList<ReceiptRecord> Rows = [Receipt(ReceiptTwo, 2, 10000), Receipt(ReceiptOne, 1, 10000), Receipt(ReceiptThree, 3, 99900)];
        public Task<IReadOnlyList<ReceiptRecord>> GetReceiptsAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
        { Calls++; return Task.FromResult(Rows); }
    }
    private sealed class Images : IReceiptImageService
    {
        public string? FailId;
        public Task<byte[]> GetPngAsync(string id, int width, CancellationToken ct = default) => id == FailId
            ? Task.FromException<byte[]>(new InvalidOperationException("Synthetic image download error")) : Task.FromResult(new byte[] { 1, 2, 3 });
        public Task<int> ClearCacheAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task CleanupAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class Details : IReceiptDetailsService
    {
        public int Calls;
        public Dictionary<string, ReceiptDetails> Values { get; } = [];
        public HashSet<string> FailIds { get; } = [];
        public List<string> RequestedIds { get; } = [];
        public bool IgnoreCancellation;
        public TaskCompletionSource<bool>? Gate;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ReceiptDetails> GetAsync(string id, CancellationToken ct = default)
        {
            Calls++; RequestedIds.Add(id); Entered.TrySetResult();
            if (Gate is not null) await Gate.Task.WaitAsync(IgnoreCancellation ? CancellationToken.None : ct);
            if (FailIds.Contains(id)) throw new InvalidOperationException("Synthetic unavailable receipt details.");
            return Values.GetValueOrDefault(id) ?? new(id, [new("Товар", "SKU", 1m, 100m)]);
        }
    }
    private sealed class Authentication : IAuthenticationService
    {
        public bool IsStored = true;
        public bool HasStoredCredentials => IsStored;
        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken ct = default) => Task.FromResult("synthetic");
        public Task SignInAndStoreAsync(string login, string password, CancellationToken ct = default) => Task.CompletedTask;
        public void InvalidateToken() { }
    }
    private sealed class Logger : IAppLogger
    {
        public string LogDirectory => "";
        public void Info(string op, string? id = null, int? status = null, string? print = null) { }
        public void Error(string op, Exception ex, string? id = null, int? status = null, string? print = null) { }
    }
    private sealed class Dialogs : IUiDialogService, IOrderLinkDialogService
    {
        public List<string> Errors { get; } = [];
        public OrderLinkChoice? Choice;
        public string Copied = "";
        public int ChoiceCalls;
        public string? ChoiceReceiptId;
        public IReadOnlyList<OrderKey> ChoiceOrderKeys = [];
        public Action? AfterSettings;
        public PrintBatchConfirmation? Confirmation;
        public Action? DuringConfirmation;
        public bool AcceptPrint = true;
        public string? PreviewId;
        public bool SettingsOpenedForMarketplace;
        public bool ConfirmPrint(PrintBatchConfirmation batch)
        { Confirmation = batch; DuringConfirmation?.Invoke(); return AcceptPrint; }
        public void ShowInfo(string message, string title = "") { }
        public void ShowError(string message, string title = "") => Errors.Add(message);
        public Task<bool> OpenSettingsAsync(bool marketplace = false)
        { SettingsOpenedForMarketplace = marketplace; AfterSettings?.Invoke(); return Task.FromResult(false); }
        public void ShowPreview(byte[] png, ReceiptRowViewModel receipt) => PreviewId = receipt.Id;
        public OrderLinkChoice? ChooseOrder(ReceiptRowViewModel receipt, ReceiptDetails? details, IReadOnlyList<MarketplaceOrder> orders, IReadOnlyList<MarketplaceOrder> candidates)
        { ChoiceCalls++; ChoiceReceiptId = receipt.Id; ChoiceOrderKeys = orders.Select(o => o.Key).ToArray(); return Choice; }
        public bool ConfirmUnlink() => true;
        public void CopyText(string text) => Copied = text;
    }
}
