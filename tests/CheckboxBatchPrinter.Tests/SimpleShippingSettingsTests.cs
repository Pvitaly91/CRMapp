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
        ("simple NP shared account key forks only the selected shop", SharedRotation),
        ("simple NP unbound shop receives its own account without guessing an existing one", Unbound),
        ("simple NP rejects invalid keys shops and account overflow without exposing secrets", Invalid),
        ("DPAPI simple NP key and explicit shop binding survive restart without changing label history", Persistence),
        ("STA simple label settings show one masked NP field and collapse advanced options", () => MarketplaceViewModelTests.StaAsync(SimplifiedUi)),
        ("STA simple label settings save the explicitly selected Prom shop only", () => MarketplaceViewModelTests.StaAsync(SelectedShopUi)),
        ("STA NP verify follows selected order shop and rejects a programmatic mismatched click without saving", () => MarketplaceViewModelTests.StaAsync(VerificationShopUi)),
        ("STA simple label settings keep typed keys separate while switching shops", () => MarketplaceViewModelTests.StaAsync(DraftsUi)),
        ("STA simple label settings closing without save writes no typed key", () => MarketplaceViewModelTests.StaAsync(CancelUi)),
        ("STA simple label settings blank key preserves latest credentials and collapsed print options", () => MarketplaceViewModelTests.StaAsync(PreserveUi)),
        ("STA simple NP key can be saved before selecting a label printer", () => MarketplaceViewModelTests.StaAsync(NoPrinterUi))
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
        Check(updated.NovaPoshtaConnections.Count == 2 && binding.NovaPoshtaConnectionId != Account &&
            updated.NovaPoshtaConnections.Single(c => c.Id == binding.NovaPoshtaConnectionId).ApiKey == NewKey &&
            updated.NovaPoshtaConnections.Single(c => c.Id == Account).ApiKey == OldKey &&
            updated.NovaPoshtaStoreBindings.Single(b => b.MarketplaceConnectionId == ShopB).NovaPoshtaConnectionId == Account,
            "Changing one shop's key changed another shop's shared account.");
        OtherSettingsUnchanged(original, updated);
        return Task.CompletedTask;
    }
    private static Task Unbound()
    {
        var original = Original with { NovaPoshtaConnections = [new(Account, "Unbound legacy account", NewKey)] };
        var updated = NovaPoshtaKeySettings.Apply(original, Prom(), NewKey);
        var binding = updated.NovaPoshtaStoreBindings.Single();
        Check(updated.NovaPoshtaConnections.Count == 2 && binding.MarketplaceConnectionId == ShopA && binding.NovaPoshtaConnectionId != Account &&
            updated.NovaPoshtaConnections.Single(c => c.Id == binding.NovaPoshtaConnectionId).ApiKey == NewKey &&
            updated.NovaPoshtaConnections.Single(c => c.Id == Account) == original.NovaPoshtaConnections.Single(),
            "Unbound shop guessed an existing account from its name or key.");
        OtherSettingsUnchanged(original, updated);
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
                LabelSubmissionState.SubmissionUnknown, [new(ShippingCarrier.NovaPoshta, Account, "20400000000001", "synthetic-document", 1, "synthetic-fingerprint")]));
            var historyBefore = await File.ReadAllBytesAsync(historyPath);
            var first = NovaPoshtaKeySettings.Apply(Original, Prom(), OldKey);
            await new DpapiShippingSettingsStore(path).SaveAsync(first);
            var restarted = await new DpapiShippingSettingsStore(path).LoadAsync();
            var id = restarted.NovaPoshtaConnections.Single().Id;
            await new DpapiShippingSettingsStore(path).SaveAsync(NovaPoshtaKeySettings.Apply(restarted, Prom(), NewKey));
            var restored = await new DpapiShippingSettingsStore(path).LoadAsync();
            Check(restored.NovaPoshtaConnections.Single().Id == id && restored.NovaPoshtaConnections.Single().ApiKey == NewKey &&
                restored.NovaPoshtaStoreBindings.Single() == new NovaPoshtaStoreBinding(ShopA, id), "Restart lost the simplified key or stable binding.");
            OtherSettingsUnchanged(Original, restored);
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
            Check(binding.NovaPoshtaConnectionId != Account && store.Value.NovaPoshtaConnections.Single(c => c.Id == binding.NovaPoshtaConnectionId).ApiKey == NewKey &&
                store.Value.NovaPoshtaConnections.Single(c => c.Id == Account).ApiKey == OldKey &&
                store.Value.NovaPoshtaStoreBindings.Single(b => b.MarketplaceConnectionId == ShopA).NovaPoshtaConnectionId == Account,
                "Settings saved the key against a different shop or changed the old shop's key.");
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
            await Save(window, store);
            foreach (var pair in new[] { (ShopA, OldKey), (ShopB, NewKey) })
            {
                var id = store.Value.NovaPoshtaStoreBindings.Single(b => b.MarketplaceConnectionId == pair.Item1).NovaPoshtaConnectionId;
                Check(store.Value.NovaPoshtaConnections.Single(c => c.Id == id).ApiKey == pair.Item2,
                    "Saving multiple shop drafts mixed or lost their keys.");
            }
            Check(store.Value.NovaPoshtaConnections.Count == 2, "Separate shop drafts unexpectedly shared one account.");
        }
        finally { window.Close(); }
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
}
