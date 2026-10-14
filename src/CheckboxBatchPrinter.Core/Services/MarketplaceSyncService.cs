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

    public Task<MarketplaceOrderDeduplication> ResolveDuplicateConnectionsAsync(MarketplaceSettings settings, CancellationToken ct = default) =>
        MarketplaceOrderDeduplication.ResolveAsync(settings, secrets, ct);

    public Task TestConnectionAsync(MarketplaceConnection connection, MarketplaceCredentials credentials, CancellationToken ct = default) =>
        _clients[connection.Marketplace].TestConnectionAsync(connection, credentials, ct);

    public async Task<MarketplaceSnapshot> SynchronizeAsync(MarketplaceSettings settings, MarketplaceRange range,
        IReadOnlyCollection<OrderKey> knownOrders, CancellationToken ct = default)
    {
        if (!await _syncGate.WaitAsync(0, ct).ConfigureAwait(false))
            throw new InvalidOperationException("Оновлення маркетплейсів уже виконується.");
        try
        {
            // Settings can be edited while I/O is pending; every lane must use the same scope.
            var connections = settings.Connections.Where(c => c.Enabled).Select(c => new MarketplaceConnection
            { Id = c.Id, Marketplace = c.Marketplace, Enabled = c.Enabled, Name = c.Name }).ToArray();
            var knownKeys = knownOrders.ToArray();
            var retentionDays = settings.CacheDays;
            // Resolve account equivalence and perform requests against the same credential
            // snapshot. A settings edit during I/O must not merge different seller accounts.
            var credentials = new Dictionary<string, MarketplaceCredentials?>();
            foreach (var connection in connections)
            {
                ct.ThrowIfCancellationRequested();
                try { credentials[connection.Id] = await secrets.LoadAsync(connection.Id, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { credentials[connection.Id] = null; }
            }
            var identity = await MarketplaceOrderDeduplication.ResolveAsync(
                new MarketplaceSettings { Connections = connections.ToList() }, new CredentialSnapshot(credentials), ct).ConfigureAwait(false);
            var previous = await cache.LoadAsync(retentionDays, ct).ConfigureAwait(false);
            var orders = previous.Orders.ToDictionary(o => o.Key);
            var states = previous.States.ToDictionary(s => s.ConnectionId);
            using var saveGate = new SemaphoreSlim(1, 1);
            // Independent providers run together. Connections, pages and details within one
            // provider stay sequential, retaining its existing rate limits and error handling.
            await Task.WhenAll(connections.GroupBy(c => c.Marketplace).Select(ReadLaneAsync)).ConfigureAwait(false);
            return new(orders.Values.ToArray(), states.Values.ToArray());

            async Task ReadLaneAsync(IGrouping<MarketplaceKind, MarketplaceConnection> lane)
            {
                var laneOrders = previous.Orders.Where(o => o.Key.Marketplace == lane.Key).ToDictionary(o => o.Key);
                var laneStates = previous.States.ToDictionary(s => s.ConnectionId);
                foreach (var account in lane.GroupBy(c => identity.NormalizeKey(new(c.Marketplace, c.Id, "")).ConnectionId))
                {
                    var aliases = account.ToArray();
                    var primary = aliases.Single(c => c.Id == account.Key);
                    await ReadConnectionAsync(primary, aliases, laneOrders, laneStates).ConfigureAwait(false);
                }
            }

            async Task ReadConnectionAsync(MarketplaceConnection connection, MarketplaceConnection[] aliases,
                Dictionary<OrderKey, MarketplaceOrder> laneOrders, Dictionary<string, ConnectionSyncState> laneStates)
            {
                ct.ThrowIfCancellationRequested();
                var updates = new Dictionary<OrderKey, MarketplaceOrder>();
                var complete = true;
                var message = "Отримано всі сторінки замовлень.";
                var detailCursor = laneStates.GetValueOrDefault(connection.Id)?.DetailCursor;
                MarketplaceApiException? apiFailure = null;
                try
                {
                    var credential = credentials.GetValueOrDefault(connection.Id)
                        ?? throw new InvalidOperationException("Введіть облікові дані у Налаштуваннях → Маркетплейси.");
                    var client = _clients[connection.Marketplace];
                    var fetched = await client.FetchAsync(connection, credential, range, ct).ConfigureAwait(false);
                    complete = fetched.Complete && !fetched.SuppressDetailRefresh;
                    message = fetched.Message.Length > 0 ? fetched.Message : message;
                    if (fetched.FailureStatus is not null || fetched.RetryAfter is not null)
                        apiFailure = new MarketplaceApiException(message, fetched.FailureStatus, fetched.RetryAfter);
                    foreach (var order in fetched.Orders)
                        if (order.Key.ConnectionId == connection.Id && order.Key.Marketplace == connection.Marketplace)
                            RememberAliases(order);

                    if (!fetched.SuppressDetailRefresh)
                    {
                        // Durable links must stay current even outside the requested range. Other cached
                        // orders only affect this range's completeness when their date is in it or unknown;
                        // dated history outside the range stays cached without an unrelated detail refresh.
                        var fetchedKeys = fetched.Orders.Select(o => o.Key).ToHashSet();
                        var relevantCachedKeys = previous.Orders.Where(o => o.CreatedAt is not { } created ||
                            created >= range.From && created < range.ToExclusive).Select(o => o.Key);
                        var refresh = knownKeys.Concat(relevantCachedKeys).Select(identity.NormalizeKey).Distinct()
                            .Where(k => k.ConnectionId == connection.Id && k.Marketplace == connection.Marketplace && !fetchedKeys.Contains(k))
                            .OrderBy(k => k.OrderId, StringComparer.Ordinal).ToArray();
                        if (refresh.Length > 500) { complete = false; message = "Частково: понад 500 старих замовлень потребують перевірки."; }
                        var start = detailCursor is null ? 0 : Array.FindIndex(refresh, k => string.CompareOrdinal(k.OrderId, detailCursor) > 0);
                        if (start < 0) start = 0;
                        var batch = refresh.Skip(start).Concat(refresh.Take(start)).Take(500).ToArray();
                        string? lastAttemptedDetail = null;
                        foreach (var key in batch)
                        {
                            ct.ThrowIfCancellationRequested();
                            lastAttemptedDetail = key.OrderId;
                            try
                            {
                                var order = await client.GetOrderAsync(connection, credential, key.OrderId, ct).ConfigureAwait(false);
                                if (order is not null && order.Key == key)
                                    RememberAliases(order);
                                else { complete = false; message = "Частково: одне з раніше пов’язаних замовлень недоступне."; }
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                            catch (Exception ex)
                            {
                                apiFailure = MoreRestrictive(apiFailure, ex as MarketplaceApiException);
                                complete = false; message = "Частково: не всі старі замовлення вдалося оновити.";
                                if (connection.Marketplace == MarketplaceKind.Prom &&
                                    ex is MarketplaceApiException { BlocksAccountRequests: true } blocked)
                                {
                                    apiFailure = blocked;
                                    message = blocked.Message;
                                    break;
                                }
                            }
                        }
                        if (fetched.Complete && lastAttemptedDetail is not null) detailCursor = lastAttemptedDetail;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    complete = false;
                    apiFailure = MoreRestrictive(apiFailure, ex as MarketplaceApiException);
                    message = ex is MarketplaceApiException ? ex.Message : "API недоступне або підключення не налаштоване.";
                }
                var attempted = _clock.GetUtcNow();
                var accountStates = aliases.Select(alias => new ConnectionSyncState(alias.Id, range, complete,
                    complete ? attempted : laneStates.GetValueOrDefault(alias.Id)?.LastSuccessUtc, message, attempted, detailCursor)
                    { FailureStatus = apiFailure?.StatusCode, RetryAfter = apiFailure?.RetryAfter }).ToArray();
                foreach (var state in accountStates) laneStates[state.ConnectionId] = state;
                // Persist every completed account and its aliases without concurrent cache writes or lost
                // updates when the other provider is still fetching, fails, or is cancelled.
                await saveGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    foreach (var (key, order) in updates) orders[key] = order;
                    foreach (var state in accountStates) states[state.ConnectionId] = state;
                    await cache.SaveAsync(new(orders.Values.ToArray(), states.Values.ToArray()), ct).ConfigureAwait(false);
                }
                finally { saveGate.Release(); }

                void RememberAliases(MarketplaceOrder order)
                {
                    foreach (var alias in aliases)
                    {
                        var key = order.Key with { ConnectionId = alias.Id };
                        var projected = order with
                        {
                            Key = key, StoreName = alias.Name,
                            FiscalReferences = order.FiscalReferences.Select(reference => reference.Order == order.Key
                                ? reference with { Order = key } : reference).ToArray()
                        };
                        updates[key] = laneOrders[key] = MarketplaceFiscalEvidence.Preserve(projected, laneOrders.GetValueOrDefault(key));
                    }
                }
            }
        }
        finally { _syncGate.Release(); }
    }

    private sealed class CredentialSnapshot(IReadOnlyDictionary<string, MarketplaceCredentials?> credentials) : IMarketplaceSecretStore
    {
        public Task<MarketplaceCredentials?> LoadAsync(string connectionId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(credentials.GetValueOrDefault(connectionId));
        }
        public Task SaveAsync(string connectionId, MarketplaceCredentials value, CancellationToken ct = default) =>
            throw new NotSupportedException("A synchronization credential snapshot is read-only.");
    }

    public static MarketplaceRange BuildRange(DateOnly receiptFrom, DateOnly receiptTo, int historyDays)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("FLE Standard Time");
        var from = receiptFrom.AddDays(-Math.Clamp(historyDays, 0, 3650)).ToDateTime(TimeOnly.MinValue);
        var end = receiptTo.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return new(new DateTimeOffset(from, zone.GetUtcOffset(from)), new DateTimeOffset(end, zone.GetUtcOffset(end)));
    }

    private static MarketplaceApiException? MoreRestrictive(MarketplaceApiException? previous, MarketplaceApiException? current)
    {
        static int Rank(MarketplaceApiException? error) => error?.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => 3,
            (System.Net.HttpStatusCode)429 => 2,
            _ when error is not null => 1,
            _ => 0
        };
        return Rank(current) > Rank(previous) ? current : previous;
    }
}
