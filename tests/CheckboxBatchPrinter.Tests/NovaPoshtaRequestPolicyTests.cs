using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using static CheckboxBatchPrinter.Tests.ShippingLabelsTests;

namespace CheckboxBatchPrinter.Tests;

internal static class NovaPoshtaRequestPolicyTests
{
    private const string Key = "synthetic_np_policy_key", OtherKey = "synthetic_np_other_key", Ttn = "20400000000001";
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("NP JSON and PDF requests proceed immediately after the previous response completes", BackToBackRequests),
        ("NP HTTP200 and429 cooldowns preserve Retry-After and never retry or disclose credentials", Cooldowns),
        ("NP queued callers observe throttle before sending while unrelated key remains usable", QueuedThrottle),
        ("NP shared client rejects duplicate lookup and cancellation releases the scan guard", DuplicateScan),
        ("NP active and queued request cancellation preserve serialization and release the request gate", CancelRequests),
        ("NP policy does not change RD or Seller HTTP429 retry behavior", OtherCarrierRetries)
    ];

    private sealed class Clock(DateTimeOffset? initial = null) : TimeProvider
    {
        private long _ticks = (initial ?? new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);
    }
    internal static ShippingLabelHttp FastTransport(HttpClient client)
    {
        var clock = new Clock();
        return new(client, novaPoshtaPolicy: new(clock));
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Interlocked.Increment(ref _calls); return respond(request, ct); }
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Empty() => Json(new { success = true, data = Array.Empty<object>(), errors = Array.Empty<string>() });
    private static HttpResponseMessage Limit(bool http429) => http429 ? new((HttpStatusCode)429) :
        Json(new { success = false, data = Array.Empty<object>(), errors = new[] { "Too many requests" } });
    private static HttpRequestMessage Post(string key) => new(HttpMethod.Post, NovaPoshtaReadOnlyContract.EntryPoint)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            apiKey = key, modelName = "InternetDocument", calledMethod = "getDocumentList",
            methodProperties = new { DateTimeFrom = "25.09.2026", DateTimeTo = "27.09.2026", GetFullList = false, Page = "1" }
        }), Encoding.UTF8, "application/json")
    };
    private static Task<byte[]> Read(ShippingLabelHttp transport, string key = Key, CancellationToken ct = default) =>
        transport.SendAsync(() => Post(key), "application/json", ct);
    private static async Task<ShippingLabelException> Failure(Task operation)
    {
        try { await operation; throw new Exception("Expected request rejection was absent."); }
        catch (ShippingLabelException ex)
        {
            Check(!ex.ToString().Contains(Key) && !ex.ToString().Contains(OtherKey) && !ex.ToString().Contains("https://"),
                "A request failure disclosed credentials or a secret-bearing URL.");
            return ex;
        }
    }

    private static async Task BackToBackRequests()
    {
        var clock = new Clock(); var started = clock.GetUtcNow(); var sends = new List<TimeSpan>();
        var handler = new Handler((request, _) =>
        {
            sends.Add(clock.GetUtcNow() - started); clock.Advance(TimeSpan.FromSeconds(3));
            return Task.FromResult(request.Method == HttpMethod.Post ? Empty() : new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent("%PDF-synthetic"u8.ToArray()) { Headers = { ContentType = new("application/pdf") } } });
        });
        using var http = new HttpClient(handler); var transport = new ShippingLabelHttp(http, novaPoshtaPolicy: new(clock));
        await Read(transport).WaitAsync(TimeSpan.FromSeconds(4));
        await Read(transport).WaitAsync(TimeSpan.FromSeconds(4));
        await transport.SendAsync(() => new(HttpMethod.Get, "https://my.novaposhta.ua/orders/printMarking100x100/orders%5B%5D/" + Ttn +
            "/type/pdf/zebra/zebra/apiKey/" + Key), "application/pdf", default).WaitAsync(TimeSpan.FromSeconds(4));
        await Read(transport, OtherKey).WaitAsync(TimeSpan.FromSeconds(4));
        Check(sends.SequenceEqual(new[] { 0, 3, 6, 9 }.Select(seconds => TimeSpan.FromSeconds(seconds))),
            "NP JSON or PDF requests added a delay beyond the preceding response's duration.");
    }

    private static async Task Cooldowns()
    {
        foreach (var http429 in new[] { false, true })
        foreach (var header in new[] { "missing", "short", "delta", "date", "negative", "huge" })
        {
            var clock = new Clock(header == "huge" ? DateTimeOffset.MaxValue.AddSeconds(-10) : null);
            var start = clock.GetUtcNow(); var limited = true;
            var handler = new Handler((_, _) =>
            {
                var response = limited ? Limit(http429) : Empty();
                if (limited)
                {
                    if (header == "short") response.Headers.RetryAfter = new(TimeSpan.FromSeconds(5));
                    if (header == "delta") response.Headers.RetryAfter = new(TimeSpan.FromSeconds(90));
                    if (header == "date") response.Headers.RetryAfter = new(start.AddSeconds(120));
                    if (header == "negative") response.Headers.TryAddWithoutValidation("Retry-After", "-5");
                    if (header == "huge") response.Headers.RetryAfter = new(TimeSpan.FromSeconds(int.MaxValue));
                }
                return Task.FromResult(response);
            });
            using var http = new HttpClient(handler);
            var transport = new ShippingLabelHttp(http, (_, _) => throw new Exception("NP limit entered an automatic retry."), new(clock));
            var expected = header == "huge" ? DateTimeOffset.MaxValue : start.AddSeconds(header == "delta" ? 90 : header == "date" ? 120 : 60);
            var first = await Failure(Read(transport));
            Check(first.Kind == LabelFailureKind.Temporary && first.RetryAtUtc == expected && handler.Calls == 1 &&
                transport.GetNovaPoshtaRetryDelay(Key) == expected - start, "HTTP200/429 lost its server cooldown or local fallback.");
            var repeated = await Failure(Read(transport));
            Check(repeated.RetryAtUtc == expected && handler.Calls == 1, "A repeat click sent a request or changed the active deadline.");
            if (header == "huge") continue;
            clock.Advance(expected - start - TimeSpan.FromMilliseconds(1));
            await Failure(Read(transport)); Check(handler.Calls == 1, "Cooldown expired early.");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                try { await Read(transport, ct: cancel.Token); throw new Exception("Cancelled request was not cancelled."); }
                catch (OperationCanceledException) { }
                Check(transport.GetNovaPoshtaRetryDelay(Key) > TimeSpan.Zero, "Cancellation cleared a cooldown.");
            }
            clock.Advance(TimeSpan.FromMilliseconds(1)); limited = false;
            await Read(transport); Check(handler.Calls == 2, "Manual request did not resume after the full cooldown.");
        }
    }

    private static async Task QueuedThrottle()
    {
        foreach (var http429 in new[] { false, true })
        {
            var clock = new Clock(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new Handler(async (request, ct) =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                if (body.RootElement.GetProperty("apiKey").GetString() == OtherKey) return Empty();
                entered.TrySetResult(); await release.Task.WaitAsync(ct); return Limit(http429);
            });
            using var http = new HttpClient(handler); var transport = new ShippingLabelHttp(http, novaPoshtaPolicy: new(clock));
            var first = Read(transport); await entered.Task;
            var second = Read(transport);
            using (var cancel = new CancellationTokenSource())
            {
                var cancelled = Read(transport, ct: cancel.Token); cancel.Cancel();
                try { await cancelled; throw new Exception("Queued cancellation was ignored."); } catch (OperationCanceledException) { }
            }
            await Read(transport, OtherKey).WaitAsync(TimeSpan.FromSeconds(4));
            Check(handler.Calls == 2, "Waiting request escaped serialization or unrelated key was blocked.");
            release.TrySetResult(); var failures = await Task.WhenAll(Failure(first), Failure(second));
            Check(handler.Calls == 2 && failures.All(e => e.RetryAtUtc == clock.GetUtcNow().AddSeconds(60)),
                "Queued caller sent before the preceding response recorded its cooldown.");
            await Read(transport, OtherKey).WaitAsync(TimeSpan.FromSeconds(4));
            Check(handler.Calls == 3 && transport.GetNovaPoshtaRetryDelay(OtherKey) == TimeSpan.Zero &&
                transport.GetNovaPoshtaRetryDelay(Key) == TimeSpan.FromSeconds(60), "A server cooldown leaked to another key.");
        }
    }

    private static async Task DuplicateScan()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var handler = new Handler(async (_, ct) =>
        { if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } return Empty(); });
        using var http = new HttpClient(handler); var client = new NovaPoshtaReadOnlyClient(FastTransport(http));
        var connection = new NovaPoshtaConnection("account", "Synthetic", Key);
        var shipment = ShippingCarrierNames.ForOrder(Order("order", ShippingCarrier.NovaPoshta, Ttn)).Single();
        var period = new NovaPoshtaSearchPeriod(new(2026, 9, 25), new(2026, 9, 27));
        using var cancel = new CancellationTokenSource();
        var first = client.LookupAsync(connection, shipment, period, cancel.Token); await entered.Task;
        var duplicate = await Failure(client.LookupAsync(connection with { Id = "same-key-other-account" }, shipment, period));
        Check(duplicate.Kind == LabelFailureKind.Temporary && handler.Calls == 1, "A duplicate scan was queued or sent under another connection Id.");
        cancel.Cancel(); try { await first; throw new Exception("Active lookup ignored cancellation."); } catch (OperationCanceledException) { }
        var result = await client.LookupAsync(connection, shipment, period);
        Check(result.Complete && !result.Found && handler.Calls == 2, "Cancelled scan retained its in-flight guard.");
    }

    private static async Task CancelRequests()
    {
        var clock = new Clock(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var keyCalls = 0;
        var handler = new Handler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (body.RootElement.GetProperty("apiKey").GetString() == OtherKey) return Empty();
            if (Interlocked.Increment(ref keyCalls) == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return Empty();
        });
        using var http = new HttpClient(handler);
        var transport = new ShippingLabelHttp(http, novaPoshtaPolicy: new(clock));
        using var activeCancel = new CancellationTokenSource();
        var first = Read(transport, ct: activeCancel.Token); await entered.Task;
        using var queuedCancel = new CancellationTokenSource();
        var cancelled = Read(transport, ct: queuedCancel.Token);
        Check(!cancelled.IsCompleted && handler.Calls == 1 && transport.GetNovaPoshtaRetryDelay(Key) == TimeSpan.Zero,
            "A queued request bypassed an active request or fabricated a rate-limit cooldown.");
        queuedCancel.Cancel();
        try { await cancelled; throw new Exception("Queued cancellation was ignored."); } catch (OperationCanceledException) { }
        var next = Read(transport);
        await Read(transport, OtherKey).WaitAsync(TimeSpan.FromSeconds(4));
        Check(!next.IsCompleted && handler.Calls == 2, "Queued cancellation released the active request's gate or blocked an unrelated key.");
        activeCancel.Cancel();
        try { await first; throw new Exception("Active request cancellation was ignored."); } catch (OperationCanceledException) { }
        await next.WaitAsync(TimeSpan.FromSeconds(4));
        Check(handler.Calls == 3 && keyCalls == 2 && transport.GetNovaPoshtaRetryDelay(Key) == TimeSpan.Zero,
            "Cancellation sent the cancelled waiter, delayed the next request, or retained the active request's gate.");
    }

    private static async Task OtherCarrierRetries()
    {
        foreach (var seller in new[] { false, true })
        {
            var waits = new List<TimeSpan>(); var calls = 0;
            var handler = new Handler((_, _) =>
            {
                var response = ++calls == 1 ? new HttpResponseMessage((HttpStatusCode)429) : Empty();
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7)); return Task.FromResult(response);
            });
            using var http = new HttpClient(handler);
            var transport = new ShippingLabelHttp(http, (wait, _) => { waits.Add(wait); return Task.CompletedTask; },
                new(new Clock()));
            await transport.SendAsync(() => new(seller ? HttpMethod.Post : HttpMethod.Get,
                seller ? "https://api-seller.rozetka.com.ua/sites" : "https://rz-delivery.rozetka.ua/api/track/synthetic"), "application/json", default);
            Check(handler.Calls == 2 && waits.SequenceEqual(new[] { TimeSpan.FromSeconds(7) }), "Existing RD/Seller retry behavior changed.");
        }
    }
}
