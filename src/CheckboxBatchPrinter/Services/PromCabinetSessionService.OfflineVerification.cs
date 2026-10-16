using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;
using Microsoft.Web.WebView2.Core;

namespace CheckboxBatchPrinter.Services;

public sealed partial class PromCabinetSessionService
{
    // Deliberately opt-in and restricted to a fresh sentinel-marked temporary folder.
    // Every browser request is intercepted locally; no cabinet, real profile or printer is used.
    public static async Task<bool> VerifyOfflineAsync(string root)
    {
        var directory = ValidateOfflineDirectory(root);
        void Stage(string value) => File.AppendAllText(Path.Combine(directory, "offline-stages.log"), DateTimeOffset.UtcNow.ToString("O") + " " + value + Environment.NewLine);
        Stage("validated temporary fixture");
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Offline browser verification requires the STA dispatcher.");
        try { _ = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (WebView2RuntimeNotFoundException) { return false; }

        const string id = "synthetic-prom-cabinet-offline";
        const string cookieName = "crmapp_offline_verification";
        const string cookieValue = "synthetic-session-only";
        var request = new PromCabinetLabelRequest(id, "433580034", "16954954", "PRM-811362308");
        var pdf = SyntheticShippingPdf.Create(pageTexts: [request.TrackingNumber]);
        var settings = new OfflineMarketplaceStore(id);
        var requestCount = 0;
        var forbiddenRequests = 0;
        var blockedIdleRequests = 0;
        var policyFailure = false;
        var mode = 200;
        var dispatcher = Dispatcher.CurrentDispatcher;

        void AttachFixture(BrowserSession session)
        {
            var core = session.Browser.CoreWebView2;
            core.NavigationStarting += (_, e) => Stage("navigation starting " + (e.Uri == PromCabinetBrowserProtocol.BridgeUri ? "bridge" : e.Uri == "about:blank" ? "blank" : "unexpected"));
            core.NavigationCompleted += (_, e) => Stage("navigation completed success=" + e.IsSuccess + " status=" + e.WebErrorStatus);
            core.WebMessageReceived += (_, _) => Stage("browser message received");
            core.WebResourceRequested += (_, e) =>
            {
                // The regular service supplies the local HTML bridge. Override every other
                // resource, including unanticipated routes, before Chromium can send it.
                if (e.Request.Uri == PromCabinetBrowserProtocol.BridgeUri && e.Request.Method == "GET") return;
                var environment = core.Environment;
                var allowedByService = e.Response is null;
                if (e.Request.Uri == request.RequestUri.AbsoluteUri && !allowedByService && session.AllowedLabelUri is null)
                { blockedIdleRequests++; return; }
                e.Response = environment.CreateWebResourceResponse(new MemoryStream(), 403, "Offline blocked", "Content-Type: text/plain");
                if (e.Request.Method != "GET" || e.Request.Uri != request.RequestUri.AbsoluteUri)
                { forbiddenRequests++; return; }
                if (!allowedByService || session.AllowedLabelUri != request.RequestUri.AbsoluteUri)
                { policyFailure = true; return; }
                requestCount++;
                Stage("synthetic label request " + requestCount + " status=" + mode);
                var content = mode == 200 ? pdf : Encoding.UTF8.GetBytes("Synthetic offline response");
                var headers = "Content-Type: " + (mode == 200 ? "application/pdf" : "application/json") +
                    "\r\nContent-Length: " + content.Length + "\r\nCache-Control: no-store" +
                    (mode == 429 ? "\r\nRetry-After: 3600" : "") +
                    (mode == 302 ? "\r\nLocation: https://my.prom.ua/login" : "");
                e.Response = environment.CreateWebResourceResponse(new MemoryStream(content, writable: false), mode, "Offline fixture", headers);
            };
        }

        using (var service = new PromCabinetSessionService(directory, settings, dispatcher))
        {
            service.WriteState(id, new(true));
            Stage("before background browser initialization");
            var browser = await service.GetBrowserAsync(id); // Uses the same background host as real label reads.
            Stage("browser initialized");
            AttachFixture(browser);
            var core = browser.Browser.CoreWebView2;
            var cookie = core.CookieManager.CreateCookie(cookieName, cookieValue, PromCabinetLabelReadOnlyContract.Host, "/");
            cookie.IsHttpOnly = true; cookie.IsSecure = true; cookie.Expires = DateTime.UtcNow.AddHours(1);
            core.CookieManager.AddOrUpdateCookie(cookie);

            Stage("before inactive request policy probe");
            await NavigateBridgeAsync(browser, CancellationToken.None);
            var idleStatus = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            void IdleProbe(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
            {
                if (e.Source != PromCabinetBrowserProtocol.BridgeUri) return;
                using var parsed = JsonDocument.Parse(e.WebMessageAsJson);
                if (parsed.RootElement.TryGetProperty("offlineIdleStatus", out var status)) idleStatus.TrySetResult(status.GetInt32());
            }
            core.WebMessageReceived += IdleProbe;
            try
            {
                await core.ExecuteScriptAsync("fetch(" + JsonSerializer.Serialize(request.RequestUri.AbsoluteUri) +
                    ",{method:'GET',credentials:'same-origin',redirect:'manual'}).then(r => window.chrome.webview.postMessage({offlineIdleStatus:r.status})).catch(() => window.chrome.webview.postMessage({offlineIdleStatus:-1}));");
                OfflineCheck(await idleStatus.Task.WaitAsync(TimeSpan.FromSeconds(5)) == 403 && blockedIdleRequests == 1 && requestCount == 0,
                    "The production request policy allowed a label read outside its current operation.");
            }
            finally { core.WebMessageReceived -= IdleProbe; }

            // Exercise the same visual-parent transition as opening and closing login.
            // The temporary window is never shown and navigates nowhere.
            var temporaryWindow = new System.Windows.Window();
            browser.BackgroundPanel.Children.Remove(browser.Browser);
            temporaryWindow.Content = browser.Browser;
            temporaryWindow.Content = null;
            browser.BackgroundPanel.Children.Add(browser.Browser);
            browser.BackgroundPanel.UpdateLayout();
            temporaryWindow.Close();

            Stage("before PDF read");
            var response = await service.ReadLabelAsync(request);
            Stage("PDF read completed");
            OfflineCheck(response.StatusCode == 200 && response.ContentType == "application/pdf" &&
                response.ConnectionId == id && response.FinalUri == request.RequestUri && response.Content.AsSpan().SequenceEqual(pdf),
                "The real browser bridge did not return the exact synthetic PDF.");
            OfflineCheck(requestCount == 1, "A successful label caused extra requests.");

            mode = 401;
            Stage("before authentication expiry read");
            await ExpectOfflineFailure(() => service.ReadLabelAsync(request), LabelFailureKind.ConnectionRequired);
            OfflineCheck(!service.ReadState(id).Configured && requestCount == 2, "Expired authentication did not clear the session marker.");
            await ExpectOfflineFailure(() => service.ReadLabelAsync(request), LabelFailureKind.ConnectionRequired);
            OfflineCheck(requestCount == 2, "An expired session repeated the cabinet request.");

            service.WriteState(id, new(true)); mode = 302;
            Stage("before login redirect read");
            await ExpectOfflineFailure(() => service.ReadLabelAsync(request), LabelFailureKind.ConnectionRequired);
            OfflineCheck(!service.ReadState(id).Configured && requestCount == 3 && forbiddenRequests == 0,
                "Login redirect was followed or failed to clear the configured session marker.");
            await ExpectOfflineFailure(() => service.ReadLabelAsync(request), LabelFailureKind.ConnectionRequired);
            OfflineCheck(requestCount == 3, "Login redirect triggered another cabinet request.");

            // The login flow saves the same existing connection; it does not make a new store.
            service.WriteState(id, new(true));
            mode = 429;
            Stage("before rate limited read");
            await ExpectOfflineFailure(() => service.ReadLabelAsync(request), LabelFailureKind.Temporary);
            OfflineCheck(service.ReadState(id).RetryAfterUtc > DateTimeOffset.UtcNow.AddMinutes(50) && requestCount == 4,
                "HTTP429 did not preserve the server cooldown.");
            await ExpectOfflineFailure(() => service.ReadLabelAsync(request), LabelFailureKind.Temporary);
            OfflineCheck(requestCount == 4, "A paused store repeated the cabinet request.");

            service.WriteState(id, new(true)); mode = 200;
            Stage("before reconnect read");
            response = await service.ReadLabelAsync(request);
            OfflineCheck(response.Content.AsSpan().SequenceEqual(pdf) && requestCount == 5,
                "Reconnecting the same store did not recover its label access.");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await service.ReadLabelAsync(request, canceled.Token); throw new InvalidOperationException("Canceled browser read was accepted."); }
            catch (OperationCanceledException) { }
            OfflineCheck(requestCount == 5, "Canceled browser read contacted the cabinet.");
        }

        // Recreate the production service and browser controller from only their saved profile.
        // This checks browser-managed persistent cookies without exporting them into app settings.
        using (var restarted = new PromCabinetSessionService(directory, settings, dispatcher))
        {
            Stage("before restarted browser initialization");
            OfflineCheck(restarted.ReadState(id).Configured, "Saved session configuration disappeared after service restart.");
            var browser = await restarted.GetBrowserAsync(id); AttachFixture(browser);
            Stage("restarted browser initialized");
            var cookies = await browser.Browser.CoreWebView2.CookieManager.GetCookiesAsync("https://my.prom.ua/");
            OfflineCheck(cookies.Count(c => c.Name == cookieName && c.Value == cookieValue) == 1,
                "The isolated browser profile did not retain its synthetic persistent session cookie.");
            var response = await restarted.ReadLabelAsync(request);
            OfflineCheck(response.Content.AsSpan().SequenceEqual(pdf) && requestCount == 6,
                "Restored browser session did not return the exact synthetic PDF.");
            OfflineCheck(Directory.EnumerateDirectories(directory).Count() == 1 && settings.Configuration.Connections.Count == 1,
                "Reconnect or restart created duplicate profiles or marketplace connections.");
        }
        OfflineCheck(forbiddenRequests == 0 && !policyFailure, "The minimal bridge attempted an unexpected resource request or bypassed its operation policy (all were blocked offline).");
        Stage("offline verification passed");
        return true;
    }

    private static string ValidateOfflineDirectory(string root)
    {
        var directory = Path.GetFullPath(root);
        var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = temporary + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(directory).StartsWith("cbp-prom-browser-", StringComparison.Ordinal) ||
            !Directory.Exists(directory))
            throw new InvalidOperationException("Offline browser verification requires its own existing cbp-prom-browser-* temporary directory.");
        var sentinel = Path.Combine(directory, "allow-offline-verification");
        if (!File.Exists(sentinel) || Directory.EnumerateFileSystemEntries(directory).Any(p => p != sentinel) ||
            (File.GetAttributes(sentinel) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Offline browser verification requires a fresh directory containing only allow-offline-verification.");
        for (var item = new DirectoryInfo(directory); item is not null && item.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase); item = item.Parent)
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Offline verification cannot use a redirected directory.");
        return directory;
    }

    private static async Task ExpectOfflineFailure(Func<Task> action, LabelFailureKind kind)
    {
        try { await action(); throw new InvalidOperationException("The synthetic browser failure was not rejected."); }
        catch (ShippingLabelException ex) when (ex.Kind == kind && ex.AppliesToBatch) { }
    }
    private static void OfflineCheck(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private sealed class OfflineMarketplaceStore(string id) : IMarketplaceSettingsStore
    {
        public MarketplaceSettings Configuration { get; } = new()
        { Connections = [new() { Id = id, Marketplace = MarketplaceKind.Prom, Enabled = true, Name = "Synthetic offline store" }] };
        public Task<MarketplaceSettings> LoadAsync(CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(Configuration); }
        public Task SaveAsync(MarketplaceSettings value, CancellationToken ct = default) =>
            throw new InvalidOperationException("Offline label verification must not change marketplace connections.");
    }
}
