using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.ViewModels;
using CheckboxBatchPrinter.Views;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    // Run in the existing isolated XAML process, on its main STA and with its
    // SyntheticApplication resources. Do not create the production App.
    private static async Task OrdersDashboardAsync()
    {
        True(Application.Current is not null, "The compiled dashboard needs the isolated XAML application's resources.");
        var fixture = new Fixture();
        fixture.Settings.Market.Connections.Add(new()
            { Id = "rozetka-test", Marketplace = MarketplaceKind.Rozetka, Enabled = true, Name = "Synthetic Rozetka" });
        var orders = Enumerable.Range(0, 48).Select(i => Order() with
        {
            Key = new(i % 2 == 0 ? MarketplaceKind.Prom : MarketplaceKind.Rozetka,
                i % 2 == 0 ? "prom-test" : "rozetka-test", (1000 + i).ToString()),
            Number = $"ORD-{i + 1:D3}", Total = 100 + i,
            ReceiptIds = i == 0 ? [ReceiptOne] : [],
            Shipments = [new(i % 2 == 0 ? "nova_poshta" : "rozetka_delivery", $"SYNTHETIC-TTN-{i + 1:D3}")]
        }).ToArray();
        fixture.Source.Orders = orders.Where(o => o.Key.Marketplace == MarketplaceKind.Prom).ToArray();
        fixture.Rozetka.Orders = orders.Where(o => o.Key.Marketplace == MarketplaceKind.Rozetka).ToArray();
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 10000)];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        main.SelectedTabIndex = 1;
        await main.PrepareOrdersAsync();
        await workspace.SyncAsync();
        var labelPrinter = new LabelPrinter();
        var labelDialogs = new LabelDialogs();
        var labelHistory = new ShippingLabelsTests.History();
        workspace.Labels = Labels(workspace, labelHistory, new ShippingLabelsTests.Source(), labelPrinter, labelDialogs);
        var window = new MainWindow { DataContext = main };
        var bindingFailures = new BindingErrorListener();
        var previousTraceLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingFailures);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            _xamlCheckpoint = "orders dashboard layout";
            LayoutContent(window);
            await DrainDispatcherAsync();
            var grid = (DataGrid)window.FindName("MarketplaceOrdersGrid");
            var receiptGrid = (DataGrid)window.FindName("OrdersReceiptsGrid");
            True(ReferenceEquals(workspace.Orders, grid.ItemsSource));
            foreach (var removed in new[] { "Маркетплейс", "Пов’язані чеки", "Зв’язок із чеками", "Доставка", "Етикетка" })
                True(grid.Columns.All(c => c.Header?.ToString() != removed), $"Removed orders column returned: {removed}.");
            True(receiptGrid.Columns.Any(c => c.Header?.ToString() == "Маркетплейс"),
                "Compact orders columns must not remove the receipt table's marketplace column.");
            var indexColumn = grid.Columns[0];
            var receiptColumn = grid.Columns.Single(c => c.Header?.ToString() == "Відкрити чек");
            var trackingColumn = grid.Columns.Single(c => c.Header?.ToString() == "ТТН");
            workspace.Orders.SortDescriptions.Clear();
            workspace.Orders.SortDescriptions.Add(new(nameof(MarketplaceOrderRowViewModel.Number), ListSortDirection.Ascending));
            await DrainDispatcherAsync();
            var rows = workspace.Orders.Cast<MarketplaceOrderRowViewModel>().ToArray();
            Equal(48, rows.Length);
            for (var i = 0; i < rows.Length; i++)
            {
                if (i % 4 < 2) continue;
                var shipment = ShippingCarrierNames.ForOrder(rows[i].Model).Single();
                rows[i].ApplyLabelHistory([new("synthetic-dashboard-" + i, "job", "synthetic-printer", DateTimeOffset.UtcNow,
                    LabelSubmissionState.Submitted,
                    [new(shipment.Carrier, shipment.Order.ConnectionId, shipment.TrackingNumber, "synthetic-doc", 1, "synthetic-hash")], 42)]);
            }
            await DrainDispatcherAsync();
            AssertDashboardIndices(workspace);

            async Task<DataGridRow> Realize(MarketplaceOrderRowViewModel model, DataGridColumn column)
            {
                True(grid.Items.Contains(model), $"Dashboard order {model.Number} is not in the current view.");
                ScrollViewer? scroll = null;
                // A hidden Window does not run its normal layout pass after a view
                // Reset. Measure/arrange its content before and after WPF's deferred
                // ScrollIntoView work, leaving production virtualization enabled.
                for (var attempt = 0; attempt < 6; attempt++)
                {
                    LayoutContent(window);
                    grid.ScrollIntoView(model, column);
                    await DrainDispatcherAsync();
                    LayoutContent(window);
                    grid.UpdateLayout();
                    if (grid.ItemContainerGenerator.ContainerFromItem(model) is DataGridRow row &&
                        ReferenceEquals(row.Item, model) && column.GetCellContent(model) is not null)
                        return row;

                    // View resets can leave the hidden scroll host at its old
                    // offset. Move that real host to the target's current position;
                    // the next ScrollIntoView then resolves the exact row/cell.
                    scroll ??= grid.Template.FindName("DG_ScrollViewer", grid) as ScrollViewer
                        ?? VisualChildren<ScrollViewer>(grid).FirstOrDefault();
                    if (scroll is not null)
                    {
                        var index = grid.Items.IndexOf(model);
                        var itemUnits = scroll.CanContentScroll &&
                            VirtualizingPanel.GetScrollUnit(grid) == ScrollUnit.Item;
                        var measuredRowHeight = VisualChildren<DataGridRow>(grid)
                            .Select(r => r.ActualHeight).FirstOrDefault(h => h > 0);
                        var rowHeight = double.IsNaN(grid.RowHeight)
                            ? measuredRowHeight > 0 ? measuredRowHeight : 28 : grid.RowHeight;
                        scroll.ScrollToVerticalOffset(itemUnits ? index : index * rowHeight);
                    }
                    await DrainDispatcherAsync();
                    LayoutContent(window);
                    if (grid.ItemContainerGenerator.ContainerFromItem(model) is DataGridRow scrolledRow &&
                        ReferenceEquals(scrolledRow.Item, model) && column.GetCellContent(model) is not null)
                        return scrolledRow;
                }
                throw new InvalidOperationException($"Dashboard order {model.Number} was not realized after layout/scroll retries. " +
                    $"View index: {grid.Items.IndexOf(model)}; viewport: {scroll?.ViewportHeight}; offset: {scroll?.VerticalOffset}; " +
                    $"grid size: {grid.ActualWidth} x {grid.ActualHeight}; target container: {grid.ItemContainerGenerator.ContainerFromItem(model)}; " +
                    $"cell: {column.GetCellContent(model)}; realized: {string.Join(",", VisualChildren<DataGridRow>(grid).Select(r => $"{r.GetIndex()}:{(r.Item as MarketplaceOrderRowViewModel)?.Number}"))}.");
            }
            async Task AssertIndexCell(MarketplaceOrderRowViewModel model)
            {
                await Realize(model, indexColumn);
                var text = VisualChildren<TextBlock>(indexColumn.GetCellContent(model))
                    .Single(t => Equals(t.Tag, "OrderRowIndex"));
                Equal(model.DisplayIndex.ToString(), text.Text);
                Equal(workspace.Orders.Cast<MarketplaceOrderRowViewModel>().ToList().IndexOf(model) + 1, model.DisplayIndex);
            }
            var colors = new[] { Color.FromRgb(0xED, 0xF5, 0xFF), Color.FromRgb(0xEF, 0xFA, 0xF2),
                Color.FromRgb(0xBF, 0xDA, 0xFA), Color.FromRgb(0xBC, 0xE8, 0xC9) };
            for (var i = 0; i < 4; i++)
            {
                var row = await Realize(rows[i], indexColumn);
                True(row.Background is SolidColorBrush brush && brush.Color == colors[i],
                    $"Wrong marketplace/printed palette for {rows[i].Number}: {row.Background}.");
                await AssertIndexCell(rows[i]);
            }

            // Both an unlinked order and a linked order retain the independent info
            // button. Opening a receipt still receives that receipt, not the order.
            foreach (var model in new[] { rows[1], rows[0] })
            {
                await Realize(model, receiptColumn);
                var cell = receiptColumn.GetCellContent(model);
                var info = VisualChildren<Button>(cell).Single(b => Equals(b.Tag, "OrderLinkInfo"));
                True(info.ToolTip is ToolTip, "Every order needs a link-information tooltip, including unlinked orders.");
                var tooltip = (ToolTip)info.ToolTip;
                tooltip.PlacementTarget = info;
                var contextBinding = tooltip.GetBindingExpression(FrameworkElement.DataContextProperty);
                True(contextBinding is not null, "Tooltip data must follow its placement target's order.");
                contextBinding!.UpdateTarget();
                await DrainDispatcherAsync();
                Equal(model, tooltip.DataContext);
                var content = (FrameworkElement)tooltip.Content;
                content.Measure(new Size(600, 1200));
                content.Arrange(new Rect(content.DesiredSize));
                content.UpdateLayout();
                var text = string.Join("\n", VisualChildren<TextBlock>(content).Select(t => t.Text));
                True(text.Contains(model.Marketplace) && text.Contains(model.LinkStatus),
                    "Info must preserve the marketplace and former link-status text.");
                if (model.LinkedReceiptsDisplay.Length > 0) True(text.Contains(model.LinkedReceiptsDisplay));
                if (model.LinkExplanation.Length > 0) True(text.Contains(model.LinkExplanation));
                var previews = VisualChildren<Button>(cell).Where(b => ReferenceEquals(b.Command, main.PreviewCommand)).ToArray();
                Equal(model.LinkedReceipts.Count, previews.Length);
                foreach (var preview in previews)
                    True(model.LinkedReceipts.Any(r => ReferenceEquals(r, preview.CommandParameter)),
                        "Receipt-preview parameters changed when the info button was added.");
            }
            Equal(0, rows[1].LinkedReceipts.Count);
            Equal(1, rows[0].LinkedReceipts.Count);

            await Realize(rows[0], trackingColumn);
            var trackingCell = trackingColumn.GetCellContent(rows[0]);
            var trackingLink = VisualChildren<TextBlock>(trackingCell).SelectMany(t => t.Inlines.OfType<Hyperlink>()).Single();
            True(ReferenceEquals(workspace.Labels.PreviewShipmentCommand, trackingLink.Command));
            True(trackingLink.Inlines.OfType<Run>().Single().Text == rows[0].ShipmentLinks.Single().TrackingNumber);
            await ExecuteAsync(trackingLink.Command!, parameter: trackingLink.CommandParameter);
            Equal(1, labelDialogs.Previews); Equal(0, labelDialogs.Choices); Equal(0, labelPrinter.Calls); Equal(0, labelHistory.Attempts.Count);
            True(labelDialogs.PreviewedBatch!.Pages.All(p => p.Shipment.TrackingNumber == rows[0].ShipmentLinks.Single().TrackingNumber));

            // Exercise the real checkbox binding before view resets. Hiding an order
            // may clear its current selection, but must preserve its label checkmark.
            await Realize(rows[0], indexColumn);
            grid.SelectedItem = rows[0];
            await DrainDispatcherAsync();
            var checkbox = VisualChildren<CheckBox>(indexColumn.GetCellContent(rows[0])).Single();
            ClickCheckBox(checkbox);
            await DrainDispatcherAsync();
            True(rows[0].IsSelectedForLabels);
            Equal(rows[0], workspace.SelectedOrder);
            workspace.OrderFilter = "Prom";
            await DrainDispatcherAsync();
            Equal(24, workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Count());
            AssertDashboardIndices(workspace);
            Equal(rows[0], workspace.SelectedOrder);
            workspace.Orders.SortDescriptions.Clear();
            workspace.Orders.SortDescriptions.Add(new(nameof(MarketplaceOrderRowViewModel.Number), ListSortDirection.Descending));
            await DrainDispatcherAsync();
            AssertDashboardIndices(workspace);
            await AssertIndexCell(rows[0]);
            True(rows[0].IsSelectedForLabels && ReferenceEquals(rows[0], workspace.SelectedOrder));
            workspace.OrderFilter = "Rozetka";
            await DrainDispatcherAsync();
            AssertDashboardIndices(workspace);
            True(rows[0].IsSelectedForLabels, "Filtering out an order erased its label mark.");
            workspace.OrderFilter = "Усі";
            workspace.OrderSearch = "ORD-00";
            await DrainDispatcherAsync();
            Equal(9, workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Count());
            AssertDashboardIndices(workspace);
            workspace.OrderSearch = "";
            await DrainDispatcherAsync();
            AssertDashboardIndices(workspace);

            // The compiled grid has more rows than its viewport. Scrolling between
            // distant items catches numbers accidentally tied to recycled containers.
            _xamlCheckpoint = "orders dashboard virtualized numbering";
            foreach (var model in new[] { rows[^1], rows[0], rows[23], rows[1] }) await AssertIndexCell(model);
            True(grid.EnableRowVirtualization, "Dashboard numbering must work with production row virtualization enabled.");
            True(rows[0].IsSelectedForLabels);
            await Realize(rows[0], indexColumn);
            Equal<bool?>(true, VisualChildren<CheckBox>(indexColumn.GetCellContent(rows[0])).Single().IsChecked);

            workspace.Orders.SortDescriptions.Clear();
            workspace.Orders.SortDescriptions.Add(new(nameof(MarketplaceOrderRowViewModel.Number), ListSortDirection.Ascending));
            grid.SelectedItem = null;
            await Realize(rows[0], indexColumn);
            LayoutContent(window);
            await DrainDispatcherAsync();
            RenderSyntheticContent(window, "orders-dashboard.png");
            RenderSyntheticContent(window, "orders-dashboard-mixed-palette.png");
            await Realize(rows[0], trackingColumn);
            RenderSyntheticContent(window, "tracking-links.png");
            Equal(0, fixture.Printer.Calls);
            Equal(0, fixture.Links.Saves);
            True(bindingFailures.Messages.Count == 0,
                "Dashboard binding errors: " + string.Join("\n", bindingFailures.Messages));
        }
        finally
        {
            window.Close();
            await DrainDispatcherAsync();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingFailures);
            PresentationTraceSources.DataBindingSource.Switch.Level = previousTraceLevel;
        }
    }

    private static void AssertDashboardIndices(MarketplaceWorkspaceViewModel workspace)
    {
        var visible = workspace.Orders.Cast<MarketplaceOrderRowViewModel>().ToArray();
        for (var i = 0; i < visible.Length; i++)
            Equal(i + 1, visible[i].DisplayIndex);
    }
}
