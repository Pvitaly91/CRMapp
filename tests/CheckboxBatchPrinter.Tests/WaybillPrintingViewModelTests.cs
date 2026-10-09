using System.ComponentModel;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static class WaybillPrintingViewModelTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("STA waybill print freezes sorted visible marks and includes every place without linked orders", () => Sta(FrozenSelection)),
        ("STA waybill preview and declined confirmation never call backend or journal", () => Sta(PreviewOnly)),
        ("STA waybill refresh preserves same-document marks but clears changed accounts and deleted documents", () => Sta(RefreshSelection)),
        ("STA waybill associations refresh and search independently of carrier loading", () => Sta(Associations)),
        ("STA waybill date cancellation rejects non-cooperative preparation and preview", () => Sta(StalePreparation)),
        ("STA waybill explicit cancel cannot submit a late preparation", () => Sta(CancelPreparation)),
        ("STA waybill credential and driver changes after preview prevent backend calls", () => Sta(ChangedCredentialsAndDriver)),
        ("STA waybill journal restoration and repeat warning are carrier-account scoped", () => Sta(HistoryScope)),
        ("STA waybill journal recognizes same-key NP aliases without crossing different keys or stale proofs", () => Sta(HistoryAliases)),
        ("STA waybill uncertain transfer never becomes an automatic retry but allows confirmed explicit reprint", () => Sta(UnknownAndReprint)),
        ("STA waybill safe failure retries only visible failed marks and clears only submitted documents", () => Sta(RetryFailure)),
        ("STA waybill late refresh before transfer callback prevents Windows boundary", () => Sta(BoundaryRefresh)),
        ("STA waybill cancellation command is disabled once Windows transfer starts", () => Sta(TransferState)),
        ("STA waybill refresh during transmission clears marks on the current submitted rows", () => Sta(RefreshDuringTransfer)),
        ("STA waybill print batch refuses more than one hundred selected documents", () => Sta(BatchLimit))
    ];

    private static Task Sta(Func<Task> action) => MarketplaceViewModelTests.StaAsync(action);
    private static readonly DateOnly Day = new(2026, 10, 9);
    private static readonly LabelPrinterGeometry Geometry = new(100, 100, 0, 0, 100, 100, 203, 203);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static WaybillRecord Document(string tracking, int hour = 12, ShippingCarrier carrier = ShippingCarrier.NovaPoshta,
        string account = "np-account", int places = 2) => new(carrier, account, "Synthetic carrier account", "doc-" + tracking, tracking,
            Day.ToDateTime(new(hour, 0)), "Створено", "Синтетичний одержувач", "Київ", 470, places)
            { CredentialFingerprint = "synthetic-account-proof" };
    private static WaybillFetchResult Result(params WaybillRecord[] documents) => new(documents, []);

    private sealed class Fixture : IDisposable
    {
        public Source Source { get; } = new();
        public Preparation Preparation { get; } = new();
        public Settings Settings { get; } = new();
        public History History { get; } = new();
        public Printer Printer { get; } = new();
        public Dialogs Dialogs { get; } = new();
        public WaybillsViewModel Model { get; }
        public Fixture()
        {
            Model = new(Source, Preparation, Settings, History, Printer, Dialogs);
            Model.SetDisplayDates(Day, Day);
        }
        public Task Load(params WaybillRecord[] documents)
        { Source.Result = Result(documents); return Model.RefreshAsync(); }
        public void Dispose() => Model.Dispose();
    }

    private static async Task FrozenSelection()
    {
        using var fixture = new Fixture();
        await fixture.Load(Document("NP-A", 8), Document("NP-B", 20), Document("RD-HIDDEN", 22, ShippingCarrier.RozetkaDelivery, "rd-account"));
        fixture.Model.SelectVisibleCommand.Execute(null);
        fixture.Model.SelectedCarrier = fixture.Model.Carriers.Single(c => c.Value == ShippingCarrier.NovaPoshta);
        fixture.Model.View.SortDescriptions.Clear();
        fixture.Model.View.SortDescriptions.Add(new(nameof(WaybillRowViewModel.TrackingNumber), ListSortDirection.Ascending));
        Check(fixture.Model.VisibleSelectedCount == 2 && fixture.Model.HiddenSelectedCount == 1, "Visible/hidden mark count is wrong.");
        fixture.Preparation.OnPrepare = () => { fixture.Model.SearchText = "NP-B"; fixture.Model.Rows.Single(r => r.TrackingNumber == "NP-B").IsSelected = false; };
        var completed = 0;
        fixture.Model.PrintCompleted += (_, _) => completed++;
        await fixture.Model.RunAsync(null, true);
        Check(fixture.Preparation.Requests.Select(r => r.Waybill.TrackingNumber).SequenceEqual(new[] { "NP-A", "NP-B" }), "Live filter or marks changed the frozen sorted batch.");
        Check(fixture.Printer.Batch!.Pages.Count == 4 && fixture.Printer.Batch.Pages.All(p => p.Shipment.Order is null), "Unlinked document or additional place was lost.");
        Check(fixture.Model.Rows.Single(r => r.TrackingNumber == "RD-HIDDEN").IsSelected &&
            fixture.Model.Rows.Where(r => r.Carrier == ShippingCarrier.NovaPoshta).All(r => !r.IsSelected && r.HasPrintedLabel), "Submitted visible documents were not cleared or hidden document was cleared.");
        Check(completed == 1 && fixture.History.Attempts.Single().Pages.Count == 4 && fixture.Preparation.ValidationCalls == 1,
            "Submission was not journaled/validated exactly once.");
    }

    private static async Task PreviewOnly()
    {
        using var fixture = new Fixture();
        await fixture.Load(Document("NP-ONE"));
        var row = fixture.Model.Rows.Single();
        fixture.Model.SelectedRow = row;
        await fixture.Model.RunAsync(row, false);
        Check(fixture.Dialogs.Previews == 1 && !fixture.Dialogs.AllowPrint && fixture.Printer.Calls == 0 && fixture.History.Attempts.Count == 0,
            "Pure preview printed or wrote the journal.");
        row.IsSelected = true;
        fixture.Dialogs.Confirm = false;
        await fixture.Model.RunAsync(null, true);
        Check(fixture.Dialogs.Previews == 2 && row.IsSelected && fixture.Printer.Calls == 0 && fixture.History.Attempts.Count == 0,
            "Declined preview changed marks or reached Windows.");
    }

    private static async Task RefreshSelection()
    {
        using var fixture = new Fixture();
        var document = Document("NP-ONE");
        await fixture.Load(document, Document("NP-TWO"));
        fixture.Model.SelectVisibleCommand.Execute(null);
        await fixture.Load(document with { Status = "Оновлено", Recipient = "Новий одержувач" },
            Document("NP-TWO") with { CredentialFingerprint = "new-account-proof" });
        Check(fixture.Model.Rows.Single(r => r.TrackingNumber == "NP-ONE").IsSelected &&
            !fixture.Model.Rows.Single(r => r.TrackingNumber == "NP-TWO").IsSelected, "Marks did not preserve exact identity or inherited another account proof.");
        await fixture.Load(document with { IsDeleted = true }, Document("NP-TWO", account: "changed-account"));
        fixture.Model.SelectVisibleCommand.Execute(null);
        var deleted = fixture.Model.Rows.Single(r => r.IsDeleted);
        deleted.IsSelected = true;
        Check(!deleted.CanSelect && !deleted.IsSelected && fixture.Model.Rows.Last().IsSelected, "Deleted waybill was selectable.");
        await fixture.Load(Document("CURRENT"));
        Check(fixture.Model.Rows.Count == 1 && fixture.Model.Rows.Single().TrackingNumber == "CURRENT" && !fixture.Model.Rows.Single().IsSelected,
            "Failed/removed account state leaked into a new result.");
    }

    private static async Task Associations()
    {
        using var fixture = new Fixture();
        fixture.Model.UpdateAssociatedOrders(document => document.TrackingNumber == "NP-ONE" ? ["4321", "4321", "4322"] : []);
        var changes = 0;
        fixture.Model.DocumentsChanged += (_, _) => changes++;
        await fixture.Load(Document("NP-ONE"), Document("NP-TWO"));
        Check(fixture.Model.Rows.Single(r => r.TrackingNumber == "NP-ONE").AssociatedOrders == "4321, 4322" && changes == 1,
            "Associated orders were not cached/applied on the carrier snapshot.");
        fixture.Model.SearchText = "4322";
        Check(fixture.Model.View.Cast<WaybillRowViewModel>().Single().TrackingNumber == "NP-ONE", "Associated order search failed.");
        fixture.Model.UpdateAssociatedOrders(_ => []);
        Check(fixture.Model.View.IsEmpty && fixture.Source.Calls == 1, "Updating associations refetched carrier APIs or left stale search rows.");
    }

    private static async Task StalePreparation()
    {
        using var fixture = new Fixture();
        await fixture.Load(Document("NP-ONE"));
        fixture.Model.SelectVisibleCommand.Execute(null);
        var gate = new TaskCompletionSource<PreparedWaybillBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Preparation.Handler = (_, _, _, _) => gate.Task;
        var printing = fixture.Model.RunAsync(null, true);
        fixture.Model.SetDisplayDates(Day.AddDays(-1), Day.AddDays(-1));
        gate.SetResult(fixture.Preparation.Build(fixture.Preparation.Requests, fixture.Settings.Value.Print, Geometry));
        await printing;
        Check(fixture.Preparation.Token.IsCancellationRequested && fixture.Dialogs.Previews == 0 && fixture.Printer.Calls == 0 &&
            !fixture.Model.IsPrintingBusy && fixture.Model.Rows.Count == 0, "A non-cooperative stale preparation reached preview/backend.");
    }

    private static async Task CancelPreparation()
    {
        using var fixture = new Fixture();
        await fixture.Load(Document("NP-ONE"));
        fixture.Model.SelectVisibleCommand.Execute(null);
        var gate = new TaskCompletionSource<PreparedWaybillBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Preparation.Handler = (_, _, _, _) => gate.Task;
        var printing = fixture.Model.RunAsync(null, true);
        Check(fixture.Model.CancelPrintCommand.CanExecute(null), "Cancel is unavailable before transmission.");
        fixture.Model.CancelPrintCommand.Execute(null);
        gate.SetResult(fixture.Preparation.Build(fixture.Preparation.Requests, fixture.Settings.Value.Print, Geometry));
        await printing;
        Check(fixture.Printer.Calls == 0 && fixture.Dialogs.Previews == 0 && fixture.Model.Rows.Single().IsSelected &&
            fixture.Model.PrintStatus.Contains("скасовано"), "Explicit cancel submitted a late batch or cleared marks.");
    }

    private static async Task ChangedCredentialsAndDriver()
    {
        foreach (var mode in new[] { "credential", "driver", "settings", "refresh" })
        {
            using var fixture = new Fixture();
            await fixture.Load(Document("NP-ONE"));
            fixture.Model.SelectVisibleCommand.Execute(null);
            fixture.Dialogs.OnPreview = () =>
            {
                if (mode == "credential") fixture.Preparation.ValidationError = new InvalidOperationException("API-підключення змінено.");
                if (mode == "driver") fixture.Printer.Geometry = Geometry with { DpiX = 300 };
                if (mode == "settings") fixture.Settings.Value = fixture.Settings.Value with { Print = fixture.Settings.Value.Print with { Copies = 2 } };
                if (mode == "refresh") fixture.Model.Invalidate();
            };
            await fixture.Model.RunAsync(null, true);
            Check(fixture.Printer.Calls == 0 && fixture.History.Attempts.Count == 0 && !fixture.Model.IsPrintingBusy,
                $"Changed {mode} reached submission.");
            if (mode != "refresh") Check(fixture.Dialogs.Errors.Count > 0 && fixture.Model.Rows.Single().IsSelected,
                $"Changed {mode} lost the mark or diagnostic.");
        }
    }

    private static LabelPrintAttempt Attempt(WaybillRecord document, LabelSubmissionState state, string id = "attempt", int pages = 2) =>
        new(id, "Synthetic labels", "Synthetic printer", DateTimeOffset.UtcNow, state,
            Enumerable.Range(1, pages).Select(page => new LabelAttemptPage(document.Carrier, document.ConnectionId, document.TrackingNumber,
                document.DocumentId, page, "synthetic-pdf-proof")).ToArray(), state == LabelSubmissionState.Submitted ? 42 : null);

    private static async Task HistoryScope()
    {
        using var fixture = new Fixture();
        var document = Document("SAME-TTN");
        await fixture.History.SaveAsync(Attempt(document, LabelSubmissionState.Submitted));
        await fixture.Load(document, document with { ConnectionId = "other-account" }, document with { Carrier = ShippingCarrier.RozetkaDelivery });
        Check(fixture.Model.Rows.Count(r => r.HasPrintedLabel) == 1 && fixture.Model.Rows.Single(r => r.Carrier == document.Carrier && r.ConnectionId == document.ConnectionId).HasPrintedLabel,
            "History crossed carrier or account boundaries.");
        var other = fixture.Model.Rows.Single(r => r.ConnectionId == "other-account");
        other.IsSelected = true;
        fixture.Dialogs.Confirm = false;
        await fixture.Model.RunAsync(null, true);
        Check(fixture.Dialogs.Previous.Count == 0, "Repeat warning inherited another carrier account.");
        other.IsSelected = false;
        var original = fixture.Model.Rows.Single(r => r.Carrier == document.Carrier && r.ConnectionId == document.ConnectionId);
        original.IsSelected = true;
        await fixture.Model.RunAsync(null, true);
        Check(fixture.Dialogs.Previous.Count == 1 && original.IsSelected && fixture.Printer.Calls == 0,
            "Same-account restart history did not require repeat confirmation.");
        var partial = new WaybillRowViewModel(document);
        partial.ApplyHistory([Attempt(document, LabelSubmissionState.Submitted, pages: 1)]);
        Check(!partial.HasPrintedLabel, "One submitted place marked a two-place document complete.");
    }

    private static async Task UnknownAndReprint()
    {
        using var fixture = new Fixture();
        await fixture.Load(Document("NP-ONE"));
        fixture.Model.SelectVisibleCommand.Execute(null);
        fixture.Printer.Failure = "unknown";
        await fixture.Model.RunAsync(null, true);
        var row = fixture.Model.Rows.Single();
        Check(row.PrintStatus == LabelSubmissionState.SubmissionUnknown && !row.HasPrintedLabel && row.IsSelected &&
            !fixture.Model.RetryFailedCommand.CanExecute(null), "Uncertain transmission became an automatic retry.");
        await fixture.Model.RunAsync(null, true, true);
        Check(fixture.Printer.Calls == 1, "Retry submitted an uncertain document.");
        fixture.Printer.Failure = "";
        fixture.Dialogs.Confirm = false;
        await fixture.Model.RunAsync(null, true);
        Check(fixture.Dialogs.Previous.Count == 1 && fixture.Printer.Calls == 1, "Uncertain job did not warn before explicit repeat.");
        fixture.Printer.Failure = "safe";
        fixture.Dialogs.Confirm = true;
        await fixture.Model.RunAsync(null, true);
        await fixture.Model.RestoreHistoryAsync();
        Check(row.PrintStatus == LabelSubmissionState.SubmissionUnknown && !row.CanRetry && !fixture.Model.RetryFailedCommand.CanExecute(null),
            "Failed repeat erased an earlier uncertain transmission and enabled automatic retry.");
        fixture.Printer.Failure = "";
        fixture.Dialogs.Confirm = true;
        await fixture.Model.RunAsync(null, true);
        Check(fixture.Printer.Calls == 3 && row.HasPrintedLabel && !row.IsSelected, "Confirmed explicit repeat could not submit the independent document.");
    }

    private static async Task HistoryAliases()
    {
        using var fixture = new Fixture();
        const string secret = "synthetic-np-key-one";
        fixture.Settings.Value = fixture.Settings.Value with
        {
            NovaPoshtaConnections = [new("np-account", "Canonical", secret), new("same-account-alias", "Alias", secret),
                new("different-account", "Different", "synthetic-np-key-two")]
        };
        var document = Document("NP-ONE") with { CredentialFingerprint = WaybillAccountProof.Create(ShippingCarrier.NovaPoshta, "np-account", secret) };
        await fixture.History.SaveAsync(Attempt(document with { ConnectionId = "same-account-alias" }, LabelSubmissionState.SubmissionUnknown));
        await fixture.History.SaveAsync(Attempt(document with { ConnectionId = "different-account" }, LabelSubmissionState.Submitted, "other-key"));
        await fixture.Load(document);
        var row = fixture.Model.Rows.Single();
        Check(row.PrintStatus == LabelSubmissionState.SubmissionUnknown && !row.HasPrintedLabel,
            "Alias history was missed or a different key marked the document printed.");
        row.IsSelected = true;
        fixture.Dialogs.Confirm = false;
        await fixture.Model.RunAsync(null, true);
        Check(fixture.Dialogs.Previous.Count == 1 && fixture.Dialogs.Previous.Single().State == LabelSubmissionState.SubmissionUnknown && fixture.Printer.Calls == 0,
            "Same-key alias was not warned before repeat, or different-key history leaked.");
        var stale = new WaybillRowViewModel(document with { CredentialFingerprint = "stale-proof" });
        stale.ApplyAccountAliases(fixture.Settings.Value);
        stale.ApplyHistory(fixture.History.Attempts);
        Check(stale.PrintStatus == LabelSubmissionState.NotSubmitted && !stale.HasPrintedLabel,
            "Stale carrier row gained another configured alias's history.");
    }

    private static async Task RetryFailure()
    {
        using var fixture = new Fixture();
        await fixture.Load(Document("NP-ONE"), Document("RD-TWO", carrier: ShippingCarrier.RozetkaDelivery, account: "rd-account"));
        fixture.Model.SelectVisibleCommand.Execute(null);
        fixture.Printer.Failure = "safe";
        await fixture.Model.RunAsync(null, true);
        Check(fixture.Model.Rows.All(r => r.CanRetry && r.IsSelected) && fixture.Model.RetryFailedCommand.CanExecute(null), "Safe pre-transfer failure cannot retry.");
        fixture.Model.SelectedCarrier = fixture.Model.Carriers.Single(c => c.Value == ShippingCarrier.NovaPoshta);
        fixture.Printer.Failure = "";
        await fixture.Model.RunAsync(null, true, true);
        Check(fixture.Printer.Batch!.Pages.All(p => p.Shipment.Carrier == ShippingCarrier.NovaPoshta) &&
            fixture.Model.Rows.Single(r => r.Carrier == ShippingCarrier.RozetkaDelivery).IsSelected &&
            fixture.Model.Rows.Single(r => r.Carrier == ShippingCarrier.NovaPoshta).HasPrintedLabel, "Retry included hidden documents or cleared unsubmitted marks.");
    }

    private static async Task BoundaryRefresh()
    {
        using var fixture = new Fixture();
        await fixture.Load(Document("NP-ONE"));
        fixture.Model.SelectVisibleCommand.Execute(null);
        fixture.History.BeforeSave = attempt =>
        {
            if (attempt.State == LabelSubmissionState.NotSubmitted) fixture.Model.Invalidate();
        };
        await fixture.Model.RunAsync(null, true);
        Check(fixture.Printer.Calls == 0 && fixture.History.Attempts.Single().State == LabelSubmissionState.NotSubmitted,
            "Late settings invalidation crossed the backend boundary after first journal write.");
    }

    private static async Task TransferState()
    {
        using var fixture = new Fixture();
        await fixture.Load(Document("NP-ONE"));
        fixture.Model.SelectVisibleCommand.Execute(null);
        fixture.Printer.AfterBoundary = () =>
        {
            Check(!fixture.Model.CancelPrintCommand.CanExecute(null), "Cancel remained available after Windows transmission started.");
            fixture.Model.CancelPrint();
            Check(!fixture.Preparation.Token.IsCancellationRequested, "Post-boundary cancel incorrectly cancelled a transmitting batch.");
        };
        await fixture.Model.RunAsync(null, true);
        Check(fixture.Model.PrintStatus.Contains("передано Windows") && fixture.Model.Rows.Single().HasPrintedLabel,
            "Completed transmission was reported as cancelled preparation.");
    }

    private static async Task BatchLimit()
    {
        using var fixture = new Fixture();
        await fixture.Load(Enumerable.Range(1, 101).Select(i => Document($"NP-{i:000}")).ToArray());
        fixture.Model.SelectVisibleCommand.Execute(null);
        await fixture.Model.RunAsync(null, true);
        Check(fixture.Preparation.Requests.Count == 0 && fixture.Dialogs.Errors.Single().Contains("100") && fixture.Printer.Calls == 0,
            "Oversized selection reached preparation/backend.");
    }

    private static async Task RefreshDuringTransfer()
    {
        using var fixture = new Fixture();
        var document = Document("NP-ONE");
        await fixture.Load(document);
        fixture.Model.SelectVisibleCommand.Execute(null);
        var oldRow = fixture.Model.Rows.Single();
        fixture.Printer.DuringTransfer = async () =>
        {
            await fixture.Load(document with { Status = "Оновлено під час передавання" });
            Check(!ReferenceEquals(oldRow, fixture.Model.Rows.Single()) && fixture.Model.Rows.Single().IsSelected,
                "Synthetic refresh did not replace the row while retaining the active mark.");
        };
        await fixture.Model.RunAsync(null, true);
        Check(!fixture.Model.Rows.Single().IsSelected && fixture.Model.Rows.Single().HasPrintedLabel &&
            fixture.History.Attempts.Single().State == LabelSubmissionState.Submitted,
            "Completed submission cleared only a detached row and left the current row marked.");
    }

    private sealed class Source : IWaybillService
    {
        public WaybillFetchResult Result { get; set; } = new([], []);
        public int Calls { get; private set; }
        public Task<WaybillFetchResult> FetchAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(Result); }
    }
    private sealed class Preparation : IWaybillLabelPreparation
    {
        public IReadOnlyList<WaybillPrintRequest> Requests { get; private set; } = [];
        public CancellationToken Token { get; private set; }
        public Action? OnPrepare { get; set; }
        public Exception? ValidationError { get; set; }
        public int ValidationCalls { get; private set; }
        public Func<IReadOnlyList<WaybillPrintRequest>, LabelPrintSettings, LabelPrinterGeometry, CancellationToken, Task<PreparedWaybillBatch>>? Handler { get; set; }
        public Task<PreparedWaybillBatch> PrepareAsync(IReadOnlyList<WaybillPrintRequest> documents, LabelPrintSettings settings,
            LabelPrinterGeometry geometry, CancellationToken cancellationToken = default)
        {
            Requests = documents; Token = cancellationToken; OnPrepare?.Invoke();
            return Handler?.Invoke(documents, settings, geometry, cancellationToken) ?? Task.FromResult(Build(documents, settings, geometry));
        }
        public PreparedWaybillBatch Build(IReadOnlyList<WaybillPrintRequest> requests, LabelPrintSettings settings, LabelPrinterGeometry geometry)
        {
            var pages = requests.SelectMany(request => Enumerable.Range(1, request.Waybill.Places ?? 1).Select(place =>
                new ShippingLabelPage(new(null, string.Join(", ", request.OrderNumbers), request.Waybill.Carrier, request.Waybill.TrackingNumber,
                    request.Waybill.DocumentId) { CarrierConnectionId = request.Waybill.ConnectionId },
                    request.Waybill.DocumentId, "synthetic-pdf-proof", place, 100, 100, [])
                { ConnectionId = request.Waybill.ConnectionId })).ToArray();
            return new(new("synthetic-batch", pages, settings) { IsWaybillBatch = true, SelectedWaybillCount = requests.Count,
                RasterDpiX = geometry.DpiX, RasterDpiY = geometry.DpiY }, new Dictionary<string, string>());
        }
        public Task ValidateAsync(PreparedWaybillBatch batch, CancellationToken cancellationToken = default)
        { ValidationCalls++; return ValidationError is null ? Task.CompletedTask : Task.FromException(ValidationError); }
    }
    private sealed class Settings : IShippingSettingsStore
    {
        public ShippingLabelSettings Value { get; set; } = new() { Print = new() { PrinterName = "Synthetic printer", WidthMm = 100, HeightMm = 100 } };
        public Task<ShippingLabelSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task SaveAsync(ShippingLabelSettings settings, CancellationToken cancellationToken = default)
        { Value = settings; return Task.CompletedTask; }
    }
    private sealed class History : ILabelHistoryStore
    {
        public List<LabelPrintAttempt> Attempts { get; } = [];
        public Action<LabelPrintAttempt>? BeforeSave { get; set; }
        public Task<IReadOnlyList<LabelPrintAttempt>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LabelPrintAttempt>>(Attempts.ToArray());
        public Task SaveAsync(LabelPrintAttempt attempt, CancellationToken cancellationToken = default)
        {
            BeforeSave?.Invoke(attempt);
            Attempts.RemoveAll(old => old.AttemptId == attempt.AttemptId);
            Attempts.Add(attempt);
            return Task.CompletedTask;
        }
    }
    private sealed class Printer : ILabelPrinter
    {
        public int Calls { get; private set; }
        public LabelPrintBatch? Batch { get; private set; }
        public string Failure { get; set; } = "";
        public Action? AfterBoundary { get; set; }
        public Func<Task>? DuringTransfer { get; set; }
        public LabelPrinterGeometry Geometry { get; set; } = WaybillPrintingViewModelTests.Geometry;
        public IReadOnlyList<string> Printers() => ["Synthetic printer"];
        public IReadOnlyList<LabelDriverFormat> Formats(string printer) => [new("Synthetic", 100, 100)];
        public LabelPrinterGeometry Inspect(LabelPrintSettings settings) => Geometry;
        public async Task<int?> SubmitAsync(LabelPrintBatch batch, string jobName, Func<Task> beforeTransfer,
            Func<int, Task> jobCreated, CancellationToken cancellationToken = default)
        {
            Calls++; Batch = batch;
            if (Failure == "safe") throw new InvalidOperationException("Synthetic failure before transfer");
            await beforeTransfer();
            AfterBoundary?.Invoke();
            if (DuringTransfer is not null) await DuringTransfer();
            if (Failure == "unknown") throw new InvalidOperationException("Synthetic failure after boundary");
            await jobCreated(42);
            return 42;
        }
    }
    private sealed class Dialogs : ILabelDialogs
    {
        public bool Confirm { get; set; } = true;
        public bool AllowPrint { get; private set; }
        public int Previews { get; private set; }
        public IReadOnlyList<LabelPrintAttempt> Previous { get; private set; } = [];
        public List<string> Errors { get; } = [];
        public Action? OnPreview { get; set; }
        public bool Preview(LabelPrintBatch batch, LabelPrinterGeometry geometry, IReadOnlyList<LabelPrintAttempt> previous, bool allowPrint)
        { Previews++; Previous = previous; AllowPrint = allowPrint; OnPreview?.Invoke(); return Confirm; }
        public Task SettingsAsync() => Task.CompletedTask;
        public void Error(string message) => Errors.Add(message);
    }
}
