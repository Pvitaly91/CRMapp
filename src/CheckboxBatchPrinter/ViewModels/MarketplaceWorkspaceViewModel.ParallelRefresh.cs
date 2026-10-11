using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.ViewModels;

public sealed partial class MarketplaceWorkspaceViewModel
{
    private readonly SemaphoreSlim _parallelRefreshGate = new(1, 1);
    private CancellationTokenSource? _parallelCancellation;
    private bool _parallelBusy;
    private bool _parallelPublishing;
    private int _configurationVersion, _parallelAccountVersion;

    public Task RefreshAllAsync(DateOnly from, DateOnly to, Task receiptsSettled, string expectedAccount,
        CancellationToken token = default) => RefreshParallelAsync(from, to, receiptsSettled, expectedAccount, false, token);

    public async Task SyncBackgroundAsync(Task receiptsSettled, string expectedAccount, CancellationToken token = default)
    {
        if (IsWorkspaceRefreshing) throw new BackgroundSourceSkippedException("Триває оновлення всіх даних");
        if (!await RefreshParallelAsync(_backgroundFrom ?? _from, _backgroundTo ?? _to, receiptsSettled, expectedAccount, true, token))
            throw new BackgroundSourceSkippedException("Оновлення замовлень пропущено: контекст змінено");
        token.ThrowIfCancellationRequested();
        if (!HasEnabledConnections) throw new BackgroundSourceSkippedException("Не налаштовано");
        var incomplete = _config.Connections.Where(c => c.Enabled).Select(c => _snapshot.States.FirstOrDefault(s => s.ConnectionId == c.Id))
            .Where(s => s is null || !s.Complete).ToArray();
        if (incomplete.Length == 0) return;
        var authorization = incomplete.FirstOrDefault(s => s?.FailureStatus is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden);
        if (authorization is not null) throw new ApiException("Потрібна повторна перевірка облікових даних маркетплейсу.", authorization.FailureStatus);
        var limited = incomplete.FirstOrDefault(s => s?.FailureStatus == (System.Net.HttpStatusCode)429);
        if (limited is not null) throw new ApiException("Маркетплейс обмежив частоту запитів.", limited.FailureStatus, retryAfter: limited.RetryAfter);
        throw new InvalidOperationException("Перевірка маркетплейсів неповна.");
    }

    private async Task<bool> RefreshParallelAsync(DateOnly from, DateOnly to, Task receiptsSettled, string expectedAccount,
        bool background, CancellationToken token)
    {
        if (to < from) throw new ArgumentException("Дата початку має бути не пізнішою за дату завершення.");
        await _parallelRefreshGate.WaitAsync(token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _parallelCancellation = cancellation;
        var ct = cancellation.Token;
        var configurationVersion = _configurationVersion;
        var accountVersion = _parallelAccountVersion;
        var display = (_displayFrom, _displayTo);
        int? applyGeneration = null;
        _parallelBusy = true; OnPropertyChanged(nameof(IsBusy)); RaiseCommands();
        try
        {
            _automaticCancel?.Cancel();
            _cancel?.Cancel();
            if (_activeSync is { IsCompleted: false } previous) await previous;
            ct.ThrowIfCancellationRequested();
            var config = JsonSerializer.Deserialize<MarketplaceSettings>(JsonSerializer.Serialize(await _settings.LoadAsync(ct)))!;
            var decisions = expectedAccount.Length > 0 ? await _links.LoadAsync(expectedAccount, ct) : [];
            var known = decisions.Where(d => d.ConfirmedOrder is not null).Select(d => d.ConfirmedOrder!).Distinct().ToArray();
            var range = MarketplaceSyncService.BuildRange(from, to, Math.Min(3650, config.HistoryDays + _extraHistory));
            Status = "Паралельно завантажую замовлення Prom і Rozetka…";
            var snapshot = await _sync.SynchronizeAsync(config, range, known, ct);
            // Receipt upserts may establish the first cashier or replace a legacy scope.
            // The network result stays private until that operation has settled.
            try { await receiptsSettled; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { ReceiptCoverageFresh = false; }
            ct.ThrowIfCancellationRequested();
            if (configurationVersion != _configurationVersion || accountVersion != _parallelAccountVersion ||
                display != (_displayFrom, _displayTo) || expectedAccount.Length > 0 && _account.Length > 0 && expectedAccount != _account)
                return false;
            if (Labels is { } labelOperation) await labelOperation.WaitForPrintOperationAsync(ct);
            await EnsureAttachedAsync();
            ct.ThrowIfCancellationRequested();
            if (configurationVersion != _configurationVersion || accountVersion != _parallelAccountVersion ||
                display != (_displayFrom, _displayTo) || expectedAccount.Length > 0 && _account.Length > 0 && expectedAccount != _account) return false;
            var generation = _generation;
            applyGeneration = generation;
            using var applyCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _scopeCancel.Token);
            var applyToken = applyCancellation.Token;
            var currentDecisions = _account.Length > 0 ? await _links.LoadAsync(_account, applyToken) : [];
            var deduplication = await _sync.ResolveDuplicateConnectionsAsync(config, applyToken);
            if (Labels is { } printing) await printing.WaitForPrintOperationAsync(applyToken);
            applyToken.ThrowIfCancellationRequested();
            if (generation != _generation || configurationVersion != _configurationVersion || accountVersion != _parallelAccountVersion ||
                display != (_displayFrom, _displayTo)) return false;
            _config = config; _configurationLoaded = true; _canApply = true;
            _decisions = currentDecisions;
            _deduplication = deduplication;
            _parallelPublishing = true;
            if (!background) { _from = from; _to = to; _orderDatesValid = true; }
            _backgroundSyncing = background;
            NotifyConfiguration();
            if (!await ApplyParallelSnapshotAsync(snapshot, range, from, to, generation, configurationVersion, accountVersion, display, background, applyToken)) return false;
            await SaveAutomaticCacheAsync(generation, applyToken);
            applyToken.ThrowIfCancellationRequested();
            return IsCurrentParallelContext(generation, configurationVersion, accountVersion, display);
        }
        catch (OperationCanceledException)
        {
            Status = "Оновлення замовлень скасовано або контекст змінено.";
            if (token.IsCancellationRequested) throw;
            return false;
        }
        finally
        {
            if (applyGeneration is { } generation && IsCurrentParallelContext(generation, configurationVersion, accountVersion, display))
            {
                _loadingDetails.Clear();
                if (Labels is { IsPrintOperationActive: true } printing)
                    _ = ReapplyAfterPrintingAsync(printing, generation, configurationVersion, accountVersion, display, background);
                else ApplyMatches();
            }
            _parallelPublishing = false;
            _backgroundSyncing = false;
            if (ReferenceEquals(_parallelCancellation, cancellation)) _parallelCancellation = null;
            _parallelBusy = false; OnPropertyChanged(nameof(IsBusy)); RaiseCommands();
            _parallelRefreshGate.Release();
        }
    }

    private async Task<bool> ApplyParallelSnapshotAsync(MarketplaceSnapshot snapshot, MarketplaceRange range, DateOnly from, DateOnly to,
        int generation, int configurationVersion, int accountVersion, (DateOnly?, DateOnly?) display, bool background, CancellationToken token)
    {
        _snapshot = snapshot;
        if (_fiscalVerifier is not null && _account.Length > 0)
        {
            var verified = await _fiscalVerifier.VerifyAsync(ActiveOrders, _account, token);
            token.ThrowIfCancellationRequested();
            if (!IsCurrentParallelContext(generation, configurationVersion, accountVersion, display)) return false;
            _snapshot = new(verified, snapshot.States);
        }
        var enabled = _config.Connections.Where(c => c.Enabled).ToArray();
        var ordersComplete = enabled.All(c => _snapshot.States.Any(s => s.ConnectionId == c.Id && s.Complete && s.Range == range));
        if (!ordersComplete && !background) _orderCoverage.RemoveAll(r => r.From <= range.From && r.ToExclusive >= range.ToExclusive);
        if (ordersComplete && !_orderCoverage.Contains(range))
        {
            _orderCoverage.Add(range);
            if (_orderCoverage.Count > 256) _orderCoverage.RemoveAt(0);
        }
        _coverageComplete = HasCoverage(_from, _to);
        Status = $"Діапазон замовлень: {range.From:dd.MM.yyyy} — {range.ToExclusive.AddDays(-1):dd.MM.yyyy} (Київ). " +
            string.Join(" | ", enabled.Select(c => { var s = _snapshot.States.FirstOrDefault(s => s.ConnectionId == c.Id); return $"{c.Name}: {s?.Message} Успішне оновлення: {s?.LastSuccessUtc?.ToLocalTime().ToString("dd.MM HH:mm") ?? "немає"}"; }));
        _loadingDetails.UnionWith(DetailTargets(from, to).Take(100).Select(r => r.Id));
        ApplyMatches();
        foreach (var row in _rows.Where(r => ReceiptCoverageFresh && _account.Length > 0 && r.RawType == ReceiptTypes.Return &&
            !_receiptDetails.ContainsKey(r.Id) && (!background || IsInRange(r, from, to))))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var details = await _details.GetAsync(row.Id, token);
                token.ThrowIfCancellationRequested();
                if (!IsCurrentParallelContext(generation, configurationVersion, accountVersion, display)) return false;
                RememberDetails(row, details);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { if (!IsCurrentParallelContext(generation, configurationVersion, accountVersion, display)) return false; _coverageComplete = false; Status += " Деталі повернення недоступні."; }
        }
        if (ordersComplete && ReceiptCoverageFresh && _account.Length > 0 && HasReceiptCoverage(from, to))
            await LoadBasketDetailsAsync(generation, token, from, to);
        if (Labels is { } printing) await printing.WaitForPrintOperationAsync(token);
        token.ThrowIfCancellationRequested();
        if (!IsCurrentParallelContext(generation, configurationVersion, accountVersion, display)) return false;
        _loadingDetails.Clear(); ApplyMatches();
        return true;
    }

    private bool IsCurrentParallelContext(int generation, int configurationVersion, int accountVersion, (DateOnly?, DateOnly?) display) =>
        generation == _generation && configurationVersion == _configurationVersion && accountVersion == _parallelAccountVersion &&
        display == (_displayFrom, _displayTo);

    private async Task ReapplyAfterPrintingAsync(ShippingLabelsViewModel labels, int generation, int configurationVersion, int accountVersion,
        (DateOnly?, DateOnly?) display, bool background)
    {
        try
        {
            await labels.WaitForPrintOperationAsync(CancellationToken.None);
            if (IsCurrentParallelContext(generation, configurationVersion, accountVersion, display))
            {
                _backgroundSyncing = background;
                try { ApplyMatches(); }
                finally { _backgroundSyncing = false; }
            }
        }
        catch (Exception) { /* A disposed print operation must not publish into a new scope. */ }
    }
}
