using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CheckboxBatchPrinter.Services;
using CheckboxBatchPrinter.Views;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task ReceiptPrintToolbarDashboardAsync()
    {
        _xamlCheckpoint = "receipt print actions above each dashboard and linked order receipt action";
        var fixture = new Fixture();
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        main.CoordinatorManaged = true;
        var window = new MainWindow { DataContext = main };
        var failures = new BindingErrorListener();
        var priorTraceLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Listeners.Add(failures);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            var footer = (Border)window.FindName("ReceiptPrintFooter");
            True(!VisualChildren<Button>(footer).Any(button =>
                button.Content?.ToString() is "Повторити невдалі" or "Друк вибраних видимих"),
                "Receipt print actions must no longer occupy the bottom footer.");
            var waybillFooter = (Border)window.FindName("WaybillPrintFooter");
            var waybillButtons = VisualChildren<Button>(waybillFooter).ToArray();
            True(waybillButtons.Any(button => BindingOperations.GetBinding(button, Button.CommandProperty)?.Path.Path == "Waybills.RetryFailedCommand") &&
                waybillButtons.Any(button => BindingOperations.GetBinding(button, Button.CommandProperty)?.Path.Path == "Waybills.PrintSelectedCommand"),
                "Moving receipt actions must preserve the separate waybill footer.");

            var receipt = main.Receipts.First(row => row.Id == ReceiptOne);
            receipt.IsSelected = true;
            foreach (var (tabIndex, prefix, gridName) in new[]
                { (0, "AllReceipts", "AllReceiptsGrid"), (1, "OrdersReceipts", "OrdersReceiptsGrid") })
            {
                main.SelectedTabIndex = tabIndex;
                await DrainDispatcherAsync();
                LayoutContent(window);
                await DrainDispatcherAsync();
                LayoutContent(window);
                var toolbar = (StackPanel)window.FindName(prefix + "PrintToolbar");
                var scroll = (ScrollViewer)window.FindName(prefix + "PrintToolbarScroll");
                var retry = (Button)window.FindName(prefix + "RetryFailedButton");
                var print = (Button)window.FindName(prefix + "PrintSelectedButton");
                var grid = (DataGrid)window.FindName(gridName);
                Equal(Orientation.Horizontal, toolbar.Orientation);
                Equal(ScrollBarVisibility.Auto, scroll.HorizontalScrollBarVisibility);
                Equal(ScrollBarVisibility.Disabled, scroll.VerticalScrollBarVisibility);
                True(ReferenceEquals(main.RetryFailedCommand, retry.Command) &&
                    ReferenceEquals(main.PrintSelectedCommand, print.Command),
                    "Receipt toolbar must retain the active table print and retry commands.");
                True(scroll.ActualHeight > 0 && grid.ActualHeight > 0,
                    "Active receipt toolbar and grid must both receive layout space.");
                var content = (FrameworkElement)window.Content;
                True(scroll.TranslatePoint(new Point(0, scroll.ActualHeight), content).Y <=
                    grid.TranslatePoint(new Point(), content).Y + 0.5,
                    "Receipt print actions must be placed above their dashboard.");
                Equal(tabIndex == 0, print.IsEnabled);
                if (tabIndex == 1)
                {
                    receipt.IsSelectedForOrders = true;
                    await DrainDispatcherAsync();
                    True(print.IsEnabled, "Orders receipts print action must use that table's marks.");
                    var orderToolbar = (StackPanel)window.FindName("OrdersWorkspaceToolbar");
                    var combined = (Button)window.FindName("PrintOrdersWithReceiptsButton");
                    var orderReceipts = (Button)window.FindName("PrintOrderReceiptsButton");
                    Equal(orderToolbar.Children.IndexOf(combined) + 1, orderToolbar.Children.IndexOf(orderReceipts));
                    Equal("Друк чеків замовлень", orderReceipts.Content?.ToString());
                    True(ReferenceEquals(main.PrintOrderReceiptsCommand, orderReceipts.Command),
                        "Order receipts action must use selected order links rather than receipt table marks.");
                    RenderSyntheticContent(window, "receipt-print-toolbar-orders.png");
                }
                else RenderSyntheticContent(window, "receipt-print-toolbar-all.png");
            }
            await ReceiptPrintRepeatAcknowledgementAsync();
            Equal(0, fixture.Printer.Calls);
            True(failures.Messages.Count == 0, "Receipt print toolbar binding errors: " + string.Join("\n", failures.Messages));
        }
        finally
        {
            main.CancelPendingOperations();
            window.Close();
            await DrainDispatcherAsync();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(failures);
            PresentationTraceSources.DataBindingSource.Switch.Level = priorTraceLevel;
        }
    }

    private static async Task ReceiptPrintRepeatAcknowledgementAsync()
    {
        _xamlCheckpoint = "compiled receipt confirmation requires explicit repeat acknowledgement";
        foreach (var previousCount in new[] { 0, 1 })
        {
            var batch = new PrintBatchConfirmation("mock-printer", 0,
                [new(1, ReceiptOne, "1", "Prom", "ORDER-41")], previouslyPrintedCount: previousCount);
            var confirmation = new PrintConfirmationWindow(batch);
            try
            {
                LayoutContent(confirmation);
                await DrainDispatcherAsync();
                var confirm = (Button)confirmation.FindName("ConfirmButton");
                var acknowledgement = (CheckBox)confirmation.FindName("RepeatAcknowledgement");
                Equal(previousCount == 0, confirm.IsEnabled);
                Equal(previousCount == 0 ? Visibility.Collapsed : Visibility.Visible,
                    ((StackPanel)acknowledgement.Parent).Visibility);
                if (previousCount == 0) continue;
                ClickCheckBox(acknowledgement);
                await DrainDispatcherAsync();
                True(acknowledgement.IsChecked == true && confirm.IsEnabled,
                    "An explicit acknowledgement must enable the repeat confirmation.");
                ClickCheckBox(acknowledgement);
                await DrainDispatcherAsync();
                True(acknowledgement.IsChecked == false && !confirm.IsEnabled,
                    "Removing the acknowledgement must block repeat printing again.");
            }
            finally
            {
                confirmation.Close();
                await DrainDispatcherAsync();
            }
        }
    }
}
