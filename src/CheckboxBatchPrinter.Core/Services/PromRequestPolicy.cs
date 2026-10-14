using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CheckboxBatchPrinter.Core.Services;

/// <summary>Bounded, token-scoped cooldowns shared by every Prom read entry point.</summary>
internal sealed class PromRequestPolicy(TimeProvider clock)
{
    private const int Capacity = 1024;
    private static readonly TimeSpan MinimumPause = TimeSpan.FromMinutes(5);
    private readonly object _sync = new();
    private readonly Dictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Token, string Scope), Failure> _failures = new();

    public async Task<T> ExecuteAsync<T>(string token, string scope, Func<Task<T>> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));
        Account account;
        lock (_sync)
        {
            PruneExpired();
            if (!_accounts.TryGetValue(key, out account!))
            {
                // Never evict a live cooldown or an account with queued callers.
                if (_accounts.Count >= Capacity)
                    throw new MarketplaceApiException("Prom: захист від масових помилок; повторіть пізніше.",
                        retryAfter: MinimumPause) { BlocksAccountRequests = true };
                _accounts[key] = account = new Account();
            }
            account.Users++;
        }

        var acquired = false;
        try
        {
            await account.Gate.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                var now = clock.GetUtcNow();
                if (account.Pause is { } pause && pause.Until > now) throw Error(pause, true);
                if (_failures.TryGetValue((key, scope), out var failure) && failure.Until > now)
                    throw Error(failure, false);
            }

            try { return await operation().ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (MarketplaceApiException error)
            {
                ct.ThrowIfCancellationRequested();
                var shared = error.BlocksAccountRequests || error.StatusCode is null or HttpStatusCode.Unauthorized
                    or HttpStatusCode.Forbidden or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                    || (int)error.StatusCode.Value >= 500;
                throw Remember(key, scope, account, error.Message, error.StatusCode, error.RetryAfter, shared);
            }
            catch (JsonException)
            { throw Remember(key, scope, account, "Prom повернув некоректну відповідь.", null, null, false); }
            catch (InvalidDataException)
            { throw Remember(key, scope, account, "Prom повернув непідтримувану структуру або повторну сторінку даних.", null, null, false); }
            catch (OperationCanceledException)
            { throw Remember(key, scope, account, "Prom: перевищено час очікування API.", null, null, true); }
            catch (Exception error) when (error is HttpRequestException or IOException)
            { throw Remember(key, scope, account, "Prom: немає з’єднання з API.", null, null, true); }
        }
        finally
        {
            if (acquired) account.Gate.Release();
            lock (_sync) account.Users--;
        }
    }

    private MarketplaceApiException Remember(string token, string scope, Account account, string message,
        HttpStatusCode? status, TimeSpan? retryAfter, bool shared)
    {
        lock (_sync)
        {
            var now = clock.GetUtcNow();
            var delay = retryAfter > MinimumPause ? retryAfter.Value : MinimumPause;
            var until = delay > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay;
            var failure = new Failure(until, message, status);
            if (shared)
            {
                var previousDeadline = _failures.Where(pair => pair.Key.Token == token)
                    .Select(pair => pair.Value.Until).Append(account.Pause?.Until ?? now).Max();
                if (previousDeadline > failure.Until) failure = failure with { Until = previousDeadline };
                account.Pause = failure;
                RemoveScopedFailures(token);
            }
            else
            {
                PruneExpired();
                if (_failures.Count >= Capacity && !_failures.ContainsKey((token, scope)))
                {
                    // Preserve evicted deadlines by promoting them to an account pause.
                    var victim = _failures.First().Key.Token;
                    var latest = _failures.Where(pair => pair.Key.Token == victim).Max(pair => pair.Value.Until);
                    _accounts[victim].Pause = new Failure(latest,
                        "Prom: багато помилок окремих замовлень; запити тимчасово призупинено.", null);
                    RemoveScopedFailures(victim);
                }
                if (account.Pause is { } pause && pause.Until > now)
                {
                    account.Pause = pause with { Until = pause.Until > until ? pause.Until : until };
                    return Error(account.Pause, true);
                }
                _failures[(token, scope)] = failure;
            }
            return Error(failure, shared);
        }
    }

    private MarketplaceApiException Error(Failure failure, bool shared)
    {
        var remaining = failure.Until - clock.GetUtcNow();
        return new MarketplaceApiException(failure.Message + " Запити призупинено; повторіть пізніше.",
            failure.Status, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero)
            { BlocksAccountRequests = shared };
    }

    private void PruneExpired()
    {
        var now = clock.GetUtcNow();
        foreach (var key in _failures.Where(pair => pair.Value.Until <= now).Select(pair => pair.Key).ToArray())
            _failures.Remove(key);
        var scopedTokens = _failures.Keys.Select(key => key.Token).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _accounts.Where(pair => pair.Value.Users == 0 &&
                     (pair.Value.Pause is null || pair.Value.Pause.Until <= now) && !scopedTokens.Contains(pair.Key))
                 .Select(pair => pair.Key).ToArray())
        {
            _accounts[key].Gate.Dispose();
            _accounts.Remove(key);
        }
    }

    private void RemoveScopedFailures(string token)
    {
        foreach (var key in _failures.Keys.Where(key => key.Token == token).ToArray()) _failures.Remove(key);
    }

    private sealed class Account
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int Users { get; set; }
        public Failure? Pause { get; set; }
    }
    private sealed record Failure(DateTimeOffset Until, string Message, HttpStatusCode? Status);
}
