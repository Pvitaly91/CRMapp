using System.ComponentModel;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public interface IAutomaticMatchCacheStore
{
    Task<AutomaticMatchCache> LoadAsync(string accountContext, int retentionDays, CancellationToken cancellationToken = default);
    Task SaveAsync(string accountContext, AutomaticMatchCache cache, int retentionDays, CancellationToken cancellationToken = default);
}

/// <summary>Optional encrypted local cache, isolated from durable manual decisions and print history.</summary>
public sealed class DpapiAutomaticMatchCacheStore(string path, TimeProvider? timeProvider = null) : IAutomaticMatchCacheStore
{
    private readonly string _path = Path.GetFullPath(path);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private const int MaxAccounts = 8;
    private const long MaxFileBytes = 32 * 1024 * 1024;

    public async Task<AutomaticMatchCache> LoadAsync(string accountContext, int retentionDays,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(accountContext, retentionDays);
        using var lease = await MarketplaceStoreFiles.LockAsync(_path, cancellationToken).ConfigureAwait(false);
        var caches = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var cache = caches.FirstOrDefault(c => c.AccountContext == accountContext) ?? new() { AccountContext = accountContext };
        cache.RetentionDays = retentionDays;
        CachedReceiptOrderMatchingService.Prune(cache, _clock.GetUtcNow());
        return cache;
    }

    public async Task SaveAsync(string accountContext, AutomaticMatchCache cache, int retentionDays,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(accountContext, retentionDays);
        ArgumentNullException.ThrowIfNull(cache);
        if (cache.AccountContext != accountContext) throw new ArgumentException("Обліковий контекст кешу не збігається.", nameof(cache));
        // Snapshot before awaiting: later UI mutations cannot alter this queued write.
        var copy = JsonSerializer.Deserialize<AutomaticMatchCache>(JsonSerializer.Serialize(cache))!;
        copy.RetentionDays = retentionDays;
        CachedReceiptOrderMatchingService.Prune(copy, _clock.GetUtcNow());
        using var lease = await MarketplaceStoreFiles.LockAsync(_path, cancellationToken).ConfigureAwait(false);
        var caches = await ReadAsync(cancellationToken).ConfigureAwait(false);
        caches.RemoveAll(c => c.AccountContext == accountContext);
        caches.Add(copy);
        foreach (var item in caches) CachedReceiptOrderMatchingService.Prune(item, _clock.GetUtcNow());
        caches = caches.Where(c => c.Details.Count > 0 || c.Snapshots.Count > 0)
            .OrderByDescending(NewestEntry).Take(MaxAccounts).ToList();
        // Bound the serialized payload as well as entry counts; unusually large API product lists
        // must not produce a file that our own guarded reader will subsequently reject.
        if (JsonSerializer.SerializeToUtf8Bytes(caches, MarketplaceStoreFiles.JsonOptions).LongLength > MaxFileBytes / 2)
            throw new InvalidDataException("Кеш автозв’язків завеликий; поточні результати доступні без його збереження.");
        await MarketplaceStoreFiles.WriteProtectedAsync(_path, caches, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<AutomaticMatchCache>> ReadAsync(CancellationToken token)
    {
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length > MaxFileBytes) return [];
            var caches = await MarketplaceStoreFiles.ReadProtectedAsync<List<AutomaticMatchCache>>(_path, token).ConfigureAwait(false) ?? [];
            if (caches.Any(c => c is null || string.IsNullOrWhiteSpace(c.AccountContext) || c.Details is null || c.Snapshots is null ||
                    c.Details.Any(d => d.Value?.Details?.Items is null || string.IsNullOrWhiteSpace(d.Value.ReceiptFingerprint) ||
                        string.IsNullOrWhiteSpace(d.Value.Details.Id) || d.Value.Details.Items.Any(i => i is null || i.Name is null || i.Sku is null)) ||
                    c.Snapshots.Any(s => s?.Matches is null || s.Matches.Any(m => m.Value is null || m.Value.Candidates is null ||
                        m.Value.GroupOrders is null || m.Value.CompetingReceiptIds is null ||
                        !Enum.IsDefined(m.Value.State) || !Enum.IsDefined(m.Value.Basis) || !Enum.IsDefined(m.Value.Products) ||
                        (m.Value.Order is not null && !ValidKey(m.Value.Order)) ||
                        m.Value.Candidates.Concat(m.Value.GroupOrders).Any(k => !ValidKey(k)))))) return [];
            return caches;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or JsonException)
        {
            // Rebuildable cache must not block receipt printing or manual/fiscal links. No payload in diagnostics.
            return [];
        }
    }

    private static DateTimeOffset NewestEntry(AutomaticMatchCache cache) => cache.Details.Values.Select(d => d.CachedAtUtc)
        .Concat(cache.Snapshots.Select(s => s.CachedAtUtc)).DefaultIfEmpty(DateTimeOffset.MinValue).Max();

    private static bool ValidKey(OrderKey? key) => key is not null && Enum.IsDefined(key.Marketplace) &&
        !string.IsNullOrWhiteSpace(key.ConnectionId) && !string.IsNullOrWhiteSpace(key.OrderId);

    private static void ValidateArguments(string account, int days)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        if (days is < 1 or > 90) throw new ArgumentOutOfRangeException(nameof(days));
    }
}
