using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Services;
using CheckboxBatchPrinter.ViewModels;
using CheckboxBatchPrinter.Views;

namespace CheckboxBatchPrinter;

public partial class App : Application
{
    private HttpClient? _httpClient;
    private HttpClient? _marketplaceHttpClient;
    private IAppLogger? _logger;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        try
        {
            var localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CheckboxBatchPrinter");
            Directory.CreateDirectory(localData);
            var settingsService = new JsonSettingsService(Path.Combine(localData, "settings.json"));
            var settings = await settingsService.LoadAsync();
            _logger = new FileAppLogger(Path.Combine(localData, "Logs"));
            _logger.Info("app.startup.begin");
            var credentialStore = new DpapiCredentialStore(Path.Combine(localData, "credential.bin"));
            _httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(settings.HttpTimeoutSeconds) };
            var authentication = new AuthenticationService(_httpClient, settingsService, credentialStore, _logger);
            var apiClient = new CheckboxApiClient(_httpClient, authentication, _logger);
            var receiptService = new ReceiptService(apiClient, settingsService);
            var imageService = new ReceiptImageService(apiClient, settingsService, Path.Combine(localData, "Cache"), _logger);
            var printHistoryStore = new JsonPrintHistoryStore(Path.Combine(localData, "printed-receipts.json"));
            var printService = new WindowsPrintService();
            var marketplaceData = Path.Combine(localData, "Marketplaces");
            var marketplaceSettings = new JsonMarketplaceSettingsStore(Path.Combine(marketplaceData, "marketplace-settings.json"));
            var marketplaceSecrets = new DpapiMarketplaceSecretStore(Path.Combine(marketplaceData, "Secrets"));
            var marketplaceCache = new DpapiMarketplaceCacheStore(Path.Combine(marketplaceData, "orders.dpapi"));
            var orderLinks = new DpapiReceiptOrderLinkStore(Path.Combine(marketplaceData, "receipt-order-links.dpapi"));
            _marketplaceHttpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
            var marketplaceTransport = new MarketplaceHttpTransport(_marketplaceHttpClient);
            var marketplaceSync = new MarketplaceSyncService(
                [new PromOrdersClient(marketplaceTransport), new RozetkaOrdersClient(marketplaceTransport)], marketplaceSecrets, marketplaceCache);
            var marketplace = new MarketplaceWorkspaceViewModel(marketplaceSettings, marketplaceSync, orderLinks,
                new ReceiptDetailsService(apiClient, settingsService), new OrderLinkDialogService(), new FiscalReferenceVerifier(apiClient, settingsService));
            var dialogs = new UiDialogService(settingsService, authentication, imageService, printService, _logger,
                () => new MarketplaceSettingsViewModel(marketplaceSettings, marketplaceSecrets, marketplaceSync));
            _logger.Info("app.viewmodel.create");
            var viewModel = new MainViewModel(receiptService, imageService, settingsService, authentication,
                printHistoryStore, printService, dialogs, _logger, marketplace);
            _logger.Info("app.window.create");
            var window = new MainWindow { DataContext = viewModel };
            _logger.Info("app.window.created");
            MainWindow = window;
            window.Show();
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
        _httpClient?.Dispose();
        _marketplaceHttpClient?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Error("app.unhandled", e.Exception);
        MessageBox.Show(MainWindow, $"Непередбачена помилка: {e.Exception.Message}", "Checkbox Batch Printer",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
