using System.IO;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class OrderNotificationTests
{
    public static IEnumerable<(string, Func<Task>)> All => new (string, Func<Task>)[]
    {
        ("notifications quietly baseline old orders and group new arrivals once", BaselineAndBatchAsync),
        ("notifications survive restart and preserve marketplace and shop identity", RestartAsync),
        ("notifications defer incomplete sources but continue successful shops", PartialAsync),
        ("muted notifications record IDs and do not replay a backlog", MutedAsync),
        ("notifications ignore archived and undated orders outside the monitored window", RangeAsync),
        ("notification persistence failure cannot return a popup before a durable record", SaveFailureAsync),
        ("notification journal is DPAPI protected without buyer or product payloads", ProtectedStoreAsync),
        ("notification setting defaults on for old profiles and persists off", SettingsAsync),
        ("concurrent notification observations hand each order to Windows only once", ConcurrentAsync)
    };
    private static readonly NotificationSource Prom = new(MarketplaceKind.Prom, "test-prom");
    private static readonly NotificationSource Rozetka = new(MarketplaceKind.Rozetka, "test-rozetka");
    private static readonly DateOnly Day = new(2026, 9, 30);
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);

    private static MarketplaceOrder Order(string id, DateTimeOffset created, NotificationSource? source = null) => new()
    {
        Key = new((source ?? Prom).Marketplace, (source ?? Prom).ConnectionId, id), Number = id,
        CreatedAt = created, Total = 100, Currency = "UAH", Buyer = new("SYNTHETIC-BUYER"),
        Items = [new("SYNTHETIC-PRODUCT", "", 1, 100)]
    };
    private static OrderNotificationSnapshot Snapshot(params MarketplaceOrder[] orders) => new(orders,
        [new(Prom, true, Start.AddMinutes(-10))]);
    private static Task<NewOrdersNotification?> Observe(NewOrderNotificationService service,
        OrderNotificationSnapshot snapshot, bool enabled = true) => service.ObserveAsync(snapshot, Day.AddDays(-6), Day, enabled);

    private static async Task BaselineAndBatchAsync()
    {
        var clock = new Clock(); var store = new Store(); var service = new NewOrderNotificationService(store, clock);
        var old = Order("old", Start.AddHours(-1));
        Check(await Observe(service, Snapshot(old)) is null);
        clock.Now = Start.AddMinutes(3);
        var arrivals = Snapshot(old, Order("new-1", Start.AddMinutes(1)), Order("new-2", Start.AddMinutes(2)));
        var notice = await Observe(service, arrivals);
        Check(notice?.Count == 2 && notice.Title == "Нові замовлення: 2" && notice.Body.Contains("Prom №new-1"));
        Check(!notice!.Body.Contains("SYNTHETIC-BUYER") && !notice.Body.Contains("SYNTHETIC-PRODUCT"));
        Check(await Observe(service, arrivals) is null);
        Check(store.State.Seen.Count == 3);
    }

    private static async Task RestartAsync()
    {
        var clock = new Clock(); var store = new Store(); var service = new NewOrderNotificationService(store, clock);
        var cached = Snapshot(Order("old", Start.AddHours(-1)));
        await service.SeedFromCacheAsync(cached);
        clock.Now = Start.AddMinutes(3);
        var fresh = Snapshot(cached.Orders.Single(), Order("new", Start.AddMinutes(1)));
        Check((await Observe(service, fresh))?.Count == 1);
        var restarted = new NewOrderNotificationService(store, clock);
        await restarted.SeedFromCacheAsync(fresh);
        Check(await Observe(restarted, fresh) is null);
        var secondShop = new NotificationSource(MarketplaceKind.Prom, "test-prom-2");
        await restarted.SeedFromCacheAsync(new([], [new(Rozetka, true), new(secondShop, true)]));
        clock.Now = Start.AddMinutes(5);
        var sameIds = new OrderNotificationSnapshot([.. fresh.Orders,
            Order("new", Start.AddMinutes(4), Rozetka), Order("new", Start.AddMinutes(4), secondShop)],
            [new(Prom, true), new(Rozetka, true), new(secondShop, true)]);
        Check((await Observe(restarted, sameIds))?.Count == 2);
        Check(await Observe(new NewOrderNotificationService(store, clock), sameIds) is null);
    }

    private static async Task PartialAsync()
    {
        var clock = new Clock(); var service = new NewOrderNotificationService(new Store(), clock);
        await service.SeedFromCacheAsync(new([], [new(Prom, true, Start), new(Rozetka, true, Start)]));
        clock.Now = Start.AddMinutes(2);
        var orders = new[] { Order("prom-new", Start.AddMinutes(1)), Order("rozetka-new", Start.AddMinutes(1), Rozetka) };
        var partial = new OrderNotificationSnapshot(orders, [new(Prom, false), new(Rozetka, true)]);
        var first = await Observe(service, partial);
        Check(first?.Count == 1 && first.Body.Contains("Rozetka") && !first.Body.Contains("prom-new"));
        var complete = partial with { Sources = [new(Prom, true), new(Rozetka, true)] };
        Check((await Observe(service, complete))?.Body.Contains("prom-new") == true);
        Check(await Observe(service, complete) is null);
    }

    private static async Task MutedAsync()
    {
        var clock = new Clock(); var service = new NewOrderNotificationService(new Store(), clock);
        await service.SeedFromCacheAsync(Snapshot());
        clock.Now = Start.AddMinutes(2);
        var muted = Snapshot(Order("muted", Start.AddMinutes(1)));
        Check(await Observe(service, muted, false) is null);
        Check(await Observe(service, muted) is null);
        clock.Now = Start.AddMinutes(4);
        Check((await Observe(service, Snapshot(muted.Orders.Single(), Order("enabled", Start.AddMinutes(3)))))?.Count == 1);
    }

    private static async Task RangeAsync()
    {
        var clock = new Clock(); var service = new NewOrderNotificationService(new Store(), clock);
        await service.SeedFromCacheAsync(Snapshot());
        clock.Now = Start.AddMinutes(5);
        var snapshot = Snapshot(Order("archive", Start.AddDays(-20)), Order("no-date", Start) with { CreatedAt = null },
            Order("current", Start.AddMinutes(1)));
        Check((await Observe(service, snapshot))?.Count == 1);
        Check(await Observe(service, snapshot) is null);
    }

    private static async Task SaveFailureAsync()
    {
        var clock = new Clock(); var store = new Store(); var service = new NewOrderNotificationService(store, clock);
        await service.SeedFromCacheAsync(Snapshot());
        clock.Now = Start.AddMinutes(2); store.FailSave = true;
        var snapshot = Snapshot(Order("new", Start.AddMinutes(1)));
        try { await Observe(service, snapshot); throw new Exception("Expected durable journal failure."); }
        catch (IOException) { }
        Check(store.State.Seen.Count == 0);
        store.FailSave = false;
        Check((await Observe(service, snapshot))?.Count == 1);
        Check(await Observe(service, snapshot) is null);
    }

    private static async Task ProtectedStoreAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cbp-notification-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "notifications.dpapi");
        var clock = new Clock(); var store = new DpapiOrderNotificationStore(path);
        var service = new NewOrderNotificationService(store, clock);
        await service.SeedFromCacheAsync(Snapshot());
        clock.Now = Start.AddMinutes(2);
        var snapshot = Snapshot(Order("new", Start.AddMinutes(1)));
        Check((await Observe(service, snapshot))?.Count == 1);
        Check(await Observe(new NewOrderNotificationService(new DpapiOrderNotificationStore(path), clock), snapshot) is null);
        var plain = await new DpapiCredentialStore(path).LoadPasswordAsync();
        Check(plain is not null && !plain.Contains("SYNTHETIC-BUYER") && !plain.Contains("SYNTHETIC-PRODUCT"));
        Check((await store.LoadAsync()).Seen.Single().Key.OrderId == "new");
    }

    private static async Task SettingsAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "cbp-notification-setting-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, "{\"AutoRefreshEnabled\":true}");
        var service = new JsonSettingsService(path);
        var old = await service.LoadAsync(); Check(old.NotifyNewOrders);
        old.NotifyNewOrders = false; await service.SaveAsync(old);
        Check(!(await new JsonSettingsService(path).LoadAsync()).NotifyNewOrders);
    }

    private static async Task ConcurrentAsync()
    {
        var clock = new Clock(); var store = new Store(); var service = new NewOrderNotificationService(store, clock);
        await service.SeedFromCacheAsync(Snapshot());
        clock.Now = Start.AddMinutes(2);
        var snapshot = Snapshot(Order("new", Start.AddMinutes(1)));
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Observe(service, snapshot)));
        Check(results.Count(n => n is not null) == 1 && store.State.Seen.Count == 1);
    }

    private static void Check(bool value) { if (!value) throw new Exception("Notification assertion failed."); }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = Start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Store : IOrderNotificationStore
    {
        private string _json = "{}";
        public bool FailSave;
        public OrderNotificationState State => JsonSerializer.Deserialize<OrderNotificationState>(_json)!;
        public Task<OrderNotificationState> LoadAsync(CancellationToken token = default) => Task.FromResult(State);
        public Task SaveAsync(OrderNotificationState state, CancellationToken token = default)
        {
            if (FailSave) throw new IOException("Synthetic journal failure.");
            _json = JsonSerializer.Serialize(state); return Task.CompletedTask;
        }
    }
}
