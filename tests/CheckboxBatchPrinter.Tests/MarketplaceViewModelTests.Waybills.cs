using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;
using CheckboxBatchPrinter.Views;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task WaybillsDashboardAsync()
    {
        var source = new DashboardWaybillSource();
        using var waybills = new WaybillsViewModel(source);
        var fixture = new Fixture();
        var (main, workspace) = fixture.Create(waybills: waybills);
        main.CoordinatorManaged = true;
        main.AllReceiptsTab.SearchText = "receipt-only-search";
        var window = new MainWindow { DataContext = main };
        var failures = new BindingErrorListener();
        var previous = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Listeners.Add(failures);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            var tabs = (TabControl)window.FindName("ReceiptTabs");
            LayoutContent(window);
            await DrainDispatcherAsync();
            tabs.SelectedIndex = 2;
            await DrainDispatcherAsync();
            await main.RefreshActiveAsync();
            LayoutContent(window);
            await DrainDispatcherAsync();
            var grid = (DataGrid)window.FindName("AllWaybillsGrid");
            Equal(2, main.SelectedTabIndex);
            True(grid.Items.Count == 3, $"Waybill grid count={grid.Items.Count}; rows={waybills.Rows.Count}; view={waybills.View.Cast<object>().Count()}; search={waybills.SearchText}; carrier={waybills.SelectedCarrier}; sourceCalls={source.Calls}; itemsSource={grid.ItemsSource?.GetType().Name}; status={waybills.Status}");
            Equal(1, source.Calls);
            Equal(0, fixture.ReceiptSource.Calls);
            Equal(0, fixture.Source.FetchCalls);
            Equal(0, fixture.Details.Calls);
            Equal(0, workspace.AllOrderRows.Count);
            True(ReferenceEquals(grid.ItemsSource, waybills.View));
            Equal(Visibility.Collapsed, ((Border)window.FindName("ReceiptPrintFooter")).Visibility);
            True(!main.PrintSelectedCommand.CanExecute(null) && !main.PreviewCommand.CanExecute(null));
            Equal(DataGridSelectionMode.Extended, grid.SelectionMode);
            var ttn = grid.Columns.Single(c => c.Header?.ToString() == "ТТН");
            var rows = waybills.View.Cast<WaybillRowViewModel>().ToArray();
            grid.SelectedCells.Add(new(rows[1], ttn));
            grid.SelectedCells.Add(new(rows[0], ttn));
            Equal(rows[0].TrackingNumber + Environment.NewLine + rows[1].TrackingNumber,
                MainWindow.BuildDashboardClipboardText(grid));
            main.SearchText = "RD-";
            Equal(1, grid.Items.Count);
            Equal("receipt-only-search", main.AllReceiptsTab.SearchText);
            main.SearchText = "";
            waybills.View.SortDescriptions.Clear();
            waybills.View.SortDescriptions.Add(new(nameof(WaybillRowViewModel.TrackingNumber), ListSortDirection.Ascending));
            var visible = waybills.View.Cast<WaybillRowViewModel>().ToArray();
            for (var i = 0; i < visible.Length; i++) Equal(i + 1, visible[i].DisplayIndex);
            await ExecuteAsync(main.RefreshCommand);
            Equal(2, source.Calls);
            Equal(0, fixture.ReceiptSource.Calls);
            await ExecuteAsync(main.TodayCommand);
            Equal(DateOnly.FromDateTime(DateRangeBuilder.TodayKyiv), source.LastFrom);
            Equal(source.LastFrom, source.LastTo);
            Equal(0, fixture.ReceiptSource.Calls);
            LayoutContent(window);
            await DrainDispatcherAsync();
            RenderSyntheticContent(window, "all-waybills.png");
            tabs.SelectedIndex = 0;
            LayoutContent(window);
            await DrainDispatcherAsync();
            Equal("receipt-only-search", main.SearchText);
            Equal(Visibility.Visible, ((Border)window.FindName("ReceiptPrintFooter")).Visibility);
            Equal(0, fixture.Printer.Calls);
            Equal(0, fixture.Links.Saves);
            True(failures.Messages.Count == 0, "Waybill dashboard binding errors: " + string.Join("\n", failures.Messages));
        }
        finally
        {
            main.CancelPendingOperations();
            window.Close();
            await DrainDispatcherAsync();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(failures);
            PresentationTraceSources.DataBindingSource.Switch.Level = previous;
        }
    }

    private sealed class DashboardWaybillSource : IWaybillService
    {
        public int Calls;
        public DateOnly LastFrom, LastTo;
        public Task<WaybillFetchResult> FetchAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
        {
            Calls++; LastFrom = from; LastTo = to;
            var date = from.ToDateTime(new TimeOnly(14, 30));
            WaybillRecord[] docs =
            [
                new(ShippingCarrier.NovaPoshta, "np-a", "Магазин 1", "np-doc-1", "20400000000001", date,
                    "Створено", "Тестовий отримувач", "Київ", 125, 1),
                new(ShippingCarrier.RozetkaDelivery, "rd-a", "Rozetka Delivery", "RD-001", "RD-001", date.AddMinutes(-10),
                    "Передано перевізнику", "Тестовий отримувач", "Львів", 440, 2),
                new(ShippingCarrier.NovaPoshta, "np-a", "Магазин 1", "np-doc-2", "20400000000002", date.AddMinutes(-20),
                    "Видалено", "Тестовий отримувач", "Одеса", 230, 1, true)
            ];
            return Task.FromResult(new WaybillFetchResult(docs,
                [new(ShippingCarrier.NovaPoshta, "Магазин 1", true, 2, ""), new(ShippingCarrier.RozetkaDelivery, "Rozetka Delivery", true, 1, "")]));
        }
    }
}
