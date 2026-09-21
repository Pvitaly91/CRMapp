using System.Net;
using System.Text;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

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
        ("long receipt validation", TestLongReceiptAsync),
        ("one submission error keeps remaining items", TestReliableBatchContinuesAsync),
        ("retry", TestRetryAsync),
        ("API errors", TestApiErrorAsync)
    ];

    public static async Task<int> Main()
    {
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

    private static PrintSubmissionResult FakeSubmission(int jobId)
    {
        var validation = new PrinterPageValidation(Mm(58), Mm(100), Mm(58), Mm(100),
            Mm(2), Mm(1), Mm(54), Mm(98), 3, false);
        return new PrintSubmissionResult(jobId, $"job-{jobId}", "Fake printer", validation,
            new WindowsPrintJobObservation(jobId, WindowsPrintJobState.Queued,
                "accepted by fake Windows queue", false));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
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
