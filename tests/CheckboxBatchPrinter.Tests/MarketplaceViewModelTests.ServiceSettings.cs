using System.IO;
using System.Windows.Controls;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Views;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static Task MarketplaceConfigureReusesConnectionsAsync() => MarketplaceConfigureReusesConnectionsCoreAsync(useWindow: false);

    private static Task MarketplaceConfigureButtonsReuseConnectionsAsync()
    {
        _xamlCheckpoint = "marketplace configuration buttons preserve service identities";
        return MarketplaceConfigureReusesConnectionsCoreAsync(useWindow: true);
    }

    private static async Task MarketplaceConfigureReusesConnectionsCoreAsync(bool useWindow)
    {
        using var files = new RozetkaSettingsFiles();
        var alias = new MarketplaceConnectionAlias(MarketplaceKind.Rozetka, SettingsRozetkaBId, SettingsRozetkaAId);
        await files.Settings.Inner.SaveAsync(new() { Connections =
            [SettingsConnection(SettingsPromId, MarketplaceKind.Prom), SettingsConnection(SettingsRozetkaAId)], ConnectionAliases = [alias] });
        await files.Secrets.Inner.SaveAsync(SettingsPromId, new(Token: "synthetic_original_prom"));
        await files.Secrets.Inner.SaveAsync(SettingsRozetkaAId, new(Token: "synthetic_original_rozetka"));
        var cache = new DpapiMarketplaceCacheStore(files.CachePath);
        await cache.SaveAsync(new([Order([ReceiptOne]) with
            { Key = new(MarketplaceKind.Rozetka, SettingsRozetkaBId, "historical-order"), RetrievedAtUtc = DateTimeOffset.UtcNow }], []));
        var cachedBefore = await File.ReadAllBytesAsync(files.CachePath);
        var editor = files.CreateMarketplace(); var parent = files.CreateParent(editor);
        await parent.LoadAsync(); await parent.EnsureMarketplaceLoadedAsync();
        var window = useWindow ? new SettingsWindow { DataContext = parent } : null;
        try
        {
            if (window is not null)
            {
                window.ShowMarketplaceTab(); LayoutContent(window); await DrainDispatcherAsync();
                Equal("Налаштувати Prom", ((Button)window.FindName("PromConnectionButton")).Content?.ToString());
                Equal("Налаштувати Rozetka", ((Button)window.FindName("RozetkaConnectionButton")).Content?.ToString());
            }
            for (var round = 1; round <= 2; round++)
            {
                foreach (var (buttonName, inputName, vmCommand, id, token) in new[]
                {
                    ("PromConnectionButton", "PromTokenInput", editor.AddPromCommand, SettingsPromId, "synthetic_rotated_prom_" + round),
                    ("RozetkaConnectionButton", "RozetkaTokenInput", editor.AddRozetkaCommand, SettingsRozetkaAId, "synthetic_rotated_rozetka_" + round)
                })
                {
                    var command = window is null ? vmCommand
                        : ((Button)window.FindName(buttonName)).Command ?? throw new InvalidOperationException("Marketplace configuration button has no command.");
                    True(command.CanExecute(null)); command.Execute(null);
                    await editor.CredentialStatusRefresh; await DrainDispatcherAsync();
                    Equal(id, editor.Selected!.Id);
                    var input = window is null ? null : (PasswordBox)window.FindName(inputName);
                    if (input is not null) input.Password = token;
                    else editor.Token = token;
                    for (var repeat = 0; repeat < 3; repeat++) command.Execute(null);
                    Equal(id, editor.Selected.Id); Equal(token, editor.Token);
                    if (input is not null) Equal(token, input.Password);
                    Equal(2, editor.Connections.Count);
                }
                await parent.SaveAsync(); await DrainDispatcherAsync();
                var saved = await files.Settings.Inner.LoadAsync();
                Equal(2, saved.Connections.Count); Equal(alias, saved.ConnectionAliases.Single());
                True(saved.Connections.Select(c => c.Id).ToHashSet().SetEquals([SettingsPromId, SettingsRozetkaAId]));
                await AssertSyntheticCredentialsAsync(files, SettingsPromId, "synthetic_rotated_prom_" + round);
                await AssertSyntheticCredentialsAsync(files, SettingsRozetkaAId, "synthetic_rotated_rozetka_" + round);
                Equal(2, Directory.GetFiles(files.SecretsDirectory, "marketplace-*.dpapi").Length);
                True(cachedBefore.SequenceEqual(await File.ReadAllBytesAsync(files.CachePath)));
            }
            Equal(4, files.Secrets.Saves);
            if (window is not null) RenderSyntheticContent(window, "marketplace-single-connections.png");
        }
        finally { window?.Close(); }
        var reopened = files.CreateMarketplace(); await reopened.LoadAsync();
        reopened.AddRozetkaCommand.Execute(null); await reopened.CredentialStatusRefresh;
        Equal(SettingsRozetkaAId, reopened.Selected!.Id); Equal("Saved", reopened.CredentialStatusKind);
        True(!await reopened.SaveAsync()); Equal(4, files.Secrets.Saves); Equal(2, reopened.Connections.Count);
        Equal(alias, (await files.Settings.Inner.LoadAsync()).ConnectionAliases.Single());
        Equal(0, files.Base.Rozetka.FetchCalls); Equal(0, files.Base.Source.FetchCalls);
        Equal(0, files.Base.Printer.Calls); Equal(0, files.Base.History.Saves); Equal(0, files.Base.Links.Saves);
    }

    private static async Task MarketplaceFirstConnectionIsReusedAsync()
    {
        using var files = new RozetkaSettingsFiles();
        await files.SeedAsync();
        var editor = files.CreateMarketplace(); await editor.LoadAsync();
        Equal("Підключити Prom", editor.PromConnectionButtonText); Equal("Підключити Rozetka", editor.RozetkaConnectionButtonText);
        editor.AddPromCommand.Execute(null); var promId = editor.Selected!.Id;
        editor.Token = "synthetic_first_prom";
        for (var repeat = 0; repeat < 4; repeat++) editor.AddPromCommand.Execute(null);
        Equal(promId, editor.Selected.Id); Equal(1, editor.Connections.Count); Equal("synthetic_first_prom", editor.Token);
        editor.AddRozetkaCommand.Execute(null); var rozetkaId = editor.Selected!.Id;
        editor.Token = "synthetic_first_rozetka";
        for (var repeat = 0; repeat < 4; repeat++) editor.AddRozetkaCommand.Execute(null);
        Equal(rozetkaId, editor.Selected.Id); Equal(2, editor.Connections.Count); Equal("synthetic_first_rozetka", editor.Token);
        editor.AddPromCommand.Execute(null);
        Equal(promId, editor.Selected!.Id); Equal("synthetic_first_prom", editor.Token);
        await editor.SaveAsync();
        Equal(2, (await files.Settings.Inner.LoadAsync()).Connections.Count);
        await AssertSyntheticCredentialsAsync(files, promId, "synthetic_first_prom");
        await AssertSyntheticCredentialsAsync(files, rozetkaId, "synthetic_first_rozetka");
        Equal("Налаштувати Prom", editor.PromConnectionButtonText); Equal("Налаштувати Rozetka", editor.RozetkaConnectionButtonText);
        editor.AddRozetkaCommand.Execute(null); await editor.CredentialStatusRefresh;
        Equal(rozetkaId, editor.Selected!.Id); True(!await editor.SaveAsync()); Equal(2, files.Secrets.Saves);
    }

    private static async Task MarketplaceExistingShopSelectionAsync()
    {
        using var files = new RozetkaSettingsFiles();
        var disabled = SettingsConnection(SettingsRozetkaBId); disabled.Enabled = false;
        await files.SeedAsync(disabled, SettingsConnection(SettingsRozetkaAId), SettingsConnection(SettingsPromId, MarketplaceKind.Prom));
        await files.Secrets.Inner.SaveAsync(SettingsRozetkaAId, new(Token: "synthetic_distinct_A"));
        await files.Secrets.Inner.SaveAsync(SettingsRozetkaBId, new(Token: "synthetic_distinct_B"));
        await files.Secrets.Inner.SaveAsync(SettingsPromId, new(Token: "synthetic_unchanged_prom"));
        var firstBefore = await File.ReadAllBytesAsync(files.SecretPath(SettingsRozetkaAId));
        var promBefore = await File.ReadAllBytesAsync(files.SecretPath(SettingsPromId));
        var editor = files.CreateMarketplace(); await editor.LoadAsync();
        Equal(SettingsRozetkaBId, editor.Selected!.Id);
        editor.AddRozetkaCommand.Execute(null); Equal(SettingsRozetkaBId, editor.Selected!.Id);
        True(!editor.Selected.Enabled); editor.Token = "synthetic_replaced_B";
        editor.AddPromCommand.Execute(null); editor.AddRozetkaCommand.Execute(null);
        Equal(SettingsRozetkaAId, editor.Selected!.Id); // Prefer an enabled account when switching services.
        Equal(3, editor.Connections.Count); await editor.SaveAsync();
        var saved = await files.Settings.Inner.LoadAsync(); Equal(3, saved.Connections.Count);
        True(!saved.Connections.Single(c => c.Id == SettingsRozetkaBId).Enabled);
        await AssertSyntheticCredentialsAsync(files, SettingsRozetkaBId, "synthetic_replaced_B");
        True(firstBefore.SequenceEqual(await File.ReadAllBytesAsync(files.SecretPath(SettingsRozetkaAId))));
        True(promBefore.SequenceEqual(await File.ReadAllBytesAsync(files.SecretPath(SettingsPromId)))); Equal(1, files.Secrets.Saves);

        await files.Settings.Inner.SaveAsync(new() { Connections = [disabled] });
        var disabledOnly = files.CreateMarketplace(); await disabledOnly.LoadAsync(); disabledOnly.Selected = null;
        disabledOnly.AddRozetkaCommand.Execute(null);
        // Selecting the account starts an async DPAPI read; let it finish before the
        // fixture removes its temporary secret directory at the end of this test.
        await disabledOnly.CredentialStatusRefresh;
        Equal(SettingsRozetkaBId, disabledOnly.Selected!.Id); Equal(1, disabledOnly.Connections.Count); True(!disabledOnly.Selected.Enabled);
        Equal("Saved", disabledOnly.CredentialStatusKind);
    }
}
