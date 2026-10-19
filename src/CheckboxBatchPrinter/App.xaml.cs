using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Threading;
using System.ComponentModel;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Services;
using CheckboxBatchPrinter.ViewModels;
using CheckboxBatchPrinter.Views;
using CheckboxBatchPrinter.Infrastructure;

namespace CheckboxBatchPrinter;

public partial class App : Application
{
    private HttpClient? _httpClient;
    private HttpClient? _marketplaceHttpClient;
    private HttpClient? _shippingHttpClient;
    private PromCabinetSessionService? _promCabinet;
    private IAppLogger? _logger;
    private SingleInstanceGuard? _singleInstance;
    private TrayController? _tray;
    private BackgroundSyncCoordinator? _coordinator;
    private MainViewModel? _viewModel;
    private MarketplaceWorkspaceViewModel? _marketplaceViewModel;
    private AppSettings? _appSettings;
    private NewOrderNotificationService? _orderNotifications;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Explicit offline packaging diagnostic. Never loads real settings/credentials,
        // constructs API clients or enumerates/submits to printers.
        if (e.Args.Length > 0 && e.Args[0] == "--verify-package")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await PackageVerification.RunAsync(e.Args));
            return;
        }
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        try
        {
            var localData = AppEnvironment.DataRoot;
            _singleInstance = new SingleInstanceGuard(AppEnvironment.Channel, localData);
            if (!_singleInstance.IsPrimary)
            {
                try
                {
                    var runningVersion = await _singleInstance.ActivateExistingAsync();
                    if (runningVersion != AppEnvironment.DisplayVersion)
                        MessageBox.Show($"Цей профіль уже відкрито у версії {runningVersion}. Нову версію не запущено паралельно.",
                            "CRMapp", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception) { MessageBox.Show("Цей профіль уже використовує інший екземпляр CRMapp.", "CRMapp"); }
                Shutdown(); return;
            }
            _singleInstance.StartServer(async () => await Dispatcher.InvokeAsync(OpenMainWindow), AppEnvironment.DisplayVersion);
            Directory.CreateDirectory(localData);
            var settingsService = new JsonSettingsService(Path.Combine(localData, "settings.json"));
            var settings = await settingsService.LoadAsync();
            _appSettings = settings;
            _logger = new FileAppLogger(Path.Combine(localData, "Logs"));
            _logger.Info("app.startup.begin");
            var credentialStore = new DpapiCredentialStore(Path.Combine(localData, "credential.bin"));
            _httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(settings.HttpTimeoutSeconds) };
            var authentication = new AuthenticationService(_httpClient, settingsService, credentialStore, _logger);
            var apiClient = new CheckboxApiClient(_httpClient, authentication, _logger);
            var receiptService = new ReceiptService(apiClient, settingsService);
            var imageService = new ReceiptImageService(apiClient, settingsService, Path.Combine(localData, "Cache"), _logger);
            var printHistoryStore = new JsonPrintHistoryStore(Path.Combine(localData, "printed-receipts.json"));
            var receiptSnapshots = new DpapiReceiptSnapshotStore(Path.Combine(localData, "receipts.dpapi"));
            var printService = new WindowsPrintService();
            var marketplaceData = Path.Combine(localData, "Marketplaces");
            var marketplaceSettings = new JsonMarketplaceSettingsStore(Path.Combine(marketplaceData, "marketplace-settings.json"));
            var marketplaceSecrets = new DpapiMarketplaceSecretStore(Path.Combine(marketplaceData, "Secrets"));
            var marketplaceCache = new DpapiMarketplaceCacheStore(Path.Combine(marketplaceData, "orders.dpapi"), preserveExpiredOrders: true);
            var orderLinks = new DpapiReceiptOrderLinkStore(Path.Combine(marketplaceData, "receipt-order-links.dpapi"));
            var automaticLinks = new DpapiAutomaticMatchCacheStore(Path.Combine(marketplaceData, "automatic-matches.dpapi"));
            _marketplaceHttpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
            var marketplaceTransport = new MarketplaceHttpTransport(_marketplaceHttpClient);
            var marketplaceSync = new MarketplaceSyncService(
                [new PromOrdersClient(marketplaceTransport), new RozetkaOrdersClient(marketplaceTransport)], marketplaceSecrets, marketplaceCache);
            var marketplace = new MarketplaceWorkspaceViewModel(marketplaceSettings, marketplaceSync, orderLinks,
                new ReceiptDetailsService(apiClient, settingsService), new OrderLinkDialogService(), new FiscalReferenceVerifier(apiClient, settingsService),
                automaticCache: automaticLinks);
            _marketplaceViewModel = marketplace;
            var shippingData = Path.Combine(localData, "ShippingLabels");
            var shippingSettings = new DpapiShippingSettingsStore(Path.Combine(shippingData, "settings.dpapi"));
            var labelHistory = new DpapiLabelHistoryStore(Path.Combine(shippingData, "attempts.dpapi"));
            _shippingHttpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
            var labelPrinter = new WindowsLabelPrinter();
            var shippingTransport = new ShippingLabelHttp(_shippingHttpClient);
            var npClient = new NovaPoshtaReadOnlyClient(shippingTransport);
            var pdfInspector = new WindowsLabelPdfInspector();
            _promCabinet = new PromCabinetSessionService(Path.Combine(localData, "PromCabinet"), marketplaceSettings, Dispatcher);
            var npSession = new NovaPoshtaVerifiedSession();
            var labelDialogs = new LabelDialogs(shippingSettings, marketplaceSettings, labelPrinter,
                new NovaPoshtaDiagnostics(npClient, pdfInspector), npSession, () => marketplace.SelectedOrder?.Model, marketplaceSecrets);
            marketplace.Labels = new ShippingLabelsViewModel(marketplace, shippingSettings, labelHistory,
                new ConfiguredShippingLabelSource(shippingSettings,
                    new NovaPoshtaDirectLabelSource(shippingSettings, npClient, pdfInspector),
                    new OfficialShippingLabelSource(shippingTransport, shippingSettings, marketplaceSettings, marketplaceSecrets,
                        promCabinet: new PromCabinetDeliveryLabelSource(marketplaceSettings, _promCabinet, pdfInspector))),
                new WindowsShippingLabelRenderer(), labelPrinter, labelDialogs);
            var dialogs = new UiDialogService(settingsService, authentication, imageService, printService, _logger,
                () => new MarketplaceSettingsViewModel(marketplaceSettings, marketplaceSecrets, marketplaceSync, _promCabinet), marketplace.Labels,
                (updated, credentialsVerified, marketplaceCredentialsSaved) =>
                {
                    var prior = _appSettings;
                    _appSettings = updated;
                    if (prior?.StartWithWindows != updated.StartWithWindows || updated.StartWithWindows)
                        new WindowsAutostart(AppEnvironment.Channel).Apply(updated.StartWithWindows);
                    if (credentialsVerified) _coordinator?.CredentialsVerified("Checkbox");
                    if (marketplaceCredentialsSaved) _coordinator?.CredentialsVerified("Маркетплейси");
                    if (updated.AutoRefreshEnabled)
                    {
                        _coordinator?.Start();
                        if (_coordinator is not null && (credentialsVerified || marketplaceCredentialsSaved || prior?.AutoRefreshEnabled != true))
                            _ = _coordinator.RefreshNowAsync(true);
                    }
                    return Task.CompletedTask;
                }, () => _appSettings?.StartWithWindows == true && new WindowsAutostart(AppEnvironment.Channel).IsMismatched
                    ? "Автозапуск вказує на старий шлях EXE. Натисніть «Зберегти», щоб оновити його." : "");
            _logger.Info("app.viewmodel.create");
            var viewModel = new MainViewModel(receiptService, imageService, settingsService, authentication,
                printHistoryStore, printService, dialogs, _logger, marketplace, receiptSnapshots,
                new WaybillsViewModel(new WaybillService(shippingTransport, shippingSettings),
                    new WaybillLabelPreparation(shippingSettings, shippingTransport, npClient, pdfInspector, new WindowsShippingLabelRenderer()),
                    shippingSettings, labelHistory, labelPrinter, labelDialogs), shippingSettings,
                new CombinedShipmentTrackingService(new ShipmentTrackingService(shippingTransport),
                    new RozetkaSellerTrackingService(shippingTransport, marketplaceSettings, marketplaceSecrets)),
                new DpapiShipmentTrackingCacheStore(Path.Combine(shippingData, "tracking.dpapi")),
                async token =>
                {
                    var account = PrintAccountContext.Create(await settingsService.LoadAsync(token));
                    var config = await marketplaceSettings.LoadAsync(token);
                    var scope = new List<object>();
                    foreach (var connection in config.Connections.Where(c => c.Enabled).OrderBy(c => c.Id, StringComparer.Ordinal))
                        scope.Add(new { connection.Id, connection.Marketplace,
                            Credentials = await marketplaceSecrets.LoadAsync(connection.Id, token) });
                    var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { Account = account, Connections = scope });
                    try { return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)); }
                    finally { Array.Clear(bytes); }
                });
            _viewModel = viewModel;
            viewModel.CoordinatorManaged = true;
            marketplace.CoordinatorManaged = true;
            await viewModel.InitializeCachedAsync();
            await marketplace.EnsureAttachedAsync();
            _orderNotifications = new NewOrderNotificationService(new DpapiOrderNotificationStore(
                Path.Combine(marketplaceData, "order-notifications.dpapi")));
            try { await _orderNotifications.SeedFromCacheAsync(marketplace.GetOrderNotificationSnapshot()); }
            catch (Exception exception) { _logger.Error("orders.notifications.initialize", exception); }
            _logger.Info("app.window.create");
            var window = new MainWindow { DataContext = viewModel };
            _logger.Info("app.window.created");
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.Closing += OnMainWindowClosing;
            SessionEnding += (_, _) => { _exiting = true; _coordinator?.Stop(); };
            void OnUi(Action action) => Dispatcher.BeginInvoke(action);
            _tray = new TrayController(() => OnUi(OpenMainWindow),
                () => OnUi(() => { if (_coordinator is not null) _ = _coordinator.RefreshNowAsync(true); }),
                () => OnUi(() => _coordinator?.SetPaused(!_coordinator.Paused)),
                () => OnUi(() => { OpenMainWindow(); if (viewModel.SettingsCommand.CanExecute(null)) viewModel.SettingsCommand.Execute(null); }),
                () => OnUi(() => _ = ExitFromTrayAsync()));
            window.Show();
            if (e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase) || settings.StartMinimizedToTray) window.Hide();
            async Task RefreshBackgroundOrdersAsync(Task? receiptsSettled, CancellationToken token)
            {
                if (viewModel.IsRefreshingAll) throw new BackgroundSourceSkippedException("Триває оновлення всіх даних");
                var current = await settingsService.LoadAsync(token);
                if (viewModel.IsRefreshingAll) throw new BackgroundSourceSkippedException("Триває оновлення всіх даних");
                var today = DateOnly.FromDateTime(DateRangeBuilder.TodayKyiv);
                var from = today.AddDays(1 - (viewModel.IsProblemOrdersTab ? 14 : current.BackgroundWorkingDays));
                var expectedAccount = authentication.HasStoredCredentials ? PrintAccountContext.Create(current) : "";
                marketplace.SetBackgroundRange(from, today);
                var started = DateTimeOffset.UtcNow;
                try
                {
                    if (receiptsSettled is null) await marketplace.SyncBackgroundAsync(token);
                    else await marketplace.SyncBackgroundAsync(receiptsSettled, expectedAccount, token);
                }
                finally
                {
                    // A failed shop must not hide new orders from another successfully checked shop.
                    if (!_exiting && !viewModel.IsRefreshingAll && !token.IsCancellationRequested)
                    {
                        try
                        {
                            var notification = await _orderNotifications.ObserveAsync(
                                marketplace.GetOrderNotificationSnapshot(started), from, today,
                                _appSettings?.NotifyNewOrders ?? current.NotifyNewOrders, token);
                            if (notification is not null && !_exiting && _appSettings?.NotifyNewOrders != false)
                                _tray?.ShowNewOrders(notification);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                        catch (Exception exception) { _logger.Error("orders.notifications.update", exception); }
                    }
                }
            }
            _coordinator = new BackgroundSyncCoordinator(
                async token =>
                {
                    if (!authentication.HasStoredCredentials) throw new BackgroundSourceSkippedException("Не налаштовано");
                    var current = await settingsService.LoadAsync(token);
                    if (viewModel.IsRefreshingAll) throw new BackgroundSourceSkippedException("Триває оновлення всіх даних");
                    var today = DateOnly.FromDateTime(DateRangeBuilder.TodayKyiv);
                    viewModel.AdvanceTodayIfFollowing(today.ToDateTime(TimeOnly.MinValue));
                    await viewModel.BackgroundRefreshAsync(today.AddDays(1 - (viewModel.IsProblemOrdersTab ? 14 : current.BackgroundWorkingDays)), today, token);
                },
                token => RefreshBackgroundOrdersAsync(null, token), token => settingsService.LoadAsync(token),
                parallelOrders: (receiptsSettled, token) => RefreshBackgroundOrdersAsync(receiptsSettled, token),
                waybills: async token =>
                {
                    if (viewModel.IsProblemOrdersTab)
                        throw new BackgroundSourceSkippedException("Статуси ТТН перевіряються у проблемних замовленнях");
                    if (viewModel.IsBusy || viewModel.IsPrinting || marketplace.Labels?.IsBusy == true ||
                        viewModel.Waybills is not { } waybills || waybills.IsPrintingBusy || waybills.IsBusy)
                        throw new BackgroundSourceSkippedException("Друк або оновлення вже виконується");
                    if (viewModel.DateFrom is null || viewModel.DateTo is null || viewModel.DateTo < viewModel.DateFrom)
                        throw new BackgroundSourceSkippedException("Оберіть коректний період накладних");
                    var shipping = await shippingSettings.LoadAsync(token);
                    var configured = new List<ShippingCarrier>();
                    if (shipping.NovaPoshtaConnections.Any(connection => NovaPoshtaReadOnlyContract.ValidKey(connection.ApiKey)))
                        configured.Add(ShippingCarrier.NovaPoshta);
                    if (!string.IsNullOrWhiteSpace(shipping.RozetkaDeliveryToken)) configured.Add(ShippingCarrier.RozetkaDelivery);
                    if (configured.Count == 0) throw new BackgroundSourceSkippedException("Не налаштовано");
                    if (viewModel.IsBusy || viewModel.IsPrinting || marketplace.Labels?.IsBusy == true ||
                        waybills.IsPrintingBusy || waybills.IsBusy)
                        throw new BackgroundSourceSkippedException("Друк або оновлення вже виконується");
                    await waybills.RefreshAllAsync(token);
                    if (!waybills.LastRefreshSucceeded || configured.Any(carrier =>
                        !waybills.LastSourceStatuses.Any(source => source.Carrier == carrier) ||
                        waybills.LastSourceStatuses.Any(source => source.Carrier == carrier && !source.Complete)))
                        throw new InvalidOperationException("Перевірка налаштованих служб доставки неповна.");
                },
                beginCycle: () =>
                {
                    // A skipped/cooling receipt source must not certify old rows for new orders.
                    if (!viewModel.IsRefreshingAll)
                    {
                        viewModel.AdvanceTodayIfFollowing(DateRangeBuilder.TodayKyiv);
                        marketplace.ReceiptCoverageFresh = false;
                    }
                },
                afterCycle: async token =>
                {
                    if (_exiting || viewModel.IsRefreshingAll || token.IsCancellationRequested) return;
                    try
                    {
                        await viewModel.UpdateWaybillOrderAssociationsAsync();
                        viewModel.QueueShipmentTrackingRefresh();
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    catch (Exception exception)
                    {
                        _logger.Error("waybills.background.associations", exception);
                    }
                }, minimumAutomaticInterval: () => viewModel.IsProblemOrdersTab ? MainViewModel.ProblemRefreshInterval : TimeSpan.Zero);
            _coordinator.StateChanged += (_, _) =>
            {
                var states = _coordinator.States;
                viewModel.BackgroundStatus = string.Join(" | ", states.Select(s =>
                    $"{s.Name}: {s.Status}" + (s.LastSuccessUtc is { } last ? $" · {last.ToLocalTime():dd.MM HH:mm}" : "") +
                    (s.NextAttemptUtc is { } next && next != DateTimeOffset.MaxValue ? $" · далі {next.ToLocalTime():HH:mm}" : "")));
                if (states.Any(s => s.Name == "Checkbox" && s.Status.StartsWith("Немає мережі", StringComparison.Ordinal)))
                    viewModel.MarkBackgroundOffline();
            };
            if (settings.AutoRefreshEnabled) _coordinator.Start();
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
            _logger.Info("app.mainwindow.shown");
        }
        catch (Exception exception)
        {
            _logger?.Error("app.startup", exception);
            MessageBox.Show($"Не вдалося запустити програму. {exception.Message}", "Checkbox Batch Printer",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _coordinator?.Stop();
        _viewModel?.CancelPendingOperations();
        _viewModel?.Waybills?.Dispose();
        _viewModel?.AllReceiptsTab.Dispose();
        _viewModel?.OrdersReceiptsTab.Dispose();
        _marketplaceViewModel?.Labels?.Dispose();
        _marketplaceViewModel?.CancelPendingOperations();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        _httpClient?.Dispose();
        _marketplaceHttpClient?.Dispose();
        _shippingHttpClient?.Dispose();
        _promCabinet?.Dispose();
        base.OnExit(e);
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume) RequestRecoveryRefresh();
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) RequestRecoveryRefresh();
    }

    private void RequestRecoveryRefresh()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_exiting && _appSettings?.AutoRefreshEnabled == true && _coordinator is { Paused: false })
                _ = _coordinator.RecoveryRefreshAsync();
        });
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        if (_appSettings?.KeepInTray == true)
        {
            e.Cancel = true;
            MainWindow.Hide();
            _tray?.ExplainFirstHide();
        }
        else
        {
            if ((_viewModel?.IsPrinting == true || _viewModel?.Waybills?.IsPrintingBusy == true || _marketplaceViewModel?.Labels?.IsBusy == true) && MessageBox.Show(MainWindow,
                    "Триває друк. Завершити програму зараз? Результат передачі може бути невизначеним.",
                    "CRMapp", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            { e.Cancel = true; return; }
            _exiting = true; Shutdown();
        }
    }

    private void OpenMainWindow()
    {
        if (MainWindow is null) return;
        MainWindow.Show();
        MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    private async Task ExitFromTrayAsync()
    {
        if ((_viewModel?.IsPrinting == true || _viewModel?.Waybills?.IsPrintingBusy == true || _marketplaceViewModel?.Labels?.IsBusy == true) && MessageBox.Show(MainWindow,
                "Триває друк. Завершити програму зараз? Результат передачі може бути невизначеним.",
                "CRMapp", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _exiting = true;
        if (_coordinator is not null) await _coordinator.DisposeAsync();
        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Error("app.unhandled", e.Exception);
        e.Handled = true;
        if (_exiting || Dispatcher.HasShutdownStarted) return;
        var message = $"Непередбачена помилка: {e.Exception.Message}";
        if (MainWindow is { } owner)
            MessageBox.Show(owner, message, "Checkbox Batch Printer", MessageBoxButton.OK, MessageBoxImage.Error);
        else
            MessageBox.Show(message, "Checkbox Batch Printer", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
