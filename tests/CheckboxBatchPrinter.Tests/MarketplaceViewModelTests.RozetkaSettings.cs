using System.IO;
using System.Text;
using System.Windows.Controls;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;
using CheckboxBatchPrinter.Views;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> RozetkaSettingsScenarios =>
    [
        ("STA new Rozetka settings survive parent Save and reopening the real JSON and DPAPI stores", () => StaAsync(RozetkaNewConnectionRoundTripAsync)),
        ("STA typed Rozetka drafts restore per shop without exposing stored passwords", () => StaAsync(RozetkaDraftSwitchRoundTripAsync)),
        ("STA incomplete Rozetka pairs and invalid names reject all pending credentials before any store write", () => StaAsync(RozetkaIncompletePreflightAsync)),
        ("STA late saved credential presence cannot overwrite a newly typed Rozetka login", () => StaAsync(RozetkaPresenceDoesNotOverwriteDraftAsync)),
        ("STA prefilled saved Rozetka login stays Saved and a no-edit Save never rewrites secret bytes", () => StaAsync(RozetkaSavedLoginIsNotDraftAsync)),
        ("STA parent coordinator failure occurs after durable Rozetka persistence", () => StaAsync(RozetkaParentCallbackFailureAsync)),
        ("STA concurrent marketplace Save is rejected while a credential store write is pending", () => StaAsync(RozetkaConcurrentSaveAsync)),
        ("STA concurrent parent settings Save is rejected before duplicate app or credential writes", () => StaAsync(RozetkaConcurrentParentSaveAsync)),
        ("STA saved Rozetka configuration reloads in the managed workspace without restart or losing row marks", () => StaAsync(RozetkaSavedConfigurationReloadAsync))
    ];

    private const string SettingsPromId = "90000000000000000000000000000001";
    private const string SettingsRozetkaAId = "90000000000000000000000000000002";
    private const string SettingsRozetkaBId = "90000000000000000000000000000003";

    private static async Task RozetkaNewConnectionRoundTripAsync()
    {
        using var files = new RozetkaSettingsFiles();
        await files.SeedAsync(SettingsConnection(SettingsPromId, MarketplaceKind.Prom));
        await files.Secrets.Inner.SaveAsync(SettingsPromId, new(Token: "synthetic_existing_prom"));
        var marketplace = files.CreateMarketplace();
        var parent = files.CreateParent(marketplace);
        await parent.LoadAsync(); await parent.EnsureMarketplaceLoadedAsync();
        marketplace.AddRozetkaCommand.Execute(null);
        var newId = marketplace.Selected!.Id;
        marketplace.Selected.Name = "Synthetic new Rozetka";
        marketplace.Login = "synthetic_new_seller";
        marketplace.Password = "synthetic_new_seller_password";
        Equal("Draft", marketplace.CredentialStatusKind);
        await parent.SaveAsync();
        Equal(1, files.Secrets.Saves);
        Equal(1, files.Settings.Saves);
        var persisted = (await files.Settings.Inner.LoadAsync()).Connections.Single(c => c.Id == newId);
        Equal(MarketplaceKind.Rozetka, persisted.Marketplace);
        True(persisted.Enabled);
        await AssertSyntheticCredentialsAsync(files, newId, "synthetic_new_seller", "synthetic_new_seller_password");
        Equal("synthetic_existing_prom", (await files.Secrets.Inner.LoadAsync(SettingsPromId))!.Token);

        var reopened = files.CreateMarketplace();
        await reopened.LoadAsync();
        reopened.Selected = reopened.Connections.Single(c => c.Id == newId);
        await reopened.CredentialStatusRefresh;
        Equal("synthetic_new_seller", reopened.Login);
        Equal("", reopened.Password);
        Equal("Saved", reopened.CredentialStatusKind);
        True(reopened.CredentialStatus.Contains("Облікові дані Rozetka збережено"));
        Equal(0, files.Base.Printer.Calls);
        Equal(0, files.Base.Source.FetchCalls);
        Equal(0, files.Base.Rozetka.FetchCalls);
    }

    private static async Task RozetkaDraftSwitchRoundTripAsync()
    {
        using var files = new RozetkaSettingsFiles();
        await files.SeedAsync(SettingsConnection(SettingsRozetkaAId), SettingsConnection(SettingsRozetkaBId),
            SettingsConnection(SettingsPromId, MarketplaceKind.Prom));
        await files.Secrets.Inner.SaveAsync(SettingsRozetkaAId, new(Login: "synthetic_seller_A", Password: "synthetic_stored_A"));
        await files.Secrets.Inner.SaveAsync(SettingsRozetkaBId, new(Login: "synthetic_seller_B", Password: "synthetic_stored_B"));
        await files.Secrets.Inner.SaveAsync(SettingsPromId, new(Token: "synthetic_stored_prom"));
        var marketplace = files.CreateMarketplace();
        await marketplace.LoadAsync();
        Equal("synthetic_seller_A", marketplace.Login); Equal("", marketplace.Password);
        marketplace.Password = "synthetic_typed_A";
        marketplace.Selected = marketplace.Connections.Single(c => c.Id == SettingsRozetkaBId);
        await marketplace.CredentialStatusRefresh;
        Equal("synthetic_seller_B", marketplace.Login); Equal("", marketplace.Password);
        marketplace.Password = "synthetic_typed_B";
        marketplace.Selected = marketplace.Connections.Single(c => c.Id == SettingsPromId);
        await marketplace.CredentialStatusRefresh;
        Equal("", marketplace.Token); Equal("", marketplace.Password);
        marketplace.Token = "synthetic_typed_prom";
        marketplace.Selected = marketplace.Connections.Single(c => c.Id == SettingsRozetkaAId);
        await marketplace.CredentialStatusRefresh;
        Equal("synthetic_seller_A", marketplace.Login); Equal("synthetic_typed_A", marketplace.Password);
        Equal("Draft", marketplace.CredentialStatusKind);
        marketplace.Selected = marketplace.Connections.Single(c => c.Id == SettingsRozetkaBId);
        await marketplace.CredentialStatusRefresh;
        Equal("synthetic_seller_B", marketplace.Login); Equal("synthetic_typed_B", marketplace.Password);
        await marketplace.SaveAsync();
        Equal(3, files.Secrets.Saves);
        await AssertSyntheticCredentialsAsync(files, SettingsRozetkaAId, "synthetic_seller_A", "synthetic_typed_A");
        await AssertSyntheticCredentialsAsync(files, SettingsRozetkaBId, "synthetic_seller_B", "synthetic_typed_B");
        Equal("synthetic_typed_prom", (await files.Secrets.Inner.LoadAsync(SettingsPromId))!.Token);
        Equal("", marketplace.Password); Equal("synthetic_seller_B", marketplace.Login);
        Equal("Saved", marketplace.CredentialStatusKind);
    }

    private static async Task RozetkaIncompletePreflightAsync()
    {
        foreach (var scenario in new[] { "login-only", "password-only", "empty-name" })
        {
            using var files = new RozetkaSettingsFiles();
            await files.SeedAsync(SettingsConnection(SettingsPromId, MarketplaceKind.Prom));
            await files.Secrets.Inner.SaveAsync(SettingsPromId, new(Token: "synthetic_original_prom"));
            var before = await File.ReadAllBytesAsync(files.SettingsPath);
            var marketplace = files.CreateMarketplace(); await marketplace.LoadAsync();
            marketplace.Token = "synthetic_pending_prom";
            marketplace.AddRozetkaCommand.Execute(null);
            if (scenario != "password-only") marketplace.Login = "synthetic_incomplete_seller";
            if (scenario != "login-only") marketplace.Password = "synthetic_incomplete_password";
            if (scenario == "empty-name") marketplace.Selected!.Name = " ";
            await MustRejectSettingsAsync(() => marketplace.SaveAsync());
            Equal(0, files.Secrets.Saves); Equal(0, files.Settings.Saves);
            True(before.SequenceEqual(await File.ReadAllBytesAsync(files.SettingsPath)), "Invalid credential draft rewrote connection metadata.");
            Equal("synthetic_original_prom", (await files.Secrets.Inner.LoadAsync(SettingsPromId))!.Token);
            True(await files.Secrets.Inner.LoadAsync(marketplace.Selected!.Id) is null);
            True(!marketplace.IsBusy);
        }
    }

    private static async Task RozetkaPresenceDoesNotOverwriteDraftAsync()
    {
        using var files = new RozetkaSettingsFiles();
        await files.SeedAsync(SettingsConnection(SettingsRozetkaAId));
        await files.Secrets.Inner.SaveAsync(SettingsRozetkaAId, new(Login: "synthetic_saved_seller", Password: "synthetic_saved_password"));
        files.Secrets.BlockNextLoad(SettingsRozetkaAId);
        var marketplace = files.CreateMarketplace();
        var loading = marketplace.LoadAsync();
        await files.Secrets.LoadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        marketplace.Login = "synthetic_typed_while_loading";
        marketplace.Password = "synthetic_typed_password";
        files.Secrets.ReleaseLoad();
        await loading.WaitAsync(TimeSpan.FromSeconds(5));
        Equal("synthetic_typed_while_loading", marketplace.Login);
        Equal("synthetic_typed_password", marketplace.Password);
        Equal("Draft", marketplace.CredentialStatusKind);
        Equal(0, files.Secrets.Saves); Equal(0, files.Settings.Saves);
    }

    private static async Task RozetkaSavedLoginIsNotDraftAsync()
    {
        using var files = new RozetkaSettingsFiles();
        await files.SeedAsync(SettingsConnection(SettingsRozetkaAId));
        await files.Secrets.Inner.SaveAsync(SettingsRozetkaAId, new(Login: "synthetic_saved_seller", Password: "synthetic_saved_password"));
        var secretPath = files.SecretPath(SettingsRozetkaAId);
        var before = await File.ReadAllBytesAsync(secretPath);
        var marketplace = files.CreateMarketplace(); await marketplace.LoadAsync();
        Equal("synthetic_saved_seller", marketplace.Login); Equal("", marketplace.Password);
        Equal("Saved", marketplace.CredentialStatusKind);
        marketplace.Login = "synthetic_saved_seller";
        Equal("Saved", marketplace.CredentialStatusKind);
        True(!await marketplace.SaveAsync(), "An unchanged displayed login was treated as a secret draft.");
        Equal(0, files.Secrets.Saves);
        True(before.SequenceEqual(await File.ReadAllBytesAsync(secretPath)), "No-edit Save changed encrypted credential bytes.");
        Equal("synthetic_saved_seller", marketplace.Login); Equal("", marketplace.Password);
        Equal("Saved", marketplace.CredentialStatusKind);
    }

    private static async Task RozetkaParentCallbackFailureAsync()
    {
        using var files = new RozetkaSettingsFiles();
        await files.SeedAsync(SettingsConnection(SettingsPromId, MarketplaceKind.Prom));
        var marketplace = files.CreateMarketplace();
        var callbacks = 0;
        var parent = files.CreateParent(marketplace, (_, _, _) =>
        {
            callbacks++;
            throw new InvalidOperationException("Synthetic coordinator callback failure.");
        });
        await parent.LoadAsync(); await parent.EnsureMarketplaceLoadedAsync();
        marketplace.AddRozetkaCommand.Execute(null);
        var id = marketplace.Selected!.Id;
        marketplace.Login = "synthetic_durable_seller"; marketplace.Password = "synthetic_durable_password";
        await MustRejectSettingsAsync(() => parent.SaveAsync());
        Equal(1, callbacks); Equal(1, files.Settings.Saves); Equal(1, files.Secrets.Saves);
        True((await files.Settings.Inner.LoadAsync()).Connections.Any(c => c.Id == id));
        await AssertSyntheticCredentialsAsync(files, id, "synthetic_durable_seller", "synthetic_durable_password");
        var reopened = files.CreateMarketplace(); await reopened.LoadAsync();
        reopened.Selected = reopened.Connections.Single(c => c.Id == id); await reopened.CredentialStatusRefresh;
        Equal("Saved", reopened.CredentialStatusKind); Equal("synthetic_durable_seller", reopened.Login);
        True(!parent.IsBusy && !marketplace.IsBusy);
    }

    private static async Task RozetkaConcurrentSaveAsync()
    {
        using var files = new RozetkaSettingsFiles();
        await files.SeedAsync(SettingsConnection(SettingsRozetkaAId));
        var marketplace = files.CreateMarketplace(); await marketplace.LoadAsync();
        marketplace.Login = "synthetic_concurrent_seller"; marketplace.Password = "synthetic_concurrent_password";
        files.Secrets.BlockNextSave();
        var first = marketplace.SaveAsync();
        await files.Secrets.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        True(marketplace.IsBusy && !marketplace.AddRozetkaCommand.CanExecute(null) && !marketplace.TestCommand.CanExecute(null));
        try { await MustRejectSettingsAsync(() => marketplace.SaveAsync()); Equal(1, files.Secrets.Saves); }
        finally { files.Secrets.ReleaseSave(); }
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Equal(1, files.Settings.Saves); Equal(1, files.Secrets.Saves); True(!marketplace.IsBusy);
        await AssertSyntheticCredentialsAsync(files, SettingsRozetkaAId, "synthetic_concurrent_seller", "synthetic_concurrent_password");
    }

    private static async Task RozetkaConcurrentParentSaveAsync()
    {
        using var files = new RozetkaSettingsFiles();
        await files.SeedAsync(SettingsConnection(SettingsRozetkaAId));
        var marketplace = files.CreateMarketplace(); var parent = files.CreateParent(marketplace);
        await parent.LoadAsync(); await parent.EnsureMarketplaceLoadedAsync();
        marketplace.Login = "synthetic_parent_seller"; marketplace.Password = "synthetic_parent_password";
        files.Secrets.BlockNextSave();
        var first = parent.SaveAsync();
        await files.Secrets.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        True(parent.IsBusy);
        try { await MustRejectSettingsAsync(() => parent.SaveAsync()); Equal(1, files.Base.Settings.AppSaves); }
        finally { files.Secrets.ReleaseSave(); }
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Equal(1, files.Base.Settings.AppSaves); Equal(1, files.Settings.Saves); Equal(1, files.Secrets.Saves);
        True(!parent.IsBusy && !marketplace.IsBusy);
    }

    private static async Task RozetkaSettingsWindowRoundTripAsync()
    {
        _xamlCheckpoint = "Rozetka settings real input and durable parent save";
        using var files = new RozetkaSettingsFiles();
        await files.SeedAsync(SettingsConnection(SettingsPromId, MarketplaceKind.Prom));
        await files.Secrets.Inner.SaveAsync(SettingsPromId, new(Token: "synthetic_prom_hidden_in_ui"));
        var marketplace = files.CreateMarketplace(); var parent = files.CreateParent(marketplace);
        await parent.LoadAsync();
        var window = new SettingsWindow { DataContext = parent };
        string id;
        try
        {
            window.ShowMarketplaceTab();
            await parent.EnsureMarketplaceLoadedAsync(); LayoutContent(window); await DrainDispatcherAsync();
            marketplace.AddRozetkaCommand.Execute(null); await marketplace.CredentialStatusRefresh;
            LayoutContent(window); await DrainDispatcherAsync();
            id = marketplace.Selected!.Id;
            var login = (TextBox)window.FindName("RozetkaLoginInput");
            var password = (PasswordBox)window.FindName("RozetkaPasswordInput");
            var indicator = (TextBlock)window.FindName("MarketplaceCredentialIndicator");
            login.Text = "synthetic_ui_seller"; password.Password = "synthetic_ui_password";
            await DrainDispatcherAsync();
            Equal("synthetic_ui_seller", marketplace.Login); Equal("synthetic_ui_password", marketplace.Password);
            Equal("Draft", indicator.Tag?.ToString());
            marketplace.Selected = marketplace.Connections.Single(c => c.Id == SettingsPromId);
            await marketplace.CredentialStatusRefresh; await DrainDispatcherAsync();
            Equal("", password.Password); Equal("", ((PasswordBox)window.FindName("PromTokenInput")).Password);
            marketplace.Selected = marketplace.Connections.Single(c => c.Id == id);
            await marketplace.CredentialStatusRefresh; await DrainDispatcherAsync();
            Equal("synthetic_ui_seller", login.Text); Equal("synthetic_ui_password", password.Password);
            await parent.SaveAsync(); LayoutContent(window); await DrainDispatcherAsync();
            Equal("Saved", indicator.Tag?.ToString()); Equal("synthetic_ui_seller", login.Text); Equal("", password.Password);
            RenderSyntheticContent(window, "rozetka-settings-saved.png");
        }
        finally { window.Close(); }

        var reopenedMarketplace = files.CreateMarketplace(); var reopenedParent = files.CreateParent(reopenedMarketplace);
        await reopenedParent.LoadAsync();
        var reopenedWindow = new SettingsWindow { DataContext = reopenedParent };
        try
        {
            reopenedWindow.ShowMarketplaceTab(); await reopenedParent.EnsureMarketplaceLoadedAsync();
            reopenedMarketplace.Selected = reopenedMarketplace.Connections.Single(c => c.Id == id);
            await reopenedMarketplace.CredentialStatusRefresh; LayoutContent(reopenedWindow); await DrainDispatcherAsync();
            Equal("synthetic_ui_seller", ((TextBox)reopenedWindow.FindName("RozetkaLoginInput")).Text);
            Equal("", ((PasswordBox)reopenedWindow.FindName("RozetkaPasswordInput")).Password);
            var indicator = (TextBlock)reopenedWindow.FindName("MarketplaceCredentialIndicator");
            Equal("Saved", indicator.Tag?.ToString());
            True(indicator.Text.Contains("збережено") && !indicator.Text.Contains("synthetic_ui_password"));
            RenderSyntheticContent(reopenedWindow, "rozetka-settings-reopened.png");
        }
        finally { reopenedWindow.Close(); }
        await AssertSyntheticCredentialsAsync(files, id, "synthetic_ui_seller", "synthetic_ui_password");
        Equal(0, files.Base.Printer.Calls); Equal(0, files.Base.Source.FetchCalls); Equal(0, files.Base.Rozetka.FetchCalls);
    }

    private static async Task RozetkaSavedConfigurationReloadAsync()
    {
        using var files = new RozetkaSettingsFiles();
        await files.SeedAsync(SettingsConnection(SettingsPromId, MarketplaceKind.Prom));
        await files.Secrets.Inner.SaveAsync(SettingsPromId, new(Token: "synthetic_managed_prom"));
        files.Base.Source.Orders = [Order([ReceiptOne]) with
            { Key = new(MarketplaceKind.Prom, SettingsPromId, "managed-prom-order"), Number = "MANAGED-PROM" }];
        var sync = new MarketplaceSyncService([files.Base.Source, files.Base.Rozetka], files.Secrets,
            new DpapiMarketplaceCacheStore(files.CachePath));
        var workspace = new MarketplaceWorkspaceViewModel(files.Settings, sync, files.Base.Links, files.Base.Details,
            files.Base.Dialogs, autoLinkEnabled: false) { CoordinatorManaged = true };
        var main = new MainViewModel(files.Base.ReceiptSource, files.Base.Images, files.Base.Settings,
            files.Base.Authentication, files.Base.History, files.Base.Printer, files.Base.Dialogs, new Logger(), workspace)
            { DateFrom = new(2026, 9, 23), DateTo = new(2026, 9, 23), CoordinatorManaged = true };
        try
        {
            await main.RefreshAllAsync();
            Equal(1, files.Base.Source.FetchCalls); Equal(0, files.Base.Rozetka.FetchCalls);
            var existingOrder = workspace.AllOrderRows.Single();
            var existingReceipt = main.Receipts.Single(r => r.Id == ReceiptOne);
            existingOrder.IsSelectedForLabels = true; existingReceipt.IsSelected = true;
            workspace.SelectedOrder = existingOrder;

            // The editor uses fresh JSON objects. Its new connection cannot leak into the workspace's old config by reference.
            var editor = files.CreateMarketplace(); var parent = files.CreateParent(editor);
            await parent.LoadAsync(); await parent.EnsureMarketplaceLoadedAsync();
            editor.AddRozetkaCommand.Execute(null);
            var addedId = editor.Selected!.Id;
            editor.Login = "synthetic_managed_rozetka"; editor.Password = "synthetic_managed_password";
            await parent.SaveAsync();
            files.Base.Rozetka.Orders = [Order([ReceiptTwo]) with
                { Key = new(MarketplaceKind.Rozetka, addedId, "managed-rz-order"), Number = "MANAGED-RZ" }];
            await workspace.EnsureAttachedAsync(); await workspace.SyncAsync();
            Equal(0, files.Base.Rozetka.FetchCalls); // Reproduce the still-attached old configuration before the settings hook.
            Equal(1, workspace.AllOrderRows.Count);

            var invalidatedWithRowsPreserved = false;
            void ObservedConfigurationInvalidation(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName != nameof(workspace.Status) || !workspace.Status.StartsWith("Налаштування маркетплейсів збережено")) return;
                True(ReferenceEquals(existingOrder, workspace.AllOrderRows.Single()));
                True(ReferenceEquals(existingOrder, workspace.SelectedOrder));
                True(existingOrder.IsSelectedForLabels && existingReceipt.IsSelected);
                invalidatedWithRowsPreserved = true;
            }
            workspace.PropertyChanged += ObservedConfigurationInvalidation;
            files.Base.Dialogs.SettingsChanged = true;
            await ExecuteAsync(main.SettingsCommand);
            workspace.PropertyChanged -= ObservedConfigurationInvalidation;
            True(invalidatedWithRowsPreserved, "Returning saved settings did not invalidate the cached marketplace configuration.");
            Equal(0, files.Base.Rozetka.FetchCalls);
            await main.RefreshAllAsync();
            Equal(1, files.Base.Rozetka.FetchCalls);
            True(workspace.AllOrderRows.Any(r => r.Key.ConnectionId == addedId && r.Number == "MANAGED-RZ"));
            True(ReferenceEquals(existingOrder, workspace.AllOrderRows.Single(r => r.Key.ConnectionId == SettingsPromId)));
            True(existingOrder.IsSelectedForLabels && existingReceipt.IsSelected);
            Equal(0, files.Base.Printer.Calls); Equal(0, files.Base.Links.Saves);
        }
        finally { main.CancelPendingOperations(); main.AllReceiptsTab.Dispose(); main.OrdersReceiptsTab.Dispose(); }
    }

    private static MarketplaceConnection SettingsConnection(string id, MarketplaceKind kind = MarketplaceKind.Rozetka) =>
        new() { Id = id, Marketplace = kind, Name = "Synthetic " + kind + " " + id[^2..], Enabled = true };

    private static async Task MustRejectSettingsAsync(Func<Task> action)
    {
        var rejected = false;
        try { await action(); }
        catch (InvalidOperationException) { rejected = true; }
        True(rejected, "Expected invalid or concurrent settings operation to be rejected.");
    }

    private static async Task AssertSyntheticCredentialsAsync(RozetkaSettingsFiles files, string id, string login, string password)
    {
        var secret = await new DpapiMarketplaceSecretStore(files.SecretsDirectory).LoadAsync(id);
        Equal(login, secret!.Login); Equal(password, secret.Password);
        var protectedBytes = await File.ReadAllBytesAsync(files.SecretPath(id));
        foreach (var marker in new[] { login, password })
        {
            True(!Encoding.UTF8.GetString(protectedBytes).Contains(marker), "Synthetic credential was persisted as plaintext.");
            True(!Encoding.Unicode.GetString(protectedBytes).Contains(marker), "Synthetic credential was persisted as plaintext.");
            True(!(await File.ReadAllTextAsync(files.SettingsPath)).Contains(marker), "Connection metadata included credential contents.");
        }
    }

    private sealed class RozetkaSettingsFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "cbp-rozetka-settings-test-" + Guid.NewGuid().ToString("N"));
        public Fixture Base { get; } = new();
        public string SettingsPath => Path.Combine(_directory, "marketplace-settings.json");
        public string CachePath => Path.Combine(_directory, "orders.dpapi");
        public string SecretsDirectory => Path.Combine(_directory, "Secrets");
        public CountedMarketplaceSettings Settings { get; }
        public GatedMarketplaceSecrets Secrets { get; }
        public RozetkaSettingsFiles()
        {
            Settings = new(new JsonMarketplaceSettingsStore(SettingsPath));
            Secrets = new(new DpapiMarketplaceSecretStore(SecretsDirectory));
        }
        public string SecretPath(string id) => Path.Combine(SecretsDirectory, "marketplace-" + id + ".dpapi");
        public Task SeedAsync(params MarketplaceConnection[] connections) =>
            Settings.Inner.SaveAsync(new() { Connections = connections.ToList() });
        public MarketplaceSettingsViewModel CreateMarketplace() => new(Settings, Secrets,
            new MarketplaceSyncService([], Secrets, new DpapiMarketplaceCacheStore(CachePath)));
        public SettingsViewModel CreateParent(MarketplaceSettingsViewModel marketplace, Func<AppSettings, bool, bool, Task>? callback = null) =>
            new(Base.Settings, Base.Authentication, Base.Images, Base.Printer, new Logger(), marketplace, callback);
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }

    private sealed class CountedMarketplaceSettings(JsonMarketplaceSettingsStore inner) : IMarketplaceSettingsStore
    {
        public JsonMarketplaceSettingsStore Inner { get; } = inner;
        public int Saves;
        public Task<MarketplaceSettings> LoadAsync(CancellationToken ct = default) => Inner.LoadAsync(ct);
        public Task SaveAsync(MarketplaceSettings settings, CancellationToken ct = default) { Saves++; return Inner.SaveAsync(settings, ct); }
    }

    private sealed class GatedMarketplaceSecrets(DpapiMarketplaceSecretStore inner) : IMarketplaceSecretStore
    {
        public DpapiMarketplaceSecretStore Inner { get; } = inner;
        public int Saves;
        private string? _loadId;
        private TaskCompletionSource? _loadGate, _saveGate;
        public TaskCompletionSource LoadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void BlockNextLoad(string id) { _loadId = id; _loadGate = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public void ReleaseLoad() => _loadGate?.TrySetResult();
        public void BlockNextSave() => _saveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseSave() => _saveGate?.TrySetResult();
        public async Task<MarketplaceCredentials?> LoadAsync(string id, CancellationToken ct = default)
        {
            var value = await Inner.LoadAsync(id, ct);
            if (_loadId == id && _loadGate is { } gate)
            {
                _loadId = null; LoadEntered.TrySetResult(); await gate.Task.WaitAsync(ct);
            }
            return value;
        }
        public async Task SaveAsync(string id, MarketplaceCredentials credentials, CancellationToken ct = default)
        {
            Saves++;
            if (_saveGate is { } gate) { SaveEntered.TrySetResult(); await gate.Task.WaitAsync(ct); }
            await Inner.SaveAsync(id, credentials, ct);
        }
    }
}
