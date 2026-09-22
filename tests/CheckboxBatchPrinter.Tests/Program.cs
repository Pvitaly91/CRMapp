using System.IO;
using System.Net;
using System.Net.Http;
using System.Printing;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static class Program
{
    private static readonly List<(string Name, Func<Task> Test)> Tests =
    [
        ("parsing API responses", TestParsingAsync),
        ("pagination", TestPaginationAsync),
        ("date filtering", TestDateRangeAsync),
        ("selection", TestSelectionAsync),
        ("image scaling", TestImageScalingAsync),
        ("printable size", TestPrintableSizeAsync),
        ("print queue continues after error", TestBatchQueueAsync),
        ("submitted is not physically printed", TestSubmittedIsNotPrintedAsync),
        ("unconfirmed result requires explicit repeat", TestUnconfirmedRetryPolicyAsync),
        ("batch cancellation stops later submissions", TestReliableBatchCancellationAsync),
        ("print order follows current sort", TestPrintOrderAsync),
        ("hidden selection is counted and excluded", TestHiddenSelectionAsync),
        ("unsupported page size is rejected", TestUnsupportedPageSizeAsync),
        ("short narrow and custom pages", TestShortNarrowAndCustomPagesAsync),
        ("installed roll driver accepts a short custom page without submission", TestInstalledRollDriverCustomPageAsync),
        ("test print persists chosen printer without saving unsaved Checkbox login", TestPrintPersistsPrinterAsync),
        ("long receipt validation", TestLongReceiptAsync),
        ("one submission error keeps remaining items", TestReliableBatchContinuesAsync),
        ("submitted receipt survives F5 and warns on repeat", TestRefreshPreservesSubmittedAttemptAsync),
        ("date range roundtrip preserves print history", TestDateRangeRoundtripAsync),
        ("restart restores unknown submission", TestRestartRestoresUnknownAttemptAsync),
        ("print history is account scoped and retained for a bounded period", TestJournalScopeAndRetentionAsync),
        ("backend throw after registration is submission unknown", TestBoundaryUnknownAsync),
        ("preparation failure is not submitted", TestPreparationFailureAsync),
        ("unknown shared job marks the full batch", TestUnknownSharedJobAsync),
        ("test bitmap keeps content and edge marks at multiple DPI", TestBitmapDpiAsync),
        ("retry", TestRetryAsync),
        ("API errors", TestApiErrorAsync)
    ];

    public static async Task<int> Main(string[] args)
    {
        if (args is ["--physical-test-rongta"])
        {
            WindowsPrintService.DiagnosticTrace = step => Console.WriteLine($"TRACE {DateTimeOffset.Now:HH:mm:ss} {step}");
            using var printer = new WindowsPrintService();
            var settings = new AppSettings
            {
                PrinterName = "RONGTA RPP210 Series Printer",
                PaperWidth = PaperWidth.Mm50,
                PrintableWidthMm = 40
            };
            var submission = await printer.PrintTestAsync(settings);
            Console.WriteLine($"Submitted job {submission.JobId} to {submission.PrinterName}; " +
                              $"media {submission.PageValidation.AcceptedWidthDip / PrintGeometry.DipPerMillimeter:0.##} × " +
                              $"{submission.PageValidation.AcceptedHeightDip / PrintGeometry.DipPerMillimeter:0.##} mm.");
            return 0;
        }
        var failed = 0;
        foreach (var (name, test) in Tests)
        {
            try { await test(); Console.WriteLine($"PASS  {name}"); }
            catch (Exception exception) { failed++; Console.WriteLine($"FAIL  {name}: {exception.Message}"); }
        }
        Console.WriteLine($"\n{Tests.Count - failed}/{Tests.Count} tests passed.");
        return failed == 0 ? 0 : 1;
    }

    private static Task TestParsingAsync()
    {
        const string shortJson = """
        {"meta":{"limit":100,"offset":0},"results":[{
          "id":"497f6eca-6276-4993-bfeb-53cbbbba6f08","fiscal_date":"2026-09-21T08:15:22+03:00",
          "fiscal_code":"FN-001","status":"DONE","type":"SELL","serial":42,"goods":[],
          "cash_register":{"id":"397f6eca-6276-4993-bfeb-53cbbbba6f08","fiscal_number":"4000000001"},
          "branch":{"id":"297f6eca-6276-4993-bfeb-53cbbbba6f08","name":"Магазин","address":"Київ"},
          "shift":{"id":"197f6eca-6276-4993-bfeb-53cbbbba6f08","serial":7},
          "payments":[{"type":"CASHLESS","label":"Картка","value":12345}],
          "total_sum":12345,"total_payment":12345,"total_rest":0,"round_sum":0,"organization_id":"097f6eca-6276-4993-bfeb-53cbbbba6f08"
        }]}
        """;
        var shortResult = ReceiptParser.ParsePage(shortJson).Single();
        Equal("SELL", shortResult.Type);
        Equal(123.45m, shortResult.TotalSum);
        Equal("Картка", shortResult.PaymentDisplay);
        Equal("4000000001", shortResult.CashRegisterFiscalNumber);

        const string fullJson = """
        {"meta":{"limit":25,"offset":0},"results":[{
          "id":"497f6eca-6276-4993-bfeb-53cbbbba6f08","serial":9,"status":"DONE","type":"RETURN",
          "created_at":"2026-09-21T09:00:00Z","total_sum":500,"payments":[{"type":"CASH","label":"Готівка","value":500}],
          "shift":{"cash_register":{"fiscal_number":"CR-FULL"}}
        }]}
        """;
        var fullResult = ReceiptParser.ParsePage(fullJson).Single();
        Equal("RETURN", fullResult.Type);
        Equal("CR-FULL", fullResult.CashRegisterFiscalNumber);
        return Task.CompletedTask;
    }

    private static async Task TestPaginationAsync()
    {
        var requestedOffsets = new List<int>();
        var handler = new StubHandler(request =>
        {
            var query = request.RequestUri!.Query;
            var offset = query.Contains("offset=100", StringComparison.Ordinal) ? 100 : 0;
            requestedOffsets.Add(offset);
            var count = offset == 0 ? 100 : 1;
            var results = Enumerable.Range(offset, count).Select(i => new
            {
                id = $"00000000-0000-0000-0000-{i:D12}", serial = i + 1, status = "DONE", type = "SELL",
                total_sum = 100, payments = Array.Empty<object>()
            });
            return JsonResponse(new { meta = new { limit = 100, offset }, results });
        });
        using var client = new HttpClient(handler);
        var settings = new MemorySettingsService(new AppSettings());
        var api = new CheckboxApiClient(client, new StaticAuthentication(), new NullLogger());
        var service = new ReceiptService(api, settings);
        var records = await service.GetReceiptsAsync(new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 21));
        Equal(101, records.Count);
        SequenceEqual(new[] { 0, 100 }, requestedOffsets);
    }

    private static Task TestDateRangeAsync()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("EET-test", TimeSpan.FromHours(3), "EET", "EET");
        var range = DateRangeBuilder.ForLocalDates(new DateOnly(2026, 9, 20), new DateOnly(2026, 9, 21), zone);
        Equal(new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.FromHours(3)), range.From);
        Equal(new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.FromHours(3)), range.ToExclusive);
        return Task.CompletedTask;
    }

    private static Task TestSelectionAsync()
    {
        var values = new[] { new Selectable(), new Selectable(), new Selectable() };
        SelectionService.SetVisibleSelection(values.Take(2), (x, selected) => x.Selected = selected, true);
        True(values[0].Selected && values[1].Selected && !values[2].Selected);
        return Task.CompletedTask;
    }

    private static Task TestImageScalingAsync()
    {
        var geometry = PrintGeometry.Calculate(400, 1200, 54, 58);
        NearlyEqual(geometry.WidthDip * 3, geometry.HeightDip);
        return Task.CompletedTask;
    }

    private static Task TestPrintableSizeAsync()
    {
        var geometry = PrintGeometry.Calculate(400, 800, 48, 50, 1);
        NearlyEqual(48 * 96 / 25.4, geometry.WidthDip);
        NearlyEqual(1 * 96 / 25.4, geometry.MarginDip);
        Throws<ArgumentOutOfRangeException>(() => PrintGeometry.Calculate(400, 800, 60, 58));
        return Task.CompletedTask;
    }

    private static async Task TestBatchQueueAsync()
    {
        var processed = new List<int>();
        var result = await BatchProcessor.RunAsync(new[] { 1, 2, 3 }, (item, _) =>
        {
            processed.Add(item);
            return item == 2 ? Task.FromException(new InvalidOperationException("printer")) : Task.CompletedTask;
        });
        SequenceEqual(new[] { 1, 2, 3 }, processed);
        Equal(2, result.SuccessCount);
        Equal(1, result.ErrorCount);
    }

    private static Task TestSubmittedIsNotPrintedAsync()
    {
        var submission = FakeSubmission(17);
        Equal(WindowsPrintJobState.Queued, submission.InitialObservation.State);
        True(submission.InitialObservation.State != WindowsPrintJobState.CompletedBySpooler);
        True(!submission.InitialObservation.IsTerminalForMonitoring);
        return Task.CompletedTask;
    }

    private static Task TestUnconfirmedRetryPolicyAsync()
    {
        True(PrintRetryPolicy.RequiresExplicitConfirmation(PrintItemStatus.ResultNotConfirmed, true));
        True(!PrintRetryPolicy.CanRetryWithoutWarning(PrintItemStatus.ResultNotConfirmed, true));
        True(!PrintRetryPolicy.CanRetryWithoutWarning(PrintItemStatus.Error, true));
        True(PrintRetryPolicy.CanRetryWithoutWarning(PrintItemStatus.Error, false));
        return Task.CompletedTask;
    }

    private static async Task TestReliableBatchCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var backend = new FakePrinterBackend();
        var result = await ReliableBatchRunner.RunAsync(new[] { 1, 2, 3 }, async (item, token) =>
        {
            var submission = await backend.SubmitAsync(item, token);
            cancellation.Cancel();
            return submission;
        }, cancellation.Token);
        SequenceEqual(new[] { 1 }, backend.Submitted);
        Equal(1, result.SubmittedCount);
        Equal(2, result.CancelledCount);
    }

    private static Task TestPrintOrderAsync()
    {
        var all = new[]
        {
            new SelectionItem("A", true), new SelectionItem("B", true), new SelectionItem("C", false)
        };
        var sortedVisible = new[] { all[1], all[2], all[0] };
        var snapshot = PrintSelectionPlanner.CreateSnapshot(all, sortedVisible, x => x.Selected);
        SequenceEqual(new[] { "B", "A" }, snapshot.Items.Select(x => x.Id));
        return Task.CompletedTask;
    }

    private static Task TestHiddenSelectionAsync()
    {
        var all = new[]
        {
            new SelectionItem("visible-1", true), new SelectionItem("hidden", true), new SelectionItem("visible-2", true)
        };
        var snapshot = PrintSelectionPlanner.CreateSnapshot(all, new[] { all[2], all[0] }, x => x.Selected);
        SequenceEqual(new[] { "visible-2", "visible-1" }, snapshot.Items.Select(x => x.Id));
        Equal(1, snapshot.HiddenSelectedCount);
        return Task.CompletedTask;
    }

    private static Task TestUnsupportedPageSizeAsync()
    {
        var request = new RequestedPageLayout(Mm(58), Mm(180), Mm(54), Mm(170), Mm(1));
        var substituted = new DriverPageMetrics(Mm(80), Mm(297), 0, 0, Mm(76), Mm(287), 5, true);
        Throws<UnsupportedPrinterPageException>(() => PrinterPageValidator.Validate(request, substituted));

        var tooNarrow = new DriverPageMetrics(Mm(58), Mm(180), Mm(3), Mm(2), Mm(48), Mm(176), 3, false);
        Throws<UnsupportedPrinterPageException>(() => PrinterPageValidator.Validate(request, tooNarrow));
        return Task.CompletedTask;
    }

    private static Task TestLongReceiptAsync()
    {
        var geometry = PrintGeometry.Calculate(400, 8000, 54, 58, 1);
        var request = new RequestedPageLayout(Mm(58), geometry.HeightDip + Mm(4), geometry.WidthDip, geometry.HeightDip, geometry.MarginDip);
        var supported = new DriverPageMetrics(Mm(58), request.PageHeightDip, Mm(2), Mm(1), Mm(54), request.PageHeightDip - Mm(2), 2, false);
        var result = PrinterPageValidator.Validate(request, supported);
        NearlyEqual(geometry.HeightDip, result.ImageHeightDip);

        var clamped = supported with { AcceptedHeightDip = Mm(300), ImageableHeightDip = Mm(298) };
        Throws<UnsupportedPrinterPageException>(() => PrinterPageValidator.Validate(request, clamped));
        return Task.CompletedTask;
    }

    private static Task TestInstalledRollDriverCustomPageAsync()
    {
        const string printerName = "RONGTA RPP210 Series Printer";
        using var printService = new WindowsPrintService();
        if (!printService.PrinterExists(printerName)) return Task.CompletedTask;

        RunOnSta(() =>
        {
            using var server = new LocalPrintServer();
            using var queue = new PrintQueue(server, printerName);
            var settings = new AppSettings
            {
                PrinterName = printerName,
                PaperWidth = PaperWidth.Mm50,
                PrintableWidthMm = 40
            };
            var source = TestReceiptBitmapRenderer.Render(settings, 203).Bitmap;
            var prepared = WindowsPrintService.PreparePage(queue, source, settings);
            using var ticketReader = new StreamReader(prepared.Ticket.GetXmlStream());
            if (!ticketReader.ReadToEnd().Contains("CustomMediaSize", StringComparison.Ordinal))
                throw new Exception("RONGTA's GDI custom media option was not selected.");
            var page = prepared.Layout.Validation;
            var gdi = GdiReceiptPrinter.Probe(printerName, prepared.DriverDevMode!);
            var expectedHeightMm = page.AcceptedHeightDip / PrintGeometry.DipPerMillimeter;
            if (Math.Abs(gdi.PhysicalHeightMm - expectedHeightMm) > 2)
                throw new Exception($"GDI page height {gdi.PhysicalHeightMm:0.##} mm differs from {expectedHeightMm:0.##} mm.");
            if (page.AcceptedWidthDip < Mm(40) || page.AcceptedWidthDip > Mm(50.5))
                throw new Exception("RONGTA did not accept a suitable short-page width.");
            if (page.AcceptedHeightDip > Mm(300) || page.AcceptedHeightDip < page.RequestedHeightDip - Mm(0.5))
                throw new Exception("RONGTA fell back to its 2527 mm roll form.");
            if (page.ImageableWidthDip < prepared.Layout.ImageWidthDip ||
                page.ImageableHeightDip < prepared.Layout.ImageHeightDip + Mm(1))
                throw new Exception("The actual imageable area clips the test receipt.");

            settings.PaperWidth = PaperWidth.Mm58;
            settings.PrintableWidthMm = 54;
            var wideSource = TestReceiptBitmapRenderer.Render(settings, 203).Bitmap;
            var fitted = WindowsPrintService.PreparePage(queue, wideSource, settings);
            if (fitted.Layout.ImageWidthDip > fitted.Layout.Validation.ImageableWidthDip ||
                fitted.Layout.ImageWidthDip >= Mm(54) ||
                fitted.Layout.Validation.AcceptedHeightDip > Mm(300))
                throw new Exception("The default 58 mm settings were not fitted to the RONGTA stock.");
        });
        return Task.CompletedTask;
    }

    private static async Task TestPrintPersistsPrinterAsync()
    {
        var saved = new AppSettings { Login = "saved@example.invalid", PrinterName = "Fax" };
        var settingsService = new MemorySettingsService(saved);
        using var printer = new ScenarioPrintService();
        var viewModel = new SettingsViewModel(settingsService, new StaticAuthentication(),
            new FakeImageService(), printer, new NullLogger());
        await viewModel.LoadAsync();
        Equal("Fake printer", viewModel.SelectedPrinter);
        viewModel.SelectedPrinter = "Fake printer";
        viewModel.Login = "unsaved@example.invalid";
        var submission = await viewModel.TestPrintAsync();
        Equal("Fake printer", submission.PrinterName);
        var persisted = await settingsService.LoadAsync();
        Equal("Fake printer", persisted.PrinterName);
        Equal("saved@example.invalid", persisted.Login);
    }

    private static Task TestShortNarrowAndCustomPagesAsync()
    {
        var narrow = new RequestedPageLayout(Mm(50), Mm(65), Mm(46), Mm(60), Mm(1));
        var narrowDriver = new DriverPageMetrics(Mm(50), Mm(65), Mm(2), Mm(1), Mm(46), Mm(63), 2, false);
        var narrowResult = PrinterPageValidator.Validate(narrow, narrowDriver);
        NearlyEqual(Mm(46), narrowResult.ImageWidthDip);

        var custom = new RequestedPageLayout(Mm(63), Mm(92), Mm(59), Mm(87), Mm(1));
        var customDriver = new DriverPageMetrics(Mm(63), Mm(92), Mm(2), Mm(1), Mm(59), Mm(90), 0, false);
        var customResult = PrinterPageValidator.Validate(custom, customDriver);
        NearlyEqual(Mm(63), customResult.PageWidthDip);
        return Task.CompletedTask;
    }

    private static async Task TestReliableBatchContinuesAsync()
    {
        var backend = new FakePrinterBackend { FailItem = 2 };
        var result = await ReliableBatchRunner.RunAsync(new[] { 1, 2, 3 }, backend.SubmitAsync);
        SequenceEqual(new[] { 1, 2, 3 }, backend.Attempted);
        SequenceEqual(new[] { 1, 3 }, backend.Submitted);
        Equal(2, result.SubmittedCount);
        Equal(1, result.ErrorCount);
    }

    private static Task TestRefreshPreservesSubmittedAttemptAsync()
    {
        RunOnSta(() =>
        {
            var receipt = TestReceipt("receipt-refresh", DateTime.Today);
            var settings = TestSettings(separateJobs: true);
            var dialogs = new RecordingDialogs();
            var store = new MemoryPrintAttemptStore();
            using var printer = new ScenarioPrintService();
            var viewModel = CreateViewModel(new ScenarioReceiptService((_, _) => [receipt]), settings, printer, store, dialogs);

            viewModel.InitializeAsync().GetAwaiter().GetResult();
            viewModel.Receipts.Single().IsSelected = true;
            viewModel.PrintSelectedCommand.Execute(null);
            Equal(1, dialogs.Confirmations.Count);
            True(!dialogs.Confirmations[0].ContainsPreviouslySubmittedReceipts);

            viewModel.RefreshCommand.Execute(null); // F5 uses the same command.
            var restored = viewModel.Receipts.Single();
            True(restored.HasSubmissionRisk);
            Equal(101, restored.WindowsJobId);
            restored.IsSelected = true;
            viewModel.PrintSelectedCommand.Execute(null);
            Equal(2, dialogs.Confirmations.Count);
            True(dialogs.Confirmations[1].ContainsPreviouslySubmittedReceipts);
        });
        return Task.CompletedTask;
    }

    private static Task TestDateRangeRoundtripAsync()
    {
        RunOnSta(() =>
        {
            var originalDate = DateTime.Today;
            var receipt = TestReceipt("receipt-date", originalDate);
            var settings = TestSettings(separateJobs: true);
            var store = new MemoryPrintAttemptStore();
            using var printer = new ScenarioPrintService();
            var viewModel = CreateViewModel(
                new ScenarioReceiptService((from, to) => from == DateOnly.FromDateTime(originalDate) && to == from ? [receipt] : []),
                settings, printer, store, new RecordingDialogs());

            viewModel.InitializeAsync().GetAwaiter().GetResult();
            viewModel.Receipts.Single().IsSelected = true;
            viewModel.PrintSelectedCommand.Execute(null);

            viewModel.DateFrom = originalDate.AddDays(-1);
            viewModel.DateTo = originalDate.AddDays(-1);
            viewModel.RefreshCommand.Execute(null);
            Equal(0, viewModel.Receipts.Count);

            viewModel.DateFrom = originalDate;
            viewModel.DateTo = originalDate;
            viewModel.RefreshCommand.Execute(null);
            var restored = viewModel.Receipts.Single();
            True(restored.HasSubmissionRisk);
            Equal(101, restored.WindowsJobId);
        });
        return Task.CompletedTask;
    }

    private static Task TestRestartRestoresUnknownAttemptAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"checkbox-attempt-test-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "attempts.json");
        try
        {
            var settings = TestSettings(separateJobs: true);
            var context = PrintAccountContext.Create(settings);
            var attempt = PrintAttemptFactory.CreateReceipt("receipt-restart", settings.PrinterName);
            var firstStore = new JsonPrintAttemptStore(path);
            firstStore.UpsertMany([
                new PrintAttemptRecord(attempt.AttemptId, context, "receipt-restart", attempt.PrinterName,
                    attempt.UniqueJobName, null, attempt.StartedAtUtc, DateTimeOffset.UtcNow,
                    PrintSubmissionState.SubmissionUnknown, PrintItemStatus.ResultNotConfirmed)
            ]);

            RunOnSta(() =>
            {
                using var printer = new ScenarioPrintService();
                var restartedStore = new JsonPrintAttemptStore(path);
                var viewModel = CreateViewModel(
                    new ScenarioReceiptService((_, _) => [TestReceipt("receipt-restart", DateTime.Today)]),
                    settings, printer, restartedStore, new RecordingDialogs());
                viewModel.InitializeAsync().GetAwaiter().GetResult();
                var restored = viewModel.Receipts.Single();
                True(restored.HasSubmissionRisk);
                Equal(PrintItemStatus.ResultNotConfirmed, restored.PrintStatus);
                True(!restored.CanRetryWithoutWarning);
            });
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        return Task.CompletedTask;
    }

    private static Task TestJournalScopeAndRetentionAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"checkbox-attempt-scope-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "attempts.json");
        try
        {
            var settingsA = TestSettings(separateJobs: true);
            settingsA.Login = "account-a@example.invalid";
            var settingsB = TestSettings(separateJobs: true);
            settingsB.Login = "account-b@example.invalid";
            var contextA = PrintAccountContext.Create(settingsA);
            var contextB = PrintAccountContext.Create(settingsB);
            var now = DateTimeOffset.UtcNow;
            var currentA = PrintAttemptFactory.CreateReceipt("same-receipt", settingsA.PrinterName,
                startedAtUtc: now.AddMinutes(-2));
            var expiredA = PrintAttemptFactory.CreateReceipt("expired", settingsA.PrinterName,
                startedAtUtc: now.AddDays(-31));
            var currentB = PrintAttemptFactory.CreateReceipt("same-receipt", settingsB.PrinterName,
                startedAtUtc: now.AddMinutes(-1));
            var store = new JsonPrintAttemptStore(path);
            store.UpsertMany([
                AttemptRecord(currentA, contextA, "same-receipt", PrintSubmissionState.Submitted,
                    PrintItemStatus.ResultNotConfirmed, now.AddMinutes(-2), 10),
                AttemptRecord(expiredA, contextA, "expired", PrintSubmissionState.Submitted,
                    PrintItemStatus.ResultNotConfirmed, now.AddDays(-31), 11),
                AttemptRecord(currentB, contextB, "same-receipt", PrintSubmissionState.SubmissionUnknown,
                    PrintItemStatus.ResultNotConfirmed, now.AddMinutes(-1), null)
            ]);

            var loadedA = store.Load(contextA);
            var loadedB = store.Load(contextB);
            Equal(1, loadedA.Count);
            Equal(10, loadedA.Single().JobId);
            Equal(1, loadedB.Count);
            Equal(PrintSubmissionState.SubmissionUnknown, loadedB.Single().SubmissionState);
            var json = File.ReadAllText(path);
            True(!json.Contains(settingsA.Login, StringComparison.OrdinalIgnoreCase));
            True(!json.Contains(settingsB.Login, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        return Task.CompletedTask;
    }

    private static async Task TestBoundaryUnknownAsync()
    {
        var registered = false;
        var attempt = PrintAttemptFactory.CreateReceipt("receipt-boundary", "Fake printer");
        var result = await ReliableBatchRunner.RunAsync(new[] { 1 }, (_, _) =>
        {
            var submission = PrintSubmissionBoundary.Execute<PrintSubmissionResult>(
                attempt,
                _ => { },
                () =>
                {
                    registered = true;
                    throw new InvalidOperationException("backend threw after registration");
                },
                () => null);
            return Task.FromResult(submission);
        });

        True(registered);
        Equal(PrintSubmissionState.SubmissionUnknown, result.Items.Single().SubmissionState);
        True(result.Items.Single().Error is PrintSubmissionUnknownException);
        True(!PrintRetryPolicy.CanRetryWithoutWarning(PrintItemStatus.ResultNotConfirmed, true));
    }

    private static Task TestPreparationFailureAsync()
    {
        RunOnSta(() =>
        {
            var settings = TestSettings(separateJobs: true);
            var store = new MemoryPrintAttemptStore();
            using var printer = new ScenarioPrintService();
            var imageService = new FakeImageService { FailDownloads = true };
            var viewModel = CreateViewModel(
                new ScenarioReceiptService((_, _) => [TestReceipt("prepare-failure", DateTime.Today)]),
                settings, printer, store, new RecordingDialogs(), imageService);
            viewModel.InitializeAsync().GetAwaiter().GetResult();
            viewModel.Receipts.Single().IsSelected = true;
            viewModel.PrintSelectedCommand.Execute(null);

            var row = viewModel.Receipts.Single();
            Equal(PrintItemStatus.Error, row.PrintStatus);
            True(!row.HasSubmissionRisk);
            True(row.CanRetryWithoutWarning);
            Equal(0, printer.ReceiptSubmissionCalls);
            var attempt = store.Load(PrintAccountContext.Create(settings)).Single();
            Equal(PrintSubmissionState.NotSubmitted, attempt.SubmissionState);
            Equal(PrintItemStatus.Error, attempt.ItemStatus);
        });
        return Task.CompletedTask;
    }

    private static Task TestUnknownSharedJobAsync()
    {
        RunOnSta(() =>
        {
            var settings = TestSettings(separateJobs: false);
            var store = new MemoryPrintAttemptStore();
            var dialogs = new RecordingDialogs();
            using var printer = new ScenarioPrintService { SharedSubmissionUnknown = true };
            var receipts = new[]
            {
                TestReceipt("shared-a", DateTime.Today),
                TestReceipt("shared-b", DateTime.Today)
            };
            var viewModel = CreateViewModel(new ScenarioReceiptService((_, _) => receipts),
                settings, printer, store, dialogs);
            viewModel.InitializeAsync().GetAwaiter().GetResult();
            foreach (var row in viewModel.Receipts) row.IsSelected = true;
            viewModel.PrintSelectedCommand.Execute(null);

            True(viewModel.Receipts.All(x => x.HasSubmissionRisk));
            True(viewModel.Receipts.All(x => x.PrintStatus == PrintItemStatus.ResultNotConfirmed));
            var context = PrintAccountContext.Create(settings);
            var unknown = store.Load(context)
                .Where(x => x.SubmissionState == PrintSubmissionState.SubmissionUnknown)
                .ToArray();
            Equal(2, unknown.Length);
            Equal(1, unknown.Select(x => x.AttemptId).Distinct().Count());
            True(unknown.All(x => x.JobId == 777));
            True(viewModel.Receipts.All(x => x.WindowsJobId == 777));

            foreach (var row in viewModel.Receipts) row.IsSelected = true;
            viewModel.PrintSelectedCommand.Execute(null);
            True(dialogs.Confirmations.Last().ContainsPreviouslySubmittedReceipts);
        });
        return Task.CompletedTask;
    }

    private static Task TestBitmapDpiAsync()
    {
        RunOnSta(() =>
        {
            var settings = TestSettings(separateJobs: true);
            foreach (var dpi in new[] { 96d, 203d, 300d })
            {
                var rendered = TestReceiptBitmapRenderer.Render(settings, dpi);
                Equal((int)Math.Ceiling(rendered.WidthDip * dpi / 96d), rendered.Bitmap.PixelWidth);
                Equal((int)Math.Ceiling(rendered.HeightDip * dpi / 96d), rendered.Bitmap.PixelHeight);
                AssertTestBitmapContentAndEdges(rendered.Bitmap, dpi);
            }
        });
        return Task.CompletedTask;
    }

    private static async Task TestRetryAsync()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return calls < 3
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"message\":\"maintenance\"}") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        });
        using var client = new HttpClient(handler);
        var api = new CheckboxApiClient(client, new StaticAuthentication(), new NullLogger());
        Equal("ok", await api.GetStringAsync("https://example.test/read", "test.retry"));
        Equal(3, calls);
    }

    private static async Task TestApiErrorAsync()
    {
        using var client = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"message\":\"bad filter\"}") }));
        var api = new CheckboxApiClient(client, new StaticAuthentication(), new NullLogger());
        try { await api.GetStringAsync("https://example.test/read", "test.error"); }
        catch (ApiException exception)
        {
            Equal(HttpStatusCode.BadRequest, exception.StatusCode);
            Equal("bad filter", exception.ResponseMessage);
            return;
        }
        throw new Exception("ApiException was not thrown.");
    }

    private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    private static AppSettings TestSettings(bool separateJobs) => new()
    {
        ApiBaseUrl = AppSettings.DefaultApiBaseUrl,
        Login = "tests@example.invalid",
        PrinterName = "Fake printer",
        PaperWidth = PaperWidth.Mm58,
        PrintableWidthMm = 54,
        SeparatePrintJobPerReceipt = separateJobs
    };

    private static PrintAttemptRecord AttemptRecord(
        PrintAttemptDescriptor attempt,
        string accountContext,
        string receiptId,
        PrintSubmissionState state,
        PrintItemStatus itemStatus,
        DateTimeOffset updatedAtUtc,
        int? jobId) =>
        new(attempt.AttemptId, accountContext, receiptId, attempt.PrinterName,
            attempt.UniqueJobName, jobId, attempt.StartedAtUtc, updatedAtUtc, state, itemStatus);

    private static ReceiptRecord TestReceipt(string id, DateTime date) => new()
    {
        Id = id,
        Type = ReceiptTypes.Sell,
        Status = "DONE",
        Serial = Math.Abs(id.GetHashCode()),
        FiscalCode = $"F-{id}",
        FiscalDate = new DateTimeOffset(date.Date.AddHours(12), TimeZoneInfo.Local.GetUtcOffset(date)),
        TotalSumMinor = 100
    };

    private static MainViewModel CreateViewModel(
        IReceiptService receiptService,
        AppSettings settings,
        IPrintService printer,
        IPrintAttemptStore store,
        IUiDialogService dialogs,
        IReceiptImageService? imageService = null) =>
        new(receiptService, imageService ?? new FakeImageService(), new MemorySettingsService(settings),
            new StaticAuthentication(), printer, store, dialogs, new NullLogger());

    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("STA test did not finish.");
        failure?.Throw();
    }

    private static void AssertTestBitmapContentAndEdges(BitmapSource bitmap, double dpi)
    {
        Equal(PixelFormats.Pbgra32, bitmap.Format);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        var band = Math.Max(4, (int)Math.Ceiling(6 * dpi / 96d));

        True(IsWhite(pixels, stride, 0, 0));
        True(HasDarkPixel(pixels, stride, bitmap.PixelWidth, bitmap.PixelHeight,
            0, bitmap.PixelWidth, 0, band));
        True(HasDarkPixel(pixels, stride, bitmap.PixelWidth, bitmap.PixelHeight,
            0, bitmap.PixelWidth, bitmap.PixelHeight - band, bitmap.PixelHeight));
        True(HasDarkPixel(pixels, stride, bitmap.PixelWidth, bitmap.PixelHeight,
            0, band, 0, bitmap.PixelHeight));
        True(HasDarkPixel(pixels, stride, bitmap.PixelWidth, bitmap.PixelHeight,
            bitmap.PixelWidth - band, bitmap.PixelWidth, 0, bitmap.PixelHeight));
        True(HasDarkPixel(pixels, stride, bitmap.PixelWidth, bitmap.PixelHeight,
            band, bitmap.PixelWidth - band, band, bitmap.PixelHeight - band));
        True(HasDarkPixel(pixels, stride, bitmap.PixelWidth, bitmap.PixelHeight,
            band, bitmap.PixelWidth - band,
            Math.Max(band, bitmap.PixelHeight - band * 5), bitmap.PixelHeight - band));
    }

    private static bool HasDarkPixel(
        byte[] pixels,
        int stride,
        int width,
        int height,
        int x0,
        int x1,
        int y0,
        int y1)
    {
        x0 = Math.Clamp(x0, 0, width);
        x1 = Math.Clamp(x1, 0, width);
        y0 = Math.Clamp(y0, 0, height);
        y1 = Math.Clamp(y1, 0, height);
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
        {
            var offset = y * stride + x * 4;
            if (pixels[offset] < 80 && pixels[offset + 1] < 80 && pixels[offset + 2] < 80 && pixels[offset + 3] > 0)
                return true;
        }
        return false;
    }

    private static bool IsWhite(byte[] pixels, int stride, int x, int y)
    {
        var offset = y * stride + x * 4;
        return pixels[offset] > 240 && pixels[offset + 1] > 240 && pixels[offset + 2] > 240 && pixels[offset + 3] > 0;
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
    }
    private static void True(bool value) { if (!value) throw new Exception("Condition is false."); }
    private static void NearlyEqual(double expected, double actual) { if (Math.Abs(expected - actual) > 0.0001) throw new Exception($"Expected {expected}, got {actual}."); }
    private static double Mm(double value) => value * PrintGeometry.DipPerMillimeter;
    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual) { if (!expected.SequenceEqual(actual)) throw new Exception("Sequences differ."); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception($"{typeof(T).Name} was not thrown."); }

    private sealed class Selectable { public bool Selected { get; set; } }
    private sealed record SelectionItem(string Id, bool Selected);

    private sealed class FakePrinterBackend
    {
        public List<int> Attempted { get; } = [];
        public List<int> Submitted { get; } = [];
        public int? FailItem { get; init; }

        public Task<PrintSubmissionResult> SubmitAsync(int item, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempted.Add(item);
            if (FailItem == item) throw new InvalidOperationException("fake driver error");
            Submitted.Add(item);
            return Task.FromResult(FakeSubmission(100 + item));
        }
    }

    private static PrintSubmissionResult FakeSubmission(int jobId, PrintAttemptDescriptor? attempt = null)
    {
        var validation = new PrinterPageValidation(Mm(58), Mm(100), Mm(58), Mm(100),
            Mm(2), Mm(1), Mm(54), Mm(98), 3, false);
        attempt ??= new PrintAttemptDescriptor(Guid.NewGuid(), "Fake printer", $"job-{jobId}", DateTimeOffset.UtcNow);
        return new PrintSubmissionResult(attempt, jobId, validation,
            new WindowsPrintJobObservation(jobId, WindowsPrintJobState.Queued,
                "accepted by fake Windows queue", false));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }

    private sealed class ScenarioReceiptService(
        Func<DateOnly, DateOnly, IReadOnlyList<ReceiptRecord>> load) : IReceiptService
    {
        public Task<IReadOnlyList<ReceiptRecord>> GetReceiptsAsync(
            DateOnly from,
            DateOnly to,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(load(from, to));
        }
    }

    private sealed class FakeImageService : IReceiptImageService
    {
        public bool FailDownloads { get; init; }
        public Task<byte[]> GetPngAsync(string receiptId, int paperWidthMm, CancellationToken cancellationToken = default) =>
            FailDownloads
                ? Task.FromException<byte[]>(new InvalidOperationException("preparation failed"))
                : Task.FromResult(new byte[] { 1, 2, 3 });
        public Task<int> ClearCacheAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MemoryPrintAttemptStore : IPrintAttemptStore
    {
        private readonly List<PrintAttemptRecord> _records = [];

        public IReadOnlyList<PrintAttemptRecord> Load(string accountContext) =>
            _records.Where(x => x.AccountContext == accountContext).OrderBy(x => x.UpdatedAtUtc).ToArray();

        public void UpsertMany(IReadOnlyList<PrintAttemptRecord> attempts)
        {
            foreach (var attempt in attempts)
            {
                _records.RemoveAll(x => x.AttemptId == attempt.AttemptId &&
                                        x.AccountContext == attempt.AccountContext &&
                                        x.ReceiptId == attempt.ReceiptId);
                _records.Add(attempt);
            }
        }
    }

    private sealed class RecordingDialogs : IUiDialogService
    {
        public List<PrintBatchConfirmation> Confirmations { get; } = [];
        public List<string> Errors { get; } = [];

        public bool ConfirmPrint(PrintBatchConfirmation confirmation)
        {
            Confirmations.Add(confirmation);
            return true;
        }

        public void ShowInfo(string message, string title = "Checkbox Batch Printer") { }
        public void ShowError(string message, string title = "Помилка") => Errors.Add(message);
        public Task<bool> OpenSettingsAsync() => Task.FromResult(false);
        public void ShowPreview(byte[] png, ReceiptRowViewModel receipt) { }
    }

    private sealed class ScenarioPrintService : IPrintService
    {
        private int _nextJobId = 100;
        public bool SharedSubmissionUnknown { get; init; }
        public int ReceiptSubmissionCalls { get; private set; }

        public IReadOnlyList<string> GetInstalledPrinters() => ["Fake printer"];
        public string? GetDefaultPrinterName() => "Fake printer";
        public bool PrinterExists(string printerName) => printerName == "Fake printer";

        public Task<PrintSubmissionResult> PrintReceiptAsync(
            byte[] png,
            AppSettings settings,
            PrintAttemptDescriptor attempt,
            Action<PrintAttemptDescriptor> markSubmissionStarted,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReceiptSubmissionCalls++;
            markSubmissionStarted(attempt);
            return Task.FromResult(FakeSubmission(Interlocked.Increment(ref _nextJobId), attempt));
        }

        public Task<PrintSubmissionResult> PrintReceiptsAsSingleJobAsync(
            IReadOnlyList<(byte[] Png, string ReceiptId)> receipts,
            AppSettings settings,
            PrintAttemptDescriptor attempt,
            Action<PrintAttemptDescriptor> markSubmissionStarted,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            markSubmissionStarted(attempt);
            if (SharedSubmissionUnknown)
                throw new PrintSubmissionUnknownException(attempt,
                    new InvalidOperationException("fake backend threw after the shared job was registered"), 777);
            return Task.FromResult(FakeSubmission(Interlocked.Increment(ref _nextJobId), attempt));
        }

        public Task<PrintSubmissionResult> PrintTestAsync(
            AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            var attempt = PrintAttemptFactory.CreateReceipt("TEST", settings.PrinterName);
            return Task.FromResult(FakeSubmission(Interlocked.Increment(ref _nextJobId), attempt));
        }

        public Task<WindowsPrintJobObservation> GetJobStatusAsync(
            PrintSubmissionResult submission,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WindowsPrintJobObservation(
                submission.JobId,
                WindowsPrintJobState.Disappeared,
                "fake job left the queue; physical result unknown",
                true));

        public void Dispose() { }
    }

    private sealed class StaticAuthentication : IAuthenticationService
    {
        public bool HasStoredCredentials => true;
        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) => Task.FromResult("test-token");
        public Task SignInAndStoreAsync(string login, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void InvalidateToken() { }
    }

    private sealed class MemorySettingsService(AppSettings value) : ISettingsService
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(value);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) { value = settings; return Task.CompletedTask; }
    }

    private sealed class NullLogger : IAppLogger
    {
        public string LogDirectory => Path.GetTempPath();
        public void Info(string operation, string? receiptId = null, int? httpStatus = null, string? printStatus = null) { }
        public void Error(string operation, Exception exception, string? receiptId = null, int? httpStatus = null, string? printStatus = null) { }
    }
}
