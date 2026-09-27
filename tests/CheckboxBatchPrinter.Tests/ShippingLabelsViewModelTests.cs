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
        ("STA multi-shipment row previews the chosen document without selecting or printing receipts", () => StaAsync(LabelChooseDocumentAsync)),
        ("STA changed TTN blocks stale preview and restarted Unknown requires repeat warning", () => StaAsync(LabelChangedAndRestartAsync))
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
        public bool ChooseFirst;
        public MarketplaceOrder? ChoosePreviewShipment(MarketplaceOrder order) => ChooseFirst ? order with { Shipments = [order.Shipments[0]] } : order;
        public IReadOnlyList<LabelPrintAttempt> Previous = []; public List<string> Errors = [];
        public bool Preview(LabelPrintBatch b, LabelPrinterGeometry g, IReadOnlyList<LabelPrintAttempt> p, bool allow)
        { Previews++; Previous = p; OnPreview?.Invoke(); return Confirm; }
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
        await restarted.RestoreHistoryAsync(); True(row2.LabelPrintStatus.Contains("невідоме"));
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
}
