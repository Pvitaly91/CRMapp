using System.Net;
using System.Net.Http;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static partial class FiscalLinkTests
{
    private static MarketplaceOrder CacheOrder(string code = Code) => new()
    {
        Key = ConnectionKey, Number = "100", FiscalReferences =
            CheckboxReceiptReference.FromRozetka(ConnectionKey, code, "checkbox", null)
    };
    private static OrderKey ConnectionKey => new(MarketplaceKind.Rozetka, Connection.Id, "100");

    private static async Task LookupSessionCacheAsync()
    {
        var clock = new LookupClock(); var settings = new Settings();
        var account = PrintAccountContext.Create(await settings.LoadAsync()); var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return ReceiptPage(Receipt()); }));
        var verifier = new FiscalReferenceVerifier(new CheckboxApiClient(http, new Auth(), new Logger()), settings, clock);
        var order = CacheOrder();
        for (var i = 0; i < 5; i++)
            Equal(ReceiptId, (await verifier.VerifyAsync([order], account)).Single().FiscalReferences.Single().VerifiedReceiptId);
        Equal(1, calls);
        clock.Utc = clock.Utc.AddMinutes(9);
        await verifier.VerifyAsync([order], account); Equal(1, calls);
        var mismatch = order with { FiscalReferences = order.FiscalReferences.Select(r => r with { OrganizationId = OtherId }).ToArray() };
        True((await verifier.VerifyAsync([mismatch], account)).Single().FiscalReferences.All(r => r.VerifiedReceiptId.Length == 0));
        Equal(1, calls); // Cached response still has to satisfy each reference's assertions.
        settings.Login = "second-cashier";
        await verifier.VerifyAsync([order], PrintAccountContext.Create(await settings.LoadAsync())); Equal(2, calls);
        settings.Login = "synthetic"; clock.Utc = clock.Utc.AddMinutes(2);
        await verifier.VerifyAsync([order], account); Equal(3, calls);
    }

    private static async Task LookupNegativeCacheAsync()
    {
        var clock = new LookupClock(); var settings = new Settings();
        var account = PrintAccountContext.Create(await settings.LoadAsync()); var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.NotFound); }));
        var verifier = new FiscalReferenceVerifier(new CheckboxApiClient(http, new Auth(), new Logger()), settings, clock);
        await verifier.VerifyAsync([CacheOrder(), CacheOrder()], account);
        await verifier.VerifyAsync([CacheOrder()], account); Equal(1, calls);
        await verifier.VerifyAsync([CacheOrder("OTHER-CODE_")], account); Equal(2, calls);
        clock.Utc = clock.Utc.AddMinutes(5);
        await verifier.VerifyAsync([CacheOrder()], account); Equal(3, calls);
    }

    private static async Task LookupAccountPauseAsync()
    {
        var clock = new LookupClock(); var settings = new Settings();
        var account = PrintAccountContext.Create(await settings.LoadAsync()); var authCalls = 0; var httpCalls = 0;
        var auth = new LookupAuth(_ =>
        {
            authCalls++;
            if (authCalls == 1) throw new ApiException("synthetic rate limit", (HttpStatusCode)429, retryAfter: TimeSpan.FromMinutes(12));
            return Task.FromResult("synthetic-access");
        });
        using var http = new HttpClient(new Handler(_ => { httpCalls++; return ReceiptPage(Receipt()); }));
        var verifier = new FiscalReferenceVerifier(new CheckboxApiClient(http, auth, new Logger()), settings, clock);
        await verifier.VerifyAsync([CacheOrder(), CacheOrder("OTHER-CODE_")], account);
        clock.Utc = clock.Utc.AddMinutes(10);
        await verifier.VerifyAsync([CacheOrder("THIRD-CODE_")], account); Equal(1, authCalls); Equal(0, httpCalls);
        settings.Login = "unrelated-cashier";
        await verifier.VerifyAsync([CacheOrder()], PrintAccountContext.Create(await settings.LoadAsync())); Equal(2, authCalls);
        settings.Login = "synthetic"; clock.Utc = clock.Utc.AddMinutes(2);
        await verifier.VerifyAsync([CacheOrder()], account); Equal(3, authCalls); Equal(2, httpCalls);
    }

    private static async Task LookupCancellationAndConcurrentCacheAsync()
    {
        var settings = new Settings(); var account = PrintAccountContext.Create(await settings.LoadAsync());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return ReceiptPage(Receipt()); }));
        var auth = new LookupAuth(async token => { entered.TrySetResult(); return await release.Task.WaitAsync(token); });
        var verifier = new FiscalReferenceVerifier(new CheckboxApiClient(http, auth, new Logger()), settings);
        using var activeCancel = new CancellationTokenSource(); using var queuedCancel = new CancellationTokenSource();
        var first = verifier.VerifyAsync([CacheOrder()], account, activeCancel.Token); await entered.Task;
        var queued = verifier.VerifyAsync([CacheOrder()], account, queuedCancel.Token);
        queuedCancel.Cancel();
        try { await queued; throw new Exception("Queued lookup should cancel"); } catch (OperationCanceledException) { }
        activeCancel.Cancel();
        try { await first; throw new Exception("Active lookup should cancel"); } catch (OperationCanceledException) { }
        Equal(0, calls);
        release.SetResult("synthetic-access");
        var results = await Task.WhenAll(verifier.VerifyAsync([CacheOrder()], account), verifier.VerifyAsync([CacheOrder()], account));
        Equal(1, calls); True(results.All(result => result.Single().FiscalReferences.Single().VerifiedReceiptId == ReceiptId));
    }

    private static async Task LookupAccountChangeEvictsProofAsync()
    {
        var settings = new Settings(); var account = PrintAccountContext.Create(await settings.LoadAsync()); var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            calls++; if (calls == 1) settings.Login = "new-cashier-during-request";
            return ReceiptPage(Receipt());
        }));
        var verifier = new FiscalReferenceVerifier(new CheckboxApiClient(http, new Auth(), new Logger()), settings);
        True((await verifier.VerifyAsync([CacheOrder()], account)).Single().FiscalReferences.Single().VerifiedReceiptId.Length == 0);
        settings.Login = "synthetic";
        Equal(ReceiptId, (await verifier.VerifyAsync([CacheOrder()], account)).Single().FiscalReferences.Single().VerifiedReceiptId);
        Equal(2, calls);
        await LookupCanceledAccountChangeEvictsProofAsync();
        await LookupCancellationKeepsCooldownAsync();
    }

    private static async Task LookupCanceledAccountChangeEvictsProofAsync()
    {
        foreach (var changeAccount in new[] { true, false })
        {
            var settings = new Settings(); var account = PrintAccountContext.Create(await settings.LoadAsync());
            var calls = 0;
            using var stop = new CancellationTokenSource();
            using var http = new HttpClient(new Handler(_ =>
            {
                calls++;
                if (calls == 1 && changeAccount) settings.Login = "new-cashier-before-cancellation";
                return ReceiptPage(Receipt());
            }));
            var verifier = new FiscalReferenceVerifier(new CheckboxApiClient(http, new Auth(), new Logger()), settings);
            // Cancel only after the first result entered the session cache. Cancelling
            // inside HTTP would already be rejected by the cache's own token check.
            var orders = new BetweenOrders(CacheOrder(), CacheOrder("OTHER-CODE_"), stop.Cancel);
            try { await verifier.VerifyAsync(orders, account, stop.Token); throw new Exception("Verification should cancel between orders"); }
            catch (OperationCanceledException) { }
            Equal(1, calls);
            settings.Login = "synthetic";
            Equal(ReceiptId, (await verifier.VerifyAsync([CacheOrder()], account)).Single().FiscalReferences.Single().VerifiedReceiptId);
            Equal(changeAccount ? 2 : 1, calls);
        }
    }

    private static async Task LookupCancellationKeepsCooldownAsync()
    {
        var settings = new Settings(); var account = PrintAccountContext.Create(await settings.LoadAsync()); var calls = 0;
        using var stop = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(_ => { calls++; return new(HttpStatusCode.Forbidden); }));
        var verifier = new FiscalReferenceVerifier(new CheckboxApiClient(http, new Auth(), new Logger()), settings);
        var orders = new BetweenOrders(CacheOrder(), CacheOrder("OTHER-CODE_"), stop.Cancel);
        try { await verifier.VerifyAsync(orders, account, stop.Token); throw new Exception("Verification should cancel between orders"); }
        catch (OperationCanceledException) { }
        Equal(2, calls); // CheckboxApiClient refreshes authentication once after HTTP 403.
        var result = await verifier.VerifyAsync([CacheOrder("OTHER-CODE_")], account);
        Equal(2, calls); // The next distinct code still shares the existing account cooldown.
        True(result.Single().FiscalReferences.All(reference => reference.VerifiedReceiptId.Length == 0));
    }

    private sealed class BetweenOrders(MarketplaceOrder first, MarketplaceOrder second, Action next) : IReadOnlyList<MarketplaceOrder>
    {
        public int Count => 2;
        public MarketplaceOrder this[int index] => index switch { 0 => first, 1 => second, _ => throw new ArgumentOutOfRangeException(nameof(index)) };
        public IEnumerator<MarketplaceOrder> GetEnumerator()
        {
            yield return first;
            next();
            yield return second;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class LookupClock : TimeProvider
    {
        public DateTimeOffset Utc = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
    }
    private sealed class LookupAuth(Func<CancellationToken, Task<string>> get) : IAuthenticationService
    {
        public bool HasStoredCredentials => true;
        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) => get(cancellationToken);
        public void InvalidateToken() { }
        public Task SignInAndStoreAsync(string login, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
