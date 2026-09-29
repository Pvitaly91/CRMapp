using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public sealed class BackgroundSourceSkippedException(string message) : Exception(message);

public enum BackgroundCooldownReason { None, Transient, RateLimited, Authorization }

public sealed record BackgroundSourceState(string Name, string Status, DateTimeOffset? LastSuccessUtc = null,
    DateTimeOffset? NextAttemptUtc = null, BackgroundCooldownReason CooldownReason = BackgroundCooldownReason.None);

/// <summary>One lifecycle-owned worker. Manual and timer requests coalesce; no missed-tick queue.</summary>
public sealed class BackgroundSyncCoordinator : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task> _receipts;
    private readonly Func<CancellationToken, Task> _orders;
    private readonly Func<CancellationToken, Task<AppSettings>> _settings;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, BackgroundSourceState> _states = [];
    private readonly Dictionary<string, int> _failures = [];
    private readonly HashSet<string> _recoveryUsed = [];
    private readonly object _gate = new();
    private Task? _loop, _cycle, _pendingRecovery;
    private bool _paused, _disposed;

    public BackgroundSyncCoordinator(Func<CancellationToken, Task> receipts,
        Func<CancellationToken, Task> orders, Func<CancellationToken, Task<AppSettings>> settings,
        TimeProvider? clock = null)
    {
        _receipts = receipts; _orders = orders; _settings = settings;
        _clock = clock ?? TimeProvider.System;
        _states["Checkbox"] = new("Checkbox", "Очікує");
        _states["Маркетплейси"] = new("Маркетплейси", "Очікує");
    }

    public event EventHandler? StateChanged;
    public bool Paused { get { lock (_gate) return _paused; } }
    public IReadOnlyList<BackgroundSourceState> States { get { lock (_gate) return _states.Values.ToArray(); } }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _loop ??= RunAsync();
        }
    }

    public void SetPaused(bool value)
    {
        lock (_gate)
        {
            _paused = value;
            if (value)
                foreach (var key in _states.Keys.ToArray())
                    _states[key] = _states[key] with { Status = "Пауза" };
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task RefreshNowAsync(bool explicitRetry = false)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cycle is { IsCompleted: false }) return _cycle;
            return _cycle = CycleAsync(_shutdown.Token, explicitRetry, false);
        }
    }

    /// <summary>One recovery override per transient failure streak; never bypasses server or authorization pauses.</summary>
    public Task RecoveryRefreshAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_paused) return Task.CompletedTask;
            var eligible = _states.Values.Any(s => s.CooldownReason == BackgroundCooldownReason.Transient &&
                !_recoveryUsed.Contains(s.Name));
            if (_cycle is { IsCompleted: false } active)
                return eligible ? _pendingRecovery ??= RecoverAfterAsync(active) : active;
            if (!eligible) return Task.CompletedTask;
            return _cycle = CycleAsync(_shutdown.Token, false, true);
        }
    }

    private async Task RecoverAfterAsync(Task active)
    {
        try { await active; }
        finally { lock (_gate) _pendingRecovery = null; }
        if (!_shutdown.IsCancellationRequested) await RecoveryRefreshAsync();
    }

    public void CredentialsVerified(string source)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(source, out var state) || state.CooldownReason != BackgroundCooldownReason.Authorization) return;
            _states[source] = state with { NextAttemptUtc = null, CooldownReason = BackgroundCooldownReason.None };
            _failures[source] = 0;
            _recoveryUsed.Remove(source);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunAsync()
    {
        try
        {
            await RefreshNowAsync(false); // Startup refresh, without waiting for the first interval.
            while (!_shutdown.IsCancellationRequested)
            {
                var settings = await _settings(_shutdown.Token);
                await Task.Delay(TimeSpan.FromMinutes(settings.BackgroundIntervalMinutes), _clock, _shutdown.Token);
                if (!settings.AutoRefreshEnabled || Paused) continue;
                await RefreshNowAsync(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task CycleAsync(CancellationToken token, bool explicitRetry, bool recovery)
    {
        await RunSourceAsync("Checkbox", _receipts, token, explicitRetry, recovery);
        await RunSourceAsync("Маркетплейси", _orders, token, explicitRetry, recovery);
    }

    private async Task RunSourceAsync(string key, Func<CancellationToken, Task> action, CancellationToken token, bool explicitRetry, bool recovery)
    {
        if (token.IsCancellationRequested) return;
        BackgroundSourceState current;
        lock (_gate)
        {
            current = _states[key];
            var cooling = current.NextAttemptUtc > _clock.GetUtcNow();
            var bypassTransient = current.CooldownReason == BackgroundCooldownReason.Transient &&
                (explicitRetry || recovery && !_recoveryUsed.Contains(key));
            if (cooling && !bypassTransient) return;
            if (recovery && bypassTransient) _recoveryUsed.Add(key);
            if (!recovery && !cooling) _recoveryUsed.Remove(key);
        }
        SetState(key, "Оновлюється");
        try
        {
            await action(token);
            lock (_gate) _failures[key] = 0;
            SetState(key, "Оновлено", _clock.GetUtcNow());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { SetState(key, "Пауза"); }
        catch (BackgroundSourceSkippedException ex) { SetState(key, ex.Message); }
        catch (Exception ex)
        {
            int count;
            lock (_gate)
            {
                count = _failures.TryGetValue(key, out var n) ? Math.Min(n + 1, 6) : 1;
                _failures[key] = count;
            }
            var authFailure = ex is ApiException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden };
            var rateLimited = ex is ApiException { StatusCode: (System.Net.HttpStatusCode)429 };
            var delay = TimeSpan.FromMinutes(Math.Min(30, 1 << count));
            if (ex is ApiException { StatusCode: (System.Net.HttpStatusCode)429, RetryAfter: { } retry } && retry > delay)
                delay = retry;
            SetState(key, authFailure ? "Потрібна авторизація" : "Немає мережі / перевірка неповна",
                current.LastSuccessUtc, authFailure ? DateTimeOffset.MaxValue : _clock.GetUtcNow() + delay,
                authFailure ? BackgroundCooldownReason.Authorization :
                    rateLimited ? BackgroundCooldownReason.RateLimited : BackgroundCooldownReason.Transient);
        }
    }

    private void SetState(string key, string status, DateTimeOffset? success = null, DateTimeOffset? retry = null,
        BackgroundCooldownReason reason = BackgroundCooldownReason.None)
    {
        lock (_gate) _states[key] = new(key, status, success ?? _states[key].LastSuccessUtc, retry, reason);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop, cycle, pendingRecovery;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _shutdown.Cancel(); loop = _loop; cycle = _cycle; pendingRecovery = _pendingRecovery;
        }
        try { if (loop is not null) await loop; if (cycle is not null) await cycle; if (pendingRecovery is not null) await pendingRecovery; }
        catch (OperationCanceledException) { }
        finally { _shutdown.Dispose(); }
    }

    public void Stop() => _shutdown.Cancel();
}
