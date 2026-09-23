using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class MarketplaceSyncTests
{
    private const string PromId = "8795934615af4d1b85c8e447158716a6";
    private const string RozetkaId = "84f8e5f93c2241af9c990f2c3dd6c331";
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly MarketplaceRange Range = new(Now.AddDays(-30), Now.AddDays(1));

    public static IReadOnlyList<(string Name, Func<Task> Test)> All { get; } =
    [
        ("sync: repeated refresh upserts and updates older orders by exact ID", RepeatedRefreshAsync),
        ("sync: platform/account keys remain isolated during detail refresh", CompoundKeysAsync),
        ("sync: partial pages and exceptions never advance last successful checkpoint", PartialFailureAsync),
        ("sync: old-order safety limit is visible and not marked complete", SafetyLimitAsync),
        ("sync: cancellation releases gate; concurrent duplicate sync is rejected", CancellationAndConcurrencyAsync),
        ("sync: Kyiv day boundaries honor DST and configurable lookback", DateRangeAsync),
        ("transport: only documented read/auth requests reach HTTP handler", ReadOnlyWhitelistAsync),
        ("transport: Retry-After delta/date control injected delay", RetryAfterAsync),
        ("transport: auth failure is sanitized and not retried", AuthorizationFailureAsync),
        ("transport: cancellation interrupts backoff before another request", RetryCancellationAsync),
        ("transport: chunked body is size bounded and preserves split UTF8 characters", ChunkedBodyAsync)
    ];

    private static async Task RepeatedRefreshAsync()
    {
        var old = Order("1", "old-status", created: Now.AddDays(-100));
        var cache = new MemoryCache(new([old], []));
        var client = new FakeClient(MarketplaceKind.Prom);
        var pass = 0;
        client.Fetch = (connection, _) =>
        {
            pass++;
            var current = Order("2", pass == 1 ? "new" : "accepted");
            return Task.FromResult(new OrdersFetchResult([current, current], true));
        };
        client.Detail = (connection, id, _) => Task.FromResult<MarketplaceOrder?>(Order(id, "delivered", created: Now.AddDays(-100)));
        var service = new MarketplaceSyncService([client], new MemorySecrets(), cache, new Clock());
        var first = await service.SynchronizeAsync(Settings(), Range, [old.Key]);
        Equal(2, first.Orders.Count);
        Equal("delivered", first.Orders.Single(order => order.Key.OrderId == "1").Status);
        Equal(ReceiptLinkState.NotFound, new ReceiptOrderMatchingService().Match(
            new ReceiptRecord { Id = Guid.NewGuid().ToString(), Type = ReceiptTypes.Sell, FiscalDate = Now, TotalSumMinor = 0 },
            "cashier", first.Orders, [], true).State);

        var second = await service.SynchronizeAsync(Settings(), Range, [old.Key]);
        Equal(2, second.Orders.Count);
        Equal("accepted", second.Orders.Single(order => order.Key.OrderId == "2").Status);
        Equal(2, client.DetailIds.Count(id => id == "1"));
        Equal(2, cache.Saves);
        True(second.States.Single().Complete);
        Equal(Now, second.States.Single().LastSuccessUtc);
    }

    private static async Task CompoundKeysAsync()
    {
        var prom = new FakeClient(MarketplaceKind.Prom)
        {
            Fetch = (_, _) => Task.FromResult(new OrdersFetchResult([Order("42", "prom", PromId, MarketplaceKind.Prom)], true))
        };
        var rozetka = new FakeClient(MarketplaceKind.Rozetka)
        {
            Fetch = (_, _) => Task.FromResult(new OrdersFetchResult([Order("42", "rozetka", RozetkaId, MarketplaceKind.Rozetka)], true))
        };
        var cache = new MemoryCache();
        var service = new MarketplaceSyncService([prom, rozetka], new MemorySecrets(), cache, new Clock());
        var result = await service.SynchronizeAsync(Settings(both: true), Range, []);
        Equal(2, result.Orders.Count);
        Equal(2, result.Orders.Select(order => order.Key).Distinct().Count());
        Equal("prom", result.Orders.Single(order => order.Key.Marketplace == MarketplaceKind.Prom).Status);
        Equal("rozetka", result.Orders.Single(order => order.Key.Marketplace == MarketplaceKind.Rozetka).Status);
        Equal(0, prom.DetailIds.Count);
        Equal(0, rozetka.DetailIds.Count);

        // A client cannot inject rows for another marketplace/account into this connection's cache.
        var untrusted = new FakeClient(MarketplaceKind.Prom)
        {
            Fetch = (_, _) => Task.FromResult(new OrdersFetchResult([Order("43", "foreign", RozetkaId, MarketplaceKind.Rozetka)], true))
        };
        var isolated = await new MarketplaceSyncService([untrusted], new MemorySecrets(), new MemoryCache(), new Clock())
            .SynchronizeAsync(Settings(), Range, []);
        Equal(0, isolated.Orders.Count);
    }

    private static async Task PartialFailureAsync()
    {
        var previousSuccess = Now.AddDays(-1);
        var previous = new ConnectionSyncState(PromId, Range, true, previousSuccess, "", previousSuccess);
        var cache = new MemoryCache(new([], [previous]));
        var client = new FakeClient(MarketplaceKind.Prom)
        {
            Fetch = (_, _) => Task.FromResult(new OrdersFetchResult([Order("1", "new")], false, "Page 2 unavailable"))
        };
        var service = new MarketplaceSyncService([client], new MemorySecrets(), cache, new Clock());
        var partial = await service.SynchronizeAsync(Settings(), Range, []);
        Equal(1, partial.Orders.Count);
        True(!partial.States.Single().Complete);
        Equal(previousSuccess, partial.States.Single().LastSuccessUtc);
        client.Fetch = (_, _) => throw new InvalidOperationException("buyer phone fake-private-marker");
        var unavailable = await service.SynchronizeAsync(Settings(), Range, []);
        Equal(1, unavailable.Orders.Count);
        True(!unavailable.States.Single().Complete);
        Equal(previousSuccess, unavailable.States.Single().LastSuccessUtc);
        True(!unavailable.States.Single().Message.Contains("fake-private-marker", StringComparison.Ordinal));

        // Failure to refresh a known older order is also incomplete, even after complete list pages.
        client.Fetch = (_, _) => Task.FromResult(new OrdersFetchResult([], true));
        client.Detail = (_, _, _) => throw new HttpRequestException("detail unavailable");
        var oldUnavailable = await service.SynchronizeAsync(Settings(), Range, []);
        True(!oldUnavailable.States.Single().Complete);
        Equal(previousSuccess, oldUnavailable.States.Single().LastSuccessUtc);
    }

    private static async Task SafetyLimitAsync()
    {
        var lastSuccess = Now.AddDays(-1);
        var old = Enumerable.Range(1, 501).Select(i => Order(i.ToString(), "old", created: Now.AddDays(-100))).ToArray();
        var cache = new MemoryCache(new(old, [new(PromId, Range, true, lastSuccess, "", lastSuccess)]));
        var client = new FakeClient(MarketplaceKind.Prom)
        {
            Detail = (_, id, _) => Task.FromResult<MarketplaceOrder?>(Order(id, "updated", created: Now.AddDays(-100)))
        };
        var result = await new MarketplaceSyncService([client], new MemorySecrets(), cache, new Clock())
            .SynchronizeAsync(Settings(), Range, []);
        Equal(500, client.DetailIds.Count);
        Equal(501, result.Orders.Count);
        True(!result.States.Single().Complete);
        True(result.States.Single().Message.Contains("500", StringComparison.Ordinal));
        Equal(lastSuccess, result.States.Single().LastSuccessUtc);
    }

    private static async Task CancellationAndConcurrencyAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource<OrdersFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient(MarketplaceKind.Prom)
        {
            Fetch = async (_, token) => { entered.TrySetResult(); return await blocked.Task.WaitAsync(token); }
        };
        var cache = new MemoryCache();
        var service = new MarketplaceSyncService([client], new MemorySecrets(), cache, new Clock());
        using var cancellation = new CancellationTokenSource();
        var first = service.SynchronizeAsync(Settings(), Range, [], cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ThrowsAsync<InvalidOperationException>(() => service.SynchronizeAsync(Settings(), Range, []));
        Equal(1, client.FetchCount);
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(async () => await first);
        Equal(0, cache.Saves);
        client.Fetch = (_, _) => Task.FromResult(new OrdersFetchResult([], true));
        var recovered = await service.SynchronizeAsync(Settings(), Range, []);
        True(recovered.States.Single().Complete);
        Equal(2, client.FetchCount);
    }

    private static Task DateRangeAsync()
    {
        var spring = MarketplaceSyncService.BuildRange(new DateOnly(2024, 3, 31), new DateOnly(2024, 3, 31), 0);
        Equal(TimeSpan.FromHours(2), spring.From.Offset);
        Equal(TimeSpan.FromHours(3), spring.ToExclusive.Offset);
        Equal(TimeSpan.FromHours(23), spring.ToExclusive - spring.From);
        var autumn = MarketplaceSyncService.BuildRange(new DateOnly(2024, 10, 27), new DateOnly(2024, 10, 27), 0);
        Equal(TimeSpan.FromHours(25), autumn.ToExclusive - autumn.From);
        var lookback = MarketplaceSyncService.BuildRange(new DateOnly(2026, 9, 23), new DateOnly(2026, 9, 23), 30);
        Equal(new DateTime(2026, 8, 24), lookback.From.Date);
        Equal(new DateTime(2026, 9, 24), lookback.ToExclusive.Date);
        Equal(TimeSpan.FromHours(3), lookback.From.Offset);
        return Task.CompletedTask;
    }

    private static async Task ReadOnlyWhitelistAsync()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler(_ => { calls++; return Response(HttpStatusCode.OK); }));
        var transport = new MarketplaceHttpTransport(http);
        var allowed = new[]
        {
            (MarketplaceKind.Prom, HttpMethod.Get, "https://my.prom.ua/api/v1/orders/list?limit=100"),
            (MarketplaceKind.Prom, HttpMethod.Get, "https://my.prom.ua/api/v1/orders/42"),
            (MarketplaceKind.Rozetka, HttpMethod.Post, "https://api-seller.rozetka.com.ua/sites"),
            (MarketplaceKind.Rozetka, HttpMethod.Get, "https://api-seller.rozetka.com.ua/orders/search"),
            (MarketplaceKind.Rozetka, HttpMethod.Get, "https://api-seller.rozetka.com.ua/orders/42"),
            (MarketplaceKind.Rozetka, HttpMethod.Get, "https://api-seller.rozetka.com.ua/prro/receipt/42")
        };
        foreach (var (market, method, url) in allowed)
            await transport.SendAsync(() => new(method, url), market);
        Equal(allowed.Length, calls);
        var blocked = new[]
        {
            (MarketplaceKind.Prom, HttpMethod.Post, "https://my.prom.ua/api/v1/orders/42"),
            (MarketplaceKind.Prom, HttpMethod.Get, "https://my.prom.ua/api/v1/orders/set_status"),
            (MarketplaceKind.Prom, HttpMethod.Get, "http://my.prom.ua/api/v1/orders/list"),
            (MarketplaceKind.Prom, HttpMethod.Get, "https://my.prom.ua.evil.test/api/v1/orders/list"),
            (MarketplaceKind.Prom, HttpMethod.Get, "https://fake-secret@my.prom.ua/api/v1/orders/list"),
            (MarketplaceKind.Prom, HttpMethod.Get, "https://my.prom.ua:8443/api/v1/orders/list"),
            (MarketplaceKind.Prom, HttpMethod.Get, "https://my.prom.ua/api/v1/orders/list#fragment"),
            (MarketplaceKind.Rozetka, HttpMethod.Put, "https://api-seller.rozetka.com.ua/orders/42"),
            (MarketplaceKind.Rozetka, HttpMethod.Delete, "https://api-seller.rozetka.com.ua/orders/42"),
            (MarketplaceKind.Rozetka, HttpMethod.Post, "https://api-seller.rozetka.com.ua/prro/receipt/42"),
            (MarketplaceKind.Rozetka, HttpMethod.Post, "https://api.checkbox.ua/api/v1/receipts/sell"),
            (MarketplaceKind.Prom, HttpMethod.Get, "https://api-seller.rozetka.com.ua/orders/42")
        };
        foreach (var (market, method, url) in blocked)
            await ThrowsAsync<InvalidOperationException>(() => transport.SendAsync(() => new(method, url), market));
        Equal(allowed.Length, calls);
    }

    private static async Task RetryAfterAsync()
    {
        var calls = 0;
        var waits = new List<TimeSpan>();
        var retryAt = DateTimeOffset.UtcNow.AddSeconds(90);
        using var http = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            var response = Response(calls < 3 ? (HttpStatusCode)429 : HttpStatusCode.OK);
            if (calls == 1) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
            if (calls == 2) response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAt);
            return response;
        }));
        var transport = new MarketplaceHttpTransport(http, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
        Equal("{}", await transport.SendAsync(() => new(HttpMethod.Get, "https://my.prom.ua/api/v1/orders/list"), MarketplaceKind.Prom));
        Equal(3, calls);
        Equal(2, waits.Count);
        Equal(TimeSpan.FromSeconds(17), waits[0]);
        True(waits[1] >= TimeSpan.FromSeconds(80) && waits[1] <= TimeSpan.FromSeconds(90));
    }

    private static async Task AuthorizationFailureAsync()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            return new(HttpStatusCode.Unauthorized) { Content = new StringContent("token=fake-private-marker; buyer=secret") };
        }));
        var transport = new MarketplaceHttpTransport(http);
        try
        {
            await transport.SendAsync(() => new(HttpMethod.Get, "https://my.prom.ua/api/v1/orders/list"), MarketplaceKind.Prom);
        }
        catch (MarketplaceApiException exception)
        {
            Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
            True(!exception.Message.Contains("fake-private-marker", StringComparison.Ordinal));
            Equal(1, calls);
            return;
        }
        throw new Exception("Expected sanitized authorization error.");
    }

    private static async Task RetryCancellationAsync()
    {
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new StubHandler(_ => { calls++; return Response(HttpStatusCode.ServiceUnavailable); }));
        var transport = new MarketplaceHttpTransport(http, (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        await ThrowsAsync<OperationCanceledException>(() => transport.SendAsync(
            () => new(HttpMethod.Get, "https://my.prom.ua/api/v1/orders/list"), MarketplaceKind.Prom, cancellation.Token));
        Equal(1, calls);
    }

    private static async Task ChunkedBodyAsync()
    {
        const string utf8Json = "{\"покупець\":\"Тестова особа\",\"emoji\":\"🧾\"}";
        var calls = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            // Non-seekable streams deliberately omit Content-Length, as chunked responses do.
            var content = calls == 1
                ? new StreamContent(new ChunkedReadStream(new byte[16 * 1024 * 1024 + 1]))
                : new StreamContent(new ChunkedReadStream(Encoding.UTF8.GetBytes(utf8Json), 3));
            True(content.Headers.ContentLength is null);
            return new(HttpStatusCode.OK) { Content = content };
        }));
        var transport = new MarketplaceHttpTransport(http);
        await ThrowsAsync<MarketplaceApiException>(() => transport.SendAsync(
            () => new(HttpMethod.Get, "https://my.prom.ua/api/v1/orders/list"), MarketplaceKind.Prom));
        Equal(1, calls);
        // Three-byte chunks split Cyrillic and the four-byte symbol across individual reads.
        Equal(utf8Json, await transport.SendAsync(
            () => new(HttpMethod.Get, "https://my.prom.ua/api/v1/orders/list"), MarketplaceKind.Prom));
        Equal(2, calls);
    }

    private static MarketplaceSettings Settings(bool both = false) => new()
    {
        Connections = both
            ? [new() { Id = PromId, Marketplace = MarketplaceKind.Prom, Enabled = true },
               new() { Id = RozetkaId, Marketplace = MarketplaceKind.Rozetka, Enabled = true }]
            : [new() { Id = PromId, Marketplace = MarketplaceKind.Prom, Enabled = true }]
    };
    private static MarketplaceOrder Order(string id, string status, string connection = PromId,
        MarketplaceKind marketplace = MarketplaceKind.Prom, DateTimeOffset? created = null) => new()
    {
        Key = new(marketplace, connection, id), Status = status, CreatedAt = created ?? Now,
        RetrievedAtUtc = Now, Currency = "UAH", Total = 100m
    };
    private static HttpResponseMessage Response(HttpStatusCode status) => new(status) { Content = new StringContent("{}") };
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
    }
    private static void True(bool condition) { if (!condition) throw new Exception("Condition is false."); }
    private static async Task ThrowsAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
    private sealed class FakeClient(MarketplaceKind marketplace) : IMarketplaceOrdersClient
    {
        public MarketplaceKind Marketplace { get; } = marketplace;
        public Func<MarketplaceConnection, CancellationToken, Task<OrdersFetchResult>> Fetch { get; set; } =
            (_, _) => Task.FromResult(new OrdersFetchResult([], true));
        public Func<MarketplaceConnection, string, CancellationToken, Task<MarketplaceOrder?>> Detail { get; set; } =
            (_, _, _) => Task.FromResult<MarketplaceOrder?>(null);
        public List<string> DetailIds { get; } = [];
        public int FetchCount { get; private set; }
        public Task TestConnectionAsync(MarketplaceConnection connection, MarketplaceCredentials credentials, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<OrdersFetchResult> FetchAsync(MarketplaceConnection connection, MarketplaceCredentials credentials,
            MarketplaceRange range, CancellationToken cancellationToken = default)
        {
            FetchCount++;
            return Fetch(connection, cancellationToken);
        }
        public Task<MarketplaceOrder?> GetOrderAsync(MarketplaceConnection connection, MarketplaceCredentials credentials,
            string orderId, CancellationToken cancellationToken = default)
        {
            DetailIds.Add(orderId);
            return Detail(connection, orderId, cancellationToken);
        }
    }
    private sealed class MemoryCache(MarketplaceSnapshot? initial = null) : IMarketplaceCacheStore
    {
        private MarketplaceSnapshot _value = initial ?? new([], []);
        public int Saves { get; private set; }
        public Task<MarketplaceSnapshot> LoadAsync(int retentionDays, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_value);
        }
        public Task SaveAsync(MarketplaceSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Saves++;
            _value = snapshot;
            return Task.CompletedTask;
        }
    }
    private sealed class MemorySecrets : IMarketplaceSecretStore
    {
        public Task<MarketplaceCredentials?> LoadAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MarketplaceCredentials?>(new(Token: "fake-token", Login: "fake-login", Password: "fake-password"));
        public Task SaveAsync(string connectionId, MarketplaceCredentials credentials, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handle(request));
    }
    private sealed class ChunkedReadStream(byte[] data, int chunkSize = 8192) : MemoryStream(data, writable: false)
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }
}
