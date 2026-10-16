using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CheckboxBatchPrinter.Controls;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.ViewModels;
using CheckboxBatchPrinter.Views;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    // Use the compiled dashboard styles rather than the TextBox's fallback theme.
    // Native copy is intercepted before it can replace the user's clipboard.
    private static async Task LeanDashboardTextCellsAsync()
    {
        _xamlCheckpoint = "lean dashboard text editor selection, clipping and internal scrolling";
        var fixture = new Fixture();
        var (main, _) = fixture.Create();
        main.CoordinatorManaged = true;
        var window = new MainWindow { DataContext = main };
        var row = new ReceiptRowViewModel(new ReceiptRecord
        {
            Id = "synthetic-lean-text-cell", FiscalCode = string.Concat(Enumerable.Repeat("20451558004295-", 12)) + "FINAL-TTN",
            TotalSumMinor = 123450,
            Status = string.Join(' ', Enumerable.Repeat("Відправлення прямує до наступного відділення перевізника", 8))
        })
        {
            IsSelected = true, PrintStatus = PrintItemStatus.Done,
            OrderMatch = new(ReceiptLinkState.Exact, Order(), "Synthetic linked receipt", [])
        };
        var dashboardStyle = (Style)window.Resources["DashboardTextStyle"];
        var amountStyle = (Style)window.Resources["DashboardAmountTextStyle"];
        var statusStyle = new Style(typeof(DashboardTextBox), dashboardStyle);
        statusStyle.Setters.Add(new Setter(TextBox.TextWrappingProperty, TextWrapping.Wrap));
        statusStyle.Setters.Add(new Setter(TextBox.MaxLinesProperty, 2));
        var grid = new DataGrid
        {
            Style = (Style)window.Resources["ReceiptGridStyle"],
            ItemsSource = new[] { row },
            EnableColumnVirtualization = false
        };
        var numberColumn = new DashboardTextColumn
        {
            Header = "Synthetic long TTN", Width = 150,
            Binding = new Binding(nameof(ReceiptRowViewModel.FiscalCode)), ElementStyle = dashboardStyle
        };
        var amountColumn = new DashboardTextColumn
        {
            Header = "Synthetic amount", Width = 100,
            Binding = new Binding(nameof(ReceiptRowViewModel.Total)) { StringFormat = "N2" }, ElementStyle = amountStyle
        };
        var statusColumn = new DashboardTextColumn
        {
            Header = "Synthetic wrapped status", Width = 180,
            Binding = new Binding(nameof(ReceiptRowViewModel.Status)), ElementStyle = statusStyle
        };
        grid.Columns.Add(numberColumn);
        grid.Columns.Add(amountColumn);
        grid.Columns.Add(statusColumn);
        window.Content = grid;
        try
        {
            LayoutContent(window);
            await DrainDispatcherAsync();
            LayoutContent(window);
            var number = (DashboardTextBox)numberColumn.GetCellContent(row);
            var amount = (DashboardTextBox)amountColumn.GetCellContent(row);
            var status = (DashboardTextBox)statusColumn.GetCellContent(row);
            True(number is not null && amount is not null && status is not null,
                "The compiled dashboard styles must be tested in realized DataGridCells.");
            True(ReferenceEquals(dashboardStyle, number!.Style) && ReferenceEquals(amountStyle, amount!.Style));
            True(VisualChildren<DataGridCell>(grid).Any(cell => ReferenceEquals(cell.Content, number)),
                "The selectable editor must retain its real DataGridCell ancestor.");
            foreach (var text in new[] { number, amount!, status! })
            {
                var contentHost = text.Template.FindName("PART_ContentHost", text) as ScrollViewer;
                True(contentHost is not null, "The lean editor must retain its scrolling content host.");
                True(!VisualChildren<ScrollBar>(text).Any(),
                    "Hidden dashboard editor scrollbars must not remain in the visual tree.");
                True(text.IsReadOnly && text.IsReadOnlyCaretVisible && !text.IsUndoEnabled,
                    "The lean dashboard editor lost native read-only selection behavior.");
                True(text.Background is SolidColorBrush background && background.Color.A == 0,
                    "The editor must keep its marketplace row color visible.");
            }

            void AssertNativeCopy(DashboardTextBox text, int start, int length)
            {
                var original = text.Text;
                text.Select(start, length);
                var expected = original.Substring(start, length);
                Equal(expected, text.SelectedText);
                True(ApplicationCommands.Copy.CanExecute(null, text));
                string? copied = null;
                DataObjectCopyingEventHandler handler = (_, args) =>
                {
                    copied = args.DataObject.GetData(DataFormats.UnicodeText) as string;
                    args.CancelCommand();
                };
                DataObject.AddCopyingHandler(text, handler);
                try { ApplicationCommands.Copy.Execute(null, text); }
                finally { DataObject.RemoveCopyingHandler(text, handler); }
                Equal(expected, copied);
                Equal(original, text.Text);
                True(!ApplicationCommands.Cut.CanExecute(null, text));
            }

            AssertNativeCopy(number, 3, 9);
            True(number.ExtentWidth > number.ViewportWidth + 30,
                "The overflow regression needs text wider than its actual cell.");
            number.CaretIndex = number.Text.Length;
            number.ScrollToHorizontalOffset(double.MaxValue);
            await DrainDispatcherAsync();
            LayoutContent(window);
            True(number.HorizontalOffset > 0,
                "Oversized selectable text must still scroll internally to reveal its suffix.");
            AssertNativeCopy(number, number.Text.Length - 9, 9);
            // Select(start, length) retains its native active endpoint at the
            // selection start. Move the caret explicitly after testing Copy.
            number.CaretIndex = number.Text.Length;
            Equal(number.Text.Length, number.CaretIndex);
            var end = number.GetRectFromCharacterIndex(number.Text.Length);
            var numberHost = (ScrollViewer)number.Template.FindName("PART_ContentHost", number);
            var presenter = (ScrollContentPresenter)numberHost.Template.FindName("PART_ScrollContentPresenter", numberHost);
            // GetRectFromCharacterIndex is relative to the TextBox; the scroll
            // viewport may start inside it rather than at its client origin.
            var viewport = new Rect(presenter.TranslatePoint(new Point(), number), presenter.RenderSize);
            True(!end.IsEmpty && end.X >= viewport.Left - 1 && end.X <= viewport.Right + 1,
                $"The end caret must remain inside the scrolled text viewport. " +
                $"Rect={end}; viewport={viewport}; extent={number.ExtentWidth}; offset={number.HorizontalOffset}; width={number.ActualWidth}.");

            True(status!.TextWrapping == TextWrapping.Wrap && status.MaxLines == 2 && status.LineCount > 2,
                "The actual status editor must wrap a long value while retaining the two-line display limit.");
            var secondLine = status.GetRectFromCharacterIndex(status.GetCharacterIndexFromLineIndex(1));
            var thirdLine = status.GetRectFromCharacterIndex(status.GetCharacterIndexFromLineIndex(2));
            True(!secondLine.IsEmpty && secondLine.Top >= -1 && secondLine.Bottom <= status.ViewportHeight + 1,
                "Both normal tracking-status lines must remain visible.");
            True(!thirdLine.IsEmpty && thirdLine.Top >= status.ViewportHeight - 1,
                "Additional status lines must be clipped rather than expanding the dashboard row.");
            AssertNativeCopy(status, 5, 11);
            Equal(FontWeights.Bold, amount!.FontWeight);
            var container = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(row);
            Equal(Color.FromRgb(0xBF, 0xDA, 0xFA), ((SolidColorBrush)container.Background).Color);
            True(row.IsSelected, "Text selection and copying must not clear the print checkmark.");
            Equal(0, fixture.Printer.Calls);
        }
        finally
        {
            main.CancelPendingOperations();
            window.Close();
            await DrainDispatcherAsync();
        }
    }

}
