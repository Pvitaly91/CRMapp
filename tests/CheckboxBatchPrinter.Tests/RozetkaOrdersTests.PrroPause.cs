using System.Net;
using System.Net.Http;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static partial class RozetkaOrdersTests
{
    private static async Task FiscalPauseLargeRangeAsync()
    {
        foreach (var unsupported in new[] { false, true })
        {
            var requestsPerProbe = unsupported ? 1 : 3;
            var clock = new ManualClock();
            var fiscalReads = 0;
            var listReads = 0;
            var phase = 0;
            var handler = new Handler(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/orders/search")
                {
                    listReads++;
                    var page = int.Parse(request.RequestUri.Query.TrimStart('?').Split('&')
                        .Single(parameter => parameter.StartsWith("page=", StringComparison.Ordinal))[5..]);
                    return Task.FromResult(Page(page, 52, Enumerable.Range(100, 1025).Skip((page - 1) * 20).Take(20)
                        .Select(id => FiscalRow(id, changed: phase == 0 ? "2026-09-23 11:00:00" : "2026-09-23 12:00:00",
                            amount: phase == 0 ? "100.10" : "200.20")).ToArray()));
                }
                Equal("?type=link", request.RequestUri.Query);
                fiscalReads++;
                return Task.FromResult(unsupported
                    ? Json(new { success = false, errors = new { code = 15005 } })
                    : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            });
            using var http = new HttpClient(handler);
            var client = new RozetkaOrdersClient(new MarketplaceHttpTransport(http, (_, _) => Task.CompletedTask), clock);
            for (var refresh = 0; refresh < 3; refresh++)
            {
                var result = await client.FetchAsync(Connection, Credentials, Range);
                True(result.Complete); Equal(1025, result.Orders.Count);
                True(result.Orders.All(order => order.Total == (phase == 0 ? 100.10m : 200.20m)));
                True(result.Orders.All(order => order.FiscalReceiptUrls.Count == 0 && order.FiscalDataStatus.Length > 0));
                Equal(requestsPerProbe, fiscalReads); phase++;
            }
            Equal(3 * 52, listReads);
            // A different connection key for the same token cannot restart the failing PRRO loop.
            var alias = new MarketplaceConnection { Id = "alias", Marketplace = MarketplaceKind.Rozetka };
            await client.FetchAsync(alias, Credentials, Range); Equal(requestsPerProbe, fiscalReads);
            clock.Advance((unsupported ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(5)) - TimeSpan.FromSeconds(1));
            await client.FetchAsync(Connection, Credentials, Range); Equal(requestsPerProbe, fiscalReads);
            clock.Advance(TimeSpan.FromSeconds(1));
            await client.FetchAsync(Connection, Credentials, Range); Equal(requestsPerProbe * 2, fiscalReads);
            await client.FetchAsync(Connection, Credentials with { Token = "other-synthetic-account" }, Range);
            Equal(requestsPerProbe * 3, fiscalReads);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            var cancellationObserved = false;
            try { await client.FetchAsync(Connection, Credentials, Range, canceled.Token); }
            catch (OperationCanceledException) { cancellationObserved = true; }
            True(cancellationObserved); Equal(requestsPerProbe * 3, fiscalReads);
        }
    }

    private static async Task FiscalPausePreservesEvidenceAsync()
    {
        var clock = new ManualClock();
        var reads = 0;
        var phase = 0;
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/orders/search")
                return Task.FromResult(Page(1, 1, phase == 0 ? new[] { FiscalRow() } :
                    new[] { FiscalRow(101), FiscalRow(code: phase >= 2 ? "CHANGED-ASSERTION" : "FISCAL-TEST") }));
            reads++;
            return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("/101") && phase < 3
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : Json(new { success = true, content = new { url = FiscalUuidUrl } }));
        });
        using var http = new HttpClient(handler);
        var client = new RozetkaOrdersClient(new MarketplaceHttpTransport(http), clock);
        var first = (await client.FetchAsync(Connection, Credentials, Range)).Orders.Single();
        Equal(FiscalUuidUrl, first.FiscalReceiptUrls.Single()); Equal(1, reads);
        phase = 1;
        var paused = (await client.FetchAsync(Connection, Credentials, Range)).Orders.Single(order => order.Number == "100");
        Equal(FiscalUuidUrl, paused.FiscalReceiptUrls.Single()); Equal(2, reads);
        phase = 2;
        var changed = (await client.FetchAsync(Connection, Credentials, Range)).Orders.Single(order => order.Number == "100");
        Equal(0, changed.FiscalReceiptUrls.Count); Equal(2, reads);
        phase = 3; clock.Advance(TimeSpan.FromMinutes(5));
        var recovered = await client.FetchAsync(Connection, Credentials, Range);
        True(recovered.Orders.All(order => order.FiscalReceiptUrls.SequenceEqual(new[] { FiscalUuidUrl })));
        Equal(4, reads);
    }

    private static async Task FiscalOrderFailureDoesNotStarveAsync()
    {
        var reads = 0;
        var clock = new ManualClock();
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/orders/search")
                return Task.FromResult(Page(1, 1, FiscalRow(100), FiscalRow(101)));
            reads++;
            return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("/100")
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Json(new { success = true, content = new { url = FiscalUuidUrl } }));
        });
        using var http = new HttpClient(handler);
        var client = new RozetkaOrdersClient(new MarketplaceHttpTransport(http), clock);
        for (var refresh = 0; refresh < 2; refresh++)
        {
            var orders = (await client.FetchAsync(Connection, Credentials, Range)).Orders;
            Equal(0, orders.Single(order => order.Number == "100").FiscalReceiptUrls.Count);
            Equal(FiscalUuidUrl, orders.Single(order => order.Number == "101").FiscalReceiptUrls.Single());
            Equal(2, reads);
        }
        clock.Advance(TimeSpan.FromMinutes(5));
        await client.FetchAsync(Connection, Credentials, Range); Equal(3, reads);
    }

    private static async Task FiscalNegativeOverflowAsync()
    {
        var reads = 0;
        var clock = new ManualClock();
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/orders/search")
            {
                var page = int.Parse(request.RequestUri.Query.TrimStart('?').Split('&')
                    .Single(parameter => parameter.StartsWith("page=", StringComparison.Ordinal))[5..]);
                return Task.FromResult(Page(page, 52, Enumerable.Range(100, 1025).Skip((page - 1) * 20).Take(20)
                    .Select(id => FiscalRow(id)).ToArray()));
            }
            reads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        using var http = new HttpClient(handler);
        var client = new RozetkaOrdersClient(new MarketplaceHttpTransport(http), clock);
        for (var refresh = 0; refresh < 3; refresh++)
        {
            var result = await client.FetchAsync(Connection, Credentials, Range);
            True(result.Complete); Equal(1025, result.Orders.Count); Equal(1025, reads);
        }
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        await client.FetchAsync(Connection, Credentials, Range); Equal(1025, reads);
    }

    private static async Task FiscalServerRetryAfterAsync()
    {
        var reads = 0;
        var clock = new ManualClock();
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/orders/search") return Task.FromResult(Page(1, 1, FiscalRow()));
            reads++;
            var response = new HttpResponseMessage((HttpStatusCode)429);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(15));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var client = new RozetkaOrdersClient(new MarketplaceHttpTransport(http, (_, _) => Task.CompletedTask), clock);
        await client.FetchAsync(Connection, Credentials, Range); Equal(3, reads);
        clock.Advance(TimeSpan.FromMinutes(5));
        await client.FetchAsync(Connection, Credentials, Range); Equal(3, reads);
        clock.Advance(TimeSpan.FromMinutes(10));
        await client.FetchAsync(Connection, Credentials, Range); Equal(6, reads);
    }
}
