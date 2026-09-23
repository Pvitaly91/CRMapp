using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public sealed class MarketplaceSyncService(
    IEnumerable<IMarketplaceOrdersClient> clients,
    IMarketplaceSecretStore secrets,
    IMarketplaceCacheStore cache,
    TimeProvider? timeProvider = null)
{
    private readonly Dictionary<MarketplaceKind, IMarketplaceOrdersClient> _clients = clients.ToDictionary(c => c.Marketplace);
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public Task<MarketplaceSnapshot> LoadCachedAsync(int retentionDays, CancellationToken ct = default) => cache.LoadAsync(retentionDays, ct);

    public Task TestConnectionAsync(MarketplaceConnection connection, MarketplaceCredentials credentials, CancellationToken ct = default) =>
        _clients[connection.Marketplace].TestConnectionAsync(connection, credentials, ct);

    public async Task<MarketplaceSnapshot> SynchronizeAsync(MarketplaceSettings settings, MarketplaceRange range,
        IReadOnlyCollection<OrderKey> knownOrders, CancellationToken ct = default)
    {
        if (!await _syncGate.WaitAsync(0, ct).ConfigureAwait(false))
            throw new InvalidOperationException("Оновлення маркетплейсів уже виконується.");
        try
        {
            var previous = await cache.LoadAsync(settings.CacheDays, ct).ConfigureAwait(false);
            var orders = previous.Orders.ToDictionary(o => o.Key);
            var states = previous.States.ToDictionary(s => s.ConnectionId);
            // Sequential connections + requests bound parallelism and avoid seller rate spikes.
            foreach (var connection in settings.Connections.Where(c => c.Enabled))
            {
                ct.ThrowIfCancellationRequested();
                var complete = true;
                var message = "Отримано всі сторінки замовлень.";
                var lastSuccess = states.GetValueOrDefault(connection.Id)?.LastSuccessUtc;
                try
                {
                    var credential = await secrets.LoadAsync(connection.Id, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("Введіть облікові дані у Налаштуваннях → Маркетплейси.");
                    var client = _clients[connection.Marketplace];
                    var fetched = await client.FetchAsync(connection, credential, range, ct).ConfigureAwait(false);
                    complete = fetched.Complete;
                    message = fetched.Message.Length > 0 ? fetched.Message : message;
                    foreach (var order in fetched.Orders)
                        if (order.Key.ConnectionId == connection.Id && order.Key.Marketplace == connection.Marketplace)
                            orders[order.Key] = MarketplaceFiscalEvidence.Preserve(order, orders.GetValueOrDefault(order.Key));

                    // Refresh changed older cached orders and durable linked IDs even outside this range.
                    var fetchedKeys = fetched.Orders.Select(o => o.Key).ToHashSet();
                    var refresh = knownOrders.Concat(previous.Orders.Select(o => o.Key)).Distinct()
                        .Where(k => k.ConnectionId == connection.Id && k.Marketplace == connection.Marketplace && !fetchedKeys.Contains(k)).ToArray();
                    if (refresh.Length > 500) { complete = false; message = "Частково: понад 500 старих замовлень потребують перевірки."; }
                    foreach (var key in refresh.Take(500))
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var order = await client.GetOrderAsync(connection, credential, key.OrderId, ct).ConfigureAwait(false);
                            if (order is not null && order.Key == key) orders[key] = MarketplaceFiscalEvidence.Preserve(order, orders.GetValueOrDefault(key));
                            else { complete = false; message = "Частково: одне з раніше пов’язаних замовлень недоступне."; }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception) { complete = false; message = "Частково: не всі старі замовлення вдалося оновити."; }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    complete = false;
                    message = ex is MarketplaceApiException ? ex.Message : "API недоступне або підключення не налаштоване.";
                }
                if (complete) lastSuccess = _clock.GetUtcNow();
                states[connection.Id] = new(connection.Id, range, complete, lastSuccess, message, _clock.GetUtcNow());
                await cache.SaveAsync(new(orders.Values.ToArray(), states.Values.ToArray()), ct).ConfigureAwait(false);
            }
            return new(orders.Values.ToArray(), states.Values.ToArray());
        }
        finally { _syncGate.Release(); }
    }

    public static MarketplaceRange BuildRange(DateOnly receiptFrom, DateOnly receiptTo, int historyDays)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("FLE Standard Time");
        var from = receiptFrom.AddDays(-Math.Clamp(historyDays, 0, 3650)).ToDateTime(TimeOnly.MinValue);
        var end = receiptTo.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return new(new DateTimeOffset(from, zone.GetUtcOffset(from)), new DateTimeOffset(end, zone.GetUtcOffset(end)));
    }
}
