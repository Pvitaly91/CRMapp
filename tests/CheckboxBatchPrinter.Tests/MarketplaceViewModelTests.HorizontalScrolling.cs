using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using CheckboxBatchPrinter.Controls;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.ViewModels;
using CheckboxBatchPrinter.Views;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    // Exercise the exact compiled columns and templates at a narrow dashboard
    // viewport. Synthetic data and a hidden host keep this independent of the
    // user's accounts, live services, desktop, clipboard, and print queues.
    private static async Task HorizontalDashboardScrollingAsync()
    {
        _xamlCheckpoint = "horizontal dashboard deferred dragging with 1000 synthetic rows";
        var fixture = new Fixture();
        var (main, _) = fixture.Create();
        main.CoordinatorManaged = true;
        var sourceWindow = new MainWindow { DataContext = main };
        var receipts = Enumerable.Range(0, 1000).Select(index => new ReceiptRowViewModel(new ReceiptRecord
        {
            Id = $"scroll-receipt-{index:D4}", Serial = index + 1,
            FiscalCode = $"FISCAL-{index:D4}", Status = "DONE", Type = ReceiptTypes.Sell,
            TotalSumMinor = 10000 + index,
            FiscalDate = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.FromHours(3)).AddSeconds(index)
        }) { IsSelected = index % 11 == 0, IsSelectedForOrders = index % 13 == 0 }).ToArray();
        var orders = Enumerable.Range(0, 1000).Select(index => new MarketplaceOrderRowViewModel(Order() with
        {
            Key = new(MarketplaceKind.Prom, "prom-test", $"scroll-order-{index:D4}"),
            Number = $"ORDER-{index:D4}", ReceiptIds = [],
            Shipments = [new("nova_poshta", $"20450000{index:D6}")]
        }) { IsSelectedForLabels = index % 17 == 0 }).ToArray();
        var waybills = Enumerable.Range(0, 1000).Select(index => new WaybillRowViewModel(new WaybillRecord(
            ShippingCarrier.NovaPoshta, "synthetic-np", "Synthetic account", $"doc-{index:D4}",
            $"20450000{index:D6}", new DateTime(2026, 9, 23, 10, 0, 0).AddSeconds(index),
            $"Synthetic status {index}", $"Recipient {index}", $"City {index}", 100 + index, 1))
            { IsSelected = index % 19 == 0 }).ToArray();
        var scenarios = new[]
        {
            new HorizontalScrollScenario("AllReceiptsGrid", receipts.Cast<object>().ToArray(),
                nameof(ReceiptRowViewModel.FiscalCode), row => ((ReceiptRowViewModel)row).IsSelected,
                row => ((ReceiptRowViewModel)row).FiscalCode),
            new HorizontalScrollScenario("OrdersReceiptsGrid", receipts.Cast<object>().ToArray(),
                nameof(ReceiptRowViewModel.FiscalCode), row => ((ReceiptRowViewModel)row).IsSelectedForOrders,
                row => ((ReceiptRowViewModel)row).FiscalCode),
            new HorizontalScrollScenario("MarketplaceOrdersGrid", orders.Cast<object>().ToArray(),
                nameof(MarketplaceOrderRowViewModel.Number), row => ((MarketplaceOrderRowViewModel)row).IsSelectedForLabels,
                row => ((MarketplaceOrderRowViewModel)row).Number),
            new HorizontalScrollScenario("AllWaybillsGrid", waybills.Cast<object>().ToArray(),
                nameof(WaybillRowViewModel.TrackingNumber), row => ((WaybillRowViewModel)row).IsSelected,
                row => ((WaybillRowViewModel)row).TrackingNumber)
        };
        try
        {
            foreach (var scenario in scenarios)
            {
                _xamlCheckpoint = "horizontal deferred dragging " + scenario.Name;
                var grid = (DataGrid)sourceWindow.FindName(scenario.Name);
                True(grid.Parent is Panel, "Expected the compiled dashboard's normal panel parent.");
                ((Panel)grid.Parent).Children.Remove(grid);
                grid.Resources = sourceWindow.Resources;
                var view = new ListCollectionView(scenario.Rows);
                grid.ItemsSource = view;
                var root = new Grid();
                root.Children.Add(grid);
                var host = new Window { DataContext = main, Content = root, Resources = sourceWindow.Resources };
                try
                {
                    void Layout()
                    {
                        grid.ApplyTemplate();
                        root.Measure(new Size(480, 320));
                        root.Arrange(new Rect(0, 0, 480, 320));
                        root.UpdateLayout();
                    }
                    async Task Settle()
                    {
                        Layout();
                        await DrainDispatcherAsync();
                        Layout();
                    }
                    await Settle();
                    // DataGrid clears the previous view's sort while initially
                    // connecting ItemsSource. Apply the user's sort afterward.
                    view.SortDescriptions.Add(new(scenario.SortProperty, ListSortDirection.Descending));
                    await Settle();
                    var scroll = grid.Template.FindName("DG_ScrollViewer", grid) as ScrollViewer
                        ?? VisualChildren<ScrollViewer>(grid).First();
                    True(scroll.ScrollableWidth > 300, "The regression requires actual horizontal overflow.");
                    var sorted = view.Cast<object>().ToArray();
                    var marked = scenario.Rows.Where(scenario.IsMarked).ToArray();
                    var identity = grid.Columns.Single(column => column.SortMemberPath == scenario.SortProperty ||
                        column is DataGridBoundColumn { Binding: Binding binding } && binding.Path?.Path == scenario.SortProperty);
                    var watch = Stopwatch.StartNew();

                    void Observe(string position)
                    {
                        var rows = VisualChildren<DataGridRow>(grid).Where(row => row.GetIndex() >= 0).ToArray();
                        var cells = rows.SelectMany(row => VisualChildren<DataGridCell>(row)).ToArray();
                        var columns = grid.Columns.Where(column => column.Visibility == Visibility.Visible)
                            .OrderBy(column => column.DisplayIndex).ToArray();
                        var left = 0d;
                        var visibleColumns = 0;
                        foreach (var column in columns)
                        {
                            var right = left + column.ActualWidth;
                            if (right > scroll.HorizontalOffset && left < scroll.HorizontalOffset + scroll.ViewportWidth)
                                visibleColumns++;
                            left = right;
                        }
                        var maxCells = rows.Select(row => VisualChildren<DataGridCell>(row).Count()).DefaultIfEmpty().Max();
                        var realizedColumns = string.Join(',', rows.Take(1).SelectMany(row => VisualChildren<DataGridCell>(row))
                            .Select(cell => $"{cell.Column.DisplayIndex}:{cell.ActualWidth:F0}"));
                        Console.WriteLine($"SCROLL {scenario.Name} {position}: rows={rows.Length}/1000 " +
                            $"cells={cells.Length} max-cells-per-row={maxCells}/{columns.Length} " +
                            $"viewport-columns={visibleColumns} offset={scroll.HorizontalOffset:F0} " +
                            $"column-virtualization={grid.EnableColumnVirtualization} row-height={rows.FirstOrDefault()?.ActualHeight:F0} " +
                            $"realized-column-widths=[{realizedColumns}] elapsed-ms={watch.ElapsedMilliseconds}");
                        True(rows.Length is >= 1 and <= 80,
                            $"{scenario.Name} {position}: row virtualization must bound the 1000-row data set; realized {rows.Length}.");
                        // Keep these interactive text cells cached. The controlled
                        // 13-position sweep was 15-45 ms with cached columns,
                        // versus 107-580 ms when recycling columns recreated
                        // their editors. Fewer cells alone is not a speed test.
                    }

                    async Task AssertIdentity(object item)
                    {
                        bool HasIdentity()
                        {
                            var content = identity.GetCellContent(item);
                            return content is DashboardTextBox text && text.Text == scenario.IdentityText(item) ||
                                content is not null && VisualChildren<DashboardTextBox>(content)
                                    .Any(nested => nested.Text == scenario.IdentityText(item));
                        }
                        for (var attempt = 0; attempt < 3; attempt++)
                        {
                            grid.ScrollIntoView(item, identity);
                            await Settle();
                            if (HasIdentity()) return;
                            // Hidden Windows do not run the normal deferred
                            // scroll/layout pass. Set the existing real host's
                            // vertical offset before retrying its exact cell.
                            var rowHeight = VisualChildren<DataGridRow>(grid).Select(row => row.ActualHeight)
                                .FirstOrDefault(height => height > 0);
                            var itemUnits = scroll.CanContentScroll && VirtualizingPanel.GetScrollUnit(grid) == ScrollUnit.Item;
                            scroll.ScrollToVerticalOffset(itemUnits ? grid.Items.IndexOf(item)
                                : grid.Items.IndexOf(item) * (rowHeight > 0 ? rowHeight : 32));
                            await Settle();
                            if (HasIdentity()) return;
                        }
                        throw new InvalidOperationException(
                            $"{scenario.Name}: a recycled dashboard cell must display its current row's identity. " +
                            $"Expected {scenario.IdentityText(item)}, actual {identity.GetCellContent(item)}, " +
                            $"offset={scroll.VerticalOffset}, rows={string.Join(',', VisualChildren<DataGridRow>(grid).Select(row => row.GetIndex()))}.");
                    }

                    Observe("left");
                    await AssertIdentity(sorted[0]);
                    scroll.ScrollToRightEnd(); await Settle(); Observe("right");
                    scroll.ScrollToLeftEnd(); await Settle(); Observe("left-again");
                    await AssertIdentity(sorted[0]);
                    await AssertIdentity(sorted[700]); Observe("row-700");
                    scroll.ScrollToRightEnd(); await Settle(); Observe("row-700-right");
                    scroll.ScrollToLeftEnd(); await Settle();
                    await AssertIdentity(sorted[700]);
                    await AssertIdentity(sorted[0]); Observe("returned");
                    var sweep = Stopwatch.StartNew();
                    var minHeight = double.MaxValue;
                    var maxHeight = 0d;
                    for (var step = 0; step <= 12; step++)
                    {
                        scroll.ScrollToHorizontalOffset(scroll.ScrollableWidth * step / 12);
                        await Settle();
                        foreach (var row in VisualChildren<DataGridRow>(grid))
                        {
                            minHeight = Math.Min(minHeight, row.ActualHeight);
                            maxHeight = Math.Max(maxHeight, row.ActualHeight);
                        }
                    }
                    Console.WriteLine($"SCROLL-SWEEP {scenario.Name}: steps=13 elapsed-ms={sweep.ElapsedMilliseconds} " +
                        $"row-height-range={minHeight:F0}-{maxHeight:F0}");
                    scroll.ScrollToLeftEnd(); await Settle();
                    await AssertIdentity(sorted[0]);
                    // Use the same public routed commands emitted by WPF's
                    // horizontal thumb: move the thumb repeatedly, then commit
                    // its final value on release. No OS input or timing limit.
                    scroll.ScrollToLeftEnd(); await Settle();
                    True(scroll.IsDeferredScrollingEnabled,
                        scenario.Name + ": the actual DataGrid scroll host must defer thumb-drag content updates.");
                    var contentOffset = scroll.ContentHorizontalOffset;
                    var endColumn = grid.Columns.Where(column => column.Visibility == Visibility.Visible)
                        .MaxBy(column => column.DisplayIndex)!;
                    var endContent = endColumn.GetCellContent(sorted[0]);
                    True(endContent is not null, scenario.Name + ": cached end-column content must remain available.");
                    var endX = endContent!.TranslatePoint(new Point(), grid).X;
                    var drag = Stopwatch.StartNew();
                    var finalOffset = scroll.ScrollableWidth;
                    var horizontalBar = scroll.Template.FindName("PART_HorizontalScrollBar", scroll) as ScrollBar
                        ?? VisualChildren<ScrollBar>(scroll).Single(bar => bar.Orientation == Orientation.Horizontal);
                    for (var step = 1; step <= 4; step++)
                    {
                        var target = finalOffset * step / 4;
                        True(ScrollBar.DeferScrollToHorizontalOffsetCommand.CanExecute(target, horizontalBar),
                            scenario.Name + ": the real horizontal drag command must be enabled.");
                        ScrollBar.DeferScrollToHorizontalOffsetCommand.Execute(target, horizontalBar);
                        // WPF's CLR HorizontalOffset getter returns the cached
                        // content offset during a deferred drag. The scrollbar
                        // binds to the dependency property, which holds the
                        // pending thumb value (ScrollViewer.cs in dotnet/wpf).
                        var immediateOffset = (double)scroll.GetValue(ScrollViewer.HorizontalOffsetProperty);
                        await Settle();
                        var pendingOffset = (double)scroll.GetValue(ScrollViewer.HorizontalOffsetProperty);
                        True(Math.Abs(pendingOffset - target) < 0.5 && Math.Abs(horizontalBar.Value - target) < 0.5,
                            $"{scenario.Name}: thumb position must follow the pending drag value. " +
                            $"Target={target}, immediate={immediateOffset}, settled={pendingOffset}, bar={horizontalBar.Value}, " +
                            $"content={scroll.ContentHorizontalOffset}, deferred={scroll.IsDeferredScrollingEnabled}.");
                        True(Math.Abs(scroll.ContentHorizontalOffset - contentOffset) < 0.5,
                            scenario.Name + ": content must stay stationary during each thumb movement.");
                        True(Math.Abs(endContent.TranslatePoint(new Point(), grid).X - endX) < 0.5,
                            scenario.Name + ": existing text cells must not move/rebuild during deferred dragging.");
                    }
                    ScrollBar.ScrollToHorizontalOffsetCommand.Execute(finalOffset, horizontalBar);
                    await Settle();
                    True(Math.Abs(scroll.ContentHorizontalOffset - finalOffset) < 0.5,
                        scenario.Name + ": releasing the thumb must commit the final content offset.");
                    var releasedEnd = endColumn.GetCellContent(sorted[0]);
                    True(releasedEnd is not null && releasedEnd.TranslatePoint(new Point(), grid).X < grid.ActualWidth &&
                        releasedEnd.TranslatePoint(new Point(releasedEnd.ActualWidth, 0), grid).X > 0,
                        scenario.Name + ": releasing at the right edge must reveal the correct end-column cell.");
                    Console.WriteLine($"SCROLL-DEFERRED {scenario.Name}: thumb-moves=4 content-moves=1 elapsed-ms={drag.ElapsedMilliseconds}");
                    scroll.ScrollToLeftEnd(); await Settle();
                    await AssertIdentity(sorted[0]);
                    True(sorted.SequenceEqual(view.Cast<object>()), scenario.Name + ": scrolling must preserve sorting.");
                    True(marked.SequenceEqual(scenario.Rows.Where(scenario.IsMarked)),
                        scenario.Name + ": scrolling/recycling must preserve print checkmarks.");
                    Equal(1, view.SortDescriptions.Count);
                    Equal(ListSortDirection.Descending, view.SortDescriptions[0].Direction);
                }
                finally
                {
                    host.Close();
                    await DrainDispatcherAsync();
                }
            }
            Equal(0, fixture.Printer.Calls);
        }
        finally
        {
            main.CancelPendingOperations();
            sourceWindow.Close();
            await DrainDispatcherAsync();
        }
    }

    private sealed record HorizontalScrollScenario(string Name, object[] Rows, string SortProperty,
        Func<object, bool> IsMarked, Func<object, string> IdentityText);
}
