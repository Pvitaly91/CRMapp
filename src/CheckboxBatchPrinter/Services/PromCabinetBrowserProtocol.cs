using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Services;

// Only the application's minimal intercepted page can complete a pending download.
// Cookies stay inside WebView2; neither messages nor application settings contain them.
public static class PromCabinetBrowserProtocol
{
    public const string BridgeUri = "https://my.prom.ua/_crmapp_session_bridge";
    public const string BridgeHtml = "<!doctype html><html><head><meta charset='utf-8'><link rel='icon' href='data:,'><title>CRMapp</title></head><body></body></html>";
    public static bool IsLoginNavigation(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        (uri.Host == "prom.ua" || uri.Host.EndsWith(".prom.ua", StringComparison.Ordinal));
    public static bool IsCabinetPage(Uri uri) => IsLoginNavigation(uri) && uri.Host == "my.prom.ua" &&
        (uri.AbsolutePath == "/" || uri.AbsolutePath == "/cms" || uri.AbsolutePath.StartsWith("/cms/", StringComparison.Ordinal));

    public static string DownloadScript(PromCabinetLabelRequest request, string nonce)
    {
        var uri = request.RequestUri;
        if (nonce.Length != 32 || nonce.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Invalid operation identity.");
        return $$"""
            (() => {
              const nonce = {{JsonSerializer.Serialize(nonce)}};
              const url = {{JsonSerializer.Serialize(uri.AbsoluteUri)}};
              const limit = {{PromCabinetLabelReadOnlyContract.MaximumBytes}};
              const controller = new AbortController();
              window.crmappAbort = () => controller.abort();
              const timer = setTimeout(() => controller.abort(), 45000);
              (async () => {
                try {
                  const response = await fetch(url, {method:'GET', credentials:'same-origin', redirect:'manual',
                    cache:'no-store', headers:{'Accept':'application/pdf'}, signal:controller.signal});
                  if (response.type === 'opaqueredirect') {
                    window.chrome.webview.postMessage({nonce, error:'login'}); return;
                  }
                  const type = (response.headers.get('content-type') || '').split(';')[0].trim().toLowerCase();
                  const retryAfter = response.headers.get('retry-after') || '';
                  const result = {nonce, status:response.status, type, url:response.url, retryAfter, content:''};
                  if (response.url !== url) throw new Error('redirect');
                  if (response.status === 200 && type === 'application/pdf') {
                    if (Number(response.headers.get('content-length') || 0) > limit) throw new Error('size');
                    const reader = response.body.getReader(); const chunks = []; let length = 0;
                    while (true) {
                      const part = await reader.read(); if (part.done) break;
                      length += part.value.length;
                      if (length > limit) { await reader.cancel(); throw new Error('size'); }
                      chunks.push(part.value);
                    }
                    const data = new Uint8Array(length); let offset = 0;
                    for (const chunk of chunks) { data.set(chunk, offset); offset += chunk.length; }
                    let binary = '';
                    for (let i = 0; i < data.length; i += 32768)
                      binary += String.fromCharCode(...data.subarray(i, i + 32768));
                    result.content = btoa(binary);
                  } else if (response.body) { await response.body.cancel(); }
                  window.chrome.webview.postMessage(result);
                } catch (error) {
                  const kind = error && error.message === 'size' ? 'size' :
                    error && error.name === 'AbortError' ? 'timeout' : 'network';
                  window.chrome.webview.postMessage({nonce, error:kind});
                } finally { clearTimeout(timer); delete window.crmappAbort; }
              })();
            })();
            """;
    }

    public static PromCabinetBrowserResult? Parse(string source, string json, string nonce, PromCabinetLabelRequest request)
    {
        if (source != BridgeUri) return null;
        if (json.Length > PromCabinetLabelReadOnlyContract.MaximumBytes * 4L / 3 + 4096)
            throw Invalid("Відповідь кабінету Prom перевищує обмеження розміру.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("nonce", out var operation) || operation.GetString() != nonce) return null;
        if (root.TryGetProperty("error", out var error))
        {
            if (error.GetString() == "login") throw new ShippingLabelException(LabelFailureKind.ConnectionRequired,
                "Сесію кабінету Prom завершено. Повторно увійдіть у Prom у налаштуваннях маркетплейсів.") { MissingCredentials = true, AppliesToBatch = true };
            throw new ShippingLabelException(error.GetString() == "size" ? LabelFailureKind.InvalidDocument : LabelFailureKind.Temporary,
                error.GetString() == "size" ? "Накладна Prom перевищує 20 МіБ." :
                "Кабінет Prom не надав накладну. Перевірте з’єднання або повторно увійдіть у кабінет.") { AppliesToBatch = true };
        }
        var uri = request.RequestUri;
        if (!root.TryGetProperty("url", out var url) || url.GetString() != uri.AbsoluteUri)
            throw Invalid("Адреса документа кабінету Prom не відповідає запиту.");
        var status = root.GetProperty("status").GetInt32();
        var type = root.GetProperty("type").GetString() ?? "";
        if (status < 100 || status > 599) throw Invalid("Некоректний статус відповіді кабінету Prom.");
        var bytes = Convert.FromBase64String(root.GetProperty("content").GetString() ?? "");
        if (bytes.Length > PromCabinetLabelReadOnlyContract.MaximumBytes) throw Invalid("Накладна Prom перевищує 20 МіБ.");
        var retry = root.TryGetProperty("retryAfter", out var retryValue) ? retryValue.GetString() ?? "" : "";
        return new(new(request.ConnectionId, uri, uri, status, type, bytes), retry);
    }
    private static ShippingLabelException Invalid(string message) => new(LabelFailureKind.InvalidDocument, message) { AppliesToBatch = true };
}

public sealed record PromCabinetBrowserResult(PromCabinetLabelResponse Response, string RetryAfter);
