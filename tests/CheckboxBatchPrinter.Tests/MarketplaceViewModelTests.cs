using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
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

    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("STA marketplace sync preserves selection, confirmed print batch and existing print history", () => StaAsync(ConcurrentPrintAsync)),
        ("STA manual link survives real MainViewModel F5 and viewmodel restart", () => StaAsync(ManualLinkAsync)),
        ("STA unavailable marketplace does not block printing loaded Checkbox receipts", () => StaAsync(FailedMarketplaceAsync)),
        ("STA settings Cancel still invalidates marketplace data after cashier credentials changed", () => StaAsync(SettingsCancelAsync)),
        ("STA stale receipt detail continuation cannot enter another account cache", () => StaAsync(StaleDetailsAsync)),
        ("STA corrupt marketplace settings do not block or overwrite Checkbox and printer settings", () => StaAsync(CorruptSettingsAsync)),
        ("STA Rozetka credential drafts merge and saved account identity cannot change", () => StaAsync(CredentialDraftAsync)),
        ("Checkbox details preserve units and do not infer marketplace IDs from opaque context", ReceiptDetailsAsync),
        ("STA Main Settings and OrderLink XAML initialize without showing windows", () => StaAsync(XamlSmokeAsync))
    ];

    private static async Task ConcurrentPrintAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [Order(receiptIds: [ReceiptTwo])];
        fixture.Source.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Printer.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var (main, workspace) = fixture.Create();
        var refresh = main.RefreshAsync();
        await fixture.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        True(workspace.IsBusy && !main.IsBusy, "Marketplace loading must not own Main.IsBusy.");
        Equal(3, main.Receipts.Count);
        main.Receipts[0].IsSelected = true;
        main.Receipts[1].IsSelected = true;
        Equal(2, main.SelectedCount);
        Equal(PrintItemStatus.Done, main.Receipts[2].PrintStatus);
        True(main.PrintSelectedCommand.CanExecute(null));
        await workspace.SyncAsync();
        Equal(1, fixture.Source.FetchCalls); // A second user action cannot duplicate the active sync.

        var rows = main.Receipts.ToArray();
        var expectedIds = new[] { ReceiptTwo, ReceiptOne };
        var print = ExecuteAsync(main.PrintSelectedCommand);
        await fixture.Printer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        True(fixture.Printer.BatchIds.SequenceEqual(expectedIds));
        Equal(0, fixture.History.Saves);
        fixture.Source.Gate.SetResult(true);
        await refresh;
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
        await main.RefreshAsync();
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
        var row = main.Receipts[0];
        row.IsSelected = true;
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
        Equal(ReceiptLinkState.Manual, main.Receipts[0].OrderMatch!.State);
        Equal("ORDER-41", main.Receipts[0].OrderNumber);
        True(main.Receipts[0].IsSelected);
        Equal(PrintItemStatus.Done, main.Receipts[2].PrintStatus);
        var (restarted, _) = fixture.Create(); // Fresh VMs + deserialized stored decisions/cache.
        await restarted.RefreshAsync();
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
        Equal(3, main.Receipts.Count);
        True(!main.IsBusy && !workspace.IsBusy);
        True(main.Receipts.All(row => row.OrderMatch?.State == ReceiptLinkState.Incomplete));
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
        main.Receipts[0].IsSelected = true;
        Equal("Тестовий покупець", main.Receipts[0].OrderBuyer);
        // Test connection inside Settings has already persisted a new cashier, then user presses Cancel.
        fixture.Dialogs.AfterSettings = () => fixture.Settings.App.Login = "other-cashier";
        await ExecuteAsync(main.SettingsCommand);
        True(main.Receipts.All(row => row.OrderMatch is null));
        True(workspace.SelectedReceipt is null);
        Equal("", main.Receipts[0].OrderBuyer);
        Equal(0, fixture.Printer.Calls);
        True(main.Receipts[0].IsSelected);
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

    private static async Task XamlSmokeAsync()
    {
        // Initialize real compiled application resources, but never run App.OnStartup or show a window.
        var application = new CheckboxBatchPrinter.App();
        application.InitializeComponent();
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        True(application.Resources["BoolToVisibility"] is BooleanToVisibilityConverter);
        var fixture = new Fixture();
        var (main, _) = fixture.Create();
        var mainWindow = new MainWindow { DataContext = main };
        var settingsWindow = new SettingsWindow();
        var orderWindow = new OrderLinkWindow(new OrderLinkViewModel(new(Receipt(ReceiptOne, 1, 10000)), null, [Order()], [Order()]));
        try
        {
            foreach (var window in new Window[] { mainWindow, settingsWindow, orderWindow })
            {
                window.Measure(new Size(1280, 720));
                window.Arrange(new Rect(0, 0, 1280, 720));
                window.UpdateLayout();
                True(window.Content is not null && !window.IsVisible);
            }
            await DrainDispatcherAsync(); // Run real deferred binding/layout work before declaring the smoke test successful.
            Equal(0, fixture.Source.FetchCalls);
            Equal(0, fixture.Printer.Calls);
        }
        finally
        {
            foreach (var window in new Window[] { mainWindow, settingsWindow, orderWindow })
            {
                window.DataContext = null;
                BindingOperations.ClearAllBindings(window);
                window.Content = null;
                window.Close();
            }
            await DrainDispatcherAsync();
            // StaAsync owns dispatcher shutdown. Application.Shutdown here can tear down
            // DataBindEngine while child binding detach operations are still queued.
        }
    }

    private static Task ExecuteAsync(ICommand command)
    {
        if (!command.CanExecute(null)) throw new InvalidOperationException("Expected enabled command.");
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, EventArgs args)
        {
            if (!command.CanExecute(null)) return;
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
                throw new InvalidOperationException("STA test dispatcher did not terminate cleanly.");
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
    private static ReceiptRecord Receipt(string id, int serial, long amount) => new()
    {
        Id = id, Serial = serial, Status = "DONE", Type = ReceiptTypes.Sell, TotalSumMinor = amount,
        FiscalDate = new DateTimeOffset(2026, 9, 23, 10 + serial, 0, 0, TimeSpan.FromHours(3))
    };
    private static void True(bool value, string message = "Assertion failed.") { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }

    private sealed class Fixture
    {
        public Settings Settings { get; } = new();
        public Source Source { get; } = new();
        public Cache Cache { get; } = new();
        public Links Links { get; } = new();
        public PrintHistory History { get; } = new();
        public Printer Printer { get; } = new();
        public Dialogs Dialogs { get; } = new();
        public Details Details { get; } = new();
        public Fixture()
        {
            var account = PrintAccountContext.Create(Settings.App);
            History.Records[ReceiptThree] = new(account, ReceiptThree, "mock-printer", DateTimeOffset.UtcNow);
        }
        public (MainViewModel Main, MarketplaceWorkspaceViewModel Workspace) Create()
        {
            var sync = new MarketplaceSyncService([Source], Settings, Cache);
            var workspace = new MarketplaceWorkspaceViewModel(Settings, sync, Links, Details, Dialogs);
            var main = new MainViewModel(new Receipts(), new Images(), Settings, new Authentication(), History,
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
        public int AppSaves, MarketplaceSaves, SecretSaves;
        Task<AppSettings> ISettingsService.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(App);
        Task<MarketplaceSettings> IMarketplaceSettingsStore.LoadAsync(CancellationToken cancellationToken) => ThrowOnMarketplaceLoad
            ? Task.FromException<MarketplaceSettings>(new JsonException("Synthetic damaged settings.")) : Task.FromResult(Market);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) { AppSaves++; return Task.CompletedTask; }
        public Task SaveAsync(MarketplaceSettings settings, CancellationToken cancellationToken = default) { MarketplaceSaves++; return Task.CompletedTask; }
        public Task<MarketplaceCredentials?> LoadAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<MarketplaceCredentials?>(Secrets.GetValueOrDefault(id) ?? new(Token: "synthetic"));
        public Task SaveAsync(string id, MarketplaceCredentials value, CancellationToken cancellationToken = default)
        { SecretSaves++; Secrets[id] = value; return Task.CompletedTask; }
    }
    private sealed class Source : IMarketplaceOrdersClient
    {
        public MarketplaceKind Marketplace => MarketplaceKind.Prom;
        public IReadOnlyList<MarketplaceOrder> Orders = [];
        public bool Fail;
        public int FetchCalls;
        public TaskCompletionSource<bool>? Gate;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task TestConnectionAsync(MarketplaceConnection c, MarketplaceCredentials s, CancellationToken ct = default) => Task.CompletedTask;
        public async Task<OrdersFetchResult> FetchAsync(MarketplaceConnection c, MarketplaceCredentials s, MarketplaceRange range, CancellationToken ct = default)
        {
            FetchCalls++; Entered.TrySetResult();
            if (Gate is not null) await Gate.Task.WaitAsync(ct);
            if (Fail) throw new MarketplaceApiException("API unavailable (test).");
            return new(Orders, true);
        }
        public Task<MarketplaceOrder?> GetOrderAsync(MarketplaceConnection c, MarketplaceCredentials s, string id, CancellationToken ct = default)
            => Task.FromResult(Orders.FirstOrDefault(o => o.Key.OrderId == id));
    }
    private sealed class Cache : IMarketplaceCacheStore
    {
        private string _json = JsonSerializer.Serialize(new MarketplaceSnapshot([], []));
        public MarketplaceSnapshot Snapshot => JsonSerializer.Deserialize<MarketplaceSnapshot>(_json)!;
        public Task<MarketplaceSnapshot> LoadAsync(int days, CancellationToken ct = default) => Task.FromResult(Snapshot);
        public Task SaveAsync(MarketplaceSnapshot value, CancellationToken ct = default) { _json = JsonSerializer.Serialize(value); return Task.CompletedTask; }
    }
    private sealed class Links : IReceiptOrderLinkStore
    {
        private string _json = "[]";
        public int Saves;
        public Task<IReadOnlyList<ReceiptOrderDecision>> LoadAsync(string account, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ReceiptOrderDecision>>(JsonSerializer.Deserialize<List<ReceiptOrderDecision>>(_json)!.Where(d => d.AccountContext == account).ToArray());
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
        public string[] BatchIds = [];
        public TaskCompletionSource<bool>? Gate;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<string> GetInstalledPrinters() => ["mock-printer"];
        public bool PrinterExists(string name) => name == "mock-printer";
        public Task PrintReceiptAsync(byte[] png, string id, AppSettings settings, CancellationToken ct = default) =>
            PrintReceiptsAsSingleJobAsync([(png, id)], settings, ct);
        public async Task PrintReceiptsAsSingleJobAsync(IReadOnlyList<(byte[] Png, string ReceiptId)> receipts, AppSettings settings, CancellationToken ct = default)
        {
            Calls++; BatchIds = receipts.Select(r => r.ReceiptId).ToArray(); Entered.TrySetResult();
            if (Gate is not null) await Gate.Task.WaitAsync(ct);
        }
        public Task PrintTestAsync(AppSettings settings, CancellationToken ct = default) => throw new InvalidOperationException("Physical test forbidden in VM scenario.");
    }
    private sealed class Receipts : IReceiptService
    {
        public Task<IReadOnlyList<ReceiptRecord>> GetReceiptsAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ReceiptRecord>>([Receipt(ReceiptTwo, 2, 10000), Receipt(ReceiptOne, 1, 10000), Receipt(ReceiptThree, 3, 99900)]);
    }
    private sealed class Images : IReceiptImageService
    {
        public Task<byte[]> GetPngAsync(string id, int width, CancellationToken ct = default) => Task.FromResult(new byte[] { 1, 2, 3 });
        public Task<int> ClearCacheAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task CleanupAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class Details : IReceiptDetailsService
    {
        public int Calls;
        public TaskCompletionSource<bool>? Gate;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ReceiptDetails> GetAsync(string id, CancellationToken ct = default)
        {
            Calls++; Entered.TrySetResult();
            if (Gate is not null) await Gate.Task.WaitAsync(ct);
            return new(id, [new("Товар", "SKU", 1m, 100m)]);
        }
    }
    private sealed class Authentication : IAuthenticationService
    {
        public bool HasStoredCredentials => true;
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
        public Action? AfterSettings;
        public bool ConfirmPrint(int count, string printer) => true;
        public void ShowInfo(string message, string title = "") { }
        public void ShowError(string message, string title = "") => Errors.Add(message);
        public Task<bool> OpenSettingsAsync() { AfterSettings?.Invoke(); return Task.FromResult(false); }
        public void ShowPreview(byte[] png, ReceiptRowViewModel receipt) { }
        public OrderLinkChoice? ChooseOrder(ReceiptRowViewModel receipt, ReceiptDetails? details, IReadOnlyList<MarketplaceOrder> orders, IReadOnlyList<MarketplaceOrder> candidates)
        { ChoiceCalls++; return Choice; }
        public bool ConfirmUnlink() => true;
        public void CopyText(string text) => Copied = text;
    }
}
