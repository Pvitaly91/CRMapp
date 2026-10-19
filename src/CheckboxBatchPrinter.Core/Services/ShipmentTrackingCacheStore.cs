using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public interface IShipmentTrackingCacheStore
{
    Task<ShipmentTrackingCache> LoadAsync(string accountContext, CancellationToken cancellationToken = default);
    Task SaveAsync(string accountContext, ShipmentTrackingCache cache, CancellationToken cancellationToken = default);
}

/// <summary>Encrypted tracking metadata, shared across date filters and retained across restarts.</summary>
public sealed class DpapiShipmentTrackingCacheStore(string path) : IShipmentTrackingCacheStore
{
    private readonly string _path = Path.GetFullPath(path);
    private const int MaxContexts = 8;
    private const int MaxStatuses = 50000;
    private const int MaxAttempts = 100000;
    private const long MaxFileBytes = 32 * 1024 * 1024;

    public async Task<ShipmentTrackingCache> LoadAsync(string accountContext, CancellationToken cancellationToken = default)
    {
        ValidateContext(accountContext);
        using var lease = await MarketplaceStoreFiles.LockAsync(_path, cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await ReadAsync(cancellationToken).ConfigureAwait(false);
            return all.FirstOrDefault(x => x.AccountContext == accountContext) ?? new() { AccountContext = accountContext };
        }
        catch (Exception exception) when (IsCacheFailure(exception))
        {
            // Optional cache cannot prevent launch. Leave the damaged file intact for recovery.
            return new() { AccountContext = accountContext };
        }
    }

    public async Task SaveAsync(string accountContext, ShipmentTrackingCache cache, CancellationToken cancellationToken = default)
    {
        ValidateContext(accountContext);
        ArgumentNullException.ThrowIfNull(cache);
        if (cache.AccountContext != accountContext)
            throw new ArgumentException("Обліковий контекст кешу відстеження не збігається.", nameof(cache));
        // Capture caller-owned collections before awaiting, so later UI mutations cannot affect this write.
        var copy = Normalize(cache);
        using var lease = await MarketplaceStoreFiles.LockAsync(_path, cancellationToken).ConfigureAwait(false);
        // Do not overwrite an unreadable previous file with a partial date-filter snapshot.
        var all = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var previous = all.FirstOrDefault(x => x.AccountContext == accountContext);
        if (previous is not null)
            copy = Normalize(copy with
            {
                Statuses = previous.Statuses.Concat(copy.Statuses).ToArray(),
                Attempts = previous.Attempts.Concat(copy.Attempts).ToArray()
            });
        all.RemoveAll(x => x.AccountContext == accountContext);
        all.Add(copy);
        all = all.OrderByDescending(NewestEntry).Take(MaxContexts).ToList();
        if (JsonSerializer.SerializeToUtf8Bytes(all, MarketplaceStoreFiles.JsonOptions).LongLength > MaxFileBytes / 2)
            throw new InvalidDataException("Кеш відстеження завеликий; останню збережену версію не змінено.");
        await MarketplaceStoreFiles.WriteProtectedAsync(_path, all, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<ShipmentTrackingCache>> ReadAsync(CancellationToken token)
    {
        if (File.Exists(_path) && new FileInfo(_path).Length > MaxFileBytes)
            throw new InvalidDataException("Кеш відстеження завеликий.");
        var all = await MarketplaceStoreFiles.ReadProtectedAsync<List<ShipmentTrackingCache>>(_path, token).ConfigureAwait(false) ?? [];
        if (all.Count > MaxContexts || all.Any(x => x is null) ||
            all.Select(x => x.AccountContext).Distinct(StringComparer.Ordinal).Count() != all.Count)
            throw new InvalidDataException("Кеш відстеження пошкоджений; його не перезаписано.");
        return all.Select(Normalize).ToList();
    }

    private static ShipmentTrackingCache Normalize(ShipmentTrackingCache cache)
    {
        ValidateContext(cache.AccountContext);
        if (cache.Schema != 1 || cache.Statuses is null || cache.Attempts is null ||
            cache.Statuses.Count > MaxStatuses * 2 || cache.Attempts.Count > MaxAttempts * 2)
            throw new InvalidDataException("Некоректний кеш відстеження.");
        foreach (var status in cache.Statuses)
        {
            if (status is null || !Enum.IsDefined(status.Carrier) || status.Carrier == ShippingCarrier.Unknown ||
                !ValidText(status.TrackingNumber, 256) || !ValidText(status.Status, 2000) ||
                !ValidText(status.Source, 1000) || !ValidText(status.ShipmentId, 256, true) ||
                !ValidText(status.StatusCode, 100, true) || status.CheckedAtUtc == default)
                throw new InvalidDataException("Некоректний статус відстеження.");
            if (status.Order is not null) MarketplaceStoreFiles.ValidateKey(status.Order);
        }
        foreach (var attempt in cache.Attempts)
        {
            if (attempt is null || !Enum.IsDefined(attempt.Carrier) || attempt.Carrier == ShippingCarrier.Unknown ||
                !ValidText(attempt.TrackingNumber, 256) || !ValidText(attempt.ShipmentId, 256, true) ||
                attempt.Order is null || attempt.AttemptedAtUtc == default)
                throw new InvalidDataException("Некоректна спроба відстеження.");
            MarketplaceStoreFiles.ValidateKey(attempt.Order);
        }
        var statuses = cache.Statuses.GroupBy(x => (x.Carrier, x.TrackingNumber, x.ShipmentId, x.Order))
            .Select(x => x.OrderByDescending(s => s.CheckedAtUtc).First()).ToArray();
        var attempts = cache.Attempts.GroupBy(x => (x.Carrier, x.TrackingNumber, x.Order, x.ShipmentId))
            .Select(x => x.OrderByDescending(a => a.AttemptedAtUtc).First()).ToArray();
        if (statuses.Length > MaxStatuses || attempts.Length > MaxAttempts)
            throw new InvalidDataException("Кеш відстеження завеликий; останню збережену версію не змінено.");
        return cache with { Statuses = statuses, Attempts = attempts };
    }

    private static DateTimeOffset NewestEntry(ShipmentTrackingCache cache) => cache.Statuses.Select(x => x.CheckedAtUtc)
        .Concat(cache.Attempts.Select(x => x.AttemptedAtUtc)).DefaultIfEmpty(DateTimeOffset.MinValue).Max();

    private static void ValidateContext(string accountContext)
    {
        if (!ValidText(accountContext, 256)) throw new ArgumentException("Некоректний обліковий контекст кешу.", nameof(accountContext));
    }

    private static bool ValidText(string? value, int maximum, bool allowEmpty = false) => value is not null &&
        value.Length <= maximum && (allowEmpty || !string.IsNullOrWhiteSpace(value)) && !value.Any(char.IsControl);

    private static bool IsCacheFailure(Exception exception) => exception is IOException or InvalidDataException or
        UnauthorizedAccessException or Win32Exception or CryptographicException or JsonException or ArgumentException;
}
