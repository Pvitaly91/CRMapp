using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public sealed class MarketplaceApiException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

// No response bodies, credentials, customer fields or request URLs enter application logs.
public sealed class MarketplaceHttpTransport(HttpClient client,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public async Task<string> SendAsync(Func<HttpRequestMessage> requestFactory, MarketplaceKind marketplace,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = requestFactory();
            ValidateRequest(request, marketplace);
            HttpResponseMessage response;
            try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt == 2) throw new MarketplaceApiException($"{marketplace}: перевищено час очікування API.");
                await WaitAsync(TimeSpan.FromSeconds(attempt + 1), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException)
            {
                if (attempt == 2) throw new MarketplaceApiException($"{marketplace}: немає з’єднання з API.");
                await WaitAsync(TimeSpan.FromSeconds(attempt + 1), cancellationToken).ConfigureAwait(false);
                continue;
            }
            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    if (response.Content.Headers.ContentLength > 16 * 1024 * 1024)
                        throw new MarketplaceApiException($"{marketplace}: відповідь перевищує допустимий розмір.");
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using var buffer = new MemoryStream();
                    var bytes = new byte[8192];
                    int length;
                    while ((length = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        if (buffer.Length + length > 16 * 1024 * 1024)
                            throw new MarketplaceApiException($"{marketplace}: відповідь перевищує допустимий розмір.");
                        buffer.Write(bytes, 0, length);
                    }
                    return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                }
                if (attempt < 2 && (response.StatusCode is HttpStatusCode.RequestTimeout or (HttpStatusCode)429 || (int)response.StatusCode >= 500))
                {
                    // Never shorten the server's Retry-After; cancellation remains responsive.
                    var retry = response.Headers.RetryAfter;
                    var wait = retry?.Delta ?? (retry?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(attempt + 1));
                    await WaitAsync(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var hint = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "Перевірте локальні облікові дані та права читання."
                    : "Оновлення неповне; повторіть пізніше.";
                throw new MarketplaceApiException($"{marketplace}: HTTP {(int)response.StatusCode}. {hint}", response.StatusCode);
            }
        }
        throw new MarketplaceApiException($"{marketplace}: API недоступне.");
    }

    private Task WaitAsync(TimeSpan value, CancellationToken ct) => (delay ?? Task.Delay)(value, ct);

    public static void ValidateRequest(HttpRequestMessage request, MarketplaceKind marketplace)
    {
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != "https" || !uri.IsDefaultPort ||
            uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("Дозволені лише HTTPS-запити до офіційного API.");
        var allowed = marketplace switch
        {
            MarketplaceKind.Prom => uri.Host == "my.prom.ua" && request.Method == HttpMethod.Get &&
                (uri.AbsolutePath == "/api/v1/orders/list" || Regex.IsMatch(uri.AbsolutePath, @"^/api/v1/orders/\d+$")),
            MarketplaceKind.Rozetka => uri.Host == "api-seller.rozetka.com.ua" &&
                ((request.Method == HttpMethod.Post && uri.AbsolutePath == "/sites") ||
                 (request.Method == HttpMethod.Get && (uri.AbsolutePath == "/orders/search" ||
                    Regex.IsMatch(uri.AbsolutePath, @"^/orders/\d+$") || Regex.IsMatch(uri.AbsolutePath, @"^/prro/receipt/\d+$") ||
                    Regex.IsMatch(uri.AbsolutePath, @"^/prro/receipt-status/\d+$")))),
            _ => false
        };
        if (!allowed) throw new InvalidOperationException("Операція не входить до дозволеного переліку читання маркетплейсів.");
    }
}
