using System.ComponentModel;
using System.IO;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class ProtectedCacheRecoveryTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("protected cache: zeroed archive restores every order and subsequent saves retain history", Archive),
        ("protected cache: both corrupt copies fail explicitly without overwriting either file", BothCorrupt),
        ("protected cache: legacy primary works without backup and missing primary recovers from backup", LegacyAndMissing),
        ("protected cache: notification recovery preserves seen orders instead of repeating notifications", Notifications),
        ("protected cache: automatic links and details survive a zeroed primary after restart", AutomaticLinks),
        ("protected writes: concurrent and canceled credential saves are atomic and never restore a stale token", Credentials),
        ("background: local cache damage is distinguished from network failure", BackgroundStatus)
    ];
    private const string Connection = "c7000000000000000000000000000001";
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static MarketplaceOrder Order(string id) => new()
    {
        Key = new(MarketplaceKind.Prom, Connection, id), Number = id, Total = 100, Currency = "UAH",
        CreatedAt = DateTimeOffset.UtcNow.AddHours(-1), RetrievedAtUtc = DateTimeOffset.UtcNow,
        Items = [new("synthetic-cache-item", "sku", 1, 100, 100)], ItemsComplete = true, ItemListComplete = true
    };
    private static async Task InDirectory(Func<string, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "cbp-cache-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await body(root); }
        finally { Directory.Delete(root, true); }
    }
    private static Task Zero(string path) => File.WriteAllBytesAsync(path, new byte[new FileInfo(path).Length]);
    private static Task Archive() => InDirectory(async root =>
    {
        var path = Path.Combine(root, "orders.dpapi");
        var store = new DpapiMarketplaceCacheStore(path, preserveExpiredOrders: true);
        await store.SaveAsync(new([Order("old")], []));
        Check(File.Exists(path + ".bak"), "Archive has no recovery copy.");
        await Zero(path);
        var zeroed = await File.ReadAllBytesAsync(path);
        Check((await new DpapiMarketplaceCacheStore(path, preserveExpiredOrders: true).LoadAsync(30)).Orders.Single().Number == "old",
            "Zeroed archive made old orders disappear.");
        Check(zeroed.SequenceEqual(await File.ReadAllBytesAsync(path)), "Reading recovery rewrote the damaged source.");
        await store.SaveAsync(new([Order("new")], []));
        await Zero(path);
        var recovered = await store.LoadAsync(30);
        Check(recovered.Orders.Count == 2, "A range-limited save lost recovered history.");
    });
    private static Task BothCorrupt() => InDirectory(async root =>
    {
        var path = Path.Combine(root, "orders.dpapi");
        var store = new DpapiMarketplaceCacheStore(path, preserveExpiredOrders: true);
        await store.SaveAsync(new([Order("one")], []));
        await Zero(path); await Zero(path + ".bak");
        var before = await File.ReadAllBytesAsync(path);
        var backupBefore = await File.ReadAllBytesAsync(path + ".bak");
        try { await store.LoadAsync(30); throw new Exception("Both invalid copies were accepted."); }
        catch (LocalCacheException ex) { Check(!ex.Message.Contains("synthetic-cache-item"), "Cache payload leaked into error."); }
        Check(before.SequenceEqual(await File.ReadAllBytesAsync(path)) && backupBefore.SequenceEqual(await File.ReadAllBytesAsync(path + ".bak")),
            "Unrecoverable read replaced the archive with an empty snapshot.");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await store.LoadAsync(30, cancellation.Token); throw new Exception("Canceled read completed."); }
        catch (OperationCanceledException) { }
    });
    private static Task LegacyAndMissing() => InDirectory(async root =>
    {
        var path = Path.Combine(root, "orders.dpapi"); var store = new DpapiMarketplaceCacheStore(path);
        await store.SaveAsync(new([Order("legacy")], []));
        File.Delete(path + ".bak");
        Check((await store.LoadAsync(30)).Orders.Count == 1, "Existing profiles now require a sidecar.");
        await store.SaveAsync(new([Order("legacy")], []));
        File.Delete(path);
        Check((await store.LoadAsync(30)).Orders.Count == 1, "Missing primary ignored the recovery copy.");
    });
    private static Task Notifications() => InDirectory(async root =>
    {
        var path = Path.Combine(root, "notifications.dpapi"); var order = Order("seen");
        var source = new NotificationSource(MarketplaceKind.Prom, Connection);
        await new DpapiOrderNotificationStore(path).SaveAsync(new()
        {
            Seen = [new(order.Key, DateTimeOffset.UtcNow)], Baselines = [new(source, DateTimeOffset.UtcNow.AddDays(-1))]
        });
        await Zero(path);
        var store = new DpapiOrderNotificationStore(path);
        Check((await store.LoadAsync()).Seen.Single().Key == order.Key, "Recovery forgot previously seen orders.");
        var notice = await new NewOrderNotificationService(store).ObserveAsync(new([order], [new(source, true, DateTimeOffset.UtcNow)]),
            DateOnly.FromDateTime(DateTime.Today).AddDays(-1), DateOnly.FromDateTime(DateTime.Today), true, CancellationToken.None);
        Check(notice is null, "Recovery repeated a notification for an already seen order.");
    });
    private static Task AutomaticLinks() => InDirectory(async root =>
    {
        const string account = "synthetic-cache-account";
        var path = Path.Combine(root, "automatic.dpapi");
        var order = Order("linked") with { CreatedAt = DateTimeOffset.UtcNow };
        var receipt = new ReceiptRecord { Id = "c7000000-0000-0000-0000-000000000001", Type = "SELL", Status = "DONE",
            FiscalDate = order.CreatedAt, TotalSumMinor = 10000 };
        var cache = new AutomaticMatchCache { AccountContext = account };
        var details = new ReceiptDetails(receipt.Id, [new("synthetic-cache-item", "sku", 1, 100, 100)]) { ItemListComplete = true };
        var matcher = new CachedReceiptOrderMatchingService();
        matcher.RememberDetails(cache, account, receipt, details);
        var initial = matcher.MatchAll(cache, [receipt], account, [order], [], true, new Dictionary<string, ReceiptDetails> { [receipt.Id] = details });
        Check(initial.Matches[receipt.Id].State == ReceiptLinkState.Suggested, "Recovery fixture must start with a valid linked receipt.");
        await new DpapiAutomaticMatchCacheStore(path).SaveAsync(account, cache, 30);
        await Zero(path);
        var restored = await new DpapiAutomaticMatchCacheStore(path).LoadAsync(account, 30);
        Check(restored.Details.ContainsKey(receipt.Id) && restored.Links.ContainsKey(receipt.Id), "Corruption discarded recoverable receipt associations.");
        Check((await new DpapiAutomaticMatchCacheStore(path).LoadAsync("different-account", 30)).Links.Count == 0, "Recovery crossed cashier scopes.");
    });
    private static Task Credentials() => InDirectory(async root =>
    {
        var path = Path.Combine(root, "credential.bin");
        var store = new DpapiCredentialStore(path);
        await store.SavePasswordAsync("synthetic-old-token");
        await File.WriteAllBytesAsync(path + ".bak", await File.ReadAllBytesAsync(path));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => store.SavePasswordAsync("synthetic-new-token-" + i)));
        Check((await store.LoadPasswordAsync())!.StartsWith("synthetic-new-token-"), "Concurrent writes corrupted the protected value.");
        var before = await File.ReadAllBytesAsync(path);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await store.SavePasswordAsync("must-not-replace", cancellation.Token); throw new Exception("Canceled write completed."); }
        catch (OperationCanceledException) { }
        Check(before.SequenceEqual(await File.ReadAllBytesAsync(path)), "Cancellation replaced the saved credential.");
        Check(Directory.GetFiles(root, "*.tmp").Length == 0, "Canceled/concurrent writes left pending files.");
        await Zero(path);
        try { await store.LoadPasswordAsync(); throw new Exception("Credential silently reverted to a stale backup."); }
        catch (Win32Exception) { }
    });
    private static async Task BackgroundStatus()
    {
        var receipts = 0;
        await using var coordinator = new BackgroundSyncCoordinator(_ => { receipts++; return Task.CompletedTask; },
            _ => throw new LocalCacheException(), _ => Task.FromResult(new AppSettings()));
        await coordinator.RefreshNowAsync();
        var state = coordinator.States.Single(s => s.Name == "Маркетплейси");
        Check(receipts == 1 && state.Status.Contains("Локальний кеш") && !state.Status.Contains("Немає мережі"),
            "Local file damage was reported as a network outage or blocked Checkbox.");
    }
}
