using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;
internal static partial class MarketplaceViewModelTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> ShippingScenarios =>
    [
        ("STA labels without Checkbox freeze visible selection through preparation and confirmation", () => StaAsync(LabelSelectionAsync)),
        ("STA label cancellation and inaccessible document never submit partial packets", () => StaAsync(LabelBlockedAsync)),
        ("STA missing RD key permits ready labels with explicit preview and leaves skipped rows selected", () => StaAsync(LabelMissingCredentialsAsync)),
        ("STA all missing label keys produce no dialog, preview, history or print job", () => StaAsync(LabelAllCredentialsMissingAsync)),
        ("STA partly skipped multi-shipment order is not marked fully printed", () => StaAsync(LabelPartlySkippedOrderAsync)),
        ("STA multi-shipment row previews the chosen document without selecting or printing receipts", () => StaAsync(LabelChooseDocumentAsync)),
        ("STA clickable TTN previews exactly its shipment without a chooser or print job", () => StaAsync(LabelTrackingLinkAsync)),
        ("STA changed TTN blocks stale preview and restarted Unknown requires repeat warning", () => StaAsync(LabelChangedAndRestartAsync)),
        ("STA printed label highlight survives restart, refresh and failed retry but follows current TTN", () => StaAsync(LabelPrintedHighlightAsync))
    ];
    private sealed class LabelPrinter : ILabelPrinter
    {
        public int Calls; public LabelPrintBatch? Batch;
        public IReadOnlyList<string> Printers() => [ShippingLabelsTests.Settings.PrinterName];
        public IReadOnlyList<LabelDriverFormat> Formats(string printer) => [new("synthetic",101.5,101.5)];
        public LabelPrinterGeometry Inspect(LabelPrintSettings settings) => ShippingLabelsTests.Geometry;
        public async Task<int?> SubmitAsync(LabelPrintBatch batch, string name, Func<Task> before, Func<int, Task> created, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); await before(); Calls++; Batch = batch; await created(42); return 42; }
    }
    private sealed class LabelDialogs : ILabelDialogs
    {
        public bool Confirm = true; public int Previews; public Action? OnPreview;
        public bool ChooseFirst; public int Choices;
        public MarketplaceOrder? ChoosePreviewShipment(MarketplaceOrder order)
        { Choices++; return ChooseFirst ? order with { Shipments = [order.Shipments[0]] } : order; }
        public IReadOnlyList<LabelPrintAttempt> Previous = []; public List<string> Errors = [];
        public LabelPrintBatch? PreviewedBatch;
        public bool Preview(LabelPrintBatch b, LabelPrinterGeometry g, IReadOnlyList<LabelPrintAttempt> p, bool allow)
        { Previews++; Previous = p; PreviewedBatch = b; OnPreview?.Invoke(); return Confirm; }
        public Task SettingsAsync() => Task.CompletedTask;
        public void Error(string message) => Errors.Add(message);
    }
    private static async Task<(MarketplaceWorkspaceViewModel Workspace, Fixture Fixture)> LabelWorkspace()
    {
        var f = new Fixture(); f.Settings.App.Login = ""; f.Authentication.IsStored = false; f.ReceiptSource.Rows = [];
        f.Source.Orders = [Order() with { Shipments = [new("nova_poshta","first")] },
            Order() with { Key = new(MarketplaceKind.Prom,"prom-test","42"), Number = "ORDER-42", Shipments = [new("rozetka_delivery","two")] }];
        var (main, workspace) = f.Create(); main.SelectedTabIndex = 1;
        await main.PrepareOrdersAsync(); await workspace.SyncAsync(); return (workspace,f);
    }
    private static ShippingLabelsViewModel Labels(MarketplaceWorkspaceViewModel w, ShippingLabelsTests.History h,
        ShippingLabelsTests.Source s, LabelPrinter p, LabelDialogs d) => new(w,new ShippingLabelsTests.Stores(),h,s,new ShippingLabelsTests.Renderer(),p,d);
    private static async Task LabelSelectionAsync()
    {
        var (w,f) = await LabelWorkspace(); var h = new ShippingLabelsTests.History(); var p = new LabelPrinter(); var d = new LabelDialogs();
        var s = new ShippingLabelsTests.Source(); var vm = Labels(w,h,s,p,d); w.Labels = vm;
        vm.SelectVisibleCommand.Execute(null); w.OrderSearch = "ORDER-41";
        s.OnLoad = () => w.OrderSearch = "ORDER-42"; d.OnPreview = () => w.OrderSearch = "";
        await vm.RunAsync(null,true);
        Equal(1,p.Calls); Equal(1,p.Batch!.Pages.Count); Equal("ORDER-41",p.Batch.Pages[0].Shipment.OrderNumber);
        True(!w.AllOrderRows.Single(r => r.Model.Number == "ORDER-41").IsSelectedForLabels);
        True(w.AllOrderRows.Single(r => r.Model.Number == "ORDER-42").IsSelectedForLabels);
        True(w.AllOrderRows.Single(r => r.Model.Number == "ORDER-41").HasPrintedLabel);
        True(!w.AllOrderRows.Single(r => r.Model.Number == "ORDER-42").HasPrintedLabel);
        Equal(0,f.Printer.Calls); Equal(0,f.ReceiptSource.Calls); Equal(0,f.Settings.AppSaves);
        vm.ClearCommand.Execute(null); True(w.AllOrderRows.All(r => !r.IsSelectedForLabels));
    }
    private static async Task LabelBlockedAsync()
    {
        var (w,_) = await LabelWorkspace(); var h = new ShippingLabelsTests.History(); var p = new LabelPrinter(); var d = new LabelDialogs { Confirm = false };
        var s = new ShippingLabelsTests.Source(); var vm = Labels(w,h,s,p,d); vm.SelectVisibleCommand.Execute(null);
        await vm.RunAsync(null,true); Equal(0,p.Calls); Equal(0,h.Attempts.Count);
        d.Confirm = true; s.FailTracking = "two";
        await vm.RunAsync(null,true); Equal(0,p.Calls); Equal(1,d.Previews); True(d.Errors.Count > 0);
        s.FailTracking = null; s.OnLoad = () => vm.CancelCommand.Execute(null);
        await vm.RunAsync(null,true); Equal(0,p.Calls); Equal(0,h.Attempts.Count);
    }
    private static async Task LabelMissingCredentialsAsync()
    {
        foreach (var confirm in new[] { false, true })
        {
            var (workspace, fixture) = await LabelWorkspace(); var history = new ShippingLabelsTests.History();
            var printer = new LabelPrinter(); var dialogs = new LabelDialogs { Confirm = confirm };
            var source = new ShippingLabelsTests.Source { MissingCredentialTracking = "two" };
            var labels = Labels(workspace, history, source, printer, dialogs); labels.SelectVisibleCommand.Execute(null);
            dialogs.OnPreview = () =>
            {
                Equal(0, printer.Calls); Equal(0, history.Attempts.Count);
                var batch = dialogs.PreviewedBatch!;
                Equal(1, batch.Pages.Count); Equal(1, batch.SkippedShipments.Count);
                var preview = new CheckboxBatchPrinter.Views.LabelPreviewWindow(batch, ShippingLabelsTests.Geometry, [], true);
                var root = (System.Windows.FrameworkElement)preview.Content;
                True(VisualChildren<System.Windows.Controls.TextBlock>(root).Any(t => t.Text.Contains("Пропущено")));
                True(VisualChildren<System.Windows.Controls.Button>(root).Any(b => b.IsEnabled && b.Content?.ToString()?.Contains("доступних") == true));
                var renderDirectory = Environment.GetEnvironmentVariable("CBP_TEST_RENDER_DIR");
                if (!string.IsNullOrEmpty(renderDirectory))
                {
                    root.Measure(new System.Windows.Size(970, 700)); root.Arrange(new System.Windows.Rect(0, 0, 970, 700)); root.UpdateLayout();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(970, 700, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    System.IO.Directory.CreateDirectory(renderDirectory);
                    using var output = System.IO.File.Create(System.IO.Path.Combine(renderDirectory, "missing-key-preview.png")); encoder.Save(output);
                }
                preview.Close();
                // A skipped order changing after preview must not invalidate the ready NP pages.
                var skipped = workspace.AllOrderRows.Single(r => r.Number == "ORDER-42");
                skipped.Update(skipped.Model with { Shipments = [new("rozetka_delivery", "new-skipped-ttn")] }, [], []);
            };
            await labels.RunAsync(null, true);
            Equal(0, dialogs.Errors.Count); Equal(1, dialogs.Previews); Equal(confirm ? 1 : 0, printer.Calls);
            var readyRow = workspace.AllOrderRows.Single(r => r.Number == "ORDER-41");
            var skippedRow = workspace.AllOrderRows.Single(r => r.Number == "ORDER-42");
            True(skippedRow.IsSelectedForLabels && !skippedRow.HasPrintedLabel);
            if (confirm)
            {
                True(!readyRow.IsSelectedForLabels && readyRow.HasPrintedLabel);
                True(history.Attempts.Single().Pages.All(p => p.TrackingNumber == "first"));
                True(labels.Status.Contains("Пропущено"));
            }
            else { True(readyRow.IsSelectedForLabels); Equal(0, history.Attempts.Count); }
            Equal(0, fixture.Printer.Calls); Equal(0, fixture.ReceiptSource.Calls);
        }
    }
    private static async Task LabelAllCredentialsMissingAsync()
    {
        var (workspace, fixture) = await LabelWorkspace(); var history = new ShippingLabelsTests.History();
        var printer = new LabelPrinter(); var dialogs = new LabelDialogs();
        var labels = Labels(workspace, history, new() { MissingAllCredentials = true }, printer, dialogs);
        labels.SelectVisibleCommand.Execute(null); await labels.RunAsync(null, true);
        Equal(0, dialogs.Errors.Count); Equal(0, dialogs.Previews); Equal(0, printer.Calls); Equal(0, history.Attempts.Count);
        True(workspace.AllOrderRows.All(r => r.IsSelectedForLabels && !r.HasPrintedLabel && r.LabelStatus.Contains("ключ")));
        True(labels.Status.Contains("Немає доступних наклейок"));
        Equal(0, fixture.Printer.Calls); Equal(0, fixture.ReceiptSource.Calls);
    }
    private static async Task LabelPartlySkippedOrderAsync()
    {
        var (workspace, _) = await LabelWorkspace(); var row = workspace.AllOrderRows.First();
        row.Update(row.Model with { Shipments = [new("nova_poshta", "first"), new("rozetka_delivery", "two")] }, [], []);
        row.IsSelectedForLabels = true;
        var history = new ShippingLabelsTests.History(); var printer = new LabelPrinter(); var dialogs = new LabelDialogs();
        var labels = Labels(workspace, history, new() { MissingCredentialTracking = "two" }, printer, dialogs);
        await labels.RunAsync(null, true);
        Equal(1, printer.Calls); Equal(0, dialogs.Errors.Count); Equal(1, history.Attempts.Single().Pages.Count);
        True(row.IsSelectedForLabels && !row.HasPrintedLabel);
        True(row.LabelPrintStatus.Contains("Частину"));
    }
    private static async Task LabelChangedAndRestartAsync()
    {
        var (w,_) = await LabelWorkspace(); var h = new ShippingLabelsTests.History(); var p = new LabelPrinter(); var d = new LabelDialogs();
        var vm = Labels(w,h,new(),p,d); vm.SelectVisibleCommand.Execute(null);
        d.OnPreview = () => { var row = w.AllOrderRows.First(); row.Update(row.Model with { Shipments = [new("nova_poshta","replacement")] }, [], []); };
        await vm.RunAsync(null,true); Equal(0,p.Calls); True(d.Errors.Count > 0);
        var row2 = w.AllOrderRows.Last(); var shipment = ShippingCarrierNames.ForOrder(row2.Model).Single();
        await h.SaveAsync(new("synthetic-old-attempt","synthetic-old-job",p.Printers()[0],DateTimeOffset.UtcNow,LabelSubmissionState.SubmissionUnknown,
            [new(shipment.Carrier,"synthetic",shipment.TrackingNumber,"synthetic-doc",1,"synthetic-hash")]));
        var restartedDialogs = new LabelDialogs { Confirm = false }; var restarted = Labels(w,h,new(),p,restartedDialogs);
        await restarted.RestoreHistoryAsync(); True(row2.LabelPrintStatus.Contains("невідоме")); True(!row2.HasPrintedLabel);
        restarted.ClearCommand.Execute(null); row2.IsSelectedForLabels = true;
        await restarted.RunAsync(null,true); Equal(1,restartedDialogs.Previous.Count); Equal(0,p.Calls);
    }
    private static async Task LabelChooseDocumentAsync()
    {
        var (w,f) = await LabelWorkspace(); var row = w.AllOrderRows.First();
        row.Update(row.Model with { Shipments = [new("nova_poshta","first"),new("rozetka_delivery","two")] },[],[]);
        var source = new ShippingLabelsTests.Source(); var loads = 0; source.OnLoad = () => loads++;
        var printer = new LabelPrinter(); var dialogs = new LabelDialogs { ChooseFirst = true };
        var vm = Labels(w,new(),source,printer,dialogs);
        await vm.RunAsync(row,false); Equal(1,loads); Equal(1,dialogs.Previews); Equal(0,printer.Calls);
        Equal(0,f.Printer.Calls); True(!row.IsSelectedForLabels);
    }

    private static async Task LabelTrackingLinkAsync()
    {
        var (workspace, fixture) = await LabelWorkspace(); var row = workspace.AllOrderRows.First();
        row.Update(row.Model with { Shipments = [new("nova_poshta", "first"), new("rozetka_delivery", "two")] }, [], []);
        var history = new ShippingLabelsTests.History(); var printer = new LabelPrinter(); var dialogs = new LabelDialogs();
        var source = new ShippingLabelsTests.Source(); var loads = 0; source.OnLoad = () => loads++;
        var labels = Labels(workspace, history, source, printer, dialogs);
        var link = row.ShipmentLinks.Single(l => l.TrackingNumber == "two");
        await ExecuteAsync(labels.PreviewShipmentCommand, parameter: link);
        Equal(0, dialogs.Choices); Equal(1, loads); Equal(1, dialogs.Previews);
        True(dialogs.PreviewedBatch!.Pages.All(p => p.Shipment.TrackingNumber == "two"));
        Equal(0, printer.Calls); Equal(0, history.Attempts.Count); True(!row.IsSelectedForLabels);
        row.Update(row.Model with { Shipments = [new("nova_poshta", "replacement")] }, [], []);
        await ExecuteAsync(labels.PreviewShipmentCommand, parameter: link);
        Equal(1, loads); Equal(1, dialogs.Previews); Equal(1, dialogs.Errors.Count);
        True(row.ShipmentLinks.Single().TrackingNumber == "replacement");
        row.Update(row.Model with { Shipments = [] }, [], []); Equal(0, row.ShipmentLinks.Count);
        Equal(0, fixture.Printer.Calls); Equal(0, fixture.ReceiptSource.Calls);
    }

    private static async Task LabelPrintedHighlightAsync()
    {
        var (w,_) = await LabelWorkspace(); var h = new ShippingLabelsTests.History(); var p = new LabelPrinter();
        var vm = Labels(w,h,new(),p,new()); vm.SelectVisibleCommand.Execute(null);
        await vm.RunAsync(null,true);
        Equal(1,p.Calls); True(w.AllOrderRows.All(r => r.HasPrintedLabel)); // NP and Rozetka Delivery.

        var (reopened,fixture) = await LabelWorkspace();
        var restored = Labels(reopened,h,new(),p,new()); reopened.Labels = restored;
        await restored.RestoreHistoryAsync(); True(reopened.AllOrderRows.All(r => r.HasPrintedLabel));
        await reopened.SyncAsync(); await restored.RestoreHistoryAsync();
        True(reopened.AllOrderRows.All(r => r.HasPrintedLabel));

        // A failed repeat does not erase an earlier successful submission of the same TTN.
        var old = h.Attempts.Single();
        await h.SaveAsync(old with { AttemptId = "failed-repeat", TimeUtc = old.TimeUtc.AddMinutes(1), State = LabelSubmissionState.NotSubmitted });
        await restored.RestoreHistoryAsync(); True(reopened.AllOrderRows.All(r => r.HasPrintedLabel));
        var row = reopened.AllOrderRows.First(); var changes = 0;
        row.PropertyChanged += (_,e) => { if (e.PropertyName == nameof(row.HasPrintedLabel)) changes++; };
        fixture.Source.Orders = fixture.Source.Orders.Select(o => o.Key == row.Key
            ? o with { Shipments = [new("nova_poshta","replacement-unprinted")] } : o).ToArray();
        await reopened.SyncAsync(); await restored.RestoreHistoryAsync();
        True(!row.HasPrintedLabel); True(changes > 0);
        True(reopened.AllOrderRows.Last().HasPrintedLabel);

        var shipment = ShippingCarrierNames.ForOrder(row.Model).Single();
        await h.SaveAsync(new("unknown-current","unknown-job",p.Printers()[0],DateTimeOffset.UtcNow.AddMinutes(2),
            LabelSubmissionState.SubmissionUnknown,[new(shipment.Carrier,"synthetic",shipment.TrackingNumber,"doc",1,"hash")]));
        await restored.RestoreHistoryAsync(); True(!row.HasPrintedLabel);
        True(row.LabelPrintStatus.Contains("невідоме"));
    }
}
