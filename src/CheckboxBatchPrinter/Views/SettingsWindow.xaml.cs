using System.Windows;
using System.Windows.Controls;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Views;

public partial class SettingsWindow : Window
{
    private MarketplaceSettingsViewModel? _credentialViewModel;
    private bool _synchronizingCredentialFields;
    public SettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_credentialViewModel is not null) _credentialViewModel.CredentialFieldsChanged -= CredentialFieldsChanged;
            _credentialViewModel = (DataContext as SettingsViewModel)?.Marketplace;
            if (_credentialViewModel is not null) _credentialViewModel.CredentialFieldsChanged += CredentialFieldsChanged;
            SynchronizeCredentialFields();
        };
        Closing += (_, e) => { if (DataContext is SettingsViewModel { IsSaving: true }) e.Cancel = true; };
        Closed += (_, _) =>
        {
            if (_credentialViewModel is not null) _credentialViewModel.CredentialFieldsChanged -= CredentialFieldsChanged;
            _credentialViewModel = null;
        };
    }

    private void CredentialFieldsChanged(object? sender, EventArgs e) => SynchronizeCredentialFields();
    private void SynchronizeCredentialFields()
    {
        _synchronizingCredentialFields = true;
        try
        {
            PromTokenInput.Password = _credentialViewModel?.IsProm == true ? _credentialViewModel.Token : "";
            RozetkaTokenInput.Password = _credentialViewModel?.IsRozetka == true ? _credentialViewModel.Token : "";
        }
        finally { _synchronizingCredentialFields = false; }
    }

    public void ShowMarketplaceTab() => MarketplaceTab.IsSelected = true;

    private async void SettingsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged also bubbles from the store picker; only the tab switch loads the module.
        if (!ReferenceEquals(e.Source, SettingsTabs) || MarketplaceTab?.IsSelected != true
            || DataContext is not SettingsViewModel viewModel) return;
        try { await viewModel.EnsureMarketplaceLoadedAsync(); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void PromTokenInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_synchronizingCredentialFields) return;
        if (DataContext is SettingsViewModel { Marketplace: { } marketplace }) marketplace.Token = PromTokenInput.Password;
    }
    private void RozetkaTokenInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_synchronizingCredentialFields) return;
        if (DataContext is SettingsViewModel { Marketplace: { } marketplace }) marketplace.Token = RozetkaTokenInput.Password;
    }

    private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel) viewModel.Password = PasswordInput.Password;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel || viewModel.IsBusy) return;
        try { await viewModel.SaveAsync(); DialogResult = true; }
        catch (ApiException ex) { ShowError(ex.ToUserMessage()); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void TestApi_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel) return;
        try { await viewModel.TestApiAsync(); PasswordInput.Clear(); MessageBox.Show(this, "Підключення до Checkbox успішне.", "Діагностика", MessageBoxButton.OK, MessageBoxImage.Information); }
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
