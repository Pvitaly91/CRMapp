using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Services;

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
        ("batch print uses one continuous page with compact separators", TestBatchPrintPageAsync),
        ("printed receipt history survives restart and is account scoped", TestPrintHistoryPersistenceAsync),
        ("print queue continues after error", TestBatchQueueAsync),
        ("retry", TestRetryAsync),
        ("API errors", TestApiErrorAsync),
        ("cashier sign-in rejects credentials without storing them", TestCashierSignInErrorAsync),
        ("cashier sign-in stores credentials only after success", TestCashierSignInSuccessAsync)
    ];

    public static async Task<int> Main()
    {
        Tests.AddRange(PromOrdersTests.All);
        Tests.AddRange(RozetkaOrdersTests.All);
        Tests.AddRange(MarketplaceMatchingTests.All);
        Tests.AddRange(MarketplaceSyncTests.All);
        Tests.AddRange(MarketplaceViewModelTests.All);
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
            True(query.Contains("self_receipts=true", StringComparison.Ordinal));
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

    private static Task TestBatchPrintPageAsync()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var receipts = Enumerable.Range(1, 3)
                    .Select(index => (CreatePng(100, 200), $"receipt-{index}"))
                    .ToArray();
                var settings = new AppSettings { PaperWidth = PaperWidth.Mm50, PrintableWidthMm = 48 };
                var page = WindowsPrintService.BuildBatchPage(receipts, settings, out var width, out var height);
                var separators = page.Children.OfType<Border>().ToArray();
                Equal(2, separators.Length);
                Equal(5, page.Children.Count);
                NearlyEqual(width * 0.82, separators[0].Width);
                NearlyEqual(0.3 * 96 / 25.4, separators[0].Height);

                var imageHeight = PrintGeometry.Calculate(100, 200, 48, 50, 0).HeightDip;
                var expectedHeight = 2 * 0.6 * 96 / 25.4 + 3 * imageHeight +
                                     2 * 1.9 * 96 / 25.4;
                NearlyEqual(expectedHeight, height);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
        return Task.CompletedTask;
    }

    private static async Task TestPrintHistoryPersistenceAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"checkbox-print-history-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "printed-receipts.json");
        try
        {
            var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
            var clock = new MutableTimeProvider(now);
            var settingsA = new AppSettings { Login = "cashier-a@example.test" };
            var settingsB = new AppSettings { Login = "cashier-b@example.test" };
            var contextA = PrintAccountContext.Create(settingsA);
            var contextB = PrintAccountContext.Create(settingsB);

            var firstRun = new JsonPrintHistoryStore(path, TimeSpan.FromDays(365), clock);
            await firstRun.MarkPrintedAsync(contextA, ["receipt-1", "receipt-2"], "Test printer");

            var restarted = new JsonPrintHistoryStore(path, TimeSpan.FromDays(365), clock);
            var restored = await restarted.LoadAsync(contextA);
            Equal(2, restored.Count);
            Equal("Test printer", restored["receipt-1"].PrinterName);
            Equal(0, (await restarted.LoadAsync(contextB)).Count);

            var json = await File.ReadAllTextAsync(path);
            True(!json.Contains(settingsA.Login, StringComparison.OrdinalIgnoreCase));
            True(!json.Contains("password", StringComparison.OrdinalIgnoreCase));
            True(!json.Contains("access_token", StringComparison.OrdinalIgnoreCase));

            clock.UtcNow = now.AddDays(366);
            Equal(0, (await restarted.LoadAsync(contextA)).Count);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] CreatePng(int width, int height)
    {
        var stride = width;
        var pixels = Enumerable.Repeat((byte)255, stride * height).ToArray();
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
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

    private static async Task TestCashierSignInErrorAsync()
    {
        var credentials = new MemoryCredentialStore();
        using var client = new HttpClient(new StubHandler(request =>
        {
            Equal(HttpMethod.Post, request.Method);
            Equal("https://api.checkbox.ua/api/v1/cashier/signin", request.RequestUri?.ToString());
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"message\":\"Невірний логін або пароль\"}")
            };
        }));
        var authentication = new AuthenticationService(client,
            new MemorySettingsService(new AppSettings()), credentials, new NullLogger());
        try { await authentication.SignInAndStoreAsync("cashier-test", "incorrect-test-password"); }
        catch (ApiException exception)
        {
            Equal(HttpStatusCode.Forbidden, exception.StatusCode);
            True(exception.ToUserMessage().Contains("пароль касира", StringComparison.Ordinal));
            True(!credentials.HasPassword);
            var unrelated = new ApiException("auth", HttpStatusCode.Forbidden, "password=secret-test-value");
            True(!unrelated.ToUserMessage().Contains("secret-test-value", StringComparison.Ordinal));
            return;
        }
        throw new Exception("Cashier sign-in should have failed.");
    }

    private static async Task TestCashierSignInSuccessAsync()
    {
        var credentials = new MemoryCredentialStore();
        var settings = new MemorySettingsService(new AppSettings());
        using var client = new HttpClient(new StubHandler(_ => JsonResponse(new { access_token = "test-access-token" })));
        var authentication = new AuthenticationService(client, settings, credentials, new NullLogger());
        await authentication.SignInAndStoreAsync(" cashier-test ", "test-password");
        Equal("cashier-test", (await settings.LoadAsync()).Login);
        Equal("test-password", await credentials.LoadPasswordAsync());
        Equal("test-access-token", await authentication.GetAccessTokenAsync());
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
    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual) { if (!expected.SequenceEqual(actual)) throw new Exception("Sequences differ."); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception($"{typeof(T).Name} was not thrown."); }

    private sealed class Selectable { public bool Selected { get; set; } }

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

    private sealed class MemoryCredentialStore : ISecureCredentialStore
    {
        private string? _password;
        public bool HasPassword => _password is not null;
        public Task SavePasswordAsync(string password, CancellationToken cancellationToken = default)
        {
            _password = password;
            return Task.CompletedTask;
        }
        public Task<string?> LoadPasswordAsync(CancellationToken cancellationToken = default) => Task.FromResult(_password);
        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            _password = null;
            return Task.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class NullLogger : IAppLogger
    {
        public string LogDirectory => Path.GetTempPath();
        public void Info(string operation, string? receiptId = null, int? httpStatus = null, string? printStatus = null) { }
        public void Error(string operation, Exception exception, string? receiptId = null, int? httpStatus = null, string? printStatus = null) { }
    }
}
