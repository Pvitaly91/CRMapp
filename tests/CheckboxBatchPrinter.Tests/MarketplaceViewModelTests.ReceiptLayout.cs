using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CheckboxBatchPrinter.Controls;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.ViewModels;
using CheckboxBatchPrinter.Views;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static Task ReceiptCombinedDateTimeAsync()
    {
        ReceiptRowViewModel Row(string id, DateTimeOffset? date) => new(new ReceiptRecord { Id = id, FiscalDate = date });
        var latest = Row("latest", new(2026, 10, 15, 22, 30, 45, TimeSpan.Zero));
        var earlierTime = Row("earlier-time", new(2026, 10, 15, 22, 29, 59, TimeSpan.Zero));
        var earlierMonth = Row("earlier-month", new(2026, 9, 30, 22, 30, 45, TimeSpan.Zero));
        var unknown = Row("unknown", null);
        Equal<DateTime?>(new(2026, 10, 16, 1, 30, 45), latest.LocalDateTime);
        Equal<DateTime?>(new(2026, 10, 16), latest.LocalDate);
        Equal("01:30:45", latest.LocalTime);
        True(unknown.LocalDateTime is null && unknown.LocalDate is null);
        Equal("—", unknown.LocalTime);

        var view = new ListCollectionView(new[] { earlierTime, unknown, latest, earlierMonth });
        view.SortDescriptions.Add(new(nameof(ReceiptRowViewModel.LocalDateTime), ListSortDirection.Descending));
        True(view.Cast<ReceiptRowViewModel>().Select(row => row.Id)
            .SequenceEqual(["latest", "earlier-time", "earlier-month", "unknown"]),
            "Combined date sorting must compare actual dates and seconds, not formatted text.");

        var changes = new List<string?>();
        latest.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        latest.Update(new ReceiptRecord { Id = "latest", CreatedAt = new(2026, 12, 1, 22, 30, 45, TimeSpan.Zero) });
        Equal<DateTime?>(new(2026, 12, 2, 0, 30, 45), latest.LocalDateTime);
        Equal("00:30:45", latest.LocalTime);
        foreach (var property in new[] { nameof(ReceiptRowViewModel.LocalDateTime), nameof(ReceiptRowViewModel.LocalDate), nameof(ReceiptRowViewModel.LocalTime) })
            True(changes.Contains(property), $"Refreshed receipt did not notify {property}.");
        return Task.CompletedTask;
    }

    private static async Task ReceiptLayoutDashboardAsync()
    {
        _xamlCheckpoint = "combined receipt date and early preview columns";
        var fixture = new Fixture();
        fixture.Source.Orders = [Order() with { Total = 123456789.12m }];
        fixture.ReceiptSource.Rows = [new ReceiptRecord
        {
            Id = ReceiptOne, Serial = 1, Type = ReceiptTypes.Sell, Status = "DONE", TotalSumMinor = 12345,
            FiscalDate = new(2026, 9, 23, 8, 7, 6, TimeSpan.Zero)
        }];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        main.CoordinatorManaged = true;
        main.SelectedTabIndex = 0;
        var window = new MainWindow { DataContext = main };
        var failures = new BindingErrorListener();
        var priorTraceLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Listeners.Add(failures);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            var receipt = main.Receipts.Single();
            foreach (var (tabIndex, gridName) in new[] { (0, "AllReceiptsGrid"), (1, "OrdersReceiptsGrid") })
            {
                main.SelectedTabIndex = tabIndex;
                await DrainDispatcherAsync();
                LayoutContent(window);
                await DrainDispatcherAsync();
                LayoutContent(window);
                var grid = (DataGrid)window.FindName(gridName);
                void AssertAmountSummary(string name, string expected, DataGrid table)
                {
                    var summary = (TextBlock)window.FindName(name);
                    Equal(expected, summary.Text);
                    Equal(TextWrapping.NoWrap, summary.TextWrapping);
                    True(summary.ActualWidth > 0 && summary.ActualHeight > 0);
                    var scrollName = table.Name switch
                    {
                        "MarketplaceOrdersGrid" => "OrdersTotalsScroll",
                        "OrdersReceiptsGrid" => "OrdersReceiptsTotalsScroll",
                        _ => "AllReceiptsTotalsScroll"
                    };
                    var scroll = (ScrollViewer)window.FindName(scrollName);
                    True(scroll.ActualWidth > 0 && scroll.ActualWidth <= table.ActualWidth,
                        $"{name} must remain inside a scrollable footer constrained to its table's width.");
                    Equal(ScrollBarVisibility.Auto, scroll.HorizontalScrollBarVisibility);
                    Equal(ScrollBarVisibility.Disabled, scroll.VerticalScrollBarVisibility);
                    var content = (FrameworkElement)window.Content;
                    True(summary.TranslatePoint(new Point(), content).Y >=
                        table.TranslatePoint(new Point(0, table.ActualHeight), content).Y,
                        $"{name} must remain below the table outside its scrolling content.");
                }
                AssertAmountSummary(tabIndex == 0 ? "AllReceiptsAmountSummary" : "OrdersReceiptsAmountSummary",
                    main.ActiveTab.AmountSummary, grid);
                AssertAmountSummary(tabIndex == 0 ? "AllReceiptsReturnAmountSummary" : "OrdersReceiptsReturnAmountSummary",
                    main.ActiveTab.ReturnAmountSummary, grid);
                var receiptSummary = (TextBlock)window.FindName(tabIndex == 0 ? "AllReceiptsAmountSummary" : "OrdersReceiptsAmountSummary");
                var returnSummary = (TextBlock)window.FindName(tabIndex == 0 ? "AllReceiptsReturnAmountSummary" : "OrdersReceiptsReturnAmountSummary");
                var rootContent = (FrameworkElement)window.Content;
                True(Math.Abs(receiptSummary.TranslatePoint(new Point(), rootContent).Y -
                    returnSummary.TranslatePoint(new Point(), rootContent).Y) < 0.5,
                    "Receipt total and return subtotal must occupy one line.");
                if (tabIndex == 1)
                {
                    var orderGrid = (DataGrid)window.FindName("MarketplaceOrdersGrid");
                    AssertAmountSummary("OrdersAmountSummary", workspace.OrderAmountSummary,
                        orderGrid);
                    AssertAmountSummary("CombinedOrdersAmountSummary", workspace.CombinedOrderAmountSummary, orderGrid);
                    AssertAmountSummary("CanceledOrdersAmountSummary", workspace.CanceledOrderAmountSummary, orderGrid);
                    var first = (TextBlock)window.FindName("OrdersAmountSummary");
                    var combined = (TextBlock)window.FindName("CombinedOrdersAmountSummary");
                    var canceled = (TextBlock)window.FindName("CanceledOrdersAmountSummary");
                    var lineY = first.TranslatePoint(new Point(), rootContent).Y;
                    True(new[] { combined, canceled, receiptSummary, returnSummary }.All(summary =>
                        Math.Abs(summary.TranslatePoint(new Point(), rootContent).Y - lineY) < 0.5),
                        "Both order and receipt footers must align their totals on one horizontal line.");
                    var totalsScroll = (ScrollViewer)window.FindName("OrdersTotalsScroll");
                    True(totalsScroll.ScrollableWidth > 0, "Long real bound amounts must be available through horizontal scrolling.");
                    totalsScroll.ScrollToRightEnd();
                    await DrainDispatcherAsync();
                    LayoutContent(window);
                    True(canceled.TranslatePoint(new Point(), totalsScroll).X >= -0.5 &&
                        canceled.TranslatePoint(new Point(canceled.ActualWidth, 0), totalsScroll).X <= totalsScroll.ActualWidth + 0.5,
                        "The final canceled subtotal must be fully reachable when scrolling long totals.");
                    totalsScroll.ScrollToLeftEnd();
                    await DrainDispatcherAsync();
                    LayoutContent(window);
                }
                async Task<FrameworkElement> RealizeCell(DataGridColumn column)
                {
                    True(grid.Items.Contains(receipt), $"{gridName} does not contain the expected receipt.");
                    ScrollViewer? scroll = null;
                    for (var attempt = 0; attempt < 6; attempt++)
                    {
                        LayoutContent(window);
                        grid.ScrollIntoView(receipt, column);
                        await DrainDispatcherAsync();
                        // Hidden windows do not schedule their own layout after a
                        // tab/view reset; realize the requested column after draining.
                        LayoutContent(window);
                        grid.UpdateLayout();
                        if (column.GetCellContent(receipt) is { } cell) return cell;
                        scroll ??= grid.Template.FindName("DG_ScrollViewer", grid) as ScrollViewer
                            ?? VisualChildren<ScrollViewer>(grid).FirstOrDefault();
                        if (scroll is not null)
                        {
                            var index = grid.Items.IndexOf(receipt);
                            var itemUnits = scroll.CanContentScroll && VirtualizingPanel.GetScrollUnit(grid) == ScrollUnit.Item;
                            var measuredHeight = VisualChildren<DataGridRow>(grid).Select(row => row.ActualHeight).FirstOrDefault(height => height > 0);
                            var rowHeight = double.IsNaN(grid.RowHeight) ? measuredHeight > 0 ? measuredHeight : 28 : grid.RowHeight;
                            scroll.ScrollToVerticalOffset(itemUnits ? index : index * rowHeight);
                        }
                        await DrainDispatcherAsync();
                    }
                    throw new InvalidOperationException($"{gridName} did not realize receipt {receipt.Id}, column {column.DisplayIndex}; " +
                        $"items: {grid.Items.Count}; size: {grid.ActualWidth}x{grid.ActualHeight}; " +
                        $"viewport: {scroll?.ViewportHeight}; offset: {scroll?.VerticalOffset}.");
                }
                True(grid.Columns.All(column => column.Header?.ToString() is not ("Дата" or "Час")),
                    $"{gridName} retained separate date/time columns.");
                var dateColumn = (DashboardTextColumn)grid.Columns.Single(column => column.Header?.ToString() == "Дата й час");
                var amountColumn = (DashboardTextColumn)grid.Columns.Single(column => column.Header?.ToString() == "Сума");
                Equal(nameof(ReceiptRowViewModel.LocalDateTime), dateColumn.SortMemberPath);
                Equal(nameof(ReceiptRowViewModel.LocalDateTime), ((Binding)dateColumn.Binding).Path.Path);
                Equal("dd.MM.yyyy HH:mm:ss", ((Binding)dateColumn.Binding).StringFormat);
                Equal(dateColumn.DisplayIndex + 1, amountColumn.DisplayIndex);
                var previewColumn = grid.Columns[amountColumn.DisplayIndex + 1];
                True(previewColumn is DataGridTemplateColumn && !previewColumn.CanUserSort,
                    $"{gridName} must place its preview immediately after the amount.");
                var date = (DashboardTextBox)await RealizeCell(dateColumn);
                Equal(receipt.LocalDateTime!.Value.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.CurrentCulture), date.Text);
                True(date.IsReadOnly && date.IsReadOnlyCaretVisible);
                var amount = (DashboardTextBox)await RealizeCell(amountColumn);
                Equal(FontWeights.Bold, amount.FontWeight);
                var preview = VisualChildren<Button>(await RealizeCell(previewColumn)).Single();
                Equal("Перегляд", preview.Content?.ToString());
                True(ReferenceEquals(main.PreviewCommand, preview.Command) && ReferenceEquals(receipt, preview.CommandParameter),
                    $"{gridName} preview lost its receipt parameter or window command.");
                fixture.Dialogs.PreviewId = null;
                await ExecuteAsync(preview.Command!, parameter: preview.CommandParameter);
                Equal(receipt.Id, fixture.Dialogs.PreviewId);
            }
            True(!receipt.IsSelected && !receipt.IsSelectedForOrders, "Preview must not change either table's print selection.");
            Equal(0, fixture.Printer.Calls);
            True(failures.Messages.Count == 0, "Receipt layout binding errors: " + string.Join("\n", failures.Messages));
            RenderSyntheticContent(window, "receipt-date-time-preview.png");
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
}
