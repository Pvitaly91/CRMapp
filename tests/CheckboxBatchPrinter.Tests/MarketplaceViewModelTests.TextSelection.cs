using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CheckboxBatchPrinter.Controls;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;
using CheckboxBatchPrinter.Views;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task DashboardTextSelectionAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [Order()];
        fixture.ReceiptSource.Rows = [new ReceiptRecord
        {
            Id = ReceiptOne, Serial = 1, Type = ReceiptTypes.Sell, Status = "DONE", FiscalCode = "FISCAL-123456",
            TotalSumMinor = 10000, FiscalDate = new DateTimeOffset(2026, 9, 23, 11, 0, 0, TimeSpan.FromHours(3))
        }];
        using var waybills = new WaybillsViewModel(new DashboardWaybillSource());
        var (main, workspace) = fixture.Create(waybills: waybills);
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        main.CoordinatorManaged = true;
        var window = new MainWindow { DataContext = main };
        var failures = new BindingErrorListener();
        var level = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Listeners.Add(failures);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            LayoutContent(window);
            await DrainDispatcherAsync();
            var tabs = (TabControl)window.FindName("ReceiptTabs");
            async Task<DashboardTextBox> Cell(DataGrid grid, object row, string header)
            {
                LayoutContent(window);
                var column = grid.Columns.Single(c => c.Header?.ToString() == header);
                grid.ScrollIntoView(row, column);
                await DrainDispatcherAsync();
                LayoutContent(window);
                var content = column.GetCellContent(row);
                var text = content as DashboardTextBox ?? VisualChildren<DashboardTextBox>(content).Single();
                True(text.IsReadOnly && text.IsReadOnlyCaretVisible && text.IsEnabled);
                Equal(new Thickness(0), text.BorderThickness);
                True(text.Text.Length > 1, "The substring test needs a value longer than one character.");
                text.Select(1, Math.Min(3, text.Text.Length - 1));
                Equal(text.Text.Substring(1, Math.Min(3, text.Text.Length - 1)), text.SelectedText);
                True(text.SelectionLength < text.Text.Length, "A fragment must stay selected without expanding to the full cell.");
                True(text.SelectionLength > 0 && ApplicationCommands.Copy.CanExecute(null, text), "Native text copy must accept the selected fragment.");
                True(text.ContextMenu is not null && text.ContextMenu.Items.Count == 2);
                return text;
            }

            tabs.SelectedIndex = 0;
            await DrainDispatcherAsync();
            var all = (DataGrid)window.FindName("AllReceiptsGrid");
            var receipt = main.Receipts.First();
            var fiscal = await Cell(all, receipt, "Фіскальний номер");
            var beforePreview = fixture.Dialogs.PreviewId;
            all.SelectedItem = receipt;
            all.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Control.MouseDoubleClickEvent, Source = fiscal });
            await DrainDispatcherAsync();
            Equal(beforePreview, fixture.Dialogs.PreviewId);
            True(!receipt.IsSelected && !receipt.IsSelectedForOrders);
            var amount = await Cell(all, receipt, "Сума");
            var amountColumn = all.Columns.Single(c => c.Header?.ToString() == "Сума");
            all.SelectedCells.Clear(); all.SelectedCells.Add(new(receipt, amountColumn));
            Equal(MainWindow.BuildDashboardClipboardText(all), amount.Text);

            tabs.SelectedIndex = 1;
            await DrainDispatcherAsync();
            var orders = (DataGrid)window.FindName("MarketplaceOrdersGrid");
            var order = workspace.Orders.Cast<MarketplaceOrderRowViewModel>().First();
            var number = await Cell(orders, order, "№ замовлення");
            Equal(order.Number, number.Text);
            True(!order.IsSelectedForLabels);
            var linked = (DataGrid)window.FindName("OrdersReceiptsGrid");
            await Cell(linked, receipt, "Фіскальний номер");

            main.DateFrom = new(2026, 10, 9); main.DateTo = new(2026, 10, 9);
            tabs.SelectedIndex = 2;
            await DrainDispatcherAsync();
            await main.RefreshActiveAsync();
            var shipments = (DataGrid)window.FindName("AllWaybillsGrid");
            var shipment = waybills.View.Cast<WaybillRowViewModel>().First();
            await Cell(shipments, shipment, "ТТН");
            await Cell(shipments, shipment, "Отримувач");
            LayoutContent(window);
            await DrainDispatcherAsync();
            RenderSyntheticContent(window, "waybill-text-fragment.png");
            Equal(0, fixture.Printer.Calls);
            True(failures.Messages.Count == 0, "Text selection binding errors: " + string.Join("\n", failures.Messages));
        }
        finally
        {
            main.CancelPendingOperations();
            window.Close();
            await DrainDispatcherAsync();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(failures);
            PresentationTraceSources.DataBindingSource.Switch.Level = level;
        }
    }
}
