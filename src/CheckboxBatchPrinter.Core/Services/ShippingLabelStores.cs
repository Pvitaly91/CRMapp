using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public sealed class DpapiShippingSettingsStore(string path) : IShippingSettingsStore
{
    public async Task<ShippingLabelSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await MarketplaceStoreFiles.LockAsync(path, cancellationToken);
        return await MarketplaceStoreFiles.ReadProtectedAsync<ShippingLabelSettings>(path, cancellationToken) ?? new();
    }
    public async Task SaveAsync(ShippingLabelSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Print.Validate();
        using var lease = await MarketplaceStoreFiles.LockAsync(path, cancellationToken);
        await MarketplaceStoreFiles.WriteProtectedAsync(path, settings, cancellationToken);
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
