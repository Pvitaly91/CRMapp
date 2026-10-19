using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class HttpReadTimeoutTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("HTTP body: Checkbox JSON timeout includes content and a later request recovers", () => CheckboxBodyAsync(false)),
        ("HTTP body: Checkbox image timeout discards incomplete bytes", () => CheckboxBodyAsync(true)),
        ("HTTP body: cashier sign-in timeout saves no credentials and releases its lock", AuthenticationBodyAsync),
        ("HTTP body: Prom content timeout is bounded without an inline retry", () => MarketplaceBodyAsync(MarketplaceKind.Prom)),
        ("HTTP body: Rozetka content timeout is bounded and the next request recovers", () => MarketplaceBodyAsync(MarketplaceKind.Rozetka)),
        ("HTTP body: explicit cancellation remains cancellation for every reader", CallerCancellationAsync),
        ("HTTP body: a stalled Checkbox source releases the parallel cycle and explicit recovery works", ParallelRecoveryAsync),
        ("HTTP body: request deadline never shortens marketplace Retry-After", MarketplaceRetryAfterAsync),
        ("background lifecycle: Stop is harmless after repeated asynchronous disposal", StopAfterDisposeAsync)
    ];

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(150);
    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { Timeout = RequestTimeout };
    private static HttpResponseMessage Response(HttpContent content, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = content };
    private static HttpContent Content(StalledStream stream)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }
    private static string Url(MarketplaceKind kind) => kind == MarketplaceKind.Prom
        ? "https://my.prom.ua/api/v1/orders/list?limit=1" : "https://api-seller.rozetka.com.ua/orders/search?page=1&types=1";
    private static async Task ExpectCheckboxTimeoutAsync(Func<CancellationToken, Task> operation)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await operation(guard.Token); throw new Exception("An incomplete response was accepted."); }
        catch (ApiException error) when (error.StatusCode == HttpStatusCode.RequestTimeout && !guard.IsCancellationRequested) { }
        catch (OperationCanceledException) when (guard.IsCancellationRequested)
        { throw new Exception("Response content outlived HttpClient.Timeout."); }
    }

    private static async Task CheckboxBodyAsync(bool image)
    {
        var stream = new StalledStream();
        var handler = new Handler(n => n == 1 ? Response(Content(stream)) : Response(new StringContent("complete")));
        using var client = Client(handler);
        var api = new CheckboxApiClient(client, new Authentication(), new Logger());
        await ExpectCheckboxTimeoutAsync(async ct =>
        {
            if (image) await api.GetBytesAsync("https://checkbox.test/image", "synthetic.image", null, ct);
            else await api.GetStringAsync("https://checkbox.test/receipts", "synthetic.receipts", ct);
        });
        if (handler.Calls != 1 || !stream.Disposed) throw new Exception("Timeout retried or retained the incomplete content.");
        if (await api.GetStringAsync("https://checkbox.test/receipts", "synthetic.retry") != "complete" || handler.Calls != 2)
            throw new Exception("A finished timeout blocked the next request.");
    }

    private static async Task AuthenticationBodyAsync()
    {
        var stream = new StalledStream(); var settings = new Settings(); var credentials = new Credentials();
        var handler = new Handler(n => n == 1 ? Response(Content(stream)) : Response(new StringContent("{\"access_token\":\"synthetic-token\"}")));
        using var client = Client(handler);
        var auth = new AuthenticationService(client, settings, credentials, new Logger());
        await ExpectCheckboxTimeoutAsync(ct => auth.SignInAndStoreAsync("synthetic-login", "synthetic-password", ct));
        if (settings.Saves != 0 || credentials.Saves != 0 || !stream.Disposed) throw new Exception("Failed sign-in changed credentials or retained its body.");
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        if (await auth.GetAccessTokenAsync(cancellationToken: guard.Token) != "synthetic-token") throw new Exception("Sign-in lock was not released.");
    }

    private static async Task MarketplaceBodyAsync(MarketplaceKind kind)
    {
        var stream = new StalledStream();
        var handler = new Handler(n => n == 1 ? Response(Content(stream)) : Response(new StringContent("{}")));
        using var client = Client(handler);
        var transport = new MarketplaceHttpTransport(client);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await transport.SendAsync(() => new(HttpMethod.Get, Url(kind)), kind, guard.Token); throw new Exception("Incomplete marketplace content was accepted."); }
        catch (MarketplaceApiException error) when (!guard.IsCancellationRequested && error.Message.Contains("час очікування")) { }
        catch (OperationCanceledException) when (guard.IsCancellationRequested)
        { throw new Exception("Marketplace content outlived HttpClient.Timeout."); }
        if (handler.Calls != 1 || !stream.Disposed) throw new Exception("Body timeout retried or retained the response.");
        if (await transport.SendAsync(() => new(HttpMethod.Get, Url(kind)), kind) != "{}") throw new Exception("Marketplace did not recover.");
    }

    private static async Task CallerCancellationAsync()
    {
        foreach (var source in new[] { "checkbox", "authentication", "prom", "rozetka" })
        {
            var stream = new StalledStream();
            using var client = new HttpClient(new Handler(_ => Response(Content(stream)))) { Timeout = TimeSpan.FromSeconds(10) };
            using var cancel = new CancellationTokenSource();
            var pending = source switch
            {
                "checkbox" => new CheckboxApiClient(client, new Authentication(), new Logger()).GetStringAsync("https://checkbox.test/receipts", "synthetic", cancel.Token),
                "authentication" => new AuthenticationService(client, new Settings(), new Credentials(), new Logger()).GetAccessTokenAsync(cancellationToken: cancel.Token),
                _ => new MarketplaceHttpTransport(client).SendAsync(() => new(HttpMethod.Get, Url(source == "prom" ? MarketplaceKind.Prom : MarketplaceKind.Rozetka)),
                    source == "prom" ? MarketplaceKind.Prom : MarketplaceKind.Rozetka, cancel.Token)
            };
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancel.Cancel();
            try { await pending; throw new Exception("Caller cancellation was ignored."); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            if (!stream.Disposed) throw new Exception("Canceled response was not disposed.");
        }
    }

    private static async Task ParallelRecoveryAsync()
    {
        var handler = new Handler(n => n == 1 ? Response(Content(new StalledStream())) : Response(new StringContent("{}")));
        using var client = Client(handler);
        var api = new CheckboxApiClient(client, new Authentication(), new Logger());
        var orders = 0; var waybills = 0;
        var coordinator = new BackgroundSyncCoordinator(async ct => { await api.GetStringAsync("https://checkbox.test/receipts", "synthetic", ct); },
            _ => Task.CompletedTask, _ => Task.FromResult(new AppSettings()), parallelOrders: async (settled, ct) =>
            { orders++; await settled.WaitAsync(ct); }, waybills: _ => { waybills++; return Task.CompletedTask; });
        try
        {
            await coordinator.RefreshNowAsync().WaitAsync(TimeSpan.FromSeconds(2));
            if (orders != 1 || waybills != 1 || coordinator.States.Single(s => s.Name == "Checkbox").CooldownReason != BackgroundCooldownReason.Transient ||
                coordinator.States.Where(s => s.Name != "Checkbox").Any(s => s.Status != "Оновлено"))
                throw new Exception("A stalled receipt source blocked independent providers or became a success.");
            await coordinator.RefreshNowAsync(true).WaitAsync(TimeSpan.FromSeconds(2));
            if (orders != 2 || waybills != 2 || coordinator.States.Any(s => s.Status != "Оновлено")) throw new Exception("Explicit recovery did not complete.");
        }
        finally { coordinator.Stop(); await coordinator.DisposeAsync(); }
    }

    private static async Task MarketplaceRetryAfterAsync()
    {
        var handler = new Handler(n =>
        {
            var response = Response(new StringContent("{}"), n == 1 ? (HttpStatusCode)429 : HttpStatusCode.OK);
            if (n == 1) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return response;
        });
        using var client = Client(handler);
        TimeSpan? observed = null;
        var transport = new MarketplaceHttpTransport(client, async (wait, ct) =>
        { observed = wait; await Task.Delay(RequestTimeout * 2, ct); });
        if (await transport.SendAsync(() => new(HttpMethod.Get, Url(MarketplaceKind.Rozetka)), MarketplaceKind.Rozetka) != "{}" ||
            observed != TimeSpan.FromSeconds(1) || handler.Calls != 2) throw new Exception("A request deadline shortened server backoff.");
    }

    private static async Task StopAfterDisposeAsync()
    {
        var coordinator = new BackgroundSyncCoordinator(_ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.FromResult(new AppSettings()));
        coordinator.Stop(); coordinator.Stop();
        await coordinator.DisposeAsync(); await coordinator.DisposeAsync();
        coordinator.Stop(); coordinator.Stop();
    }

    private sealed class Handler(Func<int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(++Calls));
    }
    private sealed class StalledStream : Stream
    {
        private bool _prefix;
        public bool Disposed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_prefix) { _prefix = true; buffer.Span[0] = (byte)'{'; return 1; }
            Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class Logger : IAppLogger
    {
        public string LogDirectory => "";
        public void Info(string operation, string? receiptId = null, int? httpStatus = null, string? printStatus = null) { }
        public void Error(string operation, Exception exception, string? receiptId = null, int? httpStatus = null, string? printStatus = null) { }
    }
    private sealed class Authentication : IAuthenticationService
    {
        public bool HasStoredCredentials => true;
        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) => Task.FromResult("synthetic-token");
        public Task SignInAndStoreAsync(string login, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void InvalidateToken() { }
    }
    private sealed class Settings : ISettingsService
    {
        public int Saves;
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings { Login = "synthetic-login" });
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) { Saves++; return Task.CompletedTask; }
    }
    private sealed class Credentials : ISecureCredentialStore
    {
        public int Saves;
        public bool HasPassword => true;
        public Task<string?> LoadPasswordAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("synthetic-password");
        public Task SavePasswordAsync(string password, CancellationToken cancellationToken = default) { Saves++; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
