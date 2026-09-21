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
    private IAppLogger? _logger;
    private IPrintService? _printService;

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
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(settings.HttpTimeoutSeconds) };
            var authentication = new AuthenticationService(_httpClient, settingsService, credentialStore, _logger);
            var apiClient = new CheckboxApiClient(_httpClient, authentication, _logger);
            var receiptService = new ReceiptService(apiClient, settingsService);
            var imageService = new ReceiptImageService(apiClient, settingsService, Path.Combine(localData, "Cache"), _logger);
            var printService = new WindowsPrintService();
            _printService = printService;
            var dialogs = new UiDialogService(settingsService, authentication, imageService, printService, _logger);
            _logger.Info("app.viewmodel.create");
            var viewModel = new MainViewModel(receiptService, imageService, settingsService, authentication, printService, dialogs, _logger);
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
        _printService?.Dispose();
        _httpClient?.Dispose();
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
