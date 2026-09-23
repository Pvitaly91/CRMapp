using System.Windows;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel { Marketplace: { } marketplace })
                marketplace.CredentialsCleared += (_, _) => { PromTokenInput.Clear(); RozetkaPasswordInput.Clear(); };
        };
    }

    private void PromTokenInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel { Marketplace: { } marketplace }) marketplace.Token = PromTokenInput.Password;
    }
    private void RozetkaPasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel { Marketplace: { } marketplace }) marketplace.Password = RozetkaPasswordInput.Password;
    }

    private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel) viewModel.Password = PasswordInput.Password;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel) return;
        try { await viewModel.SaveAsync(); DialogResult = true; }
        catch (ApiException ex) { ShowError(ex.ToUserMessage()); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void TestApi_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel) return;
        try { await viewModel.TestApiAsync(); MessageBox.Show(this, "Підключення до Checkbox успішне.", "Діагностика", MessageBoxButton.OK, MessageBoxImage.Information); }
        catch (ApiException ex) { ShowError(ex.ToUserMessage()); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void TestPrint_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel) return;
        try { await viewModel.TestPrintAsync(); MessageBox.Show(this, "Тестове завдання надіслано на принтер.", "Діагностика", MessageBoxButton.OK, MessageBoxImage.Information); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel) return;
        try { var count = await viewModel.ClearCacheAsync(); MessageBox.Show(this, $"Видалено файлів: {count}", "Кеш", MessageBoxButton.OK, MessageBoxImage.Information); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel) viewModel.OpenLogs();
    }

    private void ShowError(string message) => MessageBox.Show(this, message, "Помилка", MessageBoxButton.OK, MessageBoxImage.Error);
}
