using System.Text;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private const string CabinetProtocolNonce = "0123456789abcdef0123456789abcdef";
    private static PromCabinetLabelRequest CabinetProtocolRequest() => new("synthetic-prom-store", "433580034", "16954954", "PRM-811362308");

    private static Task PromCabinetProtocolOriginAsync()
    {
        foreach (var url in new[] { "https://prom.ua/login", "https://my.prom.ua/cms", "https://my.prom.ua/cms/orders/433580034" })
            True(PromCabinetBrowserProtocol.IsLoginNavigation(new(url)));
        foreach (var url in new[] { "http://my.prom.ua/cms", "https://my.prom.ua.evil.invalid/cms", "https://notprom.ua/cms",
                     "https://prom.ua@evil.invalid/cms", "https://user@my.prom.ua/cms", "https://my.prom.ua:8443/cms", "file:///tmp/login.html" })
            True(!PromCabinetBrowserProtocol.IsLoginNavigation(new(url)), url);
        True(!PromCabinetBrowserProtocol.IsLoginNavigation(new("/cms", UriKind.Relative)));
        foreach (var path in new[] { "/", "/cms", "/cms/orders" })
            True(PromCabinetBrowserProtocol.IsCabinetPage(new("https://my.prom.ua" + path)));
        foreach (var url in new[] { "https://prom.ua/cms", "https://my.prom.ua/login", "https://my.prom.ua/cms-evil", PromCabinetBrowserProtocol.BridgeUri })
            True(!PromCabinetBrowserProtocol.IsCabinetPage(new(url)), url);

        var request = CabinetProtocolRequest();
        var message = CabinetProtocolMessage(request);
        foreach (var source in new[] { "https://my.prom.ua/cms", "https://my.prom.ua/_crmapp_session_bridge/", "https://my.prom.ua/_crmapp_session_bridge?x=1",
                     "https://my.prom.ua/_crmapp_session_bridge#fragment", "https://my.prom.ua.evil.invalid/_crmapp_session_bridge", "about:blank" })
        {
            True(PromCabinetBrowserProtocol.Parse(source, message, CabinetProtocolNonce, request) is null);
            True(PromCabinetBrowserProtocol.Parse(source, "not-json", CabinetProtocolNonce, request) is null,
                "Messages outside the private bridge must be ignored before reading their payload.");
        }
        True(PromCabinetBrowserProtocol.Parse(PromCabinetBrowserProtocol.BridgeUri, message, new string('f', 32), request) is null);
        True(PromCabinetBrowserProtocol.Parse(PromCabinetBrowserProtocol.BridgeUri, "{}", CabinetProtocolNonce, request) is null);
        True(PromCabinetBrowserProtocol.Parse(PromCabinetBrowserProtocol.BridgeUri, "[]", CabinetProtocolNonce, request) is null);
        return Task.CompletedTask;
    }

    private static Task PromCabinetProtocolResponseAsync()
    {
        var request = CabinetProtocolRequest();
        var result = PromCabinetBrowserProtocol.Parse(PromCabinetBrowserProtocol.BridgeUri,
            CabinetProtocolMessage(request), CabinetProtocolNonce, request)!;
        Equal(request.ConnectionId, result.Response.ConnectionId);
        Equal(request.RequestUri, result.Response.RequestUri); Equal(request.RequestUri, result.Response.FinalUri);
        Equal(200, result.Response.StatusCode); Equal("application/pdf", result.Response.ContentType);
        True(result.Response.Content.SequenceEqual(Encoding.ASCII.GetBytes("%PDF-synthetic")));
        var rateLimited = PromCabinetBrowserProtocol.Parse(PromCabinetBrowserProtocol.BridgeUri,
            CabinetProtocolMessage(request, status: 429, content: "", retryAfter: "120"), CabinetProtocolNonce, request)!;
        Equal(429, rateLimited.Response.StatusCode); Equal("120", rateLimited.RetryAfter); Equal(0, rateLimited.Response.Content.Length);

        foreach (var url in new[] { "https://evil.invalid/document", request.RequestUri.AbsoluteUri + "&extra=1",
                     request.RequestUri.AbsoluteUri.Replace("433580034", "433580035"),
                     request.RequestUri.AbsoluteUri.Replace("16954954", "16954955"), "https://my.prom.ua/login" })
            CabinetProtocolRejected(() => PromCabinetBrowserProtocol.Parse(PromCabinetBrowserProtocol.BridgeUri,
                CabinetProtocolMessage(request, url: url), CabinetProtocolNonce, request));
        foreach (var status in new[] { -1, 0, 99, 600, int.MaxValue })
            CabinetProtocolRejected(() => PromCabinetBrowserProtocol.Parse(PromCabinetBrowserProtocol.BridgeUri,
                CabinetProtocolMessage(request, status: status), CabinetProtocolNonce, request));
        return Task.CompletedTask;
    }

    private static Task PromCabinetProtocolMalformedAsync()
    {
        var request = CabinetProtocolRequest();
        foreach (var json in new[] { "{", "{\"nonce\":123}", "{\"nonce\":\"" + CabinetProtocolNonce + "\"}",
                     CabinetProtocolMessage(request, content: "not-base64!"),
                     CabinetProtocolMessage(request).Replace("\"status\":200", "\"status\":\"200\""),
                     CabinetProtocolMessage(request).Replace("\"type\":\"application/pdf\"", "\"type\":[]"),
                     CabinetProtocolMessage(request).Replace("\"url\":", "\"missingUrl\":") })
            CabinetProtocolRejected(() => PromCabinetBrowserProtocol.Parse(PromCabinetBrowserProtocol.BridgeUri,
                json, CabinetProtocolNonce, request));
        var tooLarge = new string('x', (int)(PromCabinetLabelReadOnlyContract.MaximumBytes * 4L / 3 + 4097));
        CabinetProtocolRejected(() => PromCabinetBrowserProtocol.Parse(PromCabinetBrowserProtocol.BridgeUri,
            tooLarge, CabinetProtocolNonce, request));
        foreach (var error in new[] { "size", "timeout", "network", "login" })
        {
            ShippingLabelException? failure = null;
            try { PromCabinetBrowserProtocol.Parse(PromCabinetBrowserProtocol.BridgeUri,
                JsonSerializer.Serialize(new { nonce = CabinetProtocolNonce, error }), CabinetProtocolNonce, request); }
            catch (ShippingLabelException ex) { failure = ex; }
            True(failure is { AppliesToBatch: true });
            Equal(error == "size" ? LabelFailureKind.InvalidDocument : error == "login" ?
                LabelFailureKind.ConnectionRequired : LabelFailureKind.Temporary, failure!.Kind);
            Equal(error == "login", failure.MissingCredentials);
        }
        return Task.CompletedTask;
    }

    private static Task PromCabinetProtocolScriptAsync()
    {
        var request = CabinetProtocolRequest();
        var script = PromCabinetBrowserProtocol.DownloadScript(request, CabinetProtocolNonce);
        True(script.Contains("const url = " + JsonSerializer.Serialize(request.RequestUri.AbsoluteUri) + ";"));
        True(script.Contains("const nonce = " + JsonSerializer.Serialize(CabinetProtocolNonce) + ";"));
        True(script.Contains("credentials:'same-origin'") && script.Contains("redirect:'manual'") && script.Contains("method:'GET'"));
        True(script.Contains("response.type === 'opaqueredirect'") && script.Contains("error:'login'"));
        True(script.Contains("new AbortController()") && script.Contains("if (length > limit)") && script.Contains("type === 'application/pdf'"));
        True(!script.Contains(request.ConnectionId) && !script.Contains(request.TrackingNumber) && !script.Contains("Bearer") && !script.Contains("document.cookie"));
        foreach (var nonce in new[] { "", new string('a', 31), new string('a', 33), new string('g', 32), "';window.alert('not a nonce'); //" })
            CabinetProtocolRejected(() => PromCabinetBrowserProtocol.DownloadScript(request, nonce));
        foreach (var invalid in new[] { request with { OrderId = "433580034&order_id=1" },
                     request with { DeliveryOptionId = "16954954';alert(1);//" }, request with { ConnectionId = "" } })
            CabinetProtocolRejected(() => PromCabinetBrowserProtocol.DownloadScript(invalid, CabinetProtocolNonce));
        return Task.CompletedTask;
    }

    private static string CabinetProtocolMessage(PromCabinetLabelRequest request, string? url = null, int status = 200,
        string? content = null, string retryAfter = "") => JsonSerializer.Serialize(new
        {
            nonce = CabinetProtocolNonce, status, type = "application/pdf", url = url ?? request.RequestUri.AbsoluteUri,
            retryAfter, content = content ?? Convert.ToBase64String(Encoding.ASCII.GetBytes("%PDF-synthetic"))
        });
    private static void CabinetProtocolRejected(Action action)
    {
        var rejected = false;
        try { action(); }
        catch (Exception ex) when (ex is ShippingLabelException or JsonException or FormatException or InvalidOperationException or
                                      KeyNotFoundException or ArgumentException) { rejected = true; }
        True(rejected, "Malformed or mismatched browser messages must not produce a label response or executable download script.");
    }
}
