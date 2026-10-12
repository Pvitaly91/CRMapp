using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CheckboxBatchPrinter.Controls;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Services;
using CheckboxBatchPrinter.ViewModels;
using CheckboxBatchPrinter.Views;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task ShipmentCabinetPrintUiAsync()
    {
        _xamlCheckpoint = "cabinet print evidence remains independent of the Windows journal";
        var first = new OrderShipment("nova_poshta", "20400000000021") { ShipmentId = "first-ref" };
        var second = new OrderShipment("nova_poshta", "20400000000022") { ShipmentId = "second-ref" };
        var observed = new DateTimeOffset(2026, 10, 12, 9, 10, 0, TimeSpan.Zero);
        const string rawDate = "2026-10-11 23:45:12";
        var order = Order([ReceiptOne]) with { Shipments = [first, second] };
        var model = new MarketplaceOrderRowViewModel(order) { IsSelectedForLabels = true };
        var receipt = new ReceiptRowViewModel(Receipt(ReceiptOne, 1, 10000))
            { OrderMatch = new(ReceiptLinkState.Exact, order, "Exact synthetic receipt", []), IsSelectedForOrders = true };
        model.Update(order, [receipt], []);
        var serialized = JsonSerializer.Serialize(model.Model);
        model.UpdateCabinetPrintStatuses([new(first, null), new(second, false)]);
        True(!model.HasPrintedLabel && !model.HasLabelSubmission);
        Equal("Не передано", model.LabelPrintStatus);
        model.UpdateCabinetPrintStatuses([new(first, true, "Кабінет НП", rawDate, observed)]);
        True(model.HasLabelSubmission && !model.HasPrintedLabel);
        Equal("НП: частину позначено надрукованою", model.LabelPrintStatus);
        Equal(1, model.GetCabinetPrintEvidence().Count);
        model.UpdateCabinetPrintStatuses([]);
        model.UpdateCabinetPrintStatuses([new(first, null)]);
        True(model.HasLabelSubmission && model.GetCabinetPrintEvidence().Count == 1,
            "An omitted or unknown cabinet flag must not erase an already observed positive marker.");
        model.ApplyLabelHistory([]);
        True(model.HasLabelSubmission && !model.HasPrintedLabel,
            "Reloading an empty local journal must preserve independent cabinet evidence.");
        model.UpdateCabinetPrintStatuses([new(second, true, "Кабінет НП", rawDate, observed)]);
        True(model.HasPrintedLabel && model.HasLabelSubmission);
        Equal("НП: позначено надрукованою", model.LabelPrintStatus);
        Equal(serialized, JsonSerializer.Serialize(model.Model));
        True(model.IsSelectedForLabels && receipt.IsSelectedForOrders && model.HasConfirmedLink);
        True(model.LabelPrintStatusToolTip.Contains(rawDate) && model.LabelPrintStatusToolTip.Contains("часовий пояс не надано") &&
            model.LabelPrintStatusToolTip.Contains("Перевірено: 12.10.2026 12:10"));
        True(!model.LabelPrintStatus.Contains("Windows") && !model.LabelPrintStatus.Contains("job"),
            "A cabinet marker cannot fabricate a Windows transmission or job.");
        model.Update(order with { Status = "Виконано" }, [receipt], []);
        True(model.HasPrintedLabel && model.GetCabinetPrintEvidence().Count == 2);

        var localFirst = new LabelPrintAttempt("synthetic-local-first", "synthetic-job", "synthetic-printer", observed,
            LabelSubmissionState.Submitted,
            [new(ShippingCarrier.NovaPoshta, order.Key.ConnectionId, first.TrackingNumber, "first-doc", 1, "first-hash") { Order = order.Key }], 77);
        model.ApplyLabelHistory([localFirst]);
        True(model.HasPrintedLabel && model.HasLabelSubmission,
            "A locally submitted first shipment and an externally marked second shipment complete the same order.");
        Equal("Частину наклейок передано Windows, job 77", model.LabelPrintStatus);
        True(model.LabelPrintStatusToolTip.Contains("Кабінет НП") && model.LabelPrintStatusToolTip.Contains(rawDate));
        model.UpdateCabinetPrintStatuses([new(first, false), new(second, false)]);
        True(!model.HasPrintedLabel && model.HasLabelSubmission);
        Equal("Частину наклейок передано Windows, job 77", model.LabelPrintStatus);
        Equal(0, model.GetCabinetPrintEvidence().Count);
        var localAll = localFirst with
        {
            Pages = localFirst.Pages.Concat([new LabelAttemptPage(ShippingCarrier.NovaPoshta, order.Key.ConnectionId,
                second.TrackingNumber, "second-doc", 1, "second-hash") { Order = order.Key }]).ToArray()
        };
        model.ApplyLabelHistory([localAll]);
        True(model.HasPrintedLabel && model.HasLabelSubmission);
        Equal("Передано Windows, job 77", model.LabelPrintStatus);
        model.UpdateCabinetPrintStatuses([new(first, null), new(second, null)]);
        model.ClearCabinetPrintStatuses();
        True(model.HasPrintedLabel && model.HasLabelSubmission);
        Equal("Передано Windows, job 77", model.LabelPrintStatus);
        model.ApplyLabelHistory([localFirst with { State = LabelSubmissionState.SubmissionUnknown }]);
        True(!model.HasPrintedLabel && model.HasLabelSubmission);
        Equal("Передавання невідоме; можливий дубль", model.LabelPrintStatus);

        var identity = new MarketplaceOrderRowViewModel(order);
        identity.UpdateCabinetPrintStatuses([new(first, true), new(second, true)]);
        identity.Update(order with { Shipments = [first with { ShipmentId = "changed-ref" }, second] }, [], []);
        Equal(1, identity.GetCabinetPrintEvidence().Count);
        True(!identity.HasPrintedLabel && identity.HasLabelSubmission);
        identity.Update(identity.Model with { Shipments = [first, second with { TrackingNumber = "replacement-ttn" }] }, [], []);
        Equal(0, identity.GetCabinetPrintEvidence().Count);
        True(!identity.HasPrintedLabel && !identity.HasLabelSubmission);
        identity.Update(order, [], []);
        identity.UpdateCabinetPrintStatuses([new(first, true), new(second, true)]);
        identity.Update(order with { Key = order.Key with { ConnectionId = "other-account" } }, [], []);
        Equal(0, identity.GetCabinetPrintEvidence().Count);
        identity.UpdateCabinetPrintStatuses([new(first with { Carrier = "rozetka_delivery" }, true)]);
        Equal(0, identity.GetCabinetPrintEvidence().Count);

        var fixture = new Fixture();
        fixture.Settings.Market.Connections.Add(new()
            { Id = "synthetic-rozetka", Marketplace = MarketplaceKind.Rozetka, Enabled = true, Name = "Synthetic Rozetka" });
        var prom = order with { Number = "PROM-CABINET", Shipments = [first] };
        var rozetka = Order([ReceiptTwo]) with
        {
            Key = new(MarketplaceKind.Rozetka, "synthetic-rozetka", "rz-cabinet"), Number = "ROZETKA-CABINET", Shipments = [second]
        };
        var unknown = Order([ReceiptThree]) with
        {
            Key = order.Key with { OrderId = "unknown-delivery" }, Number = "UNKNOWN-DELIVERY",
            Shipments = [new("rozetka_delivery", "RMP-UNKNOWN")]
        };
        fixture.Source.Orders = [prom, unknown]; fixture.Rozetka.Orders = [rozetka];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 10000), Receipt(ReceiptTwo, 2, 10000), Receipt(ReceiptThree, 3, 10000)];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync(); await OpenOrdersAsync(main, workspace);
        main.CoordinatorManaged = true;
        var printer = new LabelPrinter(); var dialogs = new LabelDialogs(); var history = new ShippingLabelsTests.History();
        using var labels = Labels(workspace, history, new ShippingLabelsTests.Source(), printer, dialogs);
        workspace.Labels = labels;
        var window = new MainWindow { DataContext = main };
        var bindingFailures = new BindingErrorListener();
        var traceLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingFailures);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            LayoutContent(window); await DrainDispatcherAsync();
            var promRow = workspace.AllOrderRows.Single(row => row.Number == prom.Number);
            var rzRow = workspace.AllOrderRows.Single(row => row.Number == rozetka.Number);
            var unknownRow = workspace.AllOrderRows.Single(row => row.Number == unknown.Number);
            var receiptStates = main.Receipts.Select(row => (row.Id, row.PrintStatus)).ToArray();
            promRow.UpdateCabinetPrintStatuses([new(first, true, "Кабінет НП", rawDate, observed)]);
            rzRow.UpdateCabinetPrintStatuses([new(second, true, "Кабінет НП", rawDate, observed)]);
            foreach (var row in workspace.AllOrderRows) row.IsSelectedForLabels = true;
            await ExecuteAsync(labels.SelectPrintCandidatesCommand);
            True(!promRow.IsSelectedForLabels && !rzRow.IsSelectedForLabels && unknownRow.IsSelectedForLabels,
                "Confirmed cabinet markers exclude print candidates while absent RD markers do not invent a printed state.");
            True(receiptStates.SequenceEqual(main.Receipts.Select(row => (row.Id, row.PrintStatus))),
                "Carrier label markers must not mark any Checkbox receipt as printed.");
            var grid = (DataGrid)window.FindName("MarketplaceOrdersGrid");
            var column = grid.Columns.Single(c => c.Header?.ToString() == "Друк наклейки");
            foreach (var row in new[] { promRow, rzRow })
            {
                grid.ScrollIntoView(row, column); await DrainDispatcherAsync(); LayoutContent(window);
                var realized = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(row);
                var expected = (Color)ColorConverter.ConvertFromString(row.Key.Marketplace == MarketplaceKind.Prom ? "#BFDAFA" : "#BCE8C9");
                Equal(expected, ((SolidColorBrush)realized.Background).Color);
                var text = (DashboardTextBox)column.GetCellContent(row);
                Equal("НП: позначено надрукованою", text.Text);
                True(text.ToolTip?.ToString()?.Contains(rawDate) == true,
                    "Cabinet source and raw print date must be available in the actual print-status cell tooltip.");
            }
            True(unknownRow.LabelPrintStatusToolTip.Contains("Rozetka Delivery: ознаку друку з кабінету не надано"));
            Equal(0, history.Attempts.Count); Equal(0, fixture.History.Saves); Equal(0, fixture.Links.Saves);
            Equal(0, fixture.Printer.Calls); Equal(0, printer.Calls); Equal(0, dialogs.Previews);
            RenderSyntheticContent(window, "shipment-cabinet-print-status.png");
            True(bindingFailures.Messages.Count == 0, "Cabinet print binding errors: " + string.Join("\n", bindingFailures.Messages));
        }
        finally
        {
            main.CancelPendingOperations(); window.Close(); await DrainDispatcherAsync();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingFailures);
            PresentationTraceSources.DataBindingSource.Switch.Level = traceLevel;
        }

        var shipment = ShippingCarrierNames.ForOrder(prom).Single();
        var batch = new LabelPrintBatch("synthetic-cabinet-repeat",
            [new(shipment, "synthetic-doc", "synthetic-hash", 1, 100, 100, CombinedPreviewPng(prom.Number, shipment.TrackingNumber))],
            ShippingLabelsTests.Settings)
        {
            SelectedOrderCount = 1,
            CabinetPrintedLabels = [new(shipment, "Кабінет НП", rawDate, observed)]
        };
        foreach (var combined in new[] { false, true })
        {
            var confirmation = combined ? new CombinedPrintConfirmation(new("synthetic-receipt-printer", 0,
                [new(1, ReceiptOne, "1", "Prom", prom.Number, shipment.TrackingNumber)]), 0) : null;
            var preview = new LabelPreviewWindow(batch, ShippingLabelsTests.Geometry, [], true, confirmation);
            try
            {
                LayoutContent(preview);
                var content = (FrameworkElement)preview.Content;
                var repeat = VisualChildren<CheckBox>(content).Single();
                var print = VisualChildren<Button>(content).Single(button => button.Content?.ToString() ==
                    (combined ? "Друк наклейок і чеків" : "Підтверджую — друк усього пакета"));
                True(repeat.Visibility == Visibility.Visible && !print.IsEnabled,
                    "External cabinet evidence alone must require manual duplicate confirmation for both print flows.");
                var warning = VisualChildren<TextBlock>(content).Single(text => text.Text.StartsWith("УВАГА: у кабінеті НП", StringComparison.Ordinal));
                True(!warning.Text.Contains("Windows") && !warning.Text.Contains("job"));
                var detail = VisualChildren<Expander>(content).Single(expander => expander.Header?.ToString() == "Накладні з ознакою друку в кабінеті");
                detail.IsExpanded = true; LayoutContent(preview);
                var text = string.Join("\n", VisualChildren<TextBlock>(content).Select(block => block.Text));
                True(text.Contains(rawDate) && text.Contains("часовий пояс не надано") && text.Contains(shipment.TrackingNumber));
                ClickCheckBox(repeat); True(print.IsEnabled);
                ClickCheckBox(repeat); True(!print.IsEnabled);
                if (!combined) RenderSyntheticContent(preview, "cabinet-print-repeat.png");
            }
            finally { preview.Close(); }
        }
    }
}
