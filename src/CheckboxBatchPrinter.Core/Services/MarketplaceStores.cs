using System.Collections.Concurrent;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public sealed class JsonMarketplaceSettingsStore(string path) : IMarketplaceSettingsStore
{
    private readonly string _path = Path.GetFullPath(path);

    public async Task<MarketplaceSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await MarketplaceStoreFiles.LockAsync(_path, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(_path)) return new();
        var value = MarketplaceStoreFiles.Deserialize<MarketplaceSettings>(
            await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false));
        Validate(value);
        return value;
    }

    public async Task SaveAsync(MarketplaceSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);
        // This deliberately uses a separate model/file with no credentials or existing printer settings.
        var json = JsonSerializer.Serialize(settings, MarketplaceStoreFiles.JsonOptions);
        using var lease = await MarketplaceStoreFiles.LockAsync(_path, cancellationToken).ConfigureAwait(false);
        await MarketplaceStoreFiles.WriteJsonAsync(_path, json, cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(MarketplaceSettings settings)
    {
        if (settings.CacheDays is < 1 or > 90)
            throw new ArgumentOutOfRangeException(nameof(settings), "Строк кешування має бути від 1 до 90 днів.");
        if (settings.HistoryDays is < 0 or > 3650)
            throw new ArgumentOutOfRangeException(nameof(settings), "Запас історії має бути від 0 до 3650 днів.");
        if (settings.Connections is null)
            throw new InvalidDataException("Некоректний список підключень маркетплейсів.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connection in settings.Connections)
        {
            if (connection is null || !Enum.IsDefined(connection.Marketplace))
                throw new InvalidDataException("Некоректне підключення маркетплейсу.");
            if (!ids.Add(MarketplaceStoreFiles.ConnectionFileId(connection.Id)))
                throw new InvalidDataException("Підключення повинні мати різні постійні ідентифікатори.");
        }
    }
}

public sealed class DpapiMarketplaceSecretStore(string directory) : IMarketplaceSecretStore
{
    private readonly string _directory = Path.GetFullPath(directory);

    public async Task<MarketplaceCredentials?> LoadAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(connectionId);
        using var lease = await MarketplaceStoreFiles.LockAsync(path, cancellationToken).ConfigureAwait(false);
        return await MarketplaceStoreFiles.ReadProtectedAsync<MarketplaceCredentials>(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(string connectionId, MarketplaceCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var path = PathFor(connectionId);
        using var lease = await MarketplaceStoreFiles.LockAsync(path, cancellationToken).ConfigureAwait(false);
        await MarketplaceStoreFiles.WriteProtectedAsync(path, credentials, cancellationToken).ConfigureAwait(false);
    }

    private string PathFor(string connectionId) =>
        Path.Combine(_directory, $"marketplace-{MarketplaceStoreFiles.ConnectionFileId(connectionId)}.dpapi");
}

public sealed class DpapiMarketplaceCacheStore(string path, TimeProvider? timeProvider = null) : IMarketplaceCacheStore
{
    private readonly string _path = Path.GetFullPath(path);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<MarketplaceSnapshot> LoadAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        if (retentionDays is < 1 or > 90) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        using var lease = await MarketplaceStoreFiles.LockAsync(_path, cancellationToken).ConfigureAwait(false);
        var snapshot = await MarketplaceStoreFiles.ReadProtectedAsync<MarketplaceSnapshot>(_path, cancellationToken)
            .ConfigureAwait(false) ?? new([], []);
        Validate(snapshot);
        var cutoff = _clock.GetUtcNow().AddDays(-retentionDays);
        var expiredConnections = snapshot.Orders.Where(order => order.RetrievedAtUtc < cutoff)
            .Select(order => order.Key.ConnectionId).ToHashSet(StringComparer.Ordinal);
        var orders = snapshot.Orders.Where(order => order.RetrievedAtUtc >= cutoff).ToArray();
        var states = snapshot.States.Where(state => state.AttemptedAtUtc >= cutoff)
            .Select(state => expiredConnections.Contains(state.ConnectionId)
                ? state with { Complete = false, Message = "Частина кешу прострочена; потрібне оновлення." }
                : state).ToArray();
        var retained = new MarketplaceSnapshot(orders, states);
        if (orders.Length != snapshot.Orders.Count || states.Length != snapshot.States.Count)
            await MarketplaceStoreFiles.WriteProtectedAsync(_path, retained, cancellationToken).ConfigureAwait(false);
        return retained;
    }

    public async Task SaveAsync(MarketplaceSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Validate(snapshot);
        var deduplicated = new MarketplaceSnapshot(snapshot.Orders.GroupBy(order => order.Key)
            .Select(group => group.MaxBy(order => order.RetrievedAtUtc)!).ToArray(),
            snapshot.States.GroupBy(state => state.ConnectionId)
                .Select(group => group.MaxBy(state => state.AttemptedAtUtc)!).ToArray());
        using var lease = await MarketplaceStoreFiles.LockAsync(_path, cancellationToken).ConfigureAwait(false);
        await MarketplaceStoreFiles.WriteProtectedAsync(_path, deduplicated, cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(MarketplaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Orders is null || snapshot.States is null)
            throw new InvalidDataException("Некоректний кеш маркетплейсів.");
        foreach (var order in snapshot.Orders) MarketplaceStoreFiles.ValidateKey(order.Key);
        foreach (var state in snapshot.States) MarketplaceStoreFiles.ConnectionFileId(state.ConnectionId);
    }
}

public sealed class DpapiReceiptOrderLinkStore(string path) : IReceiptOrderLinkStore
{
    private readonly string _path = Path.GetFullPath(path);

    public async Task<IReadOnlyList<ReceiptOrderDecision>> LoadAsync(string accountContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountContext);
        using var lease = await MarketplaceStoreFiles.LockAsync(_path, cancellationToken).ConfigureAwait(false);
        var all = await ReadAsync(cancellationToken).ConfigureAwait(false);
        return all.Where(item => item.AccountContext == accountContext).ToArray();
    }

    public async Task SaveDecisionAsync(ReceiptOrderDecision decision, CancellationToken cancellationToken = default)
    {
        Validate(decision);
        // Snapshot before awaiting, so caller mutations cannot change the queued write.
        var copy = MarketplaceStoreFiles.Deserialize<ReceiptOrderDecision>(
            JsonSerializer.Serialize(decision, MarketplaceStoreFiles.JsonOptions));
        if (Guid.TryParse(copy.ReceiptId, out var receiptUuid)) copy.ReceiptId = receiptUuid.ToString("D");
        using var lease = await MarketplaceStoreFiles.LockAsync(_path, cancellationToken).ConfigureAwait(false);
        var all = await ReadAsync(cancellationToken).ConfigureAwait(false);
        all.RemoveAll(item => item.AccountContext == copy.AccountContext &&
            (item.ReceiptId == copy.ReceiptId || Guid.TryParse(item.ReceiptId, out var id) && id.ToString("D") == copy.ReceiptId));
        all.Add(copy);
        // Decisions intentionally have no cache TTL. They survive refresh, restart and token rotation.
        await MarketplaceStoreFiles.WriteProtectedAsync(_path, all, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<ReceiptOrderDecision>> ReadAsync(CancellationToken cancellationToken)
    {
        var all = await MarketplaceStoreFiles.ReadProtectedAsync<List<ReceiptOrderDecision>>(_path, cancellationToken)
            .ConfigureAwait(false) ?? [];
        foreach (var item in all) Validate(item);
        return all;
    }

    private static void Validate(ReceiptOrderDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrWhiteSpace(decision.AccountContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(decision.ReceiptId);
        if (decision.ConfirmedOrder is { } key) MarketplaceStoreFiles.ValidateKey(key);
        if (decision.RejectedOrders is null) throw new InvalidDataException("Некоректний список відхилених зв’язків.");
        foreach (var rejected in decision.RejectedOrders) MarketplaceStoreFiles.ValidateKey(rejected);
    }
}

internal static class MarketplaceStoreFiles
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    // Shared by path, not instance: settings dialogs and refresh services cannot overwrite each other's writes.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    internal static async Task<IDisposable> LockAsync(string path, CancellationToken token)
    {
        var gate = Gates.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        return new GateLease(gate);
    }

    internal static string ConnectionFileId(string connectionId)
    {
        if ((!Guid.TryParseExact(connectionId, "N", out var id) && !Guid.TryParseExact(connectionId, "D", out id)) || id == Guid.Empty)
            throw new ArgumentException("Підключення має містити постійний GUID, а не токен або шлях.", nameof(connectionId));
        return id.ToString("N");
    }

    internal static void ValidateKey(OrderKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ConnectionFileId(key.ConnectionId);
        if (!Enum.IsDefined(key.Marketplace) || string.IsNullOrWhiteSpace(key.OrderId) ||
            key.OrderId.Length > 200 || key.OrderId.Any(char.IsControl))
            throw new InvalidDataException("Некоректний ключ замовлення.");
    }

    internal static T Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new InvalidDataException("Локальний файл маркетплейсів порожній.");
        }
        catch (JsonException)
        {
            // Do not include JSON/API payloads or customer data in errors/logs.
            throw new InvalidDataException("Локальний файл маркетплейсів пошкоджений; його не перезаписано.");
        }
    }

    internal static async Task<T?> ReadProtectedAsync<T>(string path, CancellationToken token) where T : class
    {
        var json = await new DpapiCredentialStore(path).LoadPasswordAsync(token).ConfigureAwait(false);
        return json is null ? null : Deserialize<T>(json);
    }

    internal static Task WriteProtectedAsync<T>(string path, T value, CancellationToken token) =>
        new DpapiCredentialStore(path).SavePasswordAsync(JsonSerializer.Serialize(value, JsonOptions), token);

    internal static async Task WriteJsonAsync(string path, string json, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await using var writer = new StreamWriter(stream, leaveOpen: true);
                await writer.WriteAsync(json.AsMemory(), token).ConfigureAwait(false);
                await writer.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class GateLease(SemaphoreSlim gate) : IDisposable
    {
        private bool _released;
        public void Dispose() { if (!_released) { _released = true; gate.Release(); } }
    }
}
