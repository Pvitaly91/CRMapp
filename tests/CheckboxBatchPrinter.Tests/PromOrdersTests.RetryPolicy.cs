using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static partial class PromOrdersTests
{
    private static PromOrdersClient RetryClient(HttpClient http, RetryClock clock, int pageSize = 100) =>
        new(new MarketplaceHttpTransport(http, (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }), pageSize: pageSize, timeProvider: clock);

    private static HttpResponseMessage ValidPromResponse(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.EndsWith("/list", StringComparison.Ordinal)
            ? Json(Page())
            : Json("{\"order\":{\"id\":" + request.RequestUri.Segments.Last() + ",\"price\":\"10.00\"}}");

    private static async Task PromAccountErrorPauseAsync()
    {
        foreach (var failure in new[] { "401", "403", "408", "429", "500", "network", "timeout" })
        {
            var clock = new RetryClock();
            var calls = 0;
            var fail = true;
            using var http = Http((request, _) =>
            {
                calls++;
                if (!fail) return Task.FromResult(ValidPromResponse(request));
                if (failure == "network") throw new HttpRequestException("private-network-content");
                if (failure == "timeout") throw new TaskCanceledException("private-timeout-content");
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)int.Parse(failure))
                {
                    Content = new StringContent("private-api-content")
                });
            });
            var client = RetryClient(http, clock);
            var initial = await client.FetchAsync(Connection, Credentials, Range);
            Assert(!initial.Complete && calls == 1, $"{failure}: first failure made immediate HTTP retries.");
            Assert(!initial.Message.Contains("private-", StringComparison.Ordinal)
                && !initial.Message.Contains(Credentials.Token, StringComparison.Ordinal), "Prom error leaked private data.");

            for (var i = 0; i < 20; i++)
            {
                var paused = await client.FetchAsync(Connection, Credentials, Range);
                Assert(!paused.Complete, "A paused request was reported as a complete empty list.");
                await ExpectPromFailureAsync(() => client.GetOrderAsync(Connection, Credentials, "101"));
                await ExpectPromFailureAsync(() => client.TestConnectionAsync(Connection, Credentials));
            }
            Assert(calls == 1, $"{failure}: refresh/detail/connection checks bypassed the token pause.");
            fail = false;
            clock.Advance(TimeSpan.FromMinutes(4));
            await ExpectPromFailureAsync(() => client.GetOrderAsync(Connection, Credentials, "101"));
            Assert(calls == 1, $"{failure}: the cooldown ended before five minutes.");
            clock.Advance(TimeSpan.FromMinutes(2));
            var recovered = await client.GetOrderAsync(Connection, Credentials, "101");
            Assert(calls == 2 && recovered?.Number == "101", $"{failure}: recovery after cooldown failed.");
            Assert((await client.FetchAsync(Connection, Credentials, Range)).Complete && calls == 3,
                "A successful recovery left the account permanently paused.");
        }
    }

    private static async Task PromTokenPauseIsolationAsync()
    {
        var clock = new RetryClock();
        var calls = 0;
        using var http = Http((request, _) =>
        {
            calls++;
            return Task.FromResult(request.Headers.Authorization!.Parameter == Credentials.Token
                ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("private") }
                : ValidPromResponse(request));
        });
        var client = RetryClient(http, clock);
        Assert(!(await client.FetchAsync(Connection, Credentials, Range)).Complete && calls == 1, "Missing initial denial.");
        var alias = new MarketplaceConnection
        {
            Id = "same-prom-token-alias", Marketplace = MarketplaceKind.Prom, Name = "Той самий магазин", Enabled = true
        };
        var padded = new MarketplaceCredentials("  " + Credentials.Token + "  ");
        await ExpectPromFailureAsync(() => client.GetOrderAsync(alias, padded, "101"));
        Assert(!(await client.FetchAsync(alias, padded, Range)).Complete && calls == 1,
            "Alias connection or whitespace around the same token bypassed the pause.");

        var rotated = new MarketplaceCredentials("different-synthetic-prom-token");
        var fresh = await client.GetOrderAsync(Connection, rotated, "101");
        Assert(calls == 2 && fresh?.Number == "101", "A rotated token inherited the previous token failure.");
        Assert((await client.FetchAsync(alias, rotated, Range)).Complete && calls == 3,
            "A healthy independent token was blocked by another account.");
        await ExpectPromFailureAsync(() => client.TestConnectionAsync(Connection, Credentials));
        Assert(calls == 3, "Successful requests with another token cleared the failing token's pause.");
    }

    private static async Task PromLongRetryAfterAsync()
    {
        foreach (var useDate in new[] { false, true })
        {
            var clock = new RetryClock();
            var calls = 0;
            using var http = Http((request, _) =>
            {
                if (++calls > 1) return Task.FromResult(ValidPromResponse(request));
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = useDate
                    ? new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(12))
                    : new RetryConditionHeaderValue(TimeSpan.FromMinutes(12));
                return Task.FromResult(response);
            });
            var client = RetryClient(http, clock);
            Assert(!(await client.FetchAsync(Connection, Credentials, Range)).Complete && calls == 1,
                "Retry-After caused immediate retries inside the transport.");
            clock.Advance(TimeSpan.FromMinutes(6));
            await ExpectPromFailureAsync(() => client.TestConnectionAsync(Connection, Credentials));
            Assert(calls == 1, "The five-minute client pause shortened the server's Retry-After.");
            clock.Advance(TimeSpan.FromMinutes(5));
            await ExpectPromFailureAsync(() => client.GetOrderAsync(Connection, Credentials, "101"));
            Assert(calls == 1, "The server's long Retry-After was ignored by detail reads.");
            clock.Advance(TimeSpan.FromMinutes(2));
            await client.TestConnectionAsync(Connection, Credentials);
            Assert(calls == 2, "The request did not resume after the server's Retry-After elapsed.");
        }
    }

    private static async Task PromOrderFailureIsolationAsync()
    {
        foreach (var failure in new[] { "404", "json", "missing-order", "wrong-order" })
        {
            var clock = new RetryClock();
            var calls = 0;
            var fail = true;
            using var http = Http((request, _) =>
            {
                calls++;
                if (!fail || request.RequestUri!.AbsolutePath != "/api/v1/orders/101")
                    return Task.FromResult(ValidPromResponse(request));
                return Task.FromResult(failure switch
                {
                    "404" => new HttpResponseMessage(HttpStatusCode.NotFound),
                    "json" => Json("{"),
                    "missing-order" => Json("{}"),
                    _ => Json("{\"order\":{\"id\":102}}")
                });
            });
            var client = RetryClient(http, clock);
            for (var i = 0; i < 20; i++)
                await ExpectPromFailureAsync(() => client.GetOrderAsync(Connection, Credentials, "101"));
            Assert(calls == 1, $"{failure}: repeated detail failures reached Prom again.");
            var other = await client.GetOrderAsync(Connection, Credentials, "102");
            Assert(calls == 2 && other?.Number == "102", "A single unavailable order blocked another order.");
            Assert((await client.FetchAsync(Connection, Credentials, Range)).Complete && calls == 3,
                "An order-specific failure blocked the healthy order list.");
            await ExpectPromFailureAsync(() => client.GetOrderAsync(Connection, Credentials, "101"));
            Assert(calls == 3, "A healthy order/list read erased the failed order's pause.");
            fail = false;
            clock.Advance(TimeSpan.FromMinutes(6));
            Assert((await client.GetOrderAsync(Connection, Credentials, "101"))?.Number == "101" && calls == 4,
                "A recovered order could not be read after the detail pause.");
        }
    }

    private static async Task PromListFailureKeepsPartialDataAsync()
    {
        foreach (var failure in new[] { "json", "missing-list", "repeated-page", "invalid-id" })
        {
            var clock = new RetryClock();
            var calls = 0;
            using var http = Http((request, _) =>
            {
                calls++;
                if (!request.RequestUri!.AbsolutePath.EndsWith("/list", StringComparison.Ordinal))
                    return Task.FromResult(ValidPromResponse(request));
                return Task.FromResult(Json(calls == 1 ? Page(103, 102) : failure switch
                {
                    "json" => "{",
                    "missing-list" => "{}",
                    "invalid-id" => "{\"orders\":[{\"id\":null}]}",
                    _ => Page(103, 102)
                }));
            });
            var client = RetryClient(http, clock, pageSize: 2);
            var partial = await client.FetchAsync(Connection, Credentials, Range);
            Assert(!partial.Complete && partial.Orders.Select(o => o.Number).SequenceEqual(new[] { "103", "102" })
                && calls == 2, "Failed later page erased data already received in this refresh.");
            for (var i = 0; i < 20; i++)
                Assert(!(await client.FetchAsync(Connection, Credentials, Range)).Complete,
                    "A paused malformed list was reported complete.");
            Assert(calls == 2, $"{failure}: a malformed/repeating list was downloaded on every refresh.");
            var detail = await client.GetOrderAsync(Connection, Credentials, "104");
            Assert(detail?.Number == "104" && calls == 3, "List-specific corruption blocked valid order details.");
        }
    }

    private static async Task PromLaterPageAccountFailureAsync()
    {
        var clock = new RetryClock();
        var calls = 0;
        using var http = Http((_, _) => Task.FromResult(++calls == 1
            ? Json(Page(103, 102)) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var client = RetryClient(http, clock, pageSize: 2);
        var partial = await client.FetchAsync(Connection, Credentials, Range);
        Assert(!partial.Complete && partial.Orders.Count == 2 && calls == 2,
            "A later server error retried immediately or discarded the earlier page.");
        Assert(!(await client.FetchAsync(Connection, Credentials, Range)).Complete, "Paused refresh became complete.");
        await ExpectPromFailureAsync(() => client.GetOrderAsync(Connection, Credentials, "104"));
        Assert(calls == 2, "An account failure on a later page did not pause all account reads.");
    }

    private static async Task PromCancellationDoesNotPauseAsync()
    {
        var clock = new RetryClock();
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        using var http = Http((request, ct) =>
        {
            if (++calls == 1)
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            return Task.FromResult(ValidPromResponse(request));
        });
        var client = RetryClient(http, clock);
        try
        {
            await client.GetOrderAsync(Connection, Credentials, "101", cancellation.Token);
            throw new InvalidOperationException("Caller cancellation was swallowed.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Assert((await client.GetOrderAsync(Connection, Credentials, "101"))?.Number == "101" && calls == 2,
            "Caller cancellation incorrectly installed an account/order error pause.");
        using var alreadyCanceled = new CancellationTokenSource();
        alreadyCanceled.Cancel();
        try
        {
            await client.FetchAsync(Connection, Credentials, Range, alreadyCanceled.Token);
            throw new InvalidOperationException("Pre-canceled refresh did not cancel.");
        }
        catch (OperationCanceledException) when (alreadyCanceled.IsCancellationRequested) { }
        Assert((await client.FetchAsync(Connection, Credentials, Range)).Complete && calls == 3,
            "A pre-canceled refresh changed the account cooldown state.");
    }

    private static async Task PromConcurrentFailureBurstAsync()
    {
        var clock = new RetryClock();
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = Http(async (_, ct) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        });
        var client = RetryClient(http, clock);
        var first = ExpectPromFailureAsync(() => client.GetOrderAsync(Connection, Credentials, "101"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = Enumerable.Range(102, 20).Select(id =>
            ExpectPromFailureAsync(() => client.GetOrderAsync(Connection, Credentials, id.ToString()))).ToArray();
        release.TrySetResult();
        await Task.WhenAll(queued.Append(first)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert(calls == 1, "Concurrent detail readers all sent requests before the first account failure paused them.");
    }

    private static async Task PromFailureCacheFloodAsync()
    {
        var clock = new RetryClock();
        var calls = 0;
        var fail = true;
        using var http = Http((request, _) =>
        {
            calls++;
            return Task.FromResult(fail ? new HttpResponseMessage(HttpStatusCode.NotFound) : ValidPromResponse(request));
        });
        var client = RetryClient(http, clock);
        for (var i = 1; i <= 1100; i++)
            await ExpectPromFailureAsync(() => client.GetOrderAsync(Connection, Credentials, i.ToString()));
        Assert(calls <= 1025, "An unbounded stream of missing orders bypassed failure-cache overflow protection.");
        var beforeRepeat = calls;
        for (var i = 1; i <= 50; i++)
            await ExpectPromFailureAsync(() => client.GetOrderAsync(Connection, Credentials, i.ToString()));
        Assert(calls == beforeRepeat, "Cache overflow evicted paused orders and restarted an error loop.");
        fail = false;
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert((await client.GetOrderAsync(Connection, Credentials, "2000"))?.Number == "2000" && calls == beforeRepeat + 1,
            "Failure-cache overflow prevented recovery after cooldown.");
    }

    private static async Task PromQueuedCancellationAsync()
    {
        var clock = new RetryClock();
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = Http(async (request, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            return ValidPromResponse(request);
        });
        var client = RetryClient(http, clock);
        var first = client.GetOrderAsync(Connection, Credentials, "101");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var canceled = new CancellationTokenSource();
        var waiting = client.GetOrderAsync(Connection, Credentials, "102", canceled.Token);
        canceled.Cancel();
        try
        {
            await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            throw new InvalidOperationException("A queued canceled caller did not propagate cancellation.");
        }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }

        var next = client.GetOrderAsync(Connection, Credentials, "103");
        Assert(calls == 1 && !next.IsCompleted,
            "Canceling a queued caller released the gate still owned by an in-flight request.");
        release.TrySetResult();
        var completed = await Task.WhenAll(first, next).WaitAsync(TimeSpan.FromSeconds(5));
        Assert(calls == 2 && completed.Select(order => order?.Number).SequenceEqual(new[] { "101", "103" }),
            "Queued cancellation leaked the token gate, installed a pause, or sent the canceled request.");
    }

    private static async Task PromStalledAccountIsolationAsync()
    {
        var clock = new RetryClock();
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = Http(async (request, ct) =>
        {
            Interlocked.Increment(ref calls);
            if (request.Headers.Authorization!.Parameter == Credentials.Token)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }
            return ValidPromResponse(request);
        });
        var client = RetryClient(http, clock);
        var first = ExpectPromFailureAsync(() => client.GetOrderAsync(Connection, Credentials, "101"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var independent = new MarketplaceCredentials("independent-synthetic-prom-token");
        try
        {
            var other = await client.GetOrderAsync(Connection, independent, "102").WaitAsync(TimeSpan.FromSeconds(5));
            Assert(other?.Number == "102" && calls == 2 && !first.IsCompleted,
                "A stalled token serialized or blocked a different account.");
        }
        finally { release.TrySetResult(); }
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert((await client.GetOrderAsync(Connection, independent, "103"))?.Number == "103" && calls == 3,
            "The first token's eventual failure paused the independent token.");
    }

    private static async Task PromAccountCacheFloodAsync()
    {
        var clock = new RetryClock();
        var calls = 0;
        var fail = true;
        using var http = Http((request, _) =>
        {
            calls++;
            return Task.FromResult(fail ? new HttpResponseMessage(HttpStatusCode.Forbidden) : ValidPromResponse(request));
        });
        var client = RetryClient(http, clock);
        for (var i = 0; i < 1100; i++)
            await ExpectPromFailureAsync(() => client.GetOrderAsync(Connection,
                new MarketplaceCredentials("synthetic-flood-token-" + i), "101"));
        Assert(calls == 1024, "The bounded account cache accepted more live failing tokens than its limit.");
        await ExpectPromFailureAsync(() => client.GetOrderAsync(Connection,
            new MarketplaceCredentials("synthetic-flood-token-0"), "102"));
        Assert(calls == 1024, "Account cache pressure evicted an active cooldown and retried its denied token.");
        fail = false;
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert((await client.GetOrderAsync(Connection, new MarketplaceCredentials("synthetic-new-token"), "102"))?.Number == "102"
            && calls == 1025, "Expired account entries were not reclaimed for a new healthy token.");
        Assert((await client.GetOrderAsync(Connection, new MarketplaceCredentials("synthetic-flood-token-0"), "103"))?.Number == "103"
            && calls == 1026, "An existing token could not recover after the account cache overflow.");
    }

    private static async Task PromSharedPauseKeepsLongOrderDeadlineAsync()
    {
        var clock = new RetryClock();
        var calls = 0;
        using var http = Http((request, _) =>
        {
            calls++;
            if (calls == 1)
            {
                var conflict = new HttpResponseMessage(HttpStatusCode.Conflict);
                conflict.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(12));
                return Task.FromResult(conflict);
            }
            return Task.FromResult(calls == 2
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : ValidPromResponse(request));
        });
        var client = RetryClient(http, clock);
        await ExpectPromFailureAsync(() => client.GetOrderAsync(Connection, Credentials, "101"));
        await ExpectPromFailureAsync(() => client.GetOrderAsync(Connection, Credentials, "102"));
        Assert(calls == 2, "An order-specific conflict blocked an independent order before the account failure.");
        clock.Advance(TimeSpan.FromMinutes(6));
        await ExpectPromFailureAsync(() => client.GetOrderAsync(Connection, Credentials, "101"));
        await ExpectPromFailureAsync(() => client.TestConnectionAsync(Connection, Credentials));
        Assert(calls == 2, "A shorter account error truncated the earlier order's longer Retry-After.");
        clock.Advance(TimeSpan.FromMinutes(5));
        await ExpectPromFailureAsync(() => client.GetOrderAsync(Connection, Credentials, "103"));
        Assert(calls == 2, "Account error promotion failed to preserve the full twelve-minute deadline.");
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert((await client.GetOrderAsync(Connection, Credentials, "101"))?.Number == "101" && calls == 3,
            "The promoted long deadline did not recover after it elapsed.");
    }

    private static async Task ExpectPromFailureAsync(Func<Task> operation)
    {
        Exception? failure = null;
        try { await operation(); }
        catch (Exception exception) { failure = exception; }
        Assert(failure is not null, "An unavailable Prom read unexpectedly succeeded.");
        Assert(!failure!.Message.Contains(Credentials.Token, StringComparison.Ordinal)
            && !failure.Message.Contains("private-", StringComparison.Ordinal), "Prom failure exposed private content.");
    }

    private sealed class RetryClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
