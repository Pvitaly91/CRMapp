using System.Windows;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;
using CheckboxBatchPrinter.Views;

namespace CheckboxBatchPrinter.Services;

public sealed class UiDialogService(
    ISettingsService settingsService,
    IAuthenticationService authentication,
    IReceiptImageService imageService,
    IPrintService printService,
    IAppLogger logger,
    Func<MarketplaceSettingsViewModel>? marketplaceSettingsFactory = null) : IUiDialogService
{
    public bool ConfirmPrint(PrintBatchConfirmation batch) =>
        new PrintConfirmationWindow(batch) { Owner = Application.Current.MainWindow }.ShowDialog() == true;

    public void ShowInfo(string message, string title = "Checkbox Batch Printer") =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string message, string title = "Помилка") =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public async Task<bool> OpenSettingsAsync()
    {
        var viewModel = new SettingsViewModel(settingsService, authentication, imageService, printService, logger, marketplaceSettingsFactory?.Invoke());
        await viewModel.LoadAsync();
        var window = new SettingsWindow
        {
            Owner = Application.Current.MainWindow,
            DataContext = viewModel
        };
        return window.ShowDialog() == true;
    }

    public void ShowPreview(byte[] png, ReceiptRowViewModel receipt)
    {
        var window = new PreviewWindow(png, receipt)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
    }
}
