using System.Net;
using System.Net.Http.Headers;

namespace CheckboxBatchPrinter.Core.Services;

public sealed class CheckboxApiClient(HttpClient httpClient, IAuthenticationService authentication, IAppLogger logger)
{
    private const int MaxTransientAttempts = 3;

    public async Task<string> GetStringAsync(string url, string operation, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthenticatedAsync(() => new HttpRequestMessage(HttpMethod.Get, url), operation, cancellationToken)
            .ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> GetBytesAsync(string url, string operation, string? receiptId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthenticatedAsync(() => new HttpRequestMessage(HttpMethod.Get, url), operation, cancellationToken, receiptId)
            .ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(
        Func<HttpRequestMessage> requestFactory,
        string operation,
        CancellationToken cancellationToken,
        string? receiptId = null)
    {
        var refreshed = false;
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = await authentication.GetAccessTokenAsync(refreshed, cancellationToken).ConfigureAwait(false);
            using var request = requestFactory();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("X-Client-Name", "Checkbox Batch Printer");
            request.Headers.TryAddWithoutValidation("X-Client-Version", "1.0.0");

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (attempt < MaxTransientAttempts)
            {
                logger.Error(operation, exception, receiptId);
                await Task.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            logger.Info(operation, receiptId, (int)response.StatusCode);
            if (response.IsSuccessStatusCode) return response;

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && !refreshed)
            {
                response.Dispose();
                authentication.InvalidateToken();
                refreshed = true;
                attempt = 0;
                continue;
            }

            if (IsTransient(response.StatusCode) && attempt < MaxTransientAttempts)
            {
                var delay = GetRetryDelay(response, attempt);
                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var status = response.StatusCode;
            response.Dispose();
            throw new ApiException($"Checkbox API повернув HTTP {(int)status}.", status, AuthenticationService.ExtractMessage(body));
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or (HttpStatusCode)429 or HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta) return delta > TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : delta;
        if (retryAfter?.Date is { } date)
        {
            var calculated = date - DateTimeOffset.UtcNow;
            if (calculated > TimeSpan.Zero) return calculated > TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : calculated;
        }
        return Backoff(attempt);
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1));
}
