using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task BackgroundNotificationsAsync()
    {
        var fixture = DuplicateFixture();
        fixture.ReceiptSource.Rows = [];
        var (main, workspace) = fixture.Create();
        main.CoordinatorManaged = workspace.CoordinatorManaged = true;
        await main.RefreshAsync();
        await workspace.EnsureAttachedAsync();
        main.SearchText = "hidden receipts";
        workspace.OrderSearch = "hidden orders";
        var today = DateOnly.FromDateTime(DateRangeBuilder.TodayKyiv);
        var now = DateTimeOffset.UtcNow;
        var clock = new NotificationClock { Now = now.AddMinutes(-10) };
        var store = new NotificationStore();
        var notifications = new NewOrderNotificationService(store, clock);
        var shown = new List<NewOrdersNotification>();
        await using var coordinator = new BackgroundSyncCoordinator(_ => Task.CompletedTask, async token =>
        {
            workspace.SetBackgroundRange(today.AddDays(-6), today);
            var started = DateTimeOffset.UtcNow;
            await workspace.SyncBackgroundAsync(token);
            var snapshot = workspace.GetOrderNotificationSnapshot(started);
            Equal(1, snapshot.Sources.Count); // Proven identical-token aliases are one source.
            True(snapshot.Sources.Single().Complete);
            var notification = await notifications.ObserveAsync(snapshot, today.AddDays(-6), today, true, token);
            if (notification is not null) shown.Add(notification);
        }, _ => Task.FromResult(fixture.Settings.App));
        await coordinator.RefreshNowAsync(); // Quiet initial list, no notifications for historical orders.
        Equal(0, shown.Count);
        var arrival = Order() with { Key = new(MarketplaceKind.Prom, "prom-test", "arrival"),
            Number = "NEW-ORDER", CreatedAt = now.AddMinutes(-1) };
        fixture.Source.Orders = [.. fixture.Source.Orders, arrival,
            arrival with { Key = arrival.Key with { ConnectionId = DuplicatePromId } }];
        clock.Now = now;
        await coordinator.RefreshNowAsync();
        Equal(1, shown.Count); Equal(1, shown.Single().Count);
        True(shown.Single().Body.Contains("NEW-ORDER"));
        Equal(0, workspace.Orders.Cast<object>().Count()); // Hidden by search, still monitored.
        Equal("hidden orders", workspace.OrderSearch);
        Equal(0, main.SelectedTabIndex);
        var (_, restarted) = fixture.Create();
        restarted.CoordinatorManaged = true;
        await restarted.EnsureAttachedAsync();
        var tracker = new NewOrderNotificationService(store, clock);
        await tracker.SeedFromCacheAsync(restarted.GetOrderNotificationSnapshot());
        restarted.SetBackgroundRange(today.AddDays(-6), today);
        var restartedAt = DateTimeOffset.UtcNow;
        await restarted.SyncBackgroundAsync();
        True(await tracker.ObserveAsync(restarted.GetOrderNotificationSnapshot(restartedAt),
            today.AddDays(-6), today, true) is null);
        Equal(0, fixture.Printer.Calls);
    }

    private static async Task BackgroundPartialNotificationsAsync()
    {
        var fixture = new Fixture();
        fixture.ReceiptSource.Rows = [];
        fixture.Settings.Market.Connections.Add(new() { Id = "rz-test", Marketplace = MarketplaceKind.Rozetka,
            Enabled = true, Name = "Synthetic Rozetka" });
        var (_, workspace) = fixture.Create();
        workspace.CoordinatorManaged = true;
        await workspace.EnsureAttachedAsync();
        var now = DateTimeOffset.UtcNow;
        var today = DateRangeBuilder.KyivDate(now);
        workspace.SetBackgroundRange(today.AddDays(-6), today);
        var clock = new NotificationClock { Now = now.AddMinutes(-10) };
        var notifications = new NewOrderNotificationService(new NotificationStore(), clock);
        var started = DateTimeOffset.UtcNow;
        await workspace.SyncBackgroundAsync();
        True(await notifications.ObserveAsync(workspace.GetOrderNotificationSnapshot(started),
            today.AddDays(-6), today, true) is null);
        fixture.Source.Orders = [Order() with { CreatedAt = now.AddMinutes(-1) }];
        fixture.Rozetka.Orders = [new() { Key = new(MarketplaceKind.Rozetka, "rz-test", "new-rz"),
            Number = "RZ-NEW", CreatedAt = now.AddMinutes(-1) }];
        fixture.Source.Complete = false;
        clock.Now = now;
        started = DateTimeOffset.UtcNow;
        try { await workspace.SyncBackgroundAsync(); }
        catch (InvalidOperationException) { }
        var partial = workspace.GetOrderNotificationSnapshot(started);
        True(!partial.Sources.Single(s => s.Source.Marketplace == MarketplaceKind.Prom).Complete);
        True(partial.Sources.Single(s => s.Source.Marketplace == MarketplaceKind.Rozetka).Complete);
        var first = await notifications.ObserveAsync(partial, today.AddDays(-6), today, true);
        Equal(1, first!.Count); True(first.Body.Contains("RZ-NEW"));
        True(workspace.GetOrderNotificationSnapshot(DateTimeOffset.UtcNow.AddMinutes(1))
            .Sources.All(s => !s.Complete)); // Cached success is not fresh completion.
        fixture.Source.Complete = true;
        started = DateTimeOffset.UtcNow;
        await workspace.SyncBackgroundAsync();
        var second = await notifications.ObserveAsync(workspace.GetOrderNotificationSnapshot(started),
            today.AddDays(-6), today, true);
        Equal(1, second!.Count); True(second.Body.Contains("Prom"));
        True(await notifications.ObserveAsync(workspace.GetOrderNotificationSnapshot(started),
            today.AddDays(-6), today, true) is null);
        Equal(0, fixture.Printer.Calls);
    }

    private sealed class NotificationClock : TimeProvider
    {
        public DateTimeOffset Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class NotificationStore : IOrderNotificationStore
    {
        private string _json = "{}";
        public Task<OrderNotificationState> LoadAsync(CancellationToken token = default) =>
            Task.FromResult(JsonSerializer.Deserialize<OrderNotificationState>(_json)!);
        public Task SaveAsync(OrderNotificationState state, CancellationToken token = default)
        { _json = JsonSerializer.Serialize(state); return Task.CompletedTask; }
    }
}
