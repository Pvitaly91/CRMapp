using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;
using CheckboxBatchPrinter.Views;
using System.Windows;
using System.Windows.Controls;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task MarketplaceCredentialStatusAsync()
    {
        var fixture = new Fixture();
        fixture.Settings.Secrets["prom-test"] = new(Token: "synthetic_saved_prom_secret");
        fixture.Settings.Market.Connections.Add(new() { Id = "rz-status", Name = "Synthetic Rozetka", Marketplace = MarketplaceKind.Rozetka, Enabled = true });
        fixture.Settings.Secrets["rz-status"] = new(Token: "synthetic_saved_rz_secret");
        var vm = new MarketplaceSettingsViewModel(fixture.Settings, fixture.Settings,
            new MarketplaceSyncService([fixture.Source, fixture.Rozetka], fixture.Settings, fixture.Cache));
        await vm.LoadAsync();
        Equal("Saved", vm.CredentialStatusKind); True(vm.CredentialStatus.Contains("Токен Prom збережено"));
        Equal("", vm.Token); True(!vm.CredentialStatus.Contains("synthetic_saved_prom_secret"));
        vm.Token = "synthetic_new_prom_secret"; Equal("Draft", vm.CredentialStatusKind);
        vm.Selected = vm.Connections.Single(c => c.Id == "rz-status"); await vm.CredentialStatusRefresh;
        Equal("Saved", vm.CredentialStatusKind); True(vm.CredentialStatus.Contains("API-токен Rozetka збережено"));
        Equal("", vm.Token); True(!vm.CredentialStatus.Contains("synthetic_saved_rz_secret"));
        vm.Selected = vm.Connections.Single(c => c.Id == "prom-test"); await vm.CredentialStatusRefresh;
        Equal("Draft", vm.CredentialStatusKind); await vm.SaveAsync(); Equal("Saved", vm.CredentialStatusKind);
        fixture.Settings.Secrets["rz-status"] = new(Login: "synthetic_login");
        vm.Selected = vm.Connections.Single(c => c.Id == "rz-status"); await vm.CredentialStatusRefresh;
        Equal("Missing", vm.CredentialStatusKind);
        foreach (var malformed in new[] { "синтетичний_секрет", "synthetic token", "synthetic_token\n" })
        {
            fixture.Settings.Secrets["rz-status"] = new(Token: malformed);
            vm.Selected = null; vm.Selected = vm.Connections.Single(c => c.Id == "rz-status"); await vm.CredentialStatusRefresh;
            Equal("Missing", vm.CredentialStatusKind);
            True(!vm.CredentialStatus.Contains(malformed), "Malformed secret leaked into the credential indicator.");
        }
        Equal(0, fixture.Source.FetchCalls); Equal(0, fixture.Printer.Calls);
    }
    private static async Task CheckboxCredentialStatusAsync()
    {
        var fixture = new Fixture(); var authentication = new Authentication { IsStored = true };
        var vm = new SettingsViewModel(fixture.Settings, authentication, new Images(), fixture.Printer, new Logger());
        await vm.LoadAsync(); Equal("Saved", vm.CredentialStatusKind);
        True(vm.CredentialStatus.Contains("Checkbox") && vm.Password.Length == 0);
        vm.Password = "synthetic_uncommitted_password"; Equal("Draft", vm.CredentialStatusKind);
        True(!vm.CredentialStatus.Contains(vm.Password));
        vm.Password = ""; Equal("Saved", vm.CredentialStatusKind);
        authentication.IsStored = false; Equal("Missing", vm.CredentialStatusKind);
        Equal(0, fixture.Printer.Calls); Equal(0, fixture.Settings.MarketplaceLoads);
    }
    private static async Task CredentialIndicatorsXamlAsync()
    {
        var fixture = new Fixture(); fixture.Settings.Secrets["prom-test"] = new(Token: "synthetic_saved_prom_secret");
        var marketplace = new MarketplaceSettingsViewModel(fixture.Settings, fixture.Settings,
            new MarketplaceSyncService([fixture.Source], fixture.Settings, fixture.Cache));
        var vm = new SettingsViewModel(fixture.Settings, new Authentication(), new Images(), fixture.Printer, new Logger(), marketplace);
        await vm.LoadAsync(); await vm.EnsureMarketplaceLoadedAsync();
        var window = new SettingsWindow { DataContext = vm };
        try
        {
            LayoutContent(window); await DrainDispatcherAsync();
            var checkbox = (TextBlock)window.FindName("CheckboxCredentialIndicator");
            True(checkbox.Text.Contains("збережено") && checkbox.Tag?.ToString() == "Saved");
            RenderSyntheticContent(window, "checkbox-credential-status.png");
            window.ShowMarketplaceTab(); LayoutContent(window); await DrainDispatcherAsync();
            var prom = (TextBlock)window.FindName("MarketplaceCredentialIndicator");
            True(prom.Text.Contains("Токен Prom збережено") && prom.Tag?.ToString() == "Saved");
            RenderSyntheticContent(window, "prom-credential-status.png");
            vm.Password = "new_private_value"; LayoutContent(window); await DrainDispatcherAsync();
            True(checkbox.Tag?.ToString() == "Draft");
            True(!checkbox.Text.Contains("new_private_value") && !prom.Text.Contains("synthetic_saved_prom_secret"));
        }
        finally { window.Close(); }
    }
}
