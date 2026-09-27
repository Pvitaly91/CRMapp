using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public sealed class DpapiShippingSettingsStore(string path) : IShippingSettingsStore
{
    public async Task<ShippingLabelSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await MarketplaceStoreFiles.LockAsync(path, cancellationToken);
        var value = await MarketplaceStoreFiles.ReadProtectedAsync<ShippingLabelSettings>(path, cancellationToken) ?? new();
        ValidateConnections(value); return value;
    }
    public async Task SaveAsync(ShippingLabelSettings settings, CancellationToken cancellationToken = default)
    {
        // Connection diagnostics must not require a configured printer. Printing validates it independently.
        (settings.Print.PrinterName.Length > 0 ? settings.Print : settings.Print with { PrinterName = "diagnostic-only" }).Validate();
        ValidateConnections(settings);
        using var lease = await MarketplaceStoreFiles.LockAsync(path, cancellationToken);
        await MarketplaceStoreFiles.WriteProtectedAsync(path, settings, cancellationToken);
    }
    private static void ValidateConnections(ShippingLabelSettings value)
    {
        if (value.NovaPoshtaConnections is null || value.NovaPoshtaStoreBindings is null || value.NovaPoshtaConnections.Count > 30 || value.NovaPoshtaStoreBindings.Count > 100)
            throw new InvalidDataException("Некоректні локальні підключення NP; файл не перезаписано.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in value.NovaPoshtaConnections)
        {
            if (c is null || !ids.Add(MarketplaceStoreFiles.ConnectionFileId(c.Id)) || string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 80 ||
                c.Name.Any(char.IsControl) || c.ApiKey is null || c.ApiKey.Length > 0 && !NovaPoshtaReadOnlyContract.ValidKey(c.ApiKey))
                throw new InvalidDataException("Некоректне підключення NP; ключ не відображається.");
        }
        var stores = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in value.NovaPoshtaStoreBindings)
            if (b is null || !stores.Add(MarketplaceStoreFiles.ConnectionFileId(b.MarketplaceConnectionId)) ||
                !ids.Contains(MarketplaceStoreFiles.ConnectionFileId(b.NovaPoshtaConnectionId)))
                throw new InvalidDataException("Магазин повинен мати одне явно вибране існуюче NP-підключення.");
    }
}
public sealed class DpapiLabelHistoryStore(string path) : ILabelHistoryStore
{
    public async Task<IReadOnlyList<LabelPrintAttempt>> LoadAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await MarketplaceStoreFiles.LockAsync(path, cancellationToken);
        var all = await MarketplaceStoreFiles.ReadProtectedAsync<List<LabelPrintAttempt>>(path, cancellationToken) ?? [];
        var recent = all.Where(a => a.TimeUtc >= DateTimeOffset.UtcNow.AddDays(-365)).TakeLast(10000).ToArray();
        if (recent.Length != all.Count) await MarketplaceStoreFiles.WriteProtectedAsync(path, recent, cancellationToken);
        return recent;
    }
    public async Task SaveAsync(LabelPrintAttempt attempt, CancellationToken cancellationToken = default)
    {
        using var lease = await MarketplaceStoreFiles.LockAsync(path, cancellationToken);
        var all = await MarketplaceStoreFiles.ReadProtectedAsync<List<LabelPrintAttempt>>(path, cancellationToken) ?? [];
        all.RemoveAll(a => a.AttemptId == attempt.AttemptId || a.TimeUtc < DateTimeOffset.UtcNow.AddDays(-365));
        all.Add(attempt);
        await MarketplaceStoreFiles.WriteProtectedAsync(path, all.TakeLast(10000).ToArray(), cancellationToken);
    }
}
