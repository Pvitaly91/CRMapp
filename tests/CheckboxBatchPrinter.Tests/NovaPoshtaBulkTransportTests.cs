using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using static CheckboxBatchPrinter.Tests.ShippingLabelsTests;

namespace CheckboxBatchPrinter.Tests;

internal static class NovaPoshtaBulkTransportTests
{
    private const string Key = "synthetic_bulk_transport_key", OtherKey = "synthetic_bulk_other_key";
    private const string First = "20400000000001", Second = "20400000000002";
    private const string Prefix = "https://my.novaposhta.ua/orders/printMarking100x100/";
    private const string Suffix = "/type/pdf/zebra/zebra/apiKey/" + Key;
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("NP marking URLs use exact SDK single and comma-separated bulk routes with an application bound", CanonicalUrls),
        ("NP marking rejects invalid keys and tracking lists without exposing input", InvalidInputs),
        ("NP bulk allowlist rejects GUIDs query redirects foreign hosts and write routes", ReadOnlyRoutes),
        ("NP bulk PDF uses one HTTP GET and returns the complete response without delay", BulkPdf),
        ("NP bulk failures never retry fall back or expose response URLs and secrets", FailuresStop),
        ("NP bulk HTTP429 and JSON limits share cooldown with single and lookup requests", SharedCooldown),
        ("NP one-request handling preserves RD and Seller server-error retries", OtherCarrierRetries)
    ];

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return Task.FromResult(respond(request)); }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static HttpResponseMessage Pdf() => new(HttpStatusCode.OK)
    { Content = new ByteArrayContent("%PDF-synthetic-bulk-complete"u8.ToArray()) { Headers = { ContentType = new("application/pdf") } } };
    private static HttpRequestMessage Marking(IReadOnlyList<string> numbers, string key = Key) =>
        new(HttpMethod.Get, NovaPoshtaMarkingRequest.CreateUrl(key, numbers));
    private static Task<byte[]> Read(ShippingLabelHttp transport, IReadOnlyList<string>? numbers = null, string key = Key) =>
        transport.SendAsync(() => Marking(numbers ?? [First, Second], key), "application/pdf", default);
    private static ShippingLabelHttp Transport(HttpClient client, Clock? clock = null) =>
        new(client, (_, _) => throw new Exception("Direct NP added a wait or automatic retry."), new(clock ?? new Clock()));
    private static void NoSecrets(Exception exception)
    {
        Check(!exception.ToString().Contains(Key) && !exception.ToString().Contains(OtherKey) &&
            !exception.ToString().Contains("https://") && !exception.ToString().Contains("synthetic buyer data"),
            "A failure disclosed a key, credential-bearing URL, or response data.");
    }
    private static async Task<ShippingLabelException> Failure(Task operation)
    {
        try { await operation; throw new Exception("Expected request rejection was absent."); }
        catch (ShippingLabelException exception) { NoSecrets(exception); return exception; }
    }

    private static Task CanonicalUrls()
    {
        Check(NovaPoshtaMarkingRequest.CreateUrl(Key, [First]) == Prefix + "orders%5B%5D/" + First + Suffix,
            "Single marking route differs from the SDK contract.");
        Check(NovaPoshtaMarkingRequest.CreateUrl(Key, [Second, First]) == Prefix + "orders/" + Second + "," + First + Suffix,
            "Bulk route did not preserve exact comma-separated TTN order.");
        Check(NovaPoshtaMarkingRequest.CreateUrl(Key, [Second, First, Second]) == Prefix + "orders/" + Second + "," + First + Suffix &&
            NovaPoshtaMarkingRequest.CreateUrl(Key, [First, First]) == Prefix + "orders%5B%5D/" + First + Suffix,
            "Duplicate inputs were not de-duplicated in first-occurrence order.");
        var maximum = Enumerable.Range(1, 100).Select(i => "204" + i.ToString("00000000000")).ToArray();
        foreach (var numbers in new[] { new[] { First }, new[] { First, Second }, maximum })
        { using var request = Marking(numbers); ShippingLabelHttp.Validate(request); }
        Check(NovaPoshtaMarkingRequest.MaximumTrackingNumbers == maximum.Length, "Application bound changed without transport review.");
        return Task.CompletedTask;
    }

    private static Task InvalidInputs()
    {
        foreach (var numbers in new IReadOnlyList<string>[]
        {
            null!, [], [null!], [""], ["2040000000000"], ["204000000000001"], ["２０４００００００００００１"],
            [First + "\n"], [" " + First], [First + "," + Second], ["10000000-0000-0000-0000-000000000001"],
            [First, "synthetic buyer data"], Enumerable.Repeat(First, 101).ToArray()
        })
        {
            try { NovaPoshtaMarkingRequest.CreateUrl(Key, numbers); throw new Exception("Invalid TTN list accepted."); }
            catch (ShippingLabelException exception) { Check(exception.Kind == LabelFailureKind.InvalidDocument, "Invalid list misclassified."); NoSecrets(exception); }
        }
        foreach (var key in new[] { null!, "", " ", Key + "/extra", Key + "?query", Key + "\n", new string('a', 129) })
        {
            try { NovaPoshtaMarkingRequest.CreateUrl(key, [First, Second]); throw new Exception("Invalid key accepted."); }
            catch (ShippingLabelException exception) { Check(exception.Kind == LabelFailureKind.ConnectionRequired, "Invalid key misclassified."); NoSecrets(exception); }
        }
        return Task.CompletedTask;
    }

    private static Task ReadOnlyRoutes()
    {
        var bulk = Prefix + "orders/" + First + "," + Second + Suffix;
        foreach (var url in new[]
        {
            Prefix + "orders/" + First + Suffix,
            Prefix + "orders%5B%5D/" + First + "," + Second + Suffix,
            Prefix + "orders/" + First + "%2C" + Second + Suffix,
            Prefix + "orders/" + First + "," + Suffix,
            Prefix + "orders/10000000-0000-0000-0000-000000000001," + Second + Suffix,
            Prefix + "orders/" + string.Join(',', Enumerable.Repeat(First, 101)) + Suffix,
            bulk + "?download=1", bulk + "#fragment", bulk + "/extra",
            bulk.Replace("https://", "http://"), bulk.Replace("my.novaposhta.ua", "my.novaposhta.ua.foreign.example"),
            bulk.Replace("my.novaposhta.ua", "user@my.novaposhta.ua"), bulk.Replace("my.novaposhta.ua", "my.novaposhta.ua:444"),
            bulk.Replace("printMarking100x100", "save"), bulk.Replace("printMarking100x100", "delete"),
            bulk.Replace("type/pdf", "type/html"), bulk.Replace("zebra/zebra", "zebra/other")
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            try { ShippingLabelHttp.Validate(request); throw new Exception("Unsafe marking route accepted."); }
            catch (InvalidOperationException exception) { NoSecrets(exception); }
        }
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
        {
            using var request = new HttpRequestMessage(method, bulk);
            try { ShippingLabelHttp.Validate(request); throw new Exception("Unsafe marking method accepted."); }
            catch (InvalidOperationException exception) { NoSecrets(exception); }
        }
        return Task.CompletedTask;
    }

    private static async Task BulkPdf()
    {
        var handler = new Handler(request =>
        {
            Check(request.Method == HttpMethod.Get && request.RequestUri!.AbsoluteUri == Prefix + "orders/" + First + "," + Second + Suffix &&
                request.Content is null && request.Headers.Authorization is null, "Bulk marking used an unexpected HTTP contract.");
            return Pdf();
        });
        using var client = new HttpClient(handler);
        var transport = Transport(client);
        var bytes = await Read(transport);
        ShippingLabelHttp.ValidatePdf(bytes);
        Check(handler.Calls == 1 && bytes.SequenceEqual("%PDF-synthetic-bulk-complete"u8.ToArray()), "Bulk response was split, truncated, or requested again.");
        await Read(transport);
        Check(handler.Calls == 2, "A later explicit request was delayed or retried.");
    }

    private static async Task FailuresStop()
    {
        foreach (var status in new[] { 301, 302, 307, 308, 401, 403, 404, 500, 502, 503 })
        {
            var handler = new Handler(_ =>
            {
                var response = new HttpResponseMessage((HttpStatusCode)status)
                { Content = new StringContent(Key + " synthetic buyer data " + Prefix, Encoding.UTF8, "text/html") };
                response.Headers.Location = new Uri("https://foreign.example/" + Key);
                response.Headers.RetryAfter = new(TimeSpan.FromSeconds(1));
                return response;
            });
            using var client = new HttpClient(handler);
            var failure = await Failure(Read(Transport(client)));
            Check(handler.Calls == 1 && (status < 500 || failure.Kind == LabelFailureKind.Temporary),
                "Failure was retried, followed to a new URL, fell back to a single request, or misclassified.");
        }
        var networkHandler = new Handler(_ => throw new HttpRequestException(Prefix + "orders/" + First + "," + Second + Suffix));
        using var networkClient = new HttpClient(networkHandler);
        var networkFailure = await Failure(Read(Transport(networkClient)));
        Check(networkHandler.Calls == 1 && networkFailure.Kind == LabelFailureKind.Temporary, "Network failure retried or misclassified.");
    }

    private static async Task SharedCooldown()
    {
        foreach (var http429 in new[] { true, false })
        {
            var clock = new Clock(); var limited = true;
            var handler = new Handler(_ =>
            {
                if (!limited) return Pdf();
                var response = http429 ? new HttpResponseMessage((HttpStatusCode)429)
                    { Content = new StringContent(Key + " synthetic buyer data") } : new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(JsonSerializer.Serialize(new { success = false, data = Array.Empty<object>(), errors = new[] { "Too many requests" } }), Encoding.UTF8, "application/json") };
                response.Headers.RetryAfter = new(TimeSpan.FromSeconds(90));
                return response;
            });
            using var client = new HttpClient(handler); var transport = Transport(client, clock);
            var first = await Failure(Read(transport));
            Check(first.Kind == LabelFailureKind.Temporary && first.RetryAtUtc == clock.Now.AddSeconds(90) && handler.Calls == 1,
                "Bulk rate limit retried or lost Retry-After.");
            await Failure(Read(transport));
            await Failure(Read(transport, [First]));
            await Failure(transport.SendAsync(() => new(HttpMethod.Post, NovaPoshtaReadOnlyContract.EntryPoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    apiKey = Key, modelName = "InternetDocument", calledMethod = "getDocumentList",
                    methodProperties = new { DateTimeFrom = "25.09.2026", DateTimeTo = "27.09.2026", GetFullList = false, Page = "1" }
                }), Encoding.UTF8, "application/json")
            }, "application/json", default));
            Check(handler.Calls == 1 && transport.GetNovaPoshtaRetryDelay(Key) == TimeSpan.FromSeconds(90),
                "Single, bulk, or lookup request bypassed the same-key cooldown.");
            limited = false; await Read(transport, key: OtherKey);
            Check(handler.Calls == 2 && transport.GetNovaPoshtaRetryDelay(OtherKey) == TimeSpan.Zero, "Another key inherited the bulk cooldown.");
            clock.Now = clock.Now.AddSeconds(90); await Read(transport);
            Check(handler.Calls == 3, "Explicit request did not resume after server cooldown.");
        }
    }

    private static async Task OtherCarrierRetries()
    {
        foreach (var seller in new[] { false, true })
        {
            var calls = 0; var waits = new List<TimeSpan>();
            var handler = new Handler(_ => ++calls < 3 ? new(HttpStatusCode.ServiceUnavailable) : Pdf());
            using var client = new HttpClient(handler);
            var transport = new ShippingLabelHttp(client, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
            await transport.SendAsync(() => new(HttpMethod.Get, seller
                ? "https://api-seller.rozetka.com.ua/ttns/ttn-print/zebra"
                : "https://rz-delivery.rozetka.ua/api/track/label"), "application/pdf", default);
            Check(handler.Calls == 3 && waits.SequenceEqual(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }),
                "Direct NP changes altered RD or Seller server-error retries.");
        }
    }
}
