using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CheckboxBatchPrinter.Controls;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;
using CheckboxBatchPrinter.Views;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task ShipmentTrackingUiAsync()
    {
        await DispatchedOrderColorsAsync();
        _xamlCheckpoint = "shipment tracking projection and selectable dashboard status";
        var first = new OrderShipment("nova_poshta", "20400000000002")
            { ShipmentId = "10000000-0000-0000-0000-000000000002" };
        var second = new OrderShipment("rozetka_delivery", "RMP-00123456") { ShipmentId = "123456" };
        var checkedAt = new DateTimeOffset(2026, 10, 12, 9, 10, 0, TimeSpan.Zero);
        var order = Order([ReceiptOne]) with { Shipments = [first, second] };
        OrderShipmentTrackingStatus[] statuses =
        [
            new(first, "Відправлення прибуло у відділення", "Нова пошта · відстеження", checkedAt),
            new(second, "Очікує отримання", "Rozetka Delivery · відстеження", checkedAt.AddMinutes(-5), true),
            new(new("nova_poshta", "unrelated-ttn"), "Чужа накладна", "synthetic", checkedAt)
        ];

        // The projection must neither mutate serialized marketplace data nor touch print/link state.
        var model = new MarketplaceOrderRowViewModel(order) { IsSelectedForLabels = true };
        var receipt = new ReceiptRowViewModel(Receipt(ReceiptOne, 1, 10000))
            { OrderMatch = new(ReceiptLinkState.Exact, order, "Exact synthetic receipt", []), IsSelectedForOrders = true };
        model.Update(order, [receipt], []);
        var history = new LabelPrintAttempt("synthetic-tracking-history", "synthetic-job", "synthetic-printer", checkedAt,
            LabelSubmissionState.Submitted,
        [
            new(ShippingCarrier.NovaPoshta, order.Key.ConnectionId, first.TrackingNumber, "first", 1, "first") { Order = order.Key },
            new(ShippingCarrier.RozetkaDelivery, order.Key.ConnectionId, second.TrackingNumber, "second", 1, "second") { Order = order.Key }
        ], 42);
        model.ApplyLabelHistory([history]);
        True(model.HasPrintedLabel && model.HasLabelSubmission && model.HasConfirmedLink);
        var labelStatus = model.LabelPrintStatus;
        var linked = model.LinkedReceipts;
        var serialized = JsonSerializer.Serialize(model.Model);
        model.UpdateTrackingStatuses(statuses);
        Equal(serialized, JsonSerializer.Serialize(model.Model));
        True(model.IsSelectedForLabels && receipt.IsSelectedForOrders && model.HasPrintedLabel && model.HasLabelSubmission);
        True(ReferenceEquals(linked, model.LinkedReceipts) && model.HasConfirmedLink);
        Equal(labelStatus, model.LabelPrintStatus);
        Equal(2, model.ShipmentLinks.Count);
        Equal(statuses[0].Status, model.ShipmentLinks[0].TrackingStatus);
        Equal(statuses[1].Status, model.ShipmentLinks[1].TrackingStatus);
        True(model.ShipmentLinks[0].TrackingStatusToolTip.Contains("Нова пошта · відстеження") &&
            model.ShipmentLinks[0].TrackingStatusToolTip.Contains("12.10.2026 12:10"));
        True(model.ShipmentLinks[1].TrackingStatusToolTip.Contains("Показано попередній статус") &&
            model.ShipmentLinks[1].TrackingStatusToolTip.Contains("Rozetka Delivery"));
        model.Update(order with { StoreName = "Updated synthetic shop", Status = "Виконано" }, [receipt], []);
        Equal(statuses[0].Status, model.ShipmentLinks[0].TrackingStatus);
        Equal(statuses[1].Status, model.ShipmentLinks[1].TrackingStatus);
        True(model.HasPrintedLabel && model.IsSelectedForLabels && model.HasConfirmedLink);
        model.Update(model.Model with { Shipments = [first with { TrackingNumber = "20400000000003" }, second] }, [receipt], []);
        Equal("Статус не завантажено", model.ShipmentLinks[0].TrackingStatus);
        Equal(statuses[1].Status, model.ShipmentLinks[1].TrackingStatus);
        model.UpdateTrackingStatuses(statuses);
        Equal("Статус не завантажено", model.ShipmentLinks[0].TrackingStatus);
        model.Update(model.Model with { Shipments = [first, second] }, [receipt], []);
        model.UpdateTrackingStatuses(statuses);
        model.Update(model.Model with { Shipments = [first with { ShipmentId = "10000000-0000-0000-0000-000000000003" }, second] }, [receipt], []);
        Equal("Статус не завантажено", model.ShipmentLinks[0].TrackingStatus);
        Equal(statuses[1].Status, model.ShipmentLinks[1].TrackingStatus);
        model.Update(model.Model with { Key = order.Key with { ConnectionId = "another-synthetic-account" } }, [], []);
        True(model.ShipmentLinks.All(link => link.TrackingStatus == "Статус не завантажено"),
            "Changing an order account must clear carrier status projections even for equal TTNs.");

        var fixture = new Fixture();
        fixture.Source.Orders = [order]; fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 10000)];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync(); await OpenOrdersAsync(main, workspace);
        main.CoordinatorManaged = true;
        var labelPrinter = new LabelPrinter(); var dialogs = new LabelDialogs();
        var labelHistory = new ShippingLabelsTests.History();
        using var labels = Labels(workspace, labelHistory, new ShippingLabelsTests.Source(), labelPrinter, dialogs);
        workspace.Labels = labels;
        var window = new MainWindow { DataContext = main };
        var failures = new BindingErrorListener();
        var previousTraceLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Listeners.Add(failures);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            LayoutContent(window); await DrainDispatcherAsync();
            var grid = (DataGrid)window.FindName("MarketplaceOrdersGrid");
            var row = workspace.AllOrderRows.Single();
            row.IsSelectedForLabels = true;
            row.ApplyLabelHistory([history]);
            row.UpdateTrackingStatuses(statuses);
            var column = grid.Columns.Single(c => c.Header?.ToString() == "ТТН");
            grid.ScrollIntoView(row, column); await DrainDispatcherAsync(); LayoutContent(window);
            var cell = column.GetCellContent(row);
            var numbers = VisualChildren<DashboardTextBox>(cell).Where(text => Equals(text.Tag, "OrderTrackingNumber")).ToArray();
            var states = VisualChildren<DashboardTextBox>(cell).Where(text => Equals(text.Tag, "OrderTrackingStatus")).ToArray();
            Equal(2, numbers.Length); Equal(2, states.Length);
            for (var index = 0; index < 2; index++)
            {
                Equal(order.Shipments[index].TrackingNumber, numbers[index].Text);
                Equal(statuses[index].Status, states[index].Text);
                True(ReferenceEquals(labels.PreviewShipmentCommand, numbers[index].Command));
                True(numbers[index].CommandParameter is OrderShipmentLink link && link.Shipment == order.Shipments[index]);
                True(states[index].Command is null && states[index].IsReadOnly && states[index].IsEnabled && states[index].IsReadOnlyCaretVisible);
                True(states[index].FontSize < numbers[index].FontSize && states[index].TextWrapping == TextWrapping.Wrap);
                True(states[index].ToolTip?.ToString()?.Contains(statuses[index].Source) == true);
                var parent = (StackPanel)states[index].Parent;
                True(ReferenceEquals(parent.Children[0], numbers[index]) && ReferenceEquals(parent.Children[1], states[index]),
                    "Each independently selectable status must be directly below its own clickable TTN.");
                states[index].Select(1, 5);
                Equal(states[index].Text.Substring(1, 5), states[index].SelectedText);
                True(ApplicationCommands.Copy.CanExecute(null, states[index]) && states[index].SelectionLength < states[index].Text.Length);
                True(states[index].ContextMenu is not null && states[index].ContextMenu.Items.Count == 2);
            }
            True(row.IsSelectedForLabels && row.HasPrintedLabel && row.HasConfirmedLink);
            Equal(labelStatus, row.LabelPrintStatus);
            Equal(0, fixture.Printer.Calls); Equal(0, labelPrinter.Calls); Equal(0, dialogs.Previews);
            Equal(0, labelHistory.Attempts.Count); Equal(0, fixture.History.Saves); Equal(0, fixture.Links.Saves);
            RenderSyntheticContent(window, "shipment-tracking-status.png");
            True(failures.Messages.Count == 0, "Shipment status binding errors: " + string.Join("\n", failures.Messages));
        }
        finally
        {
            main.CancelPendingOperations(); window.Close(); await DrainDispatcherAsync();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(failures);
            PresentationTraceSources.DataBindingSource.Switch.Level = previousTraceLevel;
        }
    }
    private static async Task DispatchedOrderColorsAsync()
    {
        _xamlCheckpoint = "carrier handover colors and live tracking notifications";
        foreach (var status in new[] { "Прийнято перевізником", "В дорозі до РЦ", "Прийнято на доставку", "Посилка отримана на РЦ", "Готово до видачі", "Видано" })
            True(ShipmentTrackingProgress.IsDispatched(ShippingCarrier.RozetkaDelivery, status), status);
        foreach (var status in new[] { "Відправлення прямує до м. Київ", "Відправлення у м. Київ", "Передано кур’єру", "Відправлення отримано", "  В\u00a0дорозі  " })
            True(ShipmentTrackingProgress.IsDispatched(ShippingCarrier.NovaPoshta, status), status);
        foreach (var carrier in new[] { ShippingCarrier.NovaPoshta, ShippingCarrier.RozetkaDelivery })
            foreach (var status in new[] { "", "Статус недоступний", "Створено рахунок", "Заплановано", "Прийнято", "Готове до транспортування",
                "Підготовка до транспортування", "Не прийнято за реєстром", "Не передано перевізнику", "Повернення передано кур’єру",
                "Отримано запит на повернення (Відмова відправника)", "Відправник самостійно створив цю накладну, але ще не надав до відправки",
                "Очікується, що відправлення буде в дорозі", "На проміжному РЦ" })
                True(!ShipmentTrackingProgress.IsDispatched(carrier, status), status);
        True(!ShipmentTrackingProgress.IsDispatched(ShippingCarrier.Unknown, "В дорозі"));

        var fixture = new Fixture();
        fixture.Settings.Market.Connections.Add(new()
            { Id = "synthetic-dispatch-rz", Marketplace = MarketplaceKind.Rozetka, Enabled = true, Name = "Synthetic Rozetka" });
        var models = Enumerable.Range(0, 6).Select(index => Order() with
        {
            Key = new(index < 3 ? MarketplaceKind.Prom : MarketplaceKind.Rozetka,
                index < 3 ? "prom-test" : "synthetic-dispatch-rz", "dispatch-" + index),
            Number = index.ToString(), CreatedAt = Order().CreatedAt!.Value.AddMinutes(-index),
            Shipments = [new(index < 3 ? "nova_poshta" : "rozetka_delivery", index < 3 ? "2040000000000" + index : "RMP-DISPATCH-" + index)]
        }).ToArray();
        fixture.Source.Orders = models[..3]; fixture.Rozetka.Orders = models[3..];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync(); await OpenOrdersAsync(main, workspace);
        main.CoordinatorManaged = true; workspace.ShowCanceledOrders = true;
        var window = new MainWindow { DataContext = main };
        try
        {
            LayoutContent(window); await DrainDispatcherAsync();
            var grid = (DataGrid)window.FindName("MarketplaceOrdersGrid");
            var rows = models.Select(model => workspace.AllOrderRows.Single(row => row.Key == model.Key)).ToArray();
            for (var index = 0; index < rows.Length; index++)
            {
                var row = rows[index]; var shipment = row.Model.Shipments.Single();
                row.IsSelectedForLabels = true;
                if (index % 3 != 0)
                    row.ApplyLabelHistory([new("synthetic-dispatch", "synthetic-job", "synthetic-printer", DateTimeOffset.UtcNow,
                        LabelSubmissionState.Submitted, [new(ShippingCarrierNames.FromDocumentedDeliveryField(shipment.Carrier),
                            row.Key.ConnectionId, shipment.TrackingNumber, "synthetic-doc", 1, "synthetic-hash") { Order = row.Key }], 7)]);
                row.UpdateTrackingStatuses([new(shipment, index % 3 == 2 ? "В дорозі" : "Створено рахунок")]);
            }
            async Task AssertColor(MarketplaceOrderRowViewModel row, string color)
            {
                grid.ScrollIntoView(row); await DrainDispatcherAsync(); LayoutContent(window);
                var realized = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(row);
                Equal((Color)ColorConverter.ConvertFromString(color), ((SolidColorBrush)realized.Background).Color);
            }
            string[] palette = ["#EDF5FF", "#BFDAFA", "#8CB9EB", "#EFFAF2", "#BCE8C9", "#85CDA0"];
            for (var index = 0; index < rows.Length; index++) await AssertColor(rows[index], palette[index]);
            RenderSyntheticContent(window, "shipment-dispatch-colors.png");
            var sent = rows[2]; var sentShipment = sent.Model.Shipments.Single();
            var printText = sent.LabelPrintStatus;
            sent.UpdateTrackingStatuses([new(sentShipment, "Відправлення прямує до м. Київ", IsStale: true)]);
            await AssertColor(sent, "#8CB9EB");
            True(sent.HasDispatchedShipments && sent.HasPrintedLabel && sent.IsSelectedForLabels);
            Equal(printText, sent.LabelPrintStatus);
            sent.UpdateTrackingStatuses([new(sentShipment, "Відправник самостійно створив цю накладну, але ще не надав до відправки")]);
            await AssertColor(sent, "#BFDAFA");
            sent.UpdateTrackingStatuses([new(sentShipment, "Прийнято перевізником")]);
            await AssertColor(sent, "#8CB9EB");
            var second = new OrderShipment("rozetka_delivery", "RMP-SECOND-DISPATCH");
            sent.Update(sent.Model with { Shipments = [sentShipment, second] }, [], []);
            True(!sent.HasDispatchedShipments, "An untracked second parcel cannot inherit the first parcel's handover.");
            sent.UpdateTrackingStatuses([new(sentShipment, "В дорозі"), new(second, "Прийнято перевізником")]);
            True(sent.HasDispatchedShipments);
            sent.Update(sent.Model with { Shipments = [sentShipment with { TrackingNumber = "replacement-dispatch" }] }, [], []);
            True(!sent.HasDispatchedShipments, "Replacing a TTN must clear the carrier-progress color.");
            var rzSent = rows[5]; var rzShipment = rzSent.Model.Shipments.Single();
            rzSent.Update(rzSent.Model with { Status = "Скасовано", SourceStatusGroup = 3 }, [], []);
            await AssertColor(rzSent, "#FDE8E7");
            rzSent.Update(rzSent.Model with { Key = rzSent.Key with { ConnectionId = "other-synthetic-dispatch" }, Status = "Отримано", SourceStatusGroup = 1 }, [], []);
            True(!rzSent.HasDispatchedShipments && rzSent.TrackingStatusFor(rzShipment) is null);
            Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves); Equal(0, fixture.Links.Saves);
        }
        finally { main.CancelPendingOperations(); window.Close(); await DrainDispatcherAsync(); }
    }
}
