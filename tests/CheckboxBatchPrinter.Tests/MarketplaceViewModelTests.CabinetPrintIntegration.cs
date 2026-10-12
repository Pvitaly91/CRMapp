using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task CabinetPrintPublicationAsync()
    {
        var fixture = new Fixture();
        var shipment = new OrderShipment("Нова пошта", "20400000000001");
        fixture.Source.Orders = [Order() with { Shipments = [shipment] }];
        var documents = new[] { new WaybillRecord(ShippingCarrier.NovaPoshta, "np-main", "NP", "ttn:20400000000001",
            shipment.TrackingNumber, new DateTime(2026, 9, 23), "", "", "", null, 1)
            { CabinetPrinted = true, CabinetLastPrint = "2026-09-23 12:34:56" } };
        using var waybills = new WaybillsViewModel(new TrackingWaybillSource(documents));
        var config = new TrackingShippingSettings(new()
        {
            NovaPoshtaConnections = [new("np-main", "NP", "synthetic_np_key")],
            NovaPoshtaStoreBindings = [new("prom-test", "np-main")]
        });
        var (main, workspace) = CreateTrackingDashboard(fixture, waybills: waybills, shippingSettings: config);
        try
        {
            await main.RefreshAllAsync();
            await main.UpdateWaybillOrderAssociationsAsync();
            var row = workspace.AllOrderRows.Single();
            True(row.HasPrintedLabel && row.HasLabelSubmission, "Blank parcel status suppressed independent cabinet print metadata.");
            True(row.LabelPrintStatusToolTip.Contains("2026-09-23 12:34:56"));
            Equal(0, fixture.History.Saves); Equal(0, fixture.Printer.Calls);
            documents[0] = documents[0] with { CabinetPrinted = null, CabinetLastPrint = "" };
            await waybills.RefreshAllAsync(); await main.UpdateWaybillOrderAssociationsAsync();
            True(row.HasPrintedLabel, "Missing cabinet marker erased known external evidence.");
            documents[0] = documents[0] with { CabinetPrinted = false };
            await waybills.RefreshAllAsync(); await main.UpdateWaybillOrderAssociationsAsync();
            True(!row.HasPrintedLabel && !row.HasLabelSubmission, "Explicit negative cabinet marker became a local print job.");
            documents[0] = documents[0] with { CabinetPrinted = true, ConnectionId = "wrong-account" };
            await waybills.RefreshAllAsync(); await main.UpdateWaybillOrderAssociationsAsync();
            True(!row.HasLabelSubmission, "A different NP account supplied the external print marker.");
        }
        finally { main.CancelPendingOperations(); }
    }

    private static async Task CabinetPrintPreparedLabelsAsync()
    {
        var (workspace, fixture) = await LabelWorkspace();
        var history = new ShippingLabelsTests.History(); var printer = new LabelPrinter();
        var dialogs = new LabelDialogs { Confirm = false };
        var labels = Labels(workspace, history, new() { MissingCredentialTracking = "two" }, printer, dialogs);
        workspace.Labels = labels;
        var row = workspace.AllOrderRows.Single(r => r.Number == "ORDER-41");
        row.UpdateCabinetPrintStatuses([new(row.Model.Shipments.Single(), true, "Кабінет НП", "2026-09-23 12:00:00")]);
        labels.SelectVisibleCommand.Execute(null);
        await labels.RunAsync(null, true);
        Equal(1, dialogs.Previews); Equal(1, dialogs.PreviewedBatch!.CabinetPrintedLabels.Count);
        Equal(row.Key, dialogs.PreviewedBatch.CabinetPrintedLabels.Single().Shipment.Order);
        Equal(1, dialogs.PreviewedBatch.SkippedShipments.Count);
        Equal(0, dialogs.Previous.Count); Equal(0, history.Attempts.Count); Equal(0, printer.Calls);
        Equal(0, fixture.Printer.Calls); True(row.IsSelectedForLabels);
        labels.Dispose();
    }

    private static async Task CabinetPrintLateMarkerBlocksLabelsAsync()
    {
        var (workspace, _) = await LabelWorkspace();
        var history = new ShippingLabelsTests.History(); var printer = new LabelPrinter(); var dialogs = new LabelDialogs();
        var labels = Labels(workspace, history, new(), printer, dialogs); workspace.Labels = labels;
        labels.SelectVisibleCommand.Execute(null);
        var row = workspace.AllOrderRows.Single(r => r.Number == "ORDER-41");
        dialogs.OnPreview = () => row.UpdateCabinetPrintStatuses([new(row.Model.Shipments.Single(), true)]);
        await labels.RunAsync(null, true);
        Equal(0, printer.Calls); Equal(0, history.Attempts.Count);
        True(dialogs.Errors.Any(message => message.Contains("кабінеті НП")));
        True(row.IsSelectedForLabels); labels.Dispose();
    }

    private static async Task CabinetPrintLateMarkerBlocksCombinedAsync()
    {
        using var fixture = await CombinedFixture.CreateAsync();
        fixture.MarkOrders();
        var row = fixture.Workspace.AllOrderRows.First();
        fixture.Dialogs.OnPreview = () => row.UpdateCabinetPrintStatuses([new(row.Model.Shipments.Single(), true)]);
        await fixture.Main.PrintOrdersWithReceiptsAsync();
        Equal(0, fixture.LabelPrinter.Calls); Equal(0, fixture.ReceiptPrinter.Calls);
        Equal(0, fixture.Base.History.Saves); Equal(0, fixture.LabelHistory.Attempts.Count);
        True(fixture.Base.Dialogs.Errors.Any(message => message.Contains("кабінеті НП")));
        True(fixture.Workspace.AllOrderRows.All(r => r.IsSelectedForLabels));
    }
}
