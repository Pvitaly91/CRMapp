using System.Security.Cryptography;
using System.Text;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

// Session-local per-key serialization and server rate-limit cooldowns.
// One instance is owned by the shared shipping transport; no keys or cooldowns are persisted.
public sealed class NovaPoshtaRequestPolicy(TimeProvider? clock = null)
{
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(60);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _sync = new();
    private readonly Dictionary<string, RequestState> _states = new(StringComparer.Ordinal);

    internal sealed class RequestState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public DateTimeOffset RetryAtUtc;
    }

    internal static string KeyFingerprint(string apiKey) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
    internal DateTimeOffset UtcNow => _clock.GetUtcNow().ToUniversalTime();
    public TimeSpan GetRetryDelay(string apiKey)
    {
        lock (_sync)
        {
            var now = UtcNow;
            return _states.TryGetValue(KeyFingerprint(apiKey), out var state) && state.RetryAtUtc > now
                ? state.RetryAtUtc - now : TimeSpan.Zero;
        }
    }

    internal async Task<RequestLease> EnterAsync(string apiKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequestState state;
        lock (_sync)
        {
            var fingerprint = KeyFingerprint(apiKey);
            if (!_states.TryGetValue(fingerprint, out state!)) _states[fingerprint] = state = new();
            if (state.RetryAtUtc > UtcNow) throw RateLimited(state.RetryAtUtc);
        }
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (state.RetryAtUtc > UtcNow) throw RateLimited(state.RetryAtUtc);
            }
            return new RequestLease(this, state);
        }
        catch { state.Gate.Release(); throw; }
    }

    private static DateTimeOffset AddClamped(DateTimeOffset now, TimeSpan wait) => wait <= TimeSpan.Zero ? now :
        wait.Ticks >= DateTimeOffset.MaxValue.UtcTicks - now.UtcTicks ? DateTimeOffset.MaxValue : now + wait;

    private static ShippingLabelException RateLimited(DateTimeOffset retryAt) => new(LabelFailureKind.Temporary,
        "Нова пошта тимчасово обмежила частоту запитів. Зачекайте й повторіть перевірку пізніше. Це не означає, що ключ неправильний.")
        { RetryAtUtc = retryAt };

    internal sealed class RequestLease : IDisposable
    {
        private readonly NovaPoshtaRequestPolicy _owner;
        private readonly RequestState _state;
        private bool _disposed;
        internal RequestLease(NovaPoshtaRequestPolicy owner, RequestState state) { _owner = owner; _state = state; }

        public ShippingLabelException Limit(TimeSpan? serverRetryAfter)
        {
            lock (_owner._sync)
            {
                var now = _owner.UtcNow;
                var deadline = AddClamped(now, serverRetryAfter > DefaultCooldown ? serverRetryAfter.Value : DefaultCooldown);
                if (deadline > _state.RetryAtUtc) _state.RetryAtUtc = deadline;
                return RateLimited(_state.RetryAtUtc);
            }
        }

        public void Dispose()
        {
            lock (_owner._sync)
            {
                if (_disposed) return;
                _disposed = true;
            }
            _state.Gate.Release();
        }
    }
}
