using System.Security.Cryptography;
using System.Text.Json;
using System.ComponentModel;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public sealed record ReceiptSnapshot(string AccountContext, DateOnly From, DateOnly To,
    DateTimeOffset LastSuccessUtc, bool Complete, IReadOnlyList<ReceiptRecord> Receipts,
    IReadOnlyDictionary<string, string> Fingerprints, int Schema = 1);

public interface IReceiptSnapshotStore
{
    Task<ReceiptSnapshot?> LoadAsync(string account, CancellationToken cancellationToken = default);
    Task SaveAsync(ReceiptSnapshot snapshot, CancellationToken cancellationToken = default);
}

/// <summary>DPAPI-protected, bounded list metadata. Receipt PNGs and credentials are never stored here.</summary>
public sealed class DpapiReceiptSnapshotStore(string path, TimeProvider? clock = null) : IReceiptSnapshotStore
{
    private readonly string _path = Path.GetFullPath(path);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private const int MaxDays = 30;
    private const int MaxReceipts = 30000;
    private const long MaxFileBytes = 24 * 1024 * 1024;

    public async Task<ReceiptSnapshot?> LoadAsync(string account, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        using var lease = await MarketplaceStoreFiles.LockAsync(_path, cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length > MaxFileBytes) return null;
            var snapshots = await MarketplaceStoreFiles.ReadProtectedAsync<List<ReceiptSnapshot>>(_path, cancellationToken)
                .ConfigureAwait(false) ?? [];
            return snapshots.FirstOrDefault(x => x.Schema == 1 && x.AccountContext == account && x.Complete &&
                x.Receipts is not null && x.Fingerprints is not null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or CryptographicException)
        { return null; } // Rebuildable cache must not block launch or overwrite durable decisions.
    }

    public async Task SaveAsync(ReceiptSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.AccountContext);
        if (snapshot.Schema != 1 || snapshot.To < snapshot.From || !snapshot.Complete)
            throw new ArgumentException("Неповну вибірку чеків не можна записати як перевірену.", nameof(snapshot));
        using var lease = await MarketplaceStoreFiles.LockAsync(_path, cancellationToken).ConfigureAwait(false);
        List<ReceiptSnapshot> all;
        try { all = await MarketplaceStoreFiles.ReadProtectedAsync<List<ReceiptSnapshot>>(_path, cancellationToken).ConfigureAwait(false) ?? []; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or CryptographicException)
        { all = []; }
        var cutoff = _clock.GetUtcNow().AddDays(-MaxDays);
        all.RemoveAll(x => x.AccountContext == snapshot.AccountContext || x.LastSuccessUtc < cutoff || x.Schema != 1);
        var rows = snapshot.Receipts.Where(x => x.DisplayDate is { } date && date >= cutoff)
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(x => x.Last()).Take(MaxReceipts).ToArray();
        var copy = snapshot with { Receipts = rows, Fingerprints = FingerprintsFor(rows) };
        all.Add(copy);
        all = all.OrderByDescending(x => x.LastSuccessUtc).Take(4).ToList();
        if (JsonSerializer.SerializeToUtf8Bytes(all, MarketplaceStoreFiles.JsonOptions).LongLength > MaxFileBytes / 2)
            throw new InvalidDataException("Кеш чеків завеликий; останню збережену версію не змінено.");
        await MarketplaceStoreFiles.WriteProtectedAsync(_path, all, cancellationToken).ConfigureAwait(false);
    }

    public static IReadOnlyDictionary<string, string> FingerprintsFor(IEnumerable<ReceiptRecord> rows) =>
        rows.ToDictionary(x => x.Id, x => Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(x, MarketplaceStoreFiles.JsonOptions))), StringComparer.OrdinalIgnoreCase);
}
