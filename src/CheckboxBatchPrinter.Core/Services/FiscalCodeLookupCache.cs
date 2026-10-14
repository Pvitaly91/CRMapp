using System.Net;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

// Session-only authenticated evidence. Never persist it or share it across cashiers.
internal sealed class FiscalCodeLookupCache(TimeProvider clock)
{
    private const int MaximumEntries = 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(string Account, string Code), Entry> _entries = [];
    private readonly Dictionary<string, Failure> _accountPauses = new(StringComparer.Ordinal);
    private DateTimeOffset _overflowPause;
    private sealed record Failure(DateTimeOffset Until, HttpStatusCode? Status);
    private sealed record Entry(DateTimeOffset Until, IReadOnlyList<ReceiptRecord>? Receipts, HttpStatusCode? FailureStatus = null);

    public async Task<IReadOnlyList<ReceiptRecord>> GetAsync(string account, string code,
        Func<CancellationToken, Task<IReadOnlyList<ReceiptRecord>>> read, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var now = clock.GetUtcNow();
            foreach (var expiredKey in _entries.Where(p => p.Value.Until <= now).Select(p => p.Key).ToArray()) _entries.Remove(expiredKey);
            foreach (var expiredKey in _accountPauses.Where(p => p.Value.Until <= now).Select(p => p.Key).ToArray()) _accountPauses.Remove(expiredKey);
            if (_accountPauses.TryGetValue(account, out var paused)) throw Unavailable(paused.Until - now, paused.Status);
            if (_overflowPause > now) throw Unavailable(_overflowPause - now, null);
            var key = (account, code);
            if (_entries.TryGetValue(key, out var cached))
                return cached.Receipts ?? throw Unavailable(cached.Until - now, cached.FailureStatus);
            if (_entries.Count >= MaximumEntries)
                throw Unavailable(_entries.Values.Min(entry => entry.Until) - now, null);
            try
            {
                var receipts = (await read(token).ConfigureAwait(false)).ToArray();
                token.ThrowIfCancellationRequested();
                var unique = receipts.Length == 1 && receipts[0].FiscalCode == code &&
                    Guid.TryParse(receipts[0].Id, out var id) && id != Guid.Empty;
                _entries[key] = new(clock.GetUtcNow().AddMinutes(unique ? 10 : 5), receipts);
                return receipts;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                token.ThrowIfCancellationRequested();
                var status = (exception as ApiException)?.StatusCode;
                var delay = (exception as ApiException)?.RetryAfter is { } retry && retry > TimeSpan.FromMinutes(5)
                    ? retry : TimeSpan.FromMinutes(5);
                var failedAt = clock.GetUtcNow();
                var until = delay > DateTimeOffset.MaxValue - failedAt ? DateTimeOffset.MaxValue : failedAt.Add(delay);
                _entries[key] = new(until, null, status);
                if (exception is not ApiException || status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or
                    HttpStatusCode.RequestTimeout or (HttpStatusCode)429 || (int?)status >= 500)
                {
                    if (_accountPauses.Count < 128 || _accountPauses.ContainsKey(account))
                        _accountPauses[account] = new(until, status);
                    else if (until > _overflowPause) _overflowPause = until;
                }
                throw Unavailable(delay, status);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task InvalidateAccountAsync(string account)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var key in _entries.Keys.Where(key => key.Account == account).ToArray()) _entries.Remove(key);
            _accountPauses.Remove(account);
        }
        finally { _gate.Release(); }
    }

    private static ApiException Unavailable(TimeSpan remaining, HttpStatusCode? status) => new(
        "Перевірку фіскальних ключів тимчасово призупинено; повторні запити обмежено.", status, retryAfter: remaining);
}
