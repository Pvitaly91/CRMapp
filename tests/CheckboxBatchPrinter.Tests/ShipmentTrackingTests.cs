using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using static CheckboxBatchPrinter.Tests.ShippingLabelsTests;

namespace CheckboxBatchPrinter.Tests;

internal static class ShipmentTrackingTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("shipment tracking retains verified NP storage dates without personal data and ignores malformed optional dates", StorageDates),
        ("shipment tracking reads public NP statuses in bounded distinct batches without any saved API key or personal data", PublicBulk),
        ("shipment tracking rejects unexpected, duplicate, missing and invalid response identities before publishing any chunk", InvalidResponses),
        ("shipment tracking preserves completed chunks when another chunk fails and sanitizes API errors", PartialFailure),
        ("shipment tracking read-only allowlist rejects write methods, unknown keys, nonempty phones and malformed requests", ReadOnlyContract),
        ("shipment tracking cancellation reaches active reads and never requests the next batch", Cancellation),
        ("shipment tracking public requests share NP cooldown without retries and resume after its full deadline", PublicCooldown)
    ];

    private static async Task StorageDates()
    {
        object ResponseRow(string arrival, string firstDay, string paid) => new
        {
            Number = Number(1), Status = "Прибув у відділення", StatusCode = "7",
            ActualDeliveryDate = arrival, DateFirstDayStorage = firstDay, DatePayedKeeping = paid,
            RecipientFullName = "PRIVATE_BUYER", RecipientPhone = "PRIVATE_PHONE"
        };
        var rows = new[]
        {
            ResponseRow("2026-10-14 17:58:10", "2026-10-20", "2026-10-20 17:58:10"),
            ResponseRow("14-10-2026 10:53:05", "", "2026-10-20 10:53:05"),
            ResponseRow("tomorrow", "0001-01-01", "invalid"),
            ResponseRow("2026-03-29 03:30:00", "not a date", "")
        };
        var index = 0;
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response(rows[index++]))));
        for (var n = 0; n < rows.Length; n++)
        {
            var result = await Service(client).FetchAsync([Order(1)]);
            Check(result.Errors.Count == 0 && result.Statuses.Count == 1, "Optional date parsing discarded the valid status.");
            var status = result.Statuses[0];
            Check(status.StatusCode == "7", "Carrier code not retained.");
            Check(n < 2 ? status.PaidStorageStartsOn == new DateOnly(2026, 10, 20) && status.ArrivedAt?.Offset == TimeSpan.FromHours(3)
                : status.PaidStorageStartsOn is null && status.ArrivedAt is null, "Dates were guessed or interpreted outside Kyiv.");
            Check(!JsonSerializer.Serialize(status).Contains("PRIVATE_"), "Storage lookup retained customer details.");
        }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan time) => _now += time;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return handle(request, ct); }
    }
    private static string Number(int n) => (20400000000000L + n).ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static MarketplaceOrder Order(int n, params OrderShipment[] shipments) => new()
    {
        Key = new(MarketplaceKind.Rozetka, "seller-without-np-account", n.ToString()), Number = n.ToString(),
        Shipments = shipments.Length > 0 ? shipments : [new("Нова пошта", Number(n))]
    };
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static object Row(string number, string status = "Відправлення прямує до міста одержувача") => new
    {
        Number = number, Status = status, StatusCode = "future-9999",
        RecipientFullName = "PRIVATE_RECIPIENT", RecipientPhone = "PRIVATE_PHONE", AddressRecipient = "PRIVATE_ADDRESS"
    };
    private static HttpResponseMessage Response(params object[] rows) => Json(new { success = true, data = rows, errors = Array.Empty<string>() });
    private static string[] Requested(HttpRequestMessage request)
    {
        Check(request.Method == HttpMethod.Post && request.RequestUri?.AbsoluteUri == NovaPoshtaReadOnlyContract.EntryPoint,
            "Tracking changed its exact read-only route.");
        using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        var root = json.RootElement;
        Check(root.GetProperty("apiKey").GetString() == "" && root.GetProperty("modelName").GetString() == "TrackingDocument" &&
            root.GetProperty("calledMethod").GetString() == "getStatusDocuments", "Public tracking requires a saved credential or uses a different method.");
        var documents = root.GetProperty("methodProperties").GetProperty("Documents").EnumerateArray().ToArray();
        Check(documents.Length is > 0 and <= 50 && request.Content.Headers.ContentLength is <= 4096 &&
            documents.All(d => d.GetProperty("Phone").GetString() == ""), "Tracking sent an oversized request or a personal phone.");
        return documents.Select(d => d.GetProperty("DocumentNumber").GetString()!).ToArray();
    }
    private static ShipmentTrackingService Service(HttpClient client, Clock? clock = null) =>
        new(new(client, novaPoshtaPolicy: new(clock)), clock);

    private static async Task PublicBulk()
    {
        var chunks = new List<string[]>(); var clock = new Clock();
        var handler = new Handler((request, _) =>
        {
            var numbers = Requested(request); chunks.Add(numbers); clock.Advance(TimeSpan.FromSeconds(1));
            // API order is irrelevant. The service restores its frozen request sequence.
            return Task.FromResult(Response(numbers.Reverse().Select(n => Row(n)).ToArray()));
        });
        using var client = new HttpClient(handler);
        var orders = Enumerable.Range(1, 103).Select(n => Order(n)).Append(Order(200, new OrderShipment("nova_poshta", Number(1))))
            .Append(Order(201, new OrderShipment("Unknown", Number(104))))
            .Append(Order(202, new OrderShipment("Rozetka Delivery", "RMP-202")))
            .Append(Order(203, new OrderShipment("Нова пошта", "invalid-tracking-number"))).ToArray();
        var result = await Service(client, clock).FetchAsync(orders);
        Check(handler.Calls == 3 && chunks.Select(c => c.Length).SequenceEqual([50, 50, 3]) &&
            chunks.SelectMany(c => c).SequenceEqual(Enumerable.Range(1, 103).Select(Number)),
            "Public tracking duplicated TTNs, guessed an unknown carrier, or fanned out per order.");
        Check(result.Statuses.Select(s => s.TrackingNumber).SequenceEqual(Enumerable.Range(1, 103).Select(Number)) &&
            result.Statuses.All(s => s.Carrier == ShippingCarrier.NovaPoshta && s.Source == NovaPoshtaTrackingReadOnlyContract.Source &&
                s.Order is null && s.Status.Length > 0) && result.Statuses[0].CheckedAtUtc == clock.GetUtcNow().AddSeconds(-2),
            "Tracking dropped future statuses, ownership scope or per-chunk timestamp.");
        var savedShape = JsonSerializer.Serialize(result);
        Check(result.Errors.Count == 1 && !savedShape.Contains("PRIVATE_"), "Tracking retained carrier response personal details.");
    }

    private static async Task InvalidResponses()
    {
        object[][] invalid =
        [
            [Row(Number(1)), Row(Number(1))],
            [Row(Number(1)), Row(Number(999))],
            [Row(Number(1))],
            [Row(Number(1)), Row(Number(2), "   ")],
            [Row(Number(1)), Row(Number(2), new string('x', 241))],
            [Row(Number(1)), new { Number = 20400000000002L, Status = "Відправлено" }],
            [Row(Number(1)), new { Number = Number(2), Status = (string?)null }]
        ];
        foreach (var rows in invalid)
        {
            var handler = new Handler((_, _) => Task.FromResult(Response(rows)));
            using var client = new HttpClient(handler);
            var result = await Service(client).FetchAsync([Order(1), Order(2)]);
            Check(result.Statuses.Count == 0 && result.Errors.Count == 1 && handler.Calls == 1,
                "A malformed chunk published partial data or triggered a fallback request.");
        }
        var failures = new object[]
        {
            new { success = false, data = new[] { Row(Number(1)) }, errors = new[] { "RAW_SECRET_AND_PII" } },
            new { success = true, data = new[] { Row(Number(1)) }, errors = new[] { "RAW_SECRET_AND_PII" } },
            new { success = "true", data = new[] { Row(Number(1)) }, errors = Array.Empty<string>() }
        };
        foreach (var failure in failures)
        {
            var handler = new Handler((_, _) => Task.FromResult(Json(failure)));
            using var client = new HttpClient(handler);
            var result = await Service(client).FetchAsync([Order(1)]);
            Check(result.Statuses.Count == 0 && result.Errors.Count == 1 && !result.Errors[0].Contains("RAW_"),
                "Carrier failure text leaked or was represented as a current parcel status.");
        }
    }

    private static async Task PartialFailure()
    {
        var batch = 0;
        var handler = new Handler((request, _) =>
        {
            var numbers = Requested(request);
            return Task.FromResult(++batch == 2 ? Response(Row(Number(999), "RAW_PRIVATE_RESPONSE")) :
                Response(numbers.Select(n => Row(n)).ToArray()));
        });
        using var client = new HttpClient(handler);
        var result = await Service(client).FetchAsync(Enumerable.Range(1, 101).Select(n => Order(n)).ToArray());
        Check(handler.Calls == 3 && result.Statuses.Select(s => s.TrackingNumber).SequenceEqual(
            Enumerable.Range(1, 50).Select(Number).Append(Number(101))) && result.Errors.Count == 1 &&
            !JsonSerializer.Serialize(result).Contains("RAW_"), "Failed chunk erased other statuses or leaked raw API text.");
    }

    private static HttpRequestMessage Request(string body, string url = NovaPoshtaReadOnlyContract.EntryPoint,
        HttpMethod? method = null, string type = "application/json") => new(method ?? HttpMethod.Post, url)
    { Content = new StringContent(body, Encoding.UTF8, type) };
    private static Task ReadOnlyContract()
    {
        const string valid = "{\"apiKey\":\"\",\"modelName\":\"TrackingDocument\",\"calledMethod\":\"getStatusDocuments\",\"methodProperties\":{\"Documents\":[{\"DocumentNumber\":\"20400000000001\",\"Phone\":\"\"}]}}";
        using (var request = Request(valid)) ShippingLabelHttp.Validate(request);
        string[] denied =
        [
            valid.Replace("getStatusDocuments", "save"), valid.Replace("getStatusDocuments", "delete"),
            valid.Replace("TrackingDocument", "InternetDocument"), valid.Replace("\"apiKey\":\"\",", ""),
            valid.Replace("\"apiKey\":\"\"", "\"apiKey\":\"unexpected-key\""),
            valid.Replace("\"Phone\":\"\"", "\"Phone\":\"+380000000000\""),
            valid.Replace("20400000000001", "2040000000000"),
            valid.Replace("\"Documents\":", "\"Other\":true,\"Documents\":"),
            valid.Replace("\"Phone\":\"\"", "\"Phone\":\"\",\"DocumentNumber\":\"20400000000002\""),
            valid.Replace("\"apiKey\":\"\"", "\"apiKey\":\"\",\"apiKey\":\"\""),
            valid.Replace("[{\"DocumentNumber\":\"20400000000001\",\"Phone\":\"\"}]", "[]"),
            "{\"apiKey\":\"\",\"modelName\":\"TrackingDocument\",\"calledMethod\":\"getStatusDocuments\",\"methodProperties\":{\"Documents\":" +
                JsonSerializer.Serialize(Enumerable.Range(1, 51).Select(n => new { DocumentNumber = Number(n), Phone = "" })) + "}}"
        ];
        foreach (var body in denied)
        {
            using var request = Request(body);
            try { ShippingLabelHttp.Validate(request); throw new Exception("Forbidden tracking request was allowed."); }
            catch (InvalidOperationException) { }
        }
        foreach (var request in new[]
        {
            Request(valid, "http://api.novaposhta.ua/v2.0/json/"), Request(valid, "https://untrusted.example/v2.0/json/"),
            Request(valid, NovaPoshtaReadOnlyContract.EntryPoint + "?write=true"), Request(valid, method: HttpMethod.Get),
            Request(valid, type: "text/plain"), Request(valid + new string(' ', 4100))
        })
        {
            using (request)
            {
                try { ShippingLabelHttp.Validate(request); throw new Exception("Forbidden tracking transport was allowed."); }
                catch (InvalidOperationException) { }
            }
        }
        return Task.CompletedTask;
    }

    private static async Task Cancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (_, ct) =>
        {
            entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new Exception("Canceled tracking read continued.");
        });
        using var client = new HttpClient(handler);
        var service = Service(client);
        using (var before = new CancellationTokenSource())
        {
            before.Cancel();
            try { await service.FetchAsync([Order(1)], before.Token); throw new Exception("Pre-cancelled tracking was ignored."); }
            catch (OperationCanceledException) { }
            Check(handler.Calls == 0, "Pre-cancelled tracking sent HTTP.");
        }
        using var active = new CancellationTokenSource();
        var reading = service.FetchAsync(Enumerable.Range(1, 51).Select(n => Order(n)).ToArray(), active.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); active.Cancel();
        try { await reading.WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("Active tracking cancellation was ignored."); }
        catch (OperationCanceledException) { }
        Check(handler.Calls == 1, "Cancellation started the next tracking batch.");
    }

    private static async Task PublicCooldown()
    {
        var clock = new Clock(); var throttled = true;
        var handler = new Handler((request, _) => Task.FromResult(throttled ?
            new HttpResponseMessage((HttpStatusCode)429) : Response(Requested(request).Select(n => Row(n)).ToArray())));
        using var client = new HttpClient(handler);
        var transport = new ShippingLabelHttp(client, (_, _) => throw new Exception("Public tracking retried automatically."), new(clock));
        var first = new ShipmentTrackingService(transport, clock);
        var second = new ShipmentTrackingService(transport, clock);
        var limited = await first.FetchAsync([Order(1)]);
        var repeat = await second.FetchAsync([Order(2)]);
        Check(handler.Calls == 1 && limited.Statuses.Count == 0 && repeat.Statuses.Count == 0 &&
            limited.Errors.Count == 1 && transport.GetNovaPoshtaRetryDelay(NovaPoshtaTrackingReadOnlyContract.PolicyScope) == TimeSpan.FromSeconds(60),
            "Public tracking requests bypassed their shared cooldown.");
        clock.Advance(TimeSpan.FromSeconds(60)); throttled = false;
        var restored = await second.FetchAsync([Order(2)]);
        Check(handler.Calls == 2 && restored.Statuses.Count == 1 && restored.Errors.Count == 0,
            "Public tracking did not resume after the complete provider cooldown.");
    }
}
