using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IShipmentTrackingCacheStore? _trackingCacheStore;
    private readonly Func<CancellationToken, Task<string>>? _trackingCacheContextProvider;
    private readonly TimeProvider _trackingClock;
    private bool _trackingCacheReady;
    private string _trackingCacheContext = "";
    private Task? _trackingStartupTask;
    private bool _trackingStartupForce;
    private CancellationTokenSource? _trackingCacheLoadCancellation;

    private Task RestoreCacheAndRefreshTrackingAsync(bool force, CancellationToken token)
    {
        if (_trackingStartupTask is { IsCompleted: false })
        {
            _trackingStartupForce |= force;
            return _trackingStartupTask;
        }
        _trackingStartupForce = force;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _trackingStartupTask = completion.Task;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _trackingCacheLoadCancellation = cancellation;
        _ = RunTrackingCacheStartupAsync(force, _trackingGeneration, cancellation, completion);
        return completion.Task;
    }

    private async Task RunTrackingCacheStartupAsync(bool force, long generation, CancellationTokenSource cancellation, TaskCompletionSource completion)
    {
        try
        {
            var context = _trackingCacheContextProvider is null ? "" : await _trackingCacheContextProvider(cancellation.Token);
            if (generation != _trackingGeneration) return;
            _trackingCacheContext = context;
            var cache = context.Length == 0 ? new ShipmentTrackingCache() : await _trackingCacheStore!.LoadAsync(context, cancellation.Token);
            if (cache.AccountContext != context) cache = new() { AccountContext = context };
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != _trackingGeneration) return;
            _trackingCacheContext = context;
            var now = _trackingClock.GetUtcNow();
            foreach (var status in cache.Statuses.Where(s => s.CheckedAtUtc <= now))
            {
                var key = (status.Carrier, status.TrackingNumber, status.Order);
                if (!_liveTracking.TryGetValue(key, out var newer) || newer.CheckedAtUtc < status.CheckedAtUtc) _liveTracking[key] = status;
            }
            foreach (var attempt in cache.Attempts.Where(a => a.AttemptedAtUtc <= now))
                _trackingAttempts[(attempt.Carrier, attempt.TrackingNumber, attempt.Order, attempt.ShipmentId)] = attempt.AttemptedAtUtc;
            _trackingAutomaticAttemptAt = _trackingAttempts.Values.Where(time => time <= now).Select(time => (DateTimeOffset?)time).Max();
            _trackingCacheReady = true;
            PublishShipmentTrackingStatuses();
            await SaveTrackingCacheAsync(generation);
            await RefreshShipmentTrackingAsync(_trackingStartupForce, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.Error("shipment.tracking.cache.restore", exception);
            if (generation == _trackingGeneration)
            {
                _trackingCacheReady = true;
                await RefreshShipmentTrackingAsync(_trackingStartupForce, cancellation.Token);
            }
        }
        finally
        {
            if (ReferenceEquals(_trackingCacheLoadCancellation, cancellation)) _trackingCacheLoadCancellation = null;
            cancellation.Dispose();
            // A temporary local context read failure must not disable persistence for this session.
            if (generation == _trackingGeneration && _trackingCacheContext.Length == 0) _trackingCacheReady = false;
            completion.TrySetResult();
            if (ReferenceEquals(_trackingStartupTask, completion.Task)) _trackingStartupTask = null;
        }
    }

    private async Task SaveTrackingCacheAsync(long generation, CancellationToken token = default)
    {
        if (_trackingCacheStore is null || _trackingCacheContext.Length == 0 || generation != _trackingGeneration) return;
        try
        {
            var context = _trackingCacheContext;
            var snapshot = new ShipmentTrackingCache
            {
                AccountContext = context, Statuses = _liveTracking.Values.ToArray(),
                Attempts = _trackingAttempts.Where(pair => pair.Key.Carrier != ShippingCarrier.Unknown)
                    .Select(pair => new ShipmentTrackingCacheAttempt(pair.Key.Carrier, pair.Key.Number,
                        pair.Key.Order, pair.Key.ShipmentId, pair.Value)).ToArray()
            };
            await _trackingCacheStore.SaveAsync(context, snapshot, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { _logger.Error("shipment.tracking.cache.save", exception); }
    }
}
