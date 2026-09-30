using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public interface IOrderNotificationStore
{
    Task<OrderNotificationState> LoadAsync(CancellationToken token = default);
    Task SaveAsync(OrderNotificationState state, CancellationToken token = default);
}

public sealed class DpapiOrderNotificationStore(string path) : IOrderNotificationStore
{
    public async Task<OrderNotificationState> LoadAsync(CancellationToken token = default)
    {
        using var lease = await MarketplaceStoreFiles.LockAsync(path, token).ConfigureAwait(false);
        if (File.Exists(path) && new FileInfo(path).Length > 16 * 1024 * 1024)
            throw new InvalidDataException("Журнал сповіщень завеликий.");
        var state = await MarketplaceStoreFiles.ReadProtectedAsync<OrderNotificationState>(path, token)
            .ConfigureAwait(false) ?? new();
        if (state.Seen is null || state.Baselines is null || state.Seen.Count > NewOrderNotificationService.MaximumOrders ||
            state.Baselines.Count > NewOrderNotificationService.MaximumSources ||
            state.Seen.Any(s => s is null || s.Key is null || string.IsNullOrWhiteSpace(s.Key.OrderId)) ||
            state.Baselines.Any(s => s is null || s.Source is null || string.IsNullOrWhiteSpace(s.Source.ConnectionId)))
            throw new InvalidDataException("Некоректний журнал сповіщень.");
        return state;
    }

    public async Task SaveAsync(OrderNotificationState state, CancellationToken token = default)
    {
        using var lease = await MarketplaceStoreFiles.LockAsync(path, token).ConfigureAwait(false);
        await MarketplaceStoreFiles.WriteProtectedAsync(path, state, token).ConfigureAwait(false);
    }
}

/// <summary>Records observed IDs before returning a single optional Windows notification.</summary>
public sealed class NewOrderNotificationService(IOrderNotificationStore store, TimeProvider? clock = null)
{
    public const int MaximumOrders = 50000;
    public const int MaximumSources = 128;
    public const int RetentionDays = 32; // Longer than the maximum 30-day background window.
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OrderNotificationState? _state;

    public async Task SeedFromCacheAsync(OrderNotificationSnapshot snapshot, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var next = await CopyStateAsync(token).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            foreach (var source in snapshot.Sources)
            {
                if (next.Baselines.Any(b => b.Source == source.Source)) continue;
                var cached = ForSource(snapshot.Orders, source.Source).ToArray();
                if (!source.Complete && cached.Length == 0) continue;
                next.Baselines.Add(new(source.Source, source.LastSuccessUtc ?? now));
                next.Seen.AddRange(cached.Select(o => new SeenNotificationOrder(o.Key, now)));
            }
            await CommitAsync(next, now, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<NewOrdersNotification?> ObserveAsync(OrderNotificationSnapshot snapshot,
        DateOnly from, DateOnly to, bool enabled, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var next = await CopyStateAsync(token).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            var known = next.Seen.Select(s => s.Key).ToHashSet();
            var added = new List<MarketplaceOrder>();
            foreach (var source in snapshot.Sources.Where(s => s.Complete))
            {
                var orders = ForSource(snapshot.Orders, source.Source).DistinctBy(o => o.Key).ToArray();
                var baseline = next.Baselines.FirstOrDefault(b => b.Source == source.Source);
                if (baseline is null)
                {
                    // First complete read of a new shop establishes a quiet baseline.
                    next.Baselines.Add(new(source.Source, now));
                }
                foreach (var order in orders)
                {
                    if (!known.Add(order.Key)) continue;
                    next.Seen.Add(new(order.Key, now));
                    if (baseline is not null && order.CreatedAt is { } created && created > baseline.SinceUtc &&
                        DateRangeBuilder.KyivDate(created) is var day && day >= from && day <= to)
                        added.Add(order);
                }
            }
            // Muted notifications still record observations, so unmuting cannot replay a backlog.
            // Persist first: a restart cannot replay a notification already handed to Windows.
            await CommitAsync(next, now, token).ConfigureAwait(false);
            return enabled && added.Count > 0 ? BuildNotification(added) : null;
        }
        finally { _gate.Release(); }
    }

    private async Task<OrderNotificationState> CopyStateAsync(CancellationToken token)
    {
        _state ??= await store.LoadAsync(token).ConfigureAwait(false);
        return new() { Baselines = [.. _state.Baselines], Seen = [.. _state.Seen] };
    }

    private async Task CommitAsync(OrderNotificationState next, DateTimeOffset now, CancellationToken token)
    {
        next.Seen = next.Seen.Where(s => s.SeenAtUtc >= now.AddDays(-RetentionDays) && s.SeenAtUtc <= now)
            .GroupBy(s => s.Key).Select(g => g.MaxBy(s => s.SeenAtUtc)!)
            .OrderByDescending(s => s.SeenAtUtc).Take(MaximumOrders).ToList();
        next.Baselines = next.Baselines.DistinctBy(b => b.Source).Take(MaximumSources).ToList();
        await store.SaveAsync(next, token).ConfigureAwait(false);
        _state = next;
    }

    private static IEnumerable<MarketplaceOrder> ForSource(IEnumerable<MarketplaceOrder> orders, NotificationSource source) =>
        orders.Where(o => o.Key.Marketplace == source.Marketplace && o.Key.ConnectionId == source.ConnectionId);

    private static NewOrdersNotification BuildNotification(IReadOnlyList<MarketplaceOrder> orders)
    {
        static string Clean(string text) => new(text.Where(c => !char.IsControl(c)).Take(32).ToArray());
        var lines = orders.OrderByDescending(o => o.CreatedAt).Take(4)
            .Select(o => $"{o.Key.Marketplace} №{Clean(string.IsNullOrWhiteSpace(o.Number) ? o.Key.OrderId : o.Number)}");
        var body = string.Join("\n", lines) + (orders.Count > 4 ? $"\nЩе замовлень: {orders.Count - 4}" : "");
        body += "\nНатисніть, щоб відкрити CRMapp.";
        return new(orders.Count, orders.Count == 1 ? "Нове замовлення" : $"Нові замовлення: {orders.Count}", body);
    }
}
