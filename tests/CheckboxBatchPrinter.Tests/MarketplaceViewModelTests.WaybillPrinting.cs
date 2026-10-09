using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CheckboxBatchPrinter.Controls;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;
using CheckboxBatchPrinter.Services;
using CheckboxBatchPrinter.ViewModels;
using CheckboxBatchPrinter.Views;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task WaybillPrintingDashboardAsync()
    {
        var fixture = new Fixture();
        var related = Order() with { Shipments = [new("nova_poshta", "20400000000001")] };
        await fixture.Cache.SaveAsync(new([related], []));
        var history = new DashboardWaybillHistory();
        var settings = new DashboardWaybillSettings();
        var printer = new DashboardWaybillPrinter();
        var dialogs = new DashboardWaybillDialogs();
        using var waybills = new WaybillsViewModel(new DashboardWaybillSource(), new DashboardWaybillPreparation(),
            settings, history, printer, dialogs);
        var (main, workspace) = fixture.Create(waybills: waybills);
        main.CoordinatorManaged = true;
        main.DateFrom = new(2026, 10, 9); main.DateTo = new(2026, 10, 9);
        var window = new MainWindow { DataContext = main };
        var failures = new BindingErrorListener();
        var level = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Listeners.Add(failures);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            LayoutContent(window); await DrainDispatcherAsync();
            var tabs = (TabControl)window.FindName("ReceiptTabs");
            tabs.SelectedIndex = 2; await DrainDispatcherAsync();
            await main.RefreshActiveAsync();
            await main.UpdateWaybillOrderAssociationsAsync();
            LayoutContent(window); await DrainDispatcherAsync();
            var grid = (DataGrid)window.FindName("AllWaybillsGrid");
            var row = waybills.Rows.Single(r => r.TrackingNumber == "20400000000001");
            True(row.AssociatedOrders.Contains("ORDER-41"), "Cached order must be shown despite its earlier creation date.");
            Equal(0, fixture.ReceiptSource.Calls); Equal(0, fixture.Source.FetchCalls);
            Equal(Visibility.Visible, ((Border)window.FindName("WaybillPrintFooter")).Visibility);
            var first = grid.Columns[0];
            grid.ScrollIntoView(row, first); LayoutContent(window); await DrainDispatcherAsync();
            var checkbox = VisualChildren<CheckBox>(first.GetCellContent(row)).Single();
            ClickCheckBox(checkbox); await DrainDispatcherAsync();
            True(row.IsSelected && main.PrintSelectedCommand.CanExecute(null));
            grid.CurrentCell = new(row, grid.Columns.Single(c => c.Header?.ToString() == "ТТН"));
            await DrainDispatcherAsync(); Equal(row, waybills.SelectedRow);
            await ExecuteAsync(main.PreviewCommand);
            Equal(0, printer.Calls); True(!dialogs.AllowPrint);
            var preview = new LabelPreviewWindow(dialogs.Batch!, printer.Geometry, [], false);
            LayoutContent(preview); await DrainDispatcherAsync();
            var text = string.Join("\n", VisualChildren<TextBlock>((FrameworkElement)preview.Content).Select(t => t.Text));
            True(text.Contains("Накладних: 1") && text.Contains("ORDER-41") && !text.Contains("Замовлень: 1"));
            RenderSyntheticContent(preview, "independent-waybill-preview.png");
            preview.Close();
            await ExecuteAsync(main.PrintSelectedCommand);
            Equal(1, printer.Calls); Equal(2, printer.Batch!.Pages.Count);
            True(printer.Batch.Pages.All(p => p.Shipment.Order is null && p.ConnectionId == row.ConnectionId));
            True(row.HasPrintedLabel && !row.IsSelected);
            Equal(LabelSubmissionState.Submitted, history.Attempts.Single().State);
            Equal(0, fixture.Printer.Calls);
            var deleted = waybills.Rows.Single(r => r.IsDeleted);
            grid.ScrollIntoView(deleted, first); LayoutContent(window); await DrainDispatcherAsync();
            True(!VisualChildren<CheckBox>(first.GetCellContent(deleted)).Single().IsEnabled);
            grid.ScrollIntoView(row, first); LayoutContent(window); await DrainDispatcherAsync();
            RenderSyntheticContent(window, "all-waybills-printing.png");
            True(failures.Messages.Count == 0, "Waybill printing binding errors: " + string.Join("\n", failures.Messages));
        }
        finally
        {
            main.CancelPendingOperations(); window.Close(); await DrainDispatcherAsync();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(failures);
            PresentationTraceSources.DataBindingSource.Switch.Level = level;
        }
    }

    private sealed class DashboardWaybillPreparation : IWaybillLabelPreparation
    {
        public async Task<PreparedWaybillBatch> PrepareAsync(IReadOnlyList<WaybillPrintRequest> documents, LabelPrintSettings settings,
            LabelPrinterGeometry geometry, CancellationToken cancellationToken = default)
        {
            var request = documents.Single(); var row = request.Waybill;
            var shipment = new ShipmentReference(null, string.Join(", ", request.OrderNumbers), row.Carrier, row.TrackingNumber,
                row.DocumentId, ExpectedPlaces: 2) { CarrierConnectionId = row.ConnectionId };
            var document = new ShippingLabelDocument(shipment, row.ConnectionId, row.DocumentId,
                SyntheticShippingPdf.Create(pages: 2), "synthetic", DateTimeOffset.UtcNow, 2);
            var pages = await new WindowsShippingLabelRenderer().RenderAsync(document, geometry.DpiX, geometry.DpiY, cancellationToken);
            return new(new("synthetic", pages, settings) { IsWaybillBatch = true, SelectedWaybillCount = 1 }, new Dictionary<string, string>());
        }
        public Task ValidateAsync(PreparedWaybillBatch batch, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class DashboardWaybillSettings : IShippingSettingsStore
    {
        public Task<ShippingLabelSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ShippingLabelSettings
            { Print = new() { PrinterName = "Synthetic printer", DriverFormat = "Synthetic", WidthMm = 100, HeightMm = 100 } });
        public Task SaveAsync(ShippingLabelSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class DashboardWaybillHistory : ILabelHistoryStore
    {
        public List<LabelPrintAttempt> Attempts { get; } = [];
        public Task<IReadOnlyList<LabelPrintAttempt>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LabelPrintAttempt>>(Attempts.ToArray());
        public Task SaveAsync(LabelPrintAttempt attempt, CancellationToken cancellationToken = default)
        { Attempts.RemoveAll(old => old.AttemptId == attempt.AttemptId); Attempts.Add(attempt); return Task.CompletedTask; }
    }
    private sealed class DashboardWaybillPrinter : ILabelPrinter
    {
        public int Calls; public LabelPrintBatch? Batch;
        public LabelPrinterGeometry Geometry { get; } = new(100, 100, 0, 0, 100, 100, 203, 203);
        public IReadOnlyList<string> Printers() => ["Synthetic printer"];
        public IReadOnlyList<LabelDriverFormat> Formats(string printer) => [new("Synthetic", 100, 100)];
        public LabelPrinterGeometry Inspect(LabelPrintSettings settings) => Geometry;
        public async Task<int?> SubmitAsync(LabelPrintBatch batch, string jobName, Func<Task> beforeTransfer,
            Func<int, Task> jobCreated, CancellationToken cancellationToken = default)
        { Calls++; Batch = batch; await beforeTransfer(); await jobCreated(42); return 42; }
    }
    private sealed class DashboardWaybillDialogs : ILabelDialogs
    {
        public LabelPrintBatch? Batch; public bool AllowPrint;
        public bool Preview(LabelPrintBatch batch, LabelPrinterGeometry geometry, IReadOnlyList<LabelPrintAttempt> previous, bool allowPrint)
        { Batch = batch; AllowPrint = allowPrint; return true; }
        public Task SettingsAsync() => Task.CompletedTask;
        public void Error(string message) => throw new InvalidOperationException("Unexpected synthetic dialog: " + message);
    }
}
