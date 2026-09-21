using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public sealed class AuthenticationService(
    HttpClient httpClient,
    ISettingsService settingsService,
    ISecureCredentialStore credentialStore,
    IAppLogger logger) : IAuthenticationService
{
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;

    public bool HasStoredCredentials => credentialStore.HasPassword;

    public async Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && !string.IsNullOrWhiteSpace(_accessToken)) return _accessToken;
        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && !string.IsNullOrWhiteSpace(_accessToken)) return _accessToken;
            var settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
            var password = await credentialStore.LoadPasswordAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(settings.Login) || string.IsNullOrWhiteSpace(password))
                throw new ApiException("Облікові дані Checkbox ще не налаштовано.", HttpStatusCode.Unauthorized);

            _accessToken = await SignInCoreAsync(settings, settings.Login, password, cancellationToken).ConfigureAwait(false);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public async Task SignInAndStoreAsync(string login, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(login);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
            var token = await SignInCoreAsync(settings, login.Trim(), password, cancellationToken).ConfigureAwait(false);
            settings.Login = login.Trim();
            await settingsService.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
            await credentialStore.SavePasswordAsync(password, cancellationToken).ConfigureAwait(false);
            _accessToken = token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public void InvalidateToken() => _accessToken = null;

    private async Task<string> SignInCoreAsync(AppSettings settings, string login, string password, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.ApiBaseUrl}/api/v1/cashier/signin")
        {
            Content = JsonContent.Create(new { login, password })
        };
        request.Headers.TryAddWithoutValidation("X-Client-Name", "Checkbox Batch Printer");
        request.Headers.TryAddWithoutValidation("X-Client-Version", "1.0.0");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        logger.Info("checkbox.signin", httpStatus: (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
            throw new ApiException("Не вдалося авторизуватися в Checkbox.", response.StatusCode, ExtractMessage(body));

        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("access_token", out var tokenElement) || string.IsNullOrWhiteSpace(tokenElement.GetString()))
            throw new ApiException("Checkbox не повернув access_token.", response.StatusCode);
        return tokenElement.GetString()!;
    }

    internal static string? ExtractMessage(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("message", out var message) ? message.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
