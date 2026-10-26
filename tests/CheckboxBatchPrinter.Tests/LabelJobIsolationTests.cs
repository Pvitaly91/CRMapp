using System.IO;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static class LabelJobIsolationTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("STA label copies and all places get ordered independent native Windows jobs", () => Sta(Copies)),
        ("STA label power failure stops at its single job and preserves accepted prefix after restart", () => Sta(PowerFailure)),
        ("STA label EndDoc failure remains unknown and aborts only the active job", () => Sta(FinishFailure)),
        ("STA label journal failures stop transmission without replaying accepted jobs", () => Sta(JournalFailure)),
        ("STA cancellation between label jobs preserves prefix and never starts the next job", () => Sta(CancelBetweenJobs)),
        ("STA invalid final label raster blocks the entire packet before any Windows job", () => Sta(Preflight)),
        ("STA native label backend rejects multiple labels or driver copies in one job", () => Sta(BackendGuard)),
        ("label history combines separate places but never duplicate copies or different PDF versions", HistoryCoverage)
    ];

    private static Task Sta(Func<Task> body) => MarketplaceViewModelTests.StaAsync(body);
    private static void Check(bool condition, string message) => ShippingLabelsTests.Check(condition, message);
    private sealed class History : ILabelHistoryStore
    {
        public List<LabelPrintAttempt> Attempts { get; } = [];
        public Func<LabelPrintAttempt, bool>? Fail;
        public Action<LabelPrintAttempt>? AfterSave;
        public Task<IReadOnlyList<LabelPrintAttempt>> LoadAsync(CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<LabelPrintAttempt>>(Attempts.ToArray());
        public Task SaveAsync(LabelPrintAttempt attempt, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (Fail?.Invoke(attempt) == true) throw new IOException("Synthetic journal failure");
            Attempts.RemoveAll(a => a.AttemptId == attempt.AttemptId);
            Attempts.Add(attempt);
            AfterSave?.Invoke(attempt);
            return Task.CompletedTask;
        }
    }
    private sealed class Native
    {
        public int Jobs, Aborts, FailBeginJob, FailFinishJob;
        public List<Device> Devices { get; } = [];
        public List<string> Names { get; } = [];
        public ILabelPageDevice Open(LabelPrintSettings settings)
        {
            Check(settings.Copies == 1 || Jobs == 0, "Driver copy count escaped the physical-job split.");
            var device = new Device(this); Devices.Add(device); return device;
        }
        public sealed class Device(Native owner) : ILabelPageDevice
        {
            public LabelPrinterGeometry Geometry => ShippingLabelsTests.Geometry;
            public int Pages, Job, Finishes;
            public bool Disposed;
            public int Begin(string name)
            {
                Job = ++owner.Jobs; owner.Names.Add(name);
                if (Job == owner.FailBeginJob) throw new IOException("Synthetic power loss after registration");
                return 100 + Job;
            }
            public bool BeginPage() { Pages++; return true; }
            public bool Draw(byte[] pixels, int pw, int ph, int x, int y, int w, int h) =>
                pixels.Length == pw * ph * 4 && w > 0 && h > 0;
            public bool FinishPage() => true;
            public bool Finish() { Finishes++; return Job != owner.FailFinishJob; }
            public void Abort() => owner.Aborts++;
            public void Dispose() => Disposed = true;
        }
    }
    private static async Task Copies()
    {
        var original = await ShippingLabelsTests.Batch();
        var batch = original with { Settings = original.Settings with { Copies = 2 } };
        var history = new History(); var native = new Native();
        var result = await new LabelPrintCoordinator(history, new WindowsLabelPrinter(native.Open)).SubmitAsync(batch);
        Check(result.Count == 8 && native.Jobs == 8 && history.Attempts.Count == 8, "Copies shared a job or disappeared.");
        var expected = original.Pages.SelectMany(p => Enumerable.Repeat((p.Shipment.TrackingNumber, p.PageNumber), 2)).ToArray();
        Check(result.Select(a => (a.Pages.Single().TrackingNumber, a.Pages.Single().PageNumber)).SequenceEqual(expected), "Frozen page/copy order changed.");
        Check(native.Devices.Where(d => d.Job > 0).All(d => d.Pages == 1 && d.Finishes == 1 && d.Disposed) && native.Aborts == 0,
            "A Windows job contains multiple labels or did not close independently.");
        Check(native.Names.Distinct().Count() == 8 && result.Select(a => a.WindowsJobId).Distinct().Count() == 8 &&
            result.All(a => a.State == LabelSubmissionState.Submitted), "Independent job identities/history were lost.");
    }
    private static async Task PowerFailure()
    {
        var batch = await ShippingLabelsTests.Batch();
        var native = new Native { FailBeginJob = 2 };
        var root = Path.Combine(Path.GetTempPath(), "cbp-label-job-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "labels.dpapi");
            var store = new DpapiLabelHistoryStore(path);
            try
            {
                await new LabelPrintCoordinator(store, new WindowsLabelPrinter(native.Open)).SubmitAsync(batch);
                throw new Exception("Power loss was hidden.");
            }
            catch (LabelTransmissionException ex)
            {
                Check(ex.Attempts.Count == 2 && ex.Attempts[0].State == LabelSubmissionState.Submitted &&
                    ex.Attempt.State == LabelSubmissionState.SubmissionUnknown && ex.Attempt.Pages.Single().PageNumber == 1,
                    "Accepted prefix or current-label risk was lost.");
                Check(ex.Message.Contains("1 із 4"), "Partial progress was not disclosed.");
            }
            var restored = await new DpapiLabelHistoryStore(path).LoadAsync();
            Check(native.Jobs == 2 && restored.Count == 2 && restored.Sum(a => a.Pages.Count) == 2, "Failure replayed the prefix or sent the tail.");
            var orders = batch.Pages.DistinctBy(p => p.Shipment.Order).Select(p => ShippingLabelsTests.Order(
                p.Shipment.OrderNumber, p.Shipment.Carrier, p.Shipment.TrackingNumber)).ToArray();
            var rows = orders.Select(order => new MarketplaceOrderRowViewModel(order)).ToArray();
            foreach (var row in rows) row.ApplyLabelHistory(restored);
            Check(rows[0].HasPrintedLabel && rows[1].HasLabelSubmission && !rows[1].HasPrintedLabel && !rows[2].HasLabelSubmission,
                "Restart marked unsent/uncertain orders printed or lost the accepted label.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task FinishFailure()
    {
        var history = new History(); var native = new Native { FailFinishJob = 2 };
        try
        {
            await new LabelPrintCoordinator(history, new WindowsLabelPrinter(native.Open)).SubmitAsync(await ShippingLabelsTests.Batch());
            throw new Exception("Failed EndDoc was hidden.");
        }
        catch (LabelTransmissionException ex)
        {
            Check(ex.Attempts.Count == 2 && ex.Attempt.State == LabelSubmissionState.SubmissionUnknown && ex.Attempt.WindowsJobId == 102,
                "Unfinished Windows job was marked complete or its ID was lost.");
        }
        Check(native.Jobs == 2 && native.Aborts == 1 && native.Devices.Single(d => d.Job == 1).Finishes == 1,
            "Failed job retried, sent the tail or aborted an earlier job.");
    }
    private static async Task JournalFailure()
    {
        foreach (var state in new[] { LabelSubmissionState.NotSubmitted, LabelSubmissionState.SubmissionUnknown, LabelSubmissionState.Submitted })
        {
            var history = new History(); var native = new Native();
            history.Fail = a => a.Pages.Single().TrackingNumber == "two" && a.State == state;
            try
            {
                await new LabelPrintCoordinator(history, new WindowsLabelPrinter(native.Open)).SubmitAsync(await ShippingLabelsTests.Batch());
                throw new Exception("Journal failure was hidden.");
            }
            catch (LabelTransmissionException ex)
            {
                Check(ex.Attempts[0].State == LabelSubmissionState.Submitted && ex.Attempts.Count == 2,
                    "Journal failure erased the accepted prefix.");
                Check(ex.Attempt.State == (state == LabelSubmissionState.Submitted ? state : LabelSubmissionState.NotSubmitted),
                    "Journal failure changed the transfer boundary.");
            }
            Check(native.Jobs == (state == LabelSubmissionState.Submitted ? 2 : 1), "Journal failure replayed accepted labels or continued the tail.");
        }
    }
    private static async Task CancelBetweenJobs()
    {
        using var cancellation = new CancellationTokenSource();
        var history = new History(); var native = new Native();
        history.AfterSave = a => { if (a.State == LabelSubmissionState.Submitted) cancellation.Cancel(); };
        try
        {
            await new LabelPrintCoordinator(history, new WindowsLabelPrinter(native.Open)).SubmitAsync(await ShippingLabelsTests.Batch(), cancellation.Token);
            throw new Exception("Cancellation was hidden.");
        }
        catch (LabelTransmissionException ex)
        {
            Check(ex.Attempts.Count == 2 && ex.Attempts[0].State == LabelSubmissionState.Submitted && ex.Attempt.State == LabelSubmissionState.NotSubmitted,
                "Cancellation erased progress or made the next label ambiguous.");
        }
        Check(native.Jobs == 1, "Cancellation started or replayed another job.");
    }
    private static async Task Preflight()
    {
        var batch = await ShippingLabelsTests.Batch();
        foreach (var invalidRaster in new[] { false, true })
        {
            var last = batch.Pages.Last();
            var bad = batch with { Pages = batch.Pages.SkipLast(1).Append(invalidRaster ? last with { Png = [1, 2, 3] } :
                last with { WidthMm = 210, HeightMm = 297 }).ToArray() };
            var history = new History(); var native = new Native();
            try { await new LabelPrintCoordinator(history, new WindowsLabelPrinter(native.Open)).SubmitAsync(bad); throw new Exception("Invalid last label accepted."); }
            catch (LabelTransmissionException ex) { Check(ex.Attempt.State == LabelSubmissionState.NotSubmitted, "Preflight failure crossed transfer boundary."); }
            Check(native.Jobs == 0 && history.Attempts.All(a => a.State == LabelSubmissionState.NotSubmitted), "Preflight printed a partial packet.");
        }
    }
    private static async Task BackendGuard()
    {
        var batch = await ShippingLabelsTests.Batch(); var native = new Native();
        foreach (var invalid in new[] { batch, batch with { Pages = [batch.Pages[0]], Settings = batch.Settings with { Copies = 2 } } })
        {
            try
            {
                await new WindowsLabelPrinter(native.Open).SubmitAsync(invalid, "synthetic", () => Task.CompletedTask, _ => Task.CompletedTask);
                throw new Exception("Multiple labels entered a native job.");
            }
            catch (InvalidOperationException) { }
        }
        Check(native.Devices.Count == 0, "Invalid native job opened a printer.");
    }
    private static Task HistoryCoverage()
    {
        var page = new LabelAttemptPage(ShippingCarrier.NovaPoshta, "account", "tracking", "document", 1, "version1") { ExpectedPages = 2 };
        var one = new LabelPrintAttempt("one", "job1", "printer", DateTimeOffset.UtcNow, LabelSubmissionState.Submitted, [page]);
        var two = one with { AttemptId = "two", Pages = [page with { PageNumber = 2 }] };
        Check(!LabelPrintHistory.HasCompleteDocument([one, one], _ => true), "Duplicate copy filled a missing place.");
        Check(!LabelPrintHistory.HasCompleteDocument([one, two with { Pages = [page with { PageNumber = 2, Fingerprint = "version2" }] }], _ => true), "Different PDF versions were combined.");
        Check(LabelPrintHistory.HasCompleteDocument([one, two], _ => true), "Separate page jobs did not form a complete document.");
        return Task.CompletedTask;
    }
}
