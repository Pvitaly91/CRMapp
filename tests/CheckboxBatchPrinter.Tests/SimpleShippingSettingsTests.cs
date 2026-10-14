using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Services;
using CheckboxBatchPrinter.Views;
using CheckboxBatchPrinter.ViewModels;
using static CheckboxBatchPrinter.Tests.ShippingLabelsTests;

namespace CheckboxBatchPrinter.Tests;

internal static class SimpleShippingSettingsTests
{
    private const string ShopA = "20000000000000000000000000000001", ShopB = "20000000000000000000000000000002";
    private const string Account = "20000000000000000000000000000003", Seller = "20000000000000000000000000000004";
    private const string OldKey = "synthetic_np_old_key", NewKey = "synthetic_np_new_key";
    private const string PrinterName = "Synthetic label printer", FormatName = "Synthetic 101.5mm";
    private static MarketplaceConnection Prom(string id = ShopA) => new()
    { Id = id, Marketplace = MarketplaceKind.Prom, Enabled = true, Name = id == ShopA ? "Synthetic Prom A" : "Synthetic Prom B" };
    private static ShippingLabelSettings Original => new()
    {
        Print = new() { PrinterName = PrinterName, DriverFormat = FormatName, WidthMm = 101.5, HeightMm = 101.5,
            ScalePercent = 92, OffsetXmm = 1.25, OffsetYmm = -.5, Landscape = true, Copies = 2 },
        RozetkaDeliveryToken = "synthetic_rd_preserved", RozetkaDeliveryConnectionId = "20000000000000000000000000000005",
        NovaPoshtaSellerConnectionId = Seller
    };
    private static ShippingLabelSettings Bound => Original with
    { NovaPoshtaConnections = [new(Account, "Synthetic account", OldKey)], NovaPoshtaStoreBindings = [new(ShopA, Account)] };

    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("simple NP blank key preserves settings without choosing an account", Blank),
        ("simple NP private key rotates in place and same key keeps identity", PrivateRotation),
        ("simple NP shared account key updates in place and retains all shop bindings", SharedRotation),
        ("simple NP unbound shop reuses the sole existing account without duplicating it", Unbound),
        ("simple NP rejects invalid keys shops and account overflow without exposing secrets", Invalid),
        ("DPAPI simple NP key and explicit shop binding survive restart without changing label history", Persistence),
        ("STA simple label settings show one masked NP field and collapse advanced options", () => MarketplaceViewModelTests.StaAsync(SimplifiedUi)),
        ("STA simple label settings reuse the existing NP account for the selected Prom shop", () => MarketplaceViewModelTests.StaAsync(SelectedShopUi)),
        ("STA NP verify follows selected order shop and rejects a programmatic mismatched click without saving", () => MarketplaceViewModelTests.StaAsync(VerificationShopUi)),
        ("STA conflicting NP drafts cannot silently overwrite one account and identical drafts reuse it", () => MarketplaceViewModelTests.StaAsync(DraftsUi)),
        ("STA shipping credential indicators distinguish saved tokens drafts and shops without exposing keys", () => MarketplaceViewModelTests.StaAsync(CredentialIndicatorsUi)),
        ("STA simple label settings closing without save writes no typed key", () => MarketplaceViewModelTests.StaAsync(CancelUi)),
        ("STA simple label settings blank key preserves latest credentials and collapsed print options", () => MarketplaceViewModelTests.StaAsync(PreserveUi)),
        ("STA simple NP key can be saved before selecting a label printer", () => MarketplaceViewModelTests.StaAsync(NoPrinterUi)),
        ("STA Rozetka Delivery token rotation keeps its connection and print history identities", () => MarketplaceViewModelTests.StaAsync(DeliveryTokenRotationUi))
    ];

    private static void OtherSettingsUnchanged(ShippingLabelSettings before, ShippingLabelSettings after)
    {
        Check(before.Print == after.Print && before.RozetkaDeliveryToken == after.RozetkaDeliveryToken &&
            before.RozetkaDeliveryConnectionId == after.RozetkaDeliveryConnectionId &&
            before.NovaPoshtaSellerConnectionId == after.NovaPoshtaSellerConnectionId,
            "Saving one NP key changed other shipping settings.");
    }
    private static Task Blank()
    {
        var original = Bound;
        foreach (var key in new[] { "", " ", "\t\r\n" })
            Check(ReferenceEquals(original, NovaPoshtaKeySettings.Apply(original, null, key)),
                "Blank key changed existing settings or required a shop.");
        Check(NovaPoshtaKeySettings.HasSavedKey(original, ShopA) && !NovaPoshtaKeySettings.HasSavedKey(original, ShopB) &&
            !NovaPoshtaKeySettings.HasSavedKey(original, null), "Saved-key status guessed an unbound account.");
        Check(!NovaPoshtaKeySettings.HasSavedKey(original with
            { NovaPoshtaConnections = [original.NovaPoshtaConnections.Single() with { ApiKey = "" }] }, ShopA),
            "An empty stored key is reported as connected.");
        return Task.CompletedTask;
    }
    private static Task PrivateRotation()
    {
        var original = Bound;
        var same = NovaPoshtaKeySettings.Apply(original, Prom(), " " + OldKey + " ");
        Check(same.NovaPoshtaConnections.Single() == original.NovaPoshtaConnections.Single() &&
            same.NovaPoshtaStoreBindings.SequenceEqual(original.NovaPoshtaStoreBindings), "Same key changed stable account identity.");
        var rotated = NovaPoshtaKeySettings.Apply(original, Prom(), NewKey);
        Check(rotated.NovaPoshtaConnections.Single() == (original.NovaPoshtaConnections.Single() with { ApiKey = NewKey }) &&
            rotated.NovaPoshtaStoreBindings.SequenceEqual(original.NovaPoshtaStoreBindings), "Private rotation replaced the account or lost its binding.");
        Check(original.NovaPoshtaConnections.Single().ApiKey == OldKey, "Apply mutated its input.");
        OtherSettingsUnchanged(original, rotated);
        return Task.CompletedTask;
    }
    private static Task SharedRotation()
    {
        var original = Bound with { NovaPoshtaStoreBindings = [new(ShopA, Account), new(ShopB, Account)] };
        var same = NovaPoshtaKeySettings.Apply(original, Prom(), OldKey);
        Check(same.NovaPoshtaConnections.Count == 1 && same.NovaPoshtaStoreBindings.SequenceEqual(original.NovaPoshtaStoreBindings),
            "Entering the same shared key forked an account.");
        var updated = NovaPoshtaKeySettings.Apply(original, Prom(), NewKey);
        var binding = updated.NovaPoshtaStoreBindings.Single(b => b.MarketplaceConnectionId == ShopA);
        Check(updated.NovaPoshtaConnections.Count == 1 && binding.NovaPoshtaConnectionId == Account &&
            updated.NovaPoshtaConnections.Single(c => c.Id == binding.NovaPoshtaConnectionId).ApiKey == NewKey &&
            updated.NovaPoshtaStoreBindings.SequenceEqual(original.NovaPoshtaStoreBindings) &&
            updated.NovaPoshtaConnections.Single().Name == original.NovaPoshtaConnections.Single().Name,
            "Rotating a shared key created a new account or changed stable shop bindings.");
        Check(original.NovaPoshtaConnections.Single().ApiKey == OldKey, "Shared key rotation mutated its input.");
        OtherSettingsUnchanged(original, updated);
        return Task.CompletedTask;
    }
    private static Task Unbound()
    {
        var original = Original with { NovaPoshtaConnections = [new(Account, "Existing carrier account", OldKey)] };
        foreach (var key in new[] { OldKey, NewKey })
        {
            var updated = NovaPoshtaKeySettings.Apply(original, Prom(), key);
            var binding = updated.NovaPoshtaStoreBindings.Single();
            Check(updated.NovaPoshtaConnections.Count == 1 && binding == new NovaPoshtaStoreBinding(ShopA, Account) &&
                updated.NovaPoshtaConnections.Single() == (original.NovaPoshtaConnections.Single() with { ApiKey = key }),
                "An unbound shop created another carrier account instead of reusing the only existing connection.");
            var again = NovaPoshtaKeySettings.Apply(updated, Prom(ShopB), key);
            Check(again.NovaPoshtaConnections.Count == 1 && again.NovaPoshtaStoreBindings.Count == 2 &&
                again.NovaPoshtaStoreBindings.All(item => item.NovaPoshtaConnectionId == Account),
                "Binding a second store duplicated the carrier account.");
            OtherSettingsUnchanged(original, updated);
        }
        return Task.CompletedTask;
    }
    private static Task Invalid()
    {
        static void Rejected(ShippingLabelSettings settings, MarketplaceConnection? shop, string key)
        {
            try { NovaPoshtaKeySettings.Apply(settings, shop, key); }
            catch (Exception ex)
            {
                Check(!ex.ToString().Contains(key, StringComparison.Ordinal) && ex.Message.Length > 0,
                    "Validation exposed the key or returned no explanation.");
                return;
            }
            throw new Exception("Invalid simplified NP settings were accepted.");
        }
        var original = Bound;
        foreach (var key in new[] { "secret invalid key", "secret\ninvalid", new string('S', 129) }) Rejected(original, Prom(), key);
        Rejected(original, null, NewKey);
        Rejected(original, new() { Id = ShopA, Enabled = false, Marketplace = MarketplaceKind.Prom }, NewKey);
        Rejected(original, new() { Id = ShopA, Enabled = true, Marketplace = MarketplaceKind.Rozetka }, NewKey);
        Rejected(original, new() { Id = "invalid-shop-id", Enabled = true, Marketplace = MarketplaceKind.Prom }, NewKey);
        var full = Original with { NovaPoshtaConnections = Enumerable.Range(1, 30)
            .Select(i => new NovaPoshtaConnection(i.ToString("x32"), "Synthetic " + i, OldKey)).ToArray() };
        Rejected(full, Prom(), NewKey);
        Rejected(full with { NovaPoshtaConnections = full.NovaPoshtaConnections.Take(2).ToArray() }, Prom(), NewKey);
        var boundFull = full with { NovaPoshtaStoreBindings = [new(ShopA, full.NovaPoshtaConnections[0].Id)] };
        var rotatedFull = NovaPoshtaKeySettings.Apply(boundFull, Prom(), NewKey);
        Check(rotatedFull.NovaPoshtaConnections.Count == 30 && rotatedFull.NovaPoshtaConnections[0].ApiKey == NewKey &&
            rotatedFull.NovaPoshtaConnections.Skip(1).SequenceEqual(boundFull.NovaPoshtaConnections.Skip(1)) &&
            rotatedFull.NovaPoshtaStoreBindings.SequenceEqual(boundFull.NovaPoshtaStoreBindings),
            "Rotating an explicitly bound legacy account merged, deleted, or duplicated other accounts.");
        Check(original.NovaPoshtaConnections.Single().ApiKey == OldKey && full.NovaPoshtaConnections.Count == 30,
            "Failed validation mutated the settings.");
        return Task.CompletedTask;
    }
    private static async Task Persistence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cbp-simple-np-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "shipping.dpapi");
            var historyPath = Path.Combine(directory, "label-attempts.dpapi");
            var history = new DpapiLabelHistoryStore(historyPath);
            await history.SaveAsync(new("synthetic-attempt", "synthetic-job", PrinterName, DateTimeOffset.UtcNow,
                LabelSubmissionState.Submitted, [new(ShippingCarrier.NovaPoshta, Account, "20400000000001", "synthetic-document", 1, "synthetic-fingerprint")]));
            var historyBefore = await File.ReadAllBytesAsync(historyPath);
            var first = Bound with { NovaPoshtaStoreBindings = [new(ShopA, Account), new(ShopB, Account)] };
            await new DpapiShippingSettingsStore(path).SaveAsync(first);
            var restarted = await new DpapiShippingSettingsStore(path).LoadAsync();
            var id = restarted.NovaPoshtaConnections.Single().Id;
            await new DpapiShippingSettingsStore(path).SaveAsync(NovaPoshtaKeySettings.Apply(restarted, Prom(), NewKey));
            var restored = await new DpapiShippingSettingsStore(path).LoadAsync();
            Check(restored.NovaPoshtaConnections.Single().Id == id && restored.NovaPoshtaConnections.Single().ApiKey == NewKey &&
                restored.NovaPoshtaStoreBindings.SequenceEqual(first.NovaPoshtaStoreBindings), "Restart lost the shared key or stable bindings.");
            OtherSettingsUnchanged(Original, restored);
            var row = new WaybillRowViewModel(new(ShippingCarrier.NovaPoshta, id, "Synthetic account", "synthetic-document",
                "20400000000001", null, "", "", "", null, 1));
            row.ApplyAccountAliases(restored); row.ApplyHistory(await history.LoadAsync());
            Check(row.HasPrintedLabel, "Rotating the key orphaned the existing waybill print history.");
            Check(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)).Contains(NewKey, StringComparison.Ordinal) &&
                (await File.ReadAllBytesAsync(historyPath)).SequenceEqual(historyBefore), "Saved key is plaintext or label history changed.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class MemoryStore(ShippingLabelSettings initial) : IShippingSettingsStore
    {
        public ShippingLabelSettings Value = initial;
        public int Saves;
        public Task<ShippingLabelSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(Value);
        public Task SaveAsync(ShippingLabelSettings settings, CancellationToken ct = default)
        { Value = settings; Saves++; return Task.CompletedTask; }
    }
    private sealed class Printer(bool available = true) : ILabelPrinter
    {
        public int Inspections;
        public IReadOnlyList<string> Printers() => available ? [PrinterName] : [];
        public IReadOnlyList<LabelDriverFormat> Formats(string printer) => available ? [new(FormatName, 101.5, 101.5)] : [];
        public LabelPrinterGeometry Inspect(LabelPrintSettings settings)
        { settings.Validate(); Inspections++; return ShippingLabelsTests.Geometry; }
        public Task<int?> SubmitAsync(LabelPrintBatch batch, string jobName, Func<Task> beforeTransfer,
            Func<int, Task> jobCreated, CancellationToken ct = default) => throw new Exception("Settings submitted a print job.");
    }
    private static MarketplaceSettings Market(bool multiple = false) => new()
    {
        Connections = [Prom(), .. (multiple ? new[] { Prom(ShopB) } : []),
            new() { Id = Seller, Name = "Synthetic Seller", Marketplace = MarketplaceKind.Rozetka, Enabled = true },
            new() { Id = "20000000000000000000000000000006", Name = "Disabled Prom", Marketplace = MarketplaceKind.Prom, Enabled = false }]
    };
    private static T Field<T>(LabelSettingsWindow window, string name) =>
        (T)(typeof(LabelSettingsWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
            ?? throw new Exception("Missing simplified settings field " + name));
    private static IEnumerable<DependencyObject> Nodes(DependencyObject parent)
    {
        yield return parent;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            foreach (var node in Nodes(child)) yield return node;
    }
    private static async Task Save(LabelSettingsWindow window, MemoryStore store)
    {
        var button = Nodes(window).OfType<Button>().Single(b => Equals(b.Content, "Зберегти"));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Yield();
        // An unshown window cannot set DialogResult; persistence must already have completed.
        Check(store.Saves == 1, "Saving the real settings window did not persist exactly once.");
    }
    private static Task SimplifiedUi()
    {
        var store = new MemoryStore(Bound); var printer = new Printer();
        var window = new LabelSettingsWindow(store, printer, store.Value, Market());
        try
        {
            var advanced = Field<Expander>(window, "_advanced");
            Check(!advanced.IsExpanded && Field<ComboBox>(window, "_npStores").Visibility == Visibility.Collapsed,
                "Single-shop settings require advanced options or unnecessary shop selection.");
            var advancedNodes = Nodes(advanced).ToHashSet();
            foreach (var field in new[] { "_scale", "_x", "_y", "_landscape", "_token", "_seller" })
                Check(advancedNodes.Contains(Field<FrameworkElement>(window, field)), "An advanced shipping option remains in the primary form.");
            Check(!advancedNodes.Contains(Field<PasswordBox>(window, "_npKey")) &&
                Field<PasswordBox>(window, "_npKey").Password == "" && !advancedNodes.Contains(Field<TextBox>(window, "_copies")),
                "Primary NP key is missing, exposes the stored key, or copies moved into advanced settings.");
            var nodes = Nodes(window).ToArray();
            Check(nodes.OfType<PasswordBox>().Where(p => !advancedNodes.Contains(p)).SequenceEqual([Field<PasswordBox>(window, "_npKey")]) &&
                advancedNodes.OfType<PasswordBox>().SequenceEqual([Field<PasswordBox>(window, "_token")]),
                "The settings window contains a duplicate NP key input outside its single primary editor.");
            var verify = nodes.OfType<Button>().Where(b => Equals(b.Content, "Перевірка накладної…")).ToArray();
            Check(verify.Length == 1 && !advancedNodes.Contains(verify[0]) &&
                !advancedNodes.OfType<Button>().Any(b => b.Content?.ToString() is { } text &&
                    (text.Contains("NP", StringComparison.OrdinalIgnoreCase) || text.Contains("діагност", StringComparison.OrdinalIgnoreCase) ||
                     text.Contains("Перевірка накладної", StringComparison.OrdinalIgnoreCase))),
                "Advanced settings still expose a duplicate NP account editor or diagnostic entry point.");
            Check(advancedNodes.OfType<ComboBox>().SequenceEqual([Field<ComboBox>(window, "_seller")]),
                "Advanced settings still contain a direct NP account or store-binding selector.");
            Check(store.Saves == 0 && printer.Inspections == 0, "Opening simplified settings writes credentials or checks a print job.");
            if (Environment.GetEnvironmentVariable("CHECKBOX_TEST_RENDER_DIR") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(660, 650)); content.Arrange(new Rect(0, 0, 660, 650)); content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(660, 650, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, "simple-label-settings.png")); encoder.Save(file);
            }
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    }
    private static async Task SelectedShopUi()
    {
        var store = new MemoryStore(Bound); var printer = new Printer();
        var window = new LabelSettingsWindow(store, printer, store.Value, Market(multiple: true));
        try
        {
            var shops = Field<ComboBox>(window, "_npStores");
            Check(shops.Visibility == Visibility.Visible && shops.Items.Count == 2, "Shop picker includes disabled Prom or Seller stores.");
            shops.SelectedValue = ShopB;
            Field<PasswordBox>(window, "_npKey").Password = NewKey;
            await Save(window, store);
            var binding = store.Value.NovaPoshtaStoreBindings.Single(b => b.MarketplaceConnectionId == ShopB);
            Check(binding.NovaPoshtaConnectionId == Account && store.Value.NovaPoshtaConnections.Single().ApiKey == NewKey &&
                store.Value.NovaPoshtaStoreBindings.Single(b => b.MarketplaceConnectionId == ShopA).NovaPoshtaConnectionId == Account,
                "Settings created a duplicate NP account or changed an existing shop binding.");
            OtherSettingsUnchanged(Bound, store.Value);
        }
        finally { window.Close(); }
    }
    private sealed class NoDiagnosticAccess : HttpMessageHandler, ILabelPdfInspector
    {
        public int Requests, Inspections;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Requests++; throw new Exception("Mismatched shop verification contacted the API."); }
        public Task<LabelPdfInspection> InspectAsync(byte[] pdf, CancellationToken ct = default)
        { Inspections++; throw new Exception("Mismatched shop verification inspected a PDF."); }
    }
    private static async Task VerificationShopUi()
    {
        var original = Bound;
        var store = new MemoryStore(original);
        var access = new NoDiagnosticAccess();
        using var http = new HttpClient(access);
        var order = Order("selected", ShippingCarrier.NovaPoshta, "20400000000001") with
            { Key = new(MarketplaceKind.Prom, ShopA, "selected") };
        var diagnostics = new NovaPoshtaDiagnostics(new(NovaPoshtaRequestPolicyTests.FastTransport(http)), access);
        var window = new LabelSettingsWindow(store, new Printer(), store.Value, Market(multiple: true), diagnostics, new(), () => order);
        try
        {
            var shops = Field<ComboBox>(window, "_npStores");
            var verify = Nodes(window).OfType<Button>().Single(b => Equals(b.Content, "Перевірка накладної…"));
            Check(Equals(shops.SelectedValue, ShopA) && verify.IsEnabled, "Selected order's matching Prom shop cannot open verification.");
            shops.SelectedValue = ShopB;
            Field<PasswordBox>(window, "_npKey").Password = NewKey;
            Check(!verify.IsEnabled, "Switching the key editor to another shop left verification enabled.");
            verify.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Yield();
            Check(store.Saves == 0 && ReferenceEquals(store.Value, original) &&
                store.Value.NovaPoshtaConnections.Single().ApiKey == OldKey && access.Requests == 0 && access.Inspections == 0 &&
                window.OwnedWindows.Count == 0 && window.IsEnabled,
                "Programmatic mismatched verification saved a key, contacted API/PDF, or opened a diagnostic window.");
            shops.SelectedValue = ShopA;
            Check(verify.IsEnabled && store.Saves == 0 && access.Requests == 0,
                "Returning to the selected order's shop did not restore the manual verification action.");
        }
        finally { window.Close(); }
    }
    private static async Task DraftsUi()
    {
        var store = new MemoryStore(Original); var window = new LabelSettingsWindow(store, new Printer(), store.Value, Market(multiple: true));
        try
        {
            var shops = Field<ComboBox>(window, "_npStores"); var key = Field<PasswordBox>(window, "_npKey");
            shops.SelectedValue = ShopA; key.Password = OldKey;
            shops.SelectedValue = ShopB;
            Check(key.Password == "", "Typed key carried into another shop.");
            key.Password = NewKey; shops.SelectedValue = ShopA;
            Check(key.Password == OldKey, "Changing shops discarded the first shop's draft.");
            Nodes(window).OfType<Button>().Single(b => Equals(b.Content, "Зберегти")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Yield();
            Check(store.Saves == 0 && store.Value.NovaPoshtaConnections.Count == 0 &&
                Field<TextBlock>(window, "_status").Text.Contains("різні ключі", StringComparison.Ordinal),
                "Conflicting drafts silently overwrote the same connection or created two carrier accounts.");
            key.Password = NewKey;
            await Save(window, store);
            Check(store.Value.NovaPoshtaConnections.Single().ApiKey == NewKey && store.Value.NovaPoshtaStoreBindings.Count == 2 &&
                store.Value.NovaPoshtaStoreBindings.All(binding => binding.NovaPoshtaConnectionId == store.Value.NovaPoshtaConnections.Single().Id),
                "Identical drafts created multiple accounts or lost store bindings.");
        }
        finally { window.Close(); }
    }
    private static Task CredentialIndicatorsUi()
    {
        var store = new MemoryStore(Bound); var sellerSecrets = new IndicatorSellerSecrets();
        var window = new LabelSettingsWindow(store, new Printer(), store.Value, Market(multiple: true), marketplaceSecrets: sellerSecrets);
        try
        {
            var shops = Field<ComboBox>(window, "_npStores"); var key = Field<PasswordBox>(window, "_npKey");
            var status = Field<TextBlock>(window, "_npStatus"); var tokenStatus = Field<TextBlock>(window, "_tokenStatus");
            Check(Field<TextBlock>(window, "_sellerStatus").Tag?.ToString() == "Saved" &&
                Field<TextBlock>(window, "_sellerStatus").Text.Contains("API-токен"), "Saved Seller API-token is not marked.");
            shops.SelectedValue = ShopA; Check(status.Tag?.ToString() == "Saved" && key.Password.Length == 0, "Stored NP key is not visibly marked.");
            Check(tokenStatus.Tag?.ToString() == "Saved" && tokenStatus.Text.Contains("Rozetka Delivery"), "Stored RD token is not marked while advanced settings are collapsed.");
            key.Password = NewKey; Check(status.Tag?.ToString() == "Draft", "Typed NP key was mislabeled as saved.");
            shops.SelectedValue = ShopB; Check(status.Tag?.ToString() == "Missing", "Unbound shop inherited another shop's indicator.");
            shops.SelectedValue = ShopA; Check(status.Tag?.ToString() == "Draft", "NP draft indicator did not survive a shop switch.");
            Field<PasswordBox>(window, "_token").Password = "synthetic_new_rd_token";
            Check(tokenStatus.Tag?.ToString() == "Draft", "New RD token was mislabeled as saved.");
            var text = string.Join(" ", Nodes((DependencyObject)window.Content).OfType<TextBlock>().Select(t => t.Text));
            Check(!text.Contains(OldKey) && !text.Contains(NewKey) && !text.Contains(store.Value.RozetkaDeliveryToken), "A credential indicator exposed secret bytes.");
            Check(store.Saves == 0, "Opening status indicators wrote settings.");
            sellerSecrets.Value = new(Login: "synthetic_legacy_login", Password: "synthetic_legacy_password");
            var seller = Field<ComboBox>(window, "_seller");
            seller.SelectedValue = null; seller.SelectedValue = Seller;
            Check(Field<TextBlock>(window, "_sellerStatus").Tag?.ToString() == "Missing" &&
                Field<TextBlock>(window, "_sellerStatus").Text.Contains("Безпека API"),
                "Legacy Seller credentials were mislabeled as a configured API-token.");
            sellerSecrets.Value = new(Token: "токен");
            seller.SelectedValue = null; seller.SelectedValue = Seller;
            Check(Field<TextBlock>(window, "_sellerStatus").Tag?.ToString() == "Missing",
                "A malformed Seller token was marked as configured although the transport would reject it.");
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    }
    private sealed class IndicatorSellerSecrets : IMarketplaceSecretStore
    {
        public MarketplaceCredentials Value { get; set; } = new(Token: "synthetic_private_seller_token");
        public Task<MarketplaceCredentials?> LoadAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<MarketplaceCredentials?>(Value);
        public Task SaveAsync(string id, MarketplaceCredentials credentials, CancellationToken ct = default) =>
            throw new Exception("Reading Seller status wrote credentials.");
    }
    private static Task CancelUi()
    {
        var original = Bound; var store = new MemoryStore(original); var printer = new Printer();
        var window = new LabelSettingsWindow(store, printer, original, Market());
        Check(Nodes(window).OfType<Button>().Single(b => Equals(b.Content, "Скасувати")).IsCancel,
            "Cancel is missing from simplified settings.");
        Field<PasswordBox>(window, "_npKey").Password = NewKey;
        window.Close();
        Check(store.Saves == 0 && ReferenceEquals(store.Value, original) && printer.Inspections == 0,
            "Closing settings persisted a typed key or changed the printer.");
        return Task.CompletedTask;
    }
    private static async Task PreserveUi()
    {
        var original = Bound; var store = new MemoryStore(original);
        var window = new LabelSettingsWindow(store, new Printer(), original, Market());
        try
        {
            var latest = original with { Print = original.Print with { ScalePercent = 90, OffsetXmm = 1.75, Copies = 3 },
                RozetkaDeliveryToken = "synthetic_rd_fresh", NovaPoshtaSellerConnectionId = "20000000000000000000000000000007",
                NovaPoshtaConnections = [original.NovaPoshtaConnections.Single() with { ApiKey = NewKey }] };
            store.Value = latest;
            Check(!Field<Expander>(window, "_advanced").IsExpanded, "Advanced settings unexpectedly expanded.");
            await Save(window, store);
            Check(store.Value.Print == latest.Print && store.Value.NovaPoshtaConnections.SequenceEqual(latest.NovaPoshtaConnections) &&
                store.Value.NovaPoshtaStoreBindings.SequenceEqual(latest.NovaPoshtaStoreBindings),
                "Blank primary key discarded a newer key or changed collapsed print options.");
            OtherSettingsUnchanged(latest, store.Value);
        }
        finally { window.Close(); }
    }
    private static async Task NoPrinterUi()
    {
        var original = new ShippingLabelSettings(); var store = new MemoryStore(original); var printer = new Printer(available: false);
        var window = new LabelSettingsWindow(store, printer, original, Market());
        try
        {
            Field<PasswordBox>(window, "_npKey").Password = NewKey;
            await Save(window, store);
            Check(store.Value.NovaPoshtaConnections.Single().ApiKey == NewKey &&
                store.Value.NovaPoshtaStoreBindings.Single().MarketplaceConnectionId == ShopA &&
                store.Value.Print == original.Print && printer.Inspections == 0, "Saving a key requires a printer or invents a printer configuration.");
        }
        finally { window.Close(); }
    }
    private static async Task DeliveryTokenRotationUi()
    {
        var original = Bound;
        var store = new MemoryStore(original);
        var window = new LabelSettingsWindow(store, new Printer(), original, Market());
        try
        {
            Field<PasswordBox>(window, "_token").Password = "synthetic_rotated_rd_token";
            await Save(window, store);
            Check(store.Value.RozetkaDeliveryToken == "synthetic_rotated_rd_token" &&
                store.Value.RozetkaDeliveryConnectionId == original.RozetkaDeliveryConnectionId &&
                store.Value.NovaPoshtaConnections.SequenceEqual(original.NovaPoshtaConnections) &&
                store.Value.NovaPoshtaStoreBindings.SequenceEqual(original.NovaPoshtaStoreBindings) &&
                store.Value.Print == original.Print, "RD token rotation changed a connection identity or unrelated settings.");
            var row = new WaybillRowViewModel(new(ShippingCarrier.RozetkaDelivery, store.Value.RozetkaDeliveryConnectionId,
                "Synthetic Delivery", "synthetic-rd-document", "synthetic-rd-waybill", null, "", "", "", null, 1));
            row.ApplyHistory([new("old-rd-attempt", "old-rd-job", PrinterName, DateTimeOffset.UtcNow,
                LabelSubmissionState.Submitted, [new(ShippingCarrier.RozetkaDelivery, original.RozetkaDeliveryConnectionId,
                    "synthetic-rd-waybill", "synthetic-rd-document", 1, "synthetic-rd-fingerprint")])]);
            Check(row.HasPrintedLabel, "RD token rotation orphaned carrier print history.");
            var reopened = new LabelSettingsWindow(store, new Printer(), store.Value, Market());
            try
            {
                Check(Field<PasswordBox>(reopened, "_token").Password == "", "Reopening exposed the saved Delivery token.");
                store.Saves = 0;
                await Save(reopened, store);
                Check(store.Value.RozetkaDeliveryToken == "synthetic_rotated_rd_token" &&
                    store.Value.RozetkaDeliveryConnectionId == original.RozetkaDeliveryConnectionId,
                    "Blank RD save cleared the saved token or recreated its connection.");
            }
            finally { reopened.Close(); }
        }
        finally { window.Close(); }
    }
}
