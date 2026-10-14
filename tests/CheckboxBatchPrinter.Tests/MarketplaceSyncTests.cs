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
        ("sync: typed marketplace 401 and 429 cooldown evidence survives incomplete cache", FailureReasonAsync),
        ("sync: Prom later-page 429 preserves partial data and stops cached-detail fan-out", PromListBackoffAsync),
        ("sync: Prom account detail failure stops at the attempted cursor and preserves other providers", PromDetailBackoffAsync),
        ("sync: Prom missing individual order does not block remaining cached details", PromMissingDetailContinuesAsync),
        ("sync: irrelevant dated history does not block current range completeness", HistoricalCacheScopeAsync),
        ("sync: durable outside-range order failures still block completeness", DurableHistoryFailureAsync),
        ("sync: relevant unknown-date and durable order safety limit stays incomplete", SafetyLimitAsync),
        ("sync: cancellation releases gate; concurrent duplicate sync is rejected", CancellationAndConcurrencyAsync),
        ("sync: Prom and Rozetka lanes overlap while their account reads and merged cache writes stay serial", ParallelProviderLanesAsync),
        ("sync: a failed parallel provider preserves the other provider and sanitized individual checkpoints", ParallelProviderFailureAsync),
        ("sync: canceled late provider results cannot overwrite the other completed provider cache", ParallelProviderCancellationAsync),
        ("sync: identical seller connections fetch once and retain alias keys and conflicting fiscal evidence", DuplicateSellerAccountsAsync),
        ("sync: alias manual keys refresh one canonical detail and retain every persisted alias", DuplicateSellerKnownKeysAsync),
        ("sync: changed or unreadable seller credentials never share a request", DuplicateSellerCredentialBoundariesAsync),
        ("sync: partial duplicate-account responses update all aliases without advancing their checkpoints", DuplicateSellerPartialFailureAsync),
        ("sync: consolidated fiscal evidence survives refresh and expiration of removed connection rows without hiding conflicts", ConsolidatedFiscalEvidenceSurvivesExpiryAsync),
        ("sync: Kyiv day boundaries honor DST and configurable lookback", DateRangeAsync),
        ("transport: only documented read/auth requests reach HTTP handler", ReadOnlyWhitelistAsync),
        ("transport: Rozetka Retry-After delta/date control injected delay", RetryAfterAsync),
        ("transport: auth failure is sanitized and not retried", AuthorizationFailureAsync),
        ("transport: Rozetka cancellation interrupts backoff before another request", RetryCancellationAsync),
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

    private static async Task FailureReasonAsync()
    {
        var client = new FakeClient(MarketplaceKind.Prom);
        var cache = new MemoryCache();
        var service = new MarketplaceSyncService([client], new MemorySecrets(), cache, new Clock());
        client.Fetch = (_, _) => throw new MarketplaceApiException("synthetic unauthorized", HttpStatusCode.Unauthorized);
        var unauthorized = await service.SynchronizeAsync(Settings(), Range, []);
        True(!unauthorized.States.Single().Complete);
        Equal(HttpStatusCode.Unauthorized, unauthorized.States.Single().FailureStatus);
        client.Fetch = (_, _) => throw new MarketplaceApiException("synthetic limit", HttpStatusCode.TooManyRequests,
            TimeSpan.FromMinutes(10));
        var limited = await service.SynchronizeAsync(Settings(), Range, []);
        True(!limited.States.Single().Complete);
        Equal(HttpStatusCode.TooManyRequests, limited.States.Single().FailureStatus);
        Equal(TimeSpan.FromMinutes(10), limited.States.Single().RetryAfter);
        Equal(HttpStatusCode.TooManyRequests, (await cache.LoadAsync(30)).States.Single().FailureStatus);
    }

    private static async Task PromListBackoffAsync()
    {
        var lastSuccess = Now.AddDays(-1);
        var old = Enumerable.Range(1, 501).Select(i => Order(i.ToString(), "cached")).ToArray();
        var prior = new ConnectionSyncState(PromId, Range, true, lastSuccess, "previous", lastSuccess, "120");
        var cache = new MemoryCache(new(old, [prior]));
        var requests = new List<string>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            True(request.RequestUri.AbsolutePath == "/api/v1/orders/list");
            if (requests.Count == 1)
                return PromJson("""{"orders":[{"id":1000,"date_created":"2026-09-23T12:00:00Z","price":"125.00","currency":"UAH","status":"pending"}]}""");
            var failed = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                { Content = new StringContent("private body must not reach cached status") };
            failed.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(12));
            return failed;
        }));
        var client = new PromOrdersClient(new MarketplaceHttpTransport(http, (_, _) =>
            throw new Exception("Prom must not wait for an immediate HTTP retry.")));
        var service = new MarketplaceSyncService([client], new MemorySecrets(), cache, new Clock());
        var result = await service.SynchronizeAsync(Settings(), Range, old.Select(o => o.Key).ToArray());
        Equal(2, requests.Count);
        Equal(502, result.Orders.Count);
        True(old.All(o => result.Orders.Contains(o)));
        Equal("pending", result.Orders.Single(o => o.Key.OrderId == "1000").Status);
        var state = result.States.Single();
        True(!state.Complete);
        Equal(lastSuccess, state.LastSuccessUtc);
        Equal("120", state.DetailCursor);
        Equal(HttpStatusCode.TooManyRequests, state.FailureStatus);
        True(state.RetryAfter is { } retry && retry > TimeSpan.FromMinutes(11));
        True(!state.Message.Contains("500", StringComparison.Ordinal));
        True(!state.Message.Contains("private body", StringComparison.Ordinal));
        Equal(state, (await cache.LoadAsync(30)).States.Single());

        var paused = await service.SynchronizeAsync(Settings(), Range, old.Select(o => o.Key).ToArray());
        Equal(2, requests.Count);
        Equal(502, paused.Orders.Count);
        Equal(HttpStatusCode.TooManyRequests, paused.States.Single().FailureStatus);
        Equal(lastSuccess, paused.States.Single().LastSuccessUtc);
        Equal("120", paused.States.Single().DetailCursor);
    }

    private static async Task PromDetailBackoffAsync()
    {
        var lastSuccess = Now.AddDays(-1);
        var old = Enumerable.Range(1, 600).Select(i => Order(i.ToString("D4"), "cached")).ToArray();
        var cache = new MemoryCache(new(old,
            [new(PromId, Range, true, lastSuccess, "previous", lastSuccess, "0001")]));
        var requests = new List<string>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/api/v1/orders/list") return PromJson("""{"orders":[]}""");
            // Numeric URL normalization removes zero padding from the requested key.
            True(request.RequestUri.AbsolutePath == "/api/v1/orders/2");
            return Response(HttpStatusCode.Forbidden);
        }));
        var prom = new PromOrdersClient(new MarketplaceHttpTransport(http));
        var rozetka = new FakeClient(MarketplaceKind.Rozetka)
        {
            Fetch = (_, _) => Task.FromResult(new OrdersFetchResult([Order("42", "fresh", RozetkaId, MarketplaceKind.Rozetka)], true))
        };
        var result = await new MarketplaceSyncService([prom, rozetka], new MemorySecrets(), cache, new Clock())
            .SynchronizeAsync(Settings(both: true), Range, []);
        Equal(2, requests.Count);
        Equal(601, result.Orders.Count);
        True(old.All(o => result.Orders.Contains(o)));
        var state = result.States.Single(s => s.ConnectionId == PromId);
        True(!state.Complete);
        Equal(HttpStatusCode.Forbidden, state.FailureStatus);
        Equal(lastSuccess, state.LastSuccessUtc);
        Equal("0002", state.DetailCursor);
        True(state.RetryAfter > TimeSpan.Zero);
        True(result.States.Single(s => s.ConnectionId == RozetkaId).Complete);
        Equal(1, rozetka.FetchCount);
    }

    private static async Task PromMissingDetailContinuesAsync()
    {
        var old = new[] { Order("1", "cached"), Order("2", "cached") };
        var cache = new MemoryCache(new(old, []));
        var requests = new List<string>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            return path switch
            {
                "/api/v1/orders/list" => PromJson("""{"orders":[]}"""),
                "/api/v1/orders/1" => Response(HttpStatusCode.NotFound),
                "/api/v1/orders/2" => PromJson("""{"order":{"id":2,"date_created":"2026-09-23T12:00:00Z","price":"100.00","currency":"UAH","status":"updated"}}"""),
                _ => throw new Exception("Unexpected synthetic Prom request.")
            };
        }));
        var result = await new MarketplaceSyncService([new PromOrdersClient(new MarketplaceHttpTransport(http))],
            new MemorySecrets(), cache, new Clock()).SynchronizeAsync(Settings(), Range, []);
        Equal(3, requests.Count);
        Equal("cached", result.Orders.Single(o => o.Key.OrderId == "1").Status);
        Equal("updated", result.Orders.Single(o => o.Key.OrderId == "2").Status);
        True(!result.States.Single().Complete);
        Equal("2", result.States.Single().DetailCursor);
    }

    private static HttpResponseMessage PromJson(string json) => new(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static async Task HistoricalCacheScopeAsync()
    {
        // Reproduce a large previous snapshot: these older rows remain visible, but they
        // cannot be competitors for documents in this requested range.
        var old = Enumerable.Range(1, 645).Select(i => Order(i.ToString(), "old", created: Now.AddDays(-100))).ToArray();
        var current = Order("1000", "current");
        var pastBoundary = Order("1001", "before", created: Range.From.AddTicks(-1));
        var endBoundary = Order("1002", "after", created: Range.ToExclusive);
        var cache = new MemoryCache(new([.. old, pastBoundary, endBoundary], []));
        var client = new FakeClient(MarketplaceKind.Prom)
        {
            Fetch = (_, _) => Task.FromResult(new OrdersFetchResult([current], true)),
            Detail = (_, _, _) => throw new HttpRequestException("Unrelated dated history must not be requested.")
        };
        var service = new MarketplaceSyncService([client], new MemorySecrets(), cache, new Clock());
        var result = await service.SynchronizeAsync(Settings(), Range, []);
        Equal(0, client.DetailIds.Count);
        Equal(648, result.Orders.Count);
        True(old.All(o => result.Orders.Contains(o)));
        True(result.States.Single().Complete);
        Equal(Now, result.States.Single().LastSuccessUtc);

        // Missing rows at the inclusive start and with an unknown date remain relevant.
        var rangeStart = Order("1003", "start", created: Range.From);
        var unknownDate = Order("1004", "unknown") with { CreatedAt = null };
        await cache.SaveAsync(new([.. result.Orders, rangeStart, unknownDate], result.States));
        client.Detail = (_, id, _) => Task.FromResult<MarketplaceOrder?>(
            id == rangeStart.Key.OrderId ? rangeStart : unknownDate);
        result = await service.SynchronizeAsync(Settings(), Range, []);
        True(client.DetailIds.ToHashSet().SetEquals([rangeStart.Key.OrderId, unknownDate.Key.OrderId]));
        True(result.States.Single().Complete);
        Equal(650, result.Orders.Count);
    }

    private static async Task DurableHistoryFailureAsync()
    {
        var lastSuccess = Now.AddDays(-1);
        var old = Order("1", "old", created: Now.AddDays(-100));
        var cache = new MemoryCache(new([old], [new(PromId, Range, true, lastSuccess, "", lastSuccess)]));
        var client = new FakeClient(MarketplaceKind.Prom)
        {
            Detail = (_, _, _) => throw new HttpRequestException("Linked order unavailable.")
        };
        var service = new MarketplaceSyncService([client], new MemorySecrets(), cache, new Clock());
        var result = await service.SynchronizeAsync(Settings(), Range, [old.Key]);
        Equal(old.Key.OrderId, client.DetailIds.Single());
        True(!result.States.Single().Complete);
        Equal(lastSuccess, result.States.Single().LastSuccessUtc);
        Equal(old, result.Orders.Single());

        client.Detail = (_, _, _) => Task.FromResult<MarketplaceOrder?>(old with { Status = "updated" });
        result = await service.SynchronizeAsync(Settings(), Range, [old.Key]);
        True(result.States.Single().Complete);
        Equal("updated", result.Orders.Single().Status);
        Equal(Now, result.States.Single().LastSuccessUtc);
    }

    private static async Task SafetyLimitAsync()
    {
        foreach (var mode in new[] { "in-range", "unknown-date", "durable-history" })
        {
            var lastSuccess = Now.AddDays(-1);
            var old = Enumerable.Range(1, 501).Select(i => Order(i.ToString(), "old") with
            {
                CreatedAt = mode == "unknown-date" ? null : mode == "durable-history" ? Now.AddDays(-100) : Now
            }).ToArray();
            var cache = new MemoryCache(new(old, [new(PromId, Range, true, lastSuccess, "", lastSuccess)]));
            var client = new FakeClient(MarketplaceKind.Prom)
            {
                Detail = (_, id, _) => Task.FromResult<MarketplaceOrder?>(old.Single(o => o.Key.OrderId == id) with { Status = "updated" })
            };
            var known = mode == "durable-history" ? old.Select(o => o.Key).ToArray() : [];
            var result = await new MarketplaceSyncService([client], new MemorySecrets(), cache, new Clock())
                .SynchronizeAsync(Settings(), Range, known);
            Equal(500, client.DetailIds.Count);
            Equal(501, result.Orders.Count);
            True(!result.States.Single().Complete);
            True(result.States.Single().Message.Contains("500", StringComparison.Ordinal));
            Equal(lastSuccess, result.States.Single().LastSuccessUtc);
        }
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

    private static async Task ParallelProviderLanesAsync()
    {
        var promEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rozetkaEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeProm = 0; var activeRozetka = 0; var maxProm = 0; var maxRozetka = 0;
        var prom = new FakeClient(MarketplaceKind.Prom)
        {
            Fetch = async (connection, token) =>
            {
                maxProm = Math.Max(maxProm, Interlocked.Increment(ref activeProm));
                try
                {
                    promEntered.TrySetResult(); await release.Task.WaitAsync(token);
                    return new([Order("42", "prom", connection.Id)], true);
                }
                finally { Interlocked.Decrement(ref activeProm); }
            }
        };
        var rozetka = new FakeClient(MarketplaceKind.Rozetka)
        {
            Fetch = async (connection, token) =>
            {
                maxRozetka = Math.Max(maxRozetka, Interlocked.Increment(ref activeRozetka));
                try
                {
                    rozetkaEntered.TrySetResult(); await release.Task.WaitAsync(token);
                    return new([Order("42", "rozetka", connection.Id, MarketplaceKind.Rozetka)], true);
                }
                finally { Interlocked.Decrement(ref activeRozetka); }
            }
        };
        var config = Settings(both: true);
        config.Connections.Add(new() { Id = "prom-second", Marketplace = MarketplaceKind.Prom, Enabled = true });
        config.Connections.Add(new() { Id = "rozetka-second", Marketplace = MarketplaceKind.Rozetka, Enabled = true });
        var cache = new ParallelCommitCache();
        var service = new MarketplaceSyncService([prom, rozetka], new MemorySecrets(), cache, new Clock());
        var refreshing = service.SynchronizeAsync(config, Range, []);
        try
        {
            await Task.WhenAll(promEntered.Task, rozetkaEntered.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Equal(1, prom.FetchCount); Equal(1, rozetka.FetchCount); Equal(0, cache.Snapshots.Count);
            True(!refreshing.IsCompleted);
            release.TrySetResult();
            var result = await refreshing.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(2, prom.FetchCount); Equal(2, rozetka.FetchCount); Equal(1, maxProm); Equal(1, maxRozetka);
            Equal(4, result.Orders.Count); Equal(4, result.Orders.Select(o => o.Key).Distinct().Count());
            True(result.States.All(s => s.Complete)); Equal(4, result.States.Count);
            Equal(1, cache.MaximumConcurrentSaves);
            Equal(4, cache.Snapshots.Count); Equal(4, cache.Snapshots.Last().Orders.Count);
            Equal(4, cache.Snapshots.Last().States.Count);
            var snapshots = cache.Snapshots.ToArray();
            for (var i = 0; i < snapshots.Length; i++)
            {
                Equal(i + 1, snapshots[i].Orders.Count); Equal(i + 1, snapshots[i].States.Count);
                if (i > 0) True(snapshots[i - 1].Orders.All(previous => snapshots[i].Orders.Any(current => current.Key == previous.Key)));
            }
        }
        finally { release.TrySetResult(); await refreshing; }
    }

    private static async Task ParallelProviderFailureAsync()
    {
        var promEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rozetkaEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prom = new FakeClient(MarketplaceKind.Prom)
        {
            Fetch = async (_, token) =>
            { promEntered.TrySetResult(); await release.Task.WaitAsync(token); throw new HttpRequestException("Synthetic private buyer marker."); }
        };
        var rozetka = new FakeClient(MarketplaceKind.Rozetka)
        {
            Fetch = async (_, token) =>
            { rozetkaEntered.TrySetResult(); await release.Task.WaitAsync(token); return new([Order("42", "good", RozetkaId, MarketplaceKind.Rozetka)], true); }
        };
        var cache = new ParallelCommitCache();
        var service = new MarketplaceSyncService([prom, rozetka], new MemorySecrets(), cache, new Clock());
        var refreshing = service.SynchronizeAsync(Settings(both: true), Range, []);
        try
        {
            await Task.WhenAll(promEntered.Task, rozetkaEntered.Task).WaitAsync(TimeSpan.FromSeconds(5));
            release.TrySetResult();
            var result = await refreshing.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(RozetkaId, result.Orders.Single().Key.ConnectionId);
            True(result.States.Single(s => s.ConnectionId == RozetkaId).Complete);
            True(!result.States.Single(s => s.ConnectionId == PromId).Complete);
            True(result.States.All(s => !s.Message.Contains("private buyer", StringComparison.Ordinal)));
            Equal(1, cache.MaximumConcurrentSaves); Equal(2, cache.Snapshots.Last().States.Count);
            Equal(RozetkaId, cache.Snapshots.Last().Orders.Single().Key.ConnectionId);
        }
        finally { release.TrySetResult(); await refreshing; }
    }

    private static async Task ParallelProviderCancellationAsync()
    {
        var promEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rozetkaEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rozetkaRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prom = new FakeClient(MarketplaceKind.Prom)
        {
            Fetch = async (_, _) =>
            { promEntered.TrySetResult(); await late.Task; return new([Order("old", "late")], true); }
        };
        var rozetka = new FakeClient(MarketplaceKind.Rozetka)
        {
            Fetch = async (_, token) =>
            { rozetkaEntered.TrySetResult(); await rozetkaRelease.Task.WaitAsync(token); return new([Order("good", "good", RozetkaId, MarketplaceKind.Rozetka)], true); }
        };
        var cache = new ParallelCommitCache();
        using var cancellation = new CancellationTokenSource();
        var refreshing = new MarketplaceSyncService([prom, rozetka], new MemorySecrets(), cache, new Clock())
            .SynchronizeAsync(Settings(both: true), Range, [], cancellation.Token);
        try
        {
            await Task.WhenAll(promEntered.Task, rozetkaEntered.Task).WaitAsync(TimeSpan.FromSeconds(5));
            rozetkaRelease.TrySetResult();
            await cache.FirstSaved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel(); late.TrySetResult();
            await ThrowsAsync<OperationCanceledException>(async () => await refreshing.WaitAsync(TimeSpan.FromSeconds(5)));
            Equal(1, cache.Snapshots.Count); Equal(1, cache.MaximumConcurrentSaves);
            Equal(RozetkaId, cache.Snapshots.Last().Orders.Single().Key.ConnectionId);
            Equal(RozetkaId, cache.Snapshots.Last().States.Single().ConnectionId);
        }
        finally
        {
            cancellation.Cancel(); rozetkaRelease.TrySetResult(); late.TrySetResult();
            try { await refreshing; } catch (OperationCanceledException) { }
        }
    }

    private static MarketplaceSettings DuplicateSellerSettings() => new()
    {
        Connections =
        [
            new() { Id = "seller-z-alias", Marketplace = MarketplaceKind.Rozetka, Enabled = true, Name = "Alias shop" },
            new() { Id = "seller-a-primary", Marketplace = MarketplaceKind.Rozetka, Enabled = true, Name = "Primary shop" }
        ]
    };

    private static SellerSecrets DuplicateSellerSecrets() => new()
    {
        Values =
        {
            ["seller-a-primary"] = new(Token: "synthetic-api-token", Login: "old-seller", Password: "old-password"),
            ["seller-z-alias"] = new(Token: " synthetic-api-token ", Login: "different-legacy-login", Password: "different-legacy-password")
        }
    };

    private static async Task DuplicateSellerAccountsAsync()
    {
        var config = DuplicateSellerSettings();
        var secrets = DuplicateSellerSecrets();
        var aliasKey = new OrderKey(MarketplaceKind.Rozetka, "seller-z-alias", "42");
        var oldReference = new FiscalDocumentReference(FiscalDocumentKeyKind.CheckboxReceiptUuid,
            "11111111-1111-1111-1111-111111111111", "old assertion", aliasKey, "Checkbox", "document");
        var old = Order("42", "old", aliasKey.ConnectionId, MarketplaceKind.Rozetka) with
            { FiscalReferences = [oldReference], ReceiptIds = [oldReference.Value] };
        var cache = new MemoryCache(new([old], []));
        var client = new FakeClient(MarketplaceKind.Rozetka)
        {
            Fetch = (connection, _) =>
            {
                Equal("seller-a-primary", connection.Id);
                var order = Order("42", "Скасовано", connection.Id, MarketplaceKind.Rozetka);
                var fresh = oldReference with { Order = order.Key, Value = "22222222-2222-2222-2222-222222222222", Source = "fresh assertion" };
                return Task.FromResult(new OrdersFetchResult([order with { FiscalReferences = [fresh], ReceiptIds = [fresh.Value] }], true));
            }
        };
        var result = await new MarketplaceSyncService([client], secrets, cache, new Clock()).SynchronizeAsync(config, Range, []);
        Equal(1, client.FetchCount); Equal(0, client.DetailIds.Count); Equal(1, cache.Saves);
        Equal(2, result.Orders.Count); Equal(2, result.States.Count); True(result.States.All(s => s.Complete));
        True(result.Orders.All(o => o.IsCanceled));
        foreach (var order in result.Orders)
        {
            Equal(config.Connections.Single(c => c.Id == order.Key.ConnectionId).Name, order.StoreName);
            True(order.FiscalReferences.All(reference => reference.Order == order.Key));
            True(order.FiscalReferences.Any(reference => reference.Source == "fresh assertion"));
        }
        True(result.Orders.Single(o => o.Key == aliasKey).FiscalReferences.Contains(oldReference));
        var identity = await MarketplaceOrderDeduplication.ResolveAsync(config, secrets);
        Equal(2, identity.Merge(result.Orders).Single().FiscalReferences.Count);
        Equal(aliasKey, old.Key); Equal(aliasKey, oldReference.Order);
    }

    private static async Task DuplicateSellerKnownKeysAsync()
    {
        var config = DuplicateSellerSettings();
        var secrets = DuplicateSellerSecrets();
        var old = Order("7", "old", "seller-z-alias", MarketplaceKind.Rozetka, Now.AddDays(-100));
        var oldKey = old.Key;
        var primaryKey = oldKey with { ConnectionId = "seller-a-primary" };
        var cache = new MemoryCache(new([old], []));
        var client = new FakeClient(MarketplaceKind.Rozetka)
        {
            Detail = (connection, id, _) =>
            {
                Equal("seller-a-primary", connection.Id); Equal("7", id);
                var order = old with { Key = primaryKey, Status = "updated" };
                return Task.FromResult<MarketplaceOrder?>(order with
                {
                    FiscalReferences = [new(FiscalDocumentKeyKind.CheckboxReceiptUuid,
                        "33333333-3333-3333-3333-333333333333", "detail", primaryKey, "Checkbox", "document")]
                });
            }
        };
        var known = new[] { oldKey, primaryKey, oldKey with { ConnectionId = "disabled-other-seller" } };
        var result = await new MarketplaceSyncService([client], secrets, cache, new Clock()).SynchronizeAsync(config, Range, known);
        Equal(1, client.FetchCount); Equal("7", client.DetailIds.Single()); Equal(2, result.Orders.Count);
        True(result.Orders.All(o => o.Status == "updated" && o.FiscalReferences.Single().Order == o.Key));
        True(result.Orders.Any(o => o.Key == oldKey)); True(result.Orders.Any(o => o.Key == primaryKey));
        Equal(oldKey, known[0]); Equal(oldKey, old.Key);
        True(result.States.All(state => state.Complete && state.DetailCursor == "7"));
        True(secrets.LoadCounts.Values.All(count => count == 1));
    }

    private static async Task DuplicateSellerCredentialBoundariesAsync()
    {
        var config = DuplicateSellerSettings();
        var secrets = DuplicateSellerSecrets();
        foreach (var (id, credential) in new[]
        {
            ("different-token-a", new MarketplaceCredentials(Token: "separate-api-token-a", Login: "old-seller", Password: "old-password")),
            ("different-token-b", new MarketplaceCredentials(Token: "separate-api-token-b", Login: "old-seller", Password: "old-password")),
            ("unreadable", new MarketplaceCredentials(Token: "synthetic-api-token"))
        })
        {
            config.Connections.Add(new() { Id = id, Marketplace = MarketplaceKind.Rozetka, Enabled = true });
            secrets.Values[id] = credential;
        }
        secrets.Unreadable.Add("unreadable");
        var client = new FakeClient(MarketplaceKind.Rozetka)
        {
            Fetch = (connection, _) => Task.FromResult(new OrdersFetchResult(
                [Order("42", connection.Id, connection.Id, MarketplaceKind.Rozetka)], true))
        };
        var service = new MarketplaceSyncService([client], secrets, new MemoryCache(), new Clock());
        var first = await service.SynchronizeAsync(config, Range, []);
        Equal(3, client.FetchCount); Equal(4, first.Orders.Count);
        True(!first.States.Single(s => s.ConnectionId == "unreadable").Complete);
        True(secrets.LoadCounts.Values.All(count => count == 1));
        // Rotation is observed on the next synchronization, not retained as an old alias proof.
        secrets.Values["seller-z-alias"] = new(Token: "rotated-api-token");
        var second = await service.SynchronizeAsync(config, Range, []);
        Equal(7, client.FetchCount); Equal(4, second.Orders.Count);
        Equal("seller-z-alias", second.Orders.Single(o => o.Key.ConnectionId == "seller-z-alias").Status);
    }

    private static async Task DuplicateSellerPartialFailureAsync()
    {
        var config = DuplicateSellerSettings();
        config.Connections.Add(new() { Id = PromId, Marketplace = MarketplaceKind.Prom, Enabled = true });
        var secrets = DuplicateSellerSecrets(); secrets.Values[PromId] = new(Token: "separate-prom");
        var oldStates = config.Connections.Select((c, index) =>
            new ConnectionSyncState(c.Id, Range, true, Now.AddDays(-index - 1), "old", Now.AddDays(-index - 1))).ToArray();
        var cache = new MemoryCache(new([], oldStates));
        var client = new FakeClient(MarketplaceKind.Rozetka)
        {
            Fetch = (connection, _) => Task.FromResult(new OrdersFetchResult(
                [Order("42", "partial", connection.Id, MarketplaceKind.Rozetka)], false, "Incomplete page"))
        };
        var prom = new FakeClient(MarketplaceKind.Prom)
        { Fetch = (connection, _) => Task.FromResult(new OrdersFetchResult([Order("42", "good", connection.Id)], true)) };
        var service = new MarketplaceSyncService([client, prom], secrets, cache, new Clock());
        var partial = await service.SynchronizeAsync(config, Range, []);
        Equal(1, client.FetchCount); Equal(1, prom.FetchCount); Equal(3, partial.Orders.Count);
        foreach (var state in partial.States.Where(s => s.ConnectionId != PromId))
        {
            True(!state.Complete); Equal(oldStates.Single(s => s.ConnectionId == state.ConnectionId).LastSuccessUtc, state.LastSuccessUtc);
        }
        True(partial.States.Single(s => s.ConnectionId == PromId).Complete);
        client.Fetch = (_, _) => throw new MarketplaceApiException("Rozetka: HTTP 429.", (HttpStatusCode)429, TimeSpan.FromMinutes(2));
        var failed = await service.SynchronizeAsync(config, Range, []);
        Equal(2, client.FetchCount); Equal(3, failed.Orders.Count);
        foreach (var state in failed.States.Where(s => s.ConnectionId != PromId))
        {
            True(!state.Complete); Equal((HttpStatusCode)429, state.FailureStatus);
            Equal(TimeSpan.FromMinutes(2), state.RetryAfter);
            Equal(oldStates.Single(s => s.ConnectionId == state.ConnectionId).LastSuccessUtc, state.LastSuccessUtc);
        }
    }

    private static async Task ConsolidatedFiscalEvidenceSurvivesExpiryAsync()
    {
        foreach (var conflicting in new[] { false, true })
        {
            var directory = Path.Combine(Path.GetTempPath(), "cbp-consolidated-fiscal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                const string removedId = "90000000000000000000000000000081", unrelatedId = "90000000000000000000000000000082";
                const string oldReceipt = "11111111-1111-1111-1111-111111111111", freshReceipt = "22222222-2222-2222-2222-222222222222";
                var config = new MarketplaceSettings
                {
                    Connections = [new() { Id = RozetkaId, Marketplace = MarketplaceKind.Rozetka, Enabled = true }],
                    ConnectionAliases = [new(MarketplaceKind.Rozetka, removedId, RozetkaId)]
                };
                var oldKey = new OrderKey(MarketplaceKind.Rozetka, removedId, "42");
                var activeKey = oldKey with { ConnectionId = RozetkaId };
                var oldReference = new FiscalDocumentReference(FiscalDocumentKeyKind.CheckboxReceiptUuid,
                    oldReceipt, "historical link", oldKey, "checkbox", "same-document");
                var old = Order("42", "historical", removedId, MarketplaceKind.Rozetka) with
                {
                    RetrievedAtUtc = Now.AddDays(-29), ReceiptIds = [oldReceipt], FiscalReferences = [oldReference],
                    FiscalReceiptNumbers = ["historical-fiscal-code"],
                    FiscalReceiptUrls = ["https://check.checkbox.ua/" + oldReceipt]
                };
                var active = Order("42", "current", RozetkaId, MarketplaceKind.Rozetka) with { RetrievedAtUtc = Now.AddDays(-1) };
                var unrelated = Order("42", "other seller", unrelatedId, MarketplaceKind.Rozetka) with
                { ReceiptIds = ["33333333-3333-3333-3333-333333333333"], FiscalReceiptNumbers = ["other-seller-code"] };
                var clock = new ExpiringClock { Utc = Now };
                var path = Path.Combine(directory, "marketplace-cache.dpapi");
                var cache = new DpapiMarketplaceCacheStore(path, clock);
                await cache.SaveAsync(new([old, active, unrelated], []));
                var client = new FakeClient(MarketplaceKind.Rozetka)
                {
                    Fetch = (connection, _) =>
                    {
                        Equal(RozetkaId, connection.Id);
                        var fresh = Order("42", "fresh", connection.Id, MarketplaceKind.Rozetka) with { RetrievedAtUtc = clock.Utc };
                        if (conflicting) fresh = fresh with { ReceiptIds = [freshReceipt], FiscalReferences =
                            [oldReference with { Order = activeKey, Value = freshReceipt, Source = "fresh conflicting assertion" }] };
                        return Task.FromResult(new OrdersFetchResult([fresh], true));
                    }
                };
                var secrets = new SellerSecrets(); secrets.Values[RozetkaId] = new(Token: "synthetic-consolidated-token");
                var service = new MarketplaceSyncService([client], secrets, cache, clock);
                var first = await service.SynchronizeAsync(config, Range, [oldKey]);
                var surviving = first.Orders.Single(o => o.Key == activeKey);
                Equal("fresh", surviving.Status); Equal(conflicting ? 2 : 1, surviving.FiscalReferences.Count);
                True(surviving.FiscalReferences.All(r => r.Order == activeKey));
                True(surviving.ReceiptIds.Contains(oldReceipt));
                True(!surviving.ReceiptIds.Contains(unrelated.ReceiptIds.Single()));
                True(!surviving.FiscalReceiptNumbers.Contains("other-seller-code"));
                var retainedOld = first.Orders.Single(o => o.Key == oldKey);
                Equal(old.Status, retainedOld.Status); Equal(old.RetrievedAtUtc, retainedOld.RetrievedAtUtc);
                Equal(oldReference, retainedOld.FiscalReferences.Single());
                Equal(0, client.DetailIds.Count); Equal(1, client.FetchCount);
                True(!secrets.LoadCounts.ContainsKey(removedId));

                clock.Utc = Now.AddDays(2);
                var expired = await cache.LoadAsync(30);
                True(expired.Orders.All(o => o.Key != oldKey));
                Equal(conflicting ? 2 : 1, expired.Orders.Single(o => o.Key == activeKey).FiscalReferences.Count);
                // Reopen the protected cache and refresh again after the old alias no longer exists.
                var reopened = new DpapiMarketplaceCacheStore(path, clock);
                var next = await new MarketplaceSyncService([client], secrets, reopened, clock).SynchronizeAsync(config, Range, [oldKey]);
                surviving = next.Orders.Single(o => o.Key == activeKey);
                Equal(conflicting ? 2 : 1, surviving.FiscalReferences.Count);
                Equal(conflicting ? 2 : 1, surviving.ReceiptIds.Count);
                True(surviving.FiscalReferences.All(r => r.Order == activeKey));
                True(surviving.FiscalReceiptNumbers.Contains("historical-fiscal-code"));
                True(surviving.FiscalReceiptUrls.Contains(old.FiscalReceiptUrls.Single()));
                True(next.Orders.All(o => o.Key != oldKey)); Equal(0, client.DetailIds.Count); Equal(2, client.FetchCount);
                var receipt = new ReceiptRecord { Id = oldReceipt, Type = ReceiptTypes.Sell, Status = "DONE", FiscalDate = Now, TotalSumMinor = 10000 };
                Equal(conflicting ? ReceiptLinkState.Conflict : ReceiptLinkState.Exact,
                    new ReceiptOrderMatchingService().Match(receipt, "synthetic-cashier", [surviving], [], true).State);
            }
            finally { Directory.Delete(directory, recursive: true); }
        }
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
            (MarketplaceKind.Rozetka, HttpMethod.Get, "https://api-seller.rozetka.com.ua/orders/search"),
            (MarketplaceKind.Rozetka, HttpMethod.Get, "https://api-seller.rozetka.com.ua/orders/42"),
            (MarketplaceKind.Rozetka, HttpMethod.Get, "https://api-seller.rozetka.com.ua/prro/receipt/42?type=link"),
            (MarketplaceKind.Rozetka, HttpMethod.Get, "https://api-seller.rozetka.com.ua/prro/receipt-status/42")
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
            (MarketplaceKind.Rozetka, HttpMethod.Post, "https://api-seller.rozetka.com.ua/sites"),
            (MarketplaceKind.Rozetka, HttpMethod.Delete, "https://api-seller.rozetka.com.ua/orders/42"),
            (MarketplaceKind.Rozetka, HttpMethod.Post, "https://api-seller.rozetka.com.ua/prro/receipt/42"),
            (MarketplaceKind.Rozetka, HttpMethod.Get, "https://api-seller.rozetka.com.ua/prro/receipt/42"),
            (MarketplaceKind.Rozetka, HttpMethod.Get, "https://api-seller.rozetka.com.ua/prro/receipt/42?type=pdf"),
            (MarketplaceKind.Rozetka, HttpMethod.Get, "https://api-seller.rozetka.com.ua/prro/receipt/42?type=html"),
            (MarketplaceKind.Rozetka, HttpMethod.Get, "https://api-seller.rozetka.com.ua/prro/receipt/42?type=link&type=pdf"),
            (MarketplaceKind.Rozetka, HttpMethod.Get, "https://api-seller.rozetka.com.ua/prro/receipt/42?type=link&type=link"),
            (MarketplaceKind.Rozetka, HttpMethod.Get, "https://api-seller.rozetka.com.ua/prro/receipt/42?type="),
            (MarketplaceKind.Rozetka, HttpMethod.Get, "https://api-seller.rozetka.com.ua/prro/receipt/42?type=link&format=pdf"),
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
        Equal("{}", await transport.SendAsync(() => new(HttpMethod.Get, "https://api-seller.rozetka.com.ua/orders/search"), MarketplaceKind.Rozetka));
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
            () => new(HttpMethod.Get, "https://api-seller.rozetka.com.ua/orders/search"), MarketplaceKind.Rozetka, cancellation.Token));
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
    private sealed class ParallelCommitCache : IMarketplaceCacheStore
    {
        private MarketplaceSnapshot _value = new([], []);
        private int _active;
        public int MaximumConcurrentSaves;
        public System.Collections.Concurrent.ConcurrentQueue<MarketplaceSnapshot> Snapshots { get; } = new();
        public TaskCompletionSource FirstSaved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<MarketplaceSnapshot> LoadAsync(int retentionDays, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(_value); }
        public async Task SaveAsync(MarketplaceSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            int observed;
            do { observed = MaximumConcurrentSaves; }
            while (active > observed && Interlocked.CompareExchange(ref MaximumConcurrentSaves, active, observed) != observed);
            try
            {
                // Yield inside the commit to expose overlapping writes deterministically.
                await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
                _value = snapshot; Snapshots.Enqueue(snapshot); FirstSaved.TrySetResult();
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
    private sealed class MemorySecrets : IMarketplaceSecretStore
    {
        public Task<MarketplaceCredentials?> LoadAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MarketplaceCredentials?>(new(Token: "fake-token-" + connectionId, Login: "fake-login-" + connectionId, Password: "fake-password"));
        public Task SaveAsync(string connectionId, MarketplaceCredentials credentials, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class SellerSecrets : IMarketplaceSecretStore
    {
        public Dictionary<string, MarketplaceCredentials?> Values { get; } = new();
        public Dictionary<string, int> LoadCounts { get; } = new();
        public HashSet<string> Unreadable { get; } = new();
        public Task<MarketplaceCredentials?> LoadAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCounts[connectionId] = LoadCounts.GetValueOrDefault(connectionId) + 1;
            if (Unreadable.Contains(connectionId)) throw new InvalidOperationException("Synthetic unreadable protected credential.");
            return Task.FromResult(Values.GetValueOrDefault(connectionId));
        }
        public Task SaveAsync(string connectionId, MarketplaceCredentials credentials, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class ExpiringClock : TimeProvider
    {
        public DateTimeOffset Utc { get; set; }
        public override DateTimeOffset GetUtcNow() => Utc;
    }
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
