using System.ComponentModel;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static readonly DateOnly CabinetWaybillDay = new(2026, 10, 12);
    private static readonly DateTimeOffset CabinetCheckedAt = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
    private static WaybillRecord CabinetWaybill(string tracking, bool? printed, int places = 2,
        ShippingCarrier carrier = ShippingCarrier.NovaPoshta) => new(carrier, "cabinet-test", "Synthetic account", "doc-" + tracking,
            tracking, CabinetWaybillDay.ToDateTime(new(12, 0)), "Створено", "Synthetic recipient", "Kyiv", 100, places)
        { CabinetPrinted = printed, CabinetLastPrint = "2026-10-12 11:45:00", RetrievedAtUtc = CabinetCheckedAt,
            CredentialFingerprint = "synthetic-proof" };
    private static LabelPrintAttempt CabinetLocalAttempt(WaybillRecord document, LabelSubmissionState state, int pages = 2) =>
        new("synthetic-local-attempt", "synthetic local labels", "Synthetic printer", CabinetCheckedAt, state,
            Enumerable.Range(1, pages).Select(page => new LabelAttemptPage(document.Carrier, document.ConnectionId,
                document.TrackingNumber, document.DocumentId, page, "synthetic-pdf")).ToArray(), state == LabelSubmissionState.Submitted ? 42 : null);

    private static Task WaybillCabinetPrintUnionAsync()
    {
        var document = CabinetWaybill("20400000000001", true);
        var cabinet = new WaybillRowViewModel(document);
        True(cabinet.HasCabinetPrintedLabel && cabinet.HasPrintedLabel && !cabinet.CanRetry);
        Equal(LabelSubmissionState.NotSubmitted, cabinet.PrintStatus);
        Equal("НП: позначено надрукованою", cabinet.PrintStatusText);
        cabinet.ApplyHistory([]);
        cabinet.ApplyHistory([CabinetLocalAttempt(document, LabelSubmissionState.NotSubmitted)]);
        cabinet.MarkFailed();
        True(cabinet.HasPrintedLabel && !cabinet.CanRetry && cabinet.PrintStatus == LabelSubmissionState.NotSubmitted,
            "Positive cabinet evidence must not become a fabricated local Windows submission or a safe retry.");
        True(cabinet.PrintStatusToolTip.Contains("Кабінет НП") && cabinet.PrintStatusToolTip.Contains(document.CabinetLastPrint));
        True(!cabinet.PrintStatusToolTip.Contains("job"));

        cabinet.ApplyHistory([CabinetLocalAttempt(document, LabelSubmissionState.Submitted)]);
        True(cabinet.HasPrintedLabel && cabinet.PrintStatusText == "Передано Windows, job 42",
            "An exact local Windows job must keep its own source/status when the cabinet also confirms printing.");
        True(cabinet.PrintStatusToolTip.Contains("job 42") && cabinet.PrintStatusToolTip.Contains("Кабінет НП"));
        var partial = new WaybillRowViewModel(document with { Places = 3 });
        partial.ApplyHistory([CabinetLocalAttempt(document, LabelSubmissionState.Submitted, pages: 1)]);
        True(partial.HasPrintedLabel && partial.PrintStatusText == "Частину накладної передано Windows",
            "Cabinet evidence must not turn a partial local job into a complete local submission.");

        foreach (var flag in new bool?[] { null, false })
        {
            var unknown = new WaybillRowViewModel(document with { CabinetPrinted = flag });
            unknown.ApplyHistory([]); unknown.MarkFailed();
            True(!unknown.HasPrintedLabel && !unknown.HasCabinetPrintedLabel && unknown.CanRetry,
                "An absent/negative cabinet flag or a date alone must not imply printing.");
        }
        var delivery = new WaybillRowViewModel(CabinetWaybill("RMP-123", true, carrier: ShippingCarrier.RozetkaDelivery));
        delivery.ApplyHistory([]);
        True(!delivery.HasPrintedLabel && !delivery.HasCabinetPrintedLabel,
            "NP-specific cabinet print evidence must not be inferred for an unsupported carrier.");
        return Task.CompletedTask;
    }

    private static Task WaybillCabinetPrintRestoreAsync()
    {
        var notPrinted = new WaybillRowViewModel(CabinetWaybill("20400000000001", false));
        notPrinted.IsSelected = true; notPrinted.MarkFailed();
        var nowPrinted = new WaybillRowViewModel(notPrinted.Model with { CabinetPrinted = true });
        nowPrinted.RestoreState(notPrinted);
        True(nowPrinted.IsSelected && nowPrinted.HasPrintedLabel && !nowPrinted.CanRetry && nowPrinted.PrintStatus == LabelSubmissionState.NotSubmitted,
            "Restoring old local state erased freshly fetched positive cabinet evidence.");
        var latestNegative = new WaybillRowViewModel(nowPrinted.Model with { CabinetPrinted = false });
        latestNegative.RestoreState(nowPrinted);
        True(!latestNegative.HasPrintedLabel && latestNegative.PrintStatus == LabelSubmissionState.NotSubmitted,
            "A previous remote flag was restored as if it were local print history.");
        var latestUnknown = new WaybillRowViewModel(nowPrinted.Model with { CabinetPrinted = null });
        latestUnknown.RestoreState(nowPrinted);
        True(!latestUnknown.HasPrintedLabel, "A previous remote marker became persisted local evidence.");

        nowPrinted.ApplyHistory([CabinetLocalAttempt(nowPrinted.Model, LabelSubmissionState.Submitted)]);
        foreach (var flag in new bool?[] { true, false, null })
        {
            var refreshed = new WaybillRowViewModel(nowPrinted.Model with { CabinetPrinted = flag });
            refreshed.RestoreState(nowPrinted);
            True(refreshed.HasPrintedLabel && !refreshed.CanRetry && refreshed.PrintStatusText == "Передано Windows, job 42",
                "Refreshing the cabinet flag erased an existing local submitted job.");
        }
        return Task.CompletedTask;
    }

    private static async Task WaybillCabinetPrintPreviewAsync()
    {
        using var fixture = new CabinetWaybillFixture();
        var external = CabinetWaybill("20400000000001", true);
        var ordinary = CabinetWaybill("20400000000002", false, places: 1);
        var hidden = CabinetWaybill("20400000000003", true);
        await fixture.Load(external, ordinary, hidden);
        foreach (var row in fixture.Model.Rows) row.IsSelected = true;
        fixture.Model.SearchText = "20400000000001";
        fixture.Dialogs.Confirm = false;
        await fixture.Model.RunAsync(null, true);
        var evidence = fixture.Dialogs.Batch!.CabinetPrintedLabels;
        Equal(1, evidence.Count); Equal(2, fixture.Dialogs.Batch.Pages.Count);
        Equal(external.TrackingNumber, evidence[0].Shipment.TrackingNumber);
        Equal(external.ConnectionId, evidence[0].Shipment.CarrierConnectionId);
        Equal("Кабінет НП", evidence[0].Source); Equal(external.CabinetLastPrint, evidence[0].PrintedAtText);
        Equal<DateTimeOffset?>(CabinetCheckedAt, evidence[0].CheckedAtUtc);
        Equal(0, fixture.Dialogs.Previous.Count); Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Attempts.Count);
        True(fixture.Model.Rows.All(r => r.IsSelected), "Declining cabinet repeat confirmation changed selected marks.");

        fixture.Model.SearchText = "";
        fixture.Model.Rows.Single(r => r.TrackingNumber == hidden.TrackingNumber).IsSelected = false;
        fixture.Model.View.SortDescriptions.Clear();
        fixture.Model.View.SortDescriptions.Add(new(nameof(WaybillRowViewModel.TrackingNumber), ListSortDirection.Descending));
        fixture.Dialogs.Confirm = true;
        await fixture.Model.RunAsync(null, true);
        Equal(3, fixture.Printer.Calls); Equal(3, fixture.History.Attempts.Count);
        True(fixture.History.Attempts.All(a => a.State == LabelSubmissionState.Submitted && a.Pages.Count == 1));
        Equal(1, fixture.Dialogs.Batch!.CabinetPrintedLabels.Count);
        True(fixture.Printer.Batches.SelectMany(b => b.Pages).Select(p => p.Shipment.TrackingNumber).SequenceEqual([
            ordinary.TrackingNumber, external.TrackingNumber, external.TrackingNumber]), "Cabinet warning reordered the selected pages.");
        True(fixture.Model.Rows.Where(r => r.TrackingNumber != hidden.TrackingNumber).All(r => !r.IsSelected && r.HasPrintedLabel));
    }

    private static async Task WaybillCabinetPrintRetryAsync()
    {
        using var fixture = new CabinetWaybillFixture();
        var external = CabinetWaybill("20400000000001", true);
        var ordinary = CabinetWaybill("20400000000002", false);
        await fixture.Load(external, ordinary);
        foreach (var row in fixture.Model.Rows)
        {
            row.IsSelected = true;
            row.ApplyHistory([CabinetLocalAttempt(row.Model, LabelSubmissionState.NotSubmitted)]);
        }
        True(!fixture.Model.Rows.Single(r => r.TrackingNumber == external.TrackingNumber).CanRetry);
        True(fixture.Model.Rows.Single(r => r.TrackingNumber == ordinary.TrackingNumber).CanRetry);
        await fixture.Model.RunAsync(null, true, retryFailed: true);
        Equal(2, fixture.Printer.Calls);
        True(fixture.Printer.Batch!.Pages.All(p => p.Shipment.TrackingNumber == ordinary.TrackingNumber),
            "Automatic retry included an externally printed document.");
        Equal(0, fixture.Dialogs.Batch!.CabinetPrintedLabels.Count);
        True(fixture.Model.Rows.Single(r => r.TrackingNumber == external.TrackingNumber).IsSelected);

        // Same-document refresh preserves local state while reading the latest cabinet marker.
        await fixture.Load(external with { CabinetPrinted = false, CabinetLastPrint = "" }, ordinary);
        True(!fixture.Model.Rows.Single(r => r.TrackingNumber == external.TrackingNumber).HasPrintedLabel,
            "The refresh promoted an old external marker to permanent local history.");
        True(fixture.Model.Rows.Single(r => r.TrackingNumber == ordinary.TrackingNumber).HasPrintedLabel,
            "The refresh discarded the locally submitted retry.");
    }

    private sealed class CabinetWaybillFixture : IDisposable
    {
        private readonly CabinetWaybillSource _source = new();
        public DashboardWaybillHistory History { get; } = new();
        public DashboardWaybillPrinter Printer { get; } = new();
        public CabinetWaybillDialogs Dialogs { get; } = new();
        public WaybillsViewModel Model { get; }
        public CabinetWaybillFixture()
        {
            Model = new(_source, new CabinetWaybillPreparation(), new DashboardWaybillSettings(), History, Printer, Dialogs);
            Model.SetDisplayDates(CabinetWaybillDay, CabinetWaybillDay);
        }
        public Task Load(params WaybillRecord[] records) { _source.Result = new(records, []); return Model.RefreshAsync(); }
        public void Dispose() => Model.Dispose();
    }
    private sealed class CabinetWaybillSource : IWaybillService
    {
        public WaybillFetchResult Result = new([], []);
        public Task<WaybillFetchResult> FetchAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) => Task.FromResult(Result);
    }
    private sealed class CabinetWaybillPreparation : IWaybillLabelPreparation
    {
        public Task<PreparedWaybillBatch> PrepareAsync(IReadOnlyList<WaybillPrintRequest> documents, LabelPrintSettings settings,
            LabelPrinterGeometry geometry, CancellationToken cancellationToken = default)
        {
            var pages = documents.SelectMany(request => Enumerable.Range(1, request.Waybill.Places ?? 1).Select(page =>
                new ShippingLabelPage(new(null, "", request.Waybill.Carrier, request.Waybill.TrackingNumber, request.Waybill.DocumentId)
                { CarrierConnectionId = request.Waybill.ConnectionId }, request.Waybill.DocumentId, "synthetic-fingerprint", page, 100, 100, [])
                { ConnectionId = request.Waybill.ConnectionId })).ToArray();
            return Task.FromResult(new PreparedWaybillBatch(new("synthetic-cabinet", pages, settings)
                { IsWaybillBatch = true, SelectedWaybillCount = documents.Count }, new Dictionary<string, string>()));
        }
        public Task ValidateAsync(PreparedWaybillBatch batch, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class CabinetWaybillDialogs : ILabelDialogs
    {
        public LabelPrintBatch? Batch;
        public IReadOnlyList<LabelPrintAttempt> Previous = [];
        public bool Confirm = true;
        public bool Preview(LabelPrintBatch batch, LabelPrinterGeometry geometry, IReadOnlyList<LabelPrintAttempt> previous, bool allowPrint)
        { Batch = batch; Previous = previous; return Confirm; }
        public Task SettingsAsync() => Task.CompletedTask;
        public void Error(string message) => throw new InvalidOperationException("Unexpected cabinet-print dialog: " + message);
    }
}
