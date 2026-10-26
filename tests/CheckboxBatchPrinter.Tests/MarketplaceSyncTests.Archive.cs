using System.IO;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceSyncTests
{
    private static async Task ArchiveRetentionAndRestartAsync()
    {
        await InArchiveDirectoryAsync(async directory =>
        {
            var clock = new ExpiringClock { Utc = Now };
            var old = Order("historical", "accepted", created: Now.AddDays(-150)) with
            { RetrievedAtUtc = Now.AddDays(-120) };
            var recent = Order("recent", "new") with { RetrievedAtUtc = Now.AddDays(-1) };
            var currentRange = new MarketplaceRange(Now.AddDays(-14), Now.AddDays(1));
            var state = new ConnectionSyncState(PromId, currentRange, true, Now, "complete", Now);
            var path = Path.Combine(directory, "archive.dpapi");
            await new DpapiMarketplaceCacheStore(path, clock, preserveExpiredOrders: true)
                .SaveAsync(new([old, recent], [state]));

            var first = await new DpapiMarketplaceCacheStore(path, clock, preserveExpiredOrders: true).LoadAsync(30);
            Equal(2, first.Orders.Count);
            Equal(old.RetrievedAtUtc, first.Orders.Single(order => order.Key == old.Key).RetrievedAtUtc);
            True(first.States.Single().Complete);

            clock.Utc = Now.AddDays(60);
            var restarted = await new DpapiMarketplaceCacheStore(path, clock, preserveExpiredOrders: true).LoadAsync(30);
            Equal(2, restarted.Orders.Count);
            Equal(0, restarted.States.Count); // Retaining rows does not extend a successful coverage checkpoint.
            Equal(old.RetrievedAtUtc, restarted.Orders.Single(order => order.Key == old.Key).RetrievedAtUtc);
            Equal(recent.RetrievedAtUtc, restarted.Orders.Single(order => order.Key == recent.Key).RetrievedAtUtc);
            Equal(2, (await new DpapiMarketplaceCacheStore(path, clock, preserveExpiredOrders: true).LoadAsync(1)).Orders.Count);

            // Existing callers keep their explicit TTL contract unless they opt into the archive.
            var expiringPath = Path.Combine(directory, "ttl.dpapi");
            await new DpapiMarketplaceCacheStore(expiringPath, clock).SaveAsync(new([old, recent], [state]));
            var expired = await new DpapiMarketplaceCacheStore(expiringPath, clock).LoadAsync(30);
            Equal(0, expired.Orders.Count); Equal(0, expired.States.Count);
        });
    }

    private static async Task ArchiveRangeCoverageAsync()
    {
        await InArchiveDirectoryAsync(async directory =>
        {
            foreach (var scope in new[] { "outside", "inside", "unknown" })
            {
                var old = Order("historical", "accepted", created: Now.AddDays(-40)) with
                { RetrievedAtUtc = Now.AddDays(-31) };
                if (scope == "unknown") old = old with { CreatedAt = null };
                var range = new MarketplaceRange(Now.AddDays(scope == "inside" ? -90 : -14), Now.AddDays(1));
                var state = new ConnectionSyncState(PromId, range, true, Now, "complete", Now);
                var path = Path.Combine(directory, scope + ".dpapi");
                var store = new DpapiMarketplaceCacheStore(path, new Clock(), preserveExpiredOrders: true);
                await store.SaveAsync(new([old], [state]));

                var loaded = await new DpapiMarketplaceCacheStore(path, new Clock(), preserveExpiredOrders: true).LoadAsync(30);
                Equal(1, loaded.Orders.Count);
                Equal(scope == "outside", loaded.States.Single().Complete);
                Equal(state.AttemptedAtUtc, loaded.States.Single().AttemptedAtUtc);
                Equal(state.LastSuccessUtc, loaded.States.Single().LastSuccessUtc);
                Equal(state.Range, loaded.States.Single().Range);
                if (scope != "outside") True(loaded.States.Single().Message.Contains("прострочена"));
            }
        });
    }

    private static async Task ArchiveSaveMergeAsync()
    {
        await InArchiveDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "archive.dpapi");
            var clock = new Clock();
            var archived = Order("42", "old", created: Now.AddDays(-100)) with { RetrievedAtUtc = Now.AddDays(-90) };
            var newer = archived with { Status = "accepted", RetrievedAtUtc = Now.AddDays(-2) };
            var otherSeller = archived with { Key = archived.Key with { ConnectionId = RozetkaId } };
            var otherMarketplace = archived with { Key = archived.Key with { Marketplace = MarketplaceKind.Rozetka } };
            var cache = new DpapiMarketplaceCacheStore(path, clock, preserveExpiredOrders: true);
            await cache.SaveAsync(new([archived, newer, otherSeller, otherMarketplace], []));

            var current = Order("current", "new");
            // Simulate a caller saving only the currently selected range and a delayed old response.
            await new DpapiMarketplaceCacheStore(path, clock, preserveExpiredOrders: true)
                .SaveAsync(new([current, archived], []));
            var restarted = await new DpapiMarketplaceCacheStore(path, clock, preserveExpiredOrders: true).LoadAsync(30);
            Equal(4, restarted.Orders.Count);
            Equal("accepted", restarted.Orders.Single(order => order.Key == archived.Key).Status);
            Equal(newer.RetrievedAtUtc, restarted.Orders.Single(order => order.Key == archived.Key).RetrievedAtUtc);
            True(restarted.Orders.Any(order => order.Key == otherSeller.Key));
            True(restarted.Orders.Any(order => order.Key == otherMarketplace.Key));

            var updated = newer with { Status = "delivered", RetrievedAtUtc = Now };
            await cache.SaveAsync(new([updated, updated], []));
            var final = await new DpapiMarketplaceCacheStore(path, clock, preserveExpiredOrders: true).LoadAsync(30);
            Equal(4, final.Orders.Count);
            Equal("delivered", final.Orders.Single(order => order.Key == archived.Key).Status);
        });
    }

    private static async Task ArchiveCurrentRangeRefreshAsync()
    {
        await InArchiveDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "archive.dpapi");
            var clock = new Clock();
            var history = Enumerable.Range(0, 1000).Select(index =>
                Order("historical-" + index, "accepted", created: Now.AddDays(-120)) with
                { RetrievedAtUtc = Now.AddDays(-100) }).ToArray();
            var cache = new DpapiMarketplaceCacheStore(path, clock, preserveExpiredOrders: true);
            await cache.SaveAsync(new(history, []));
            // Reproduce a power-loss snapshot: the primary exists at its real size but contains only zeros.
            await File.WriteAllBytesAsync(path, new byte[new FileInfo(path).Length]);
            var current = Order("current", "new");
            var client = new FakeClient(MarketplaceKind.Prom)
            { Fetch = (_, _) => Task.FromResult(new OrdersFetchResult([current], true)) };
            var service = new MarketplaceSyncService([client], new MemorySecrets(), cache, clock);
            var currentRange = new MarketplaceRange(Now.AddDays(-14), Now.AddDays(1));

            var result = await service.SynchronizeAsync(Settings(), currentRange, []);
            Equal(1001, result.Orders.Count); True(result.States.Single().Complete);
            Equal(1, client.FetchCount); Equal(0, client.DetailIds.Count);
            var restarted = await new DpapiMarketplaceCacheStore(path, clock, preserveExpiredOrders: true).LoadAsync(30);
            Equal(1001, restarted.Orders.Count); True(restarted.States.Single().Complete);
            True(restarted.Orders.Where(order => order.Key != current.Key)
                .All(order => order.RetrievedAtUtc == Now.AddDays(-100)));
        });
    }

    private static async Task InArchiveDirectoryAsync(Func<string, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cbp-marketplace-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { await test(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
