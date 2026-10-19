using System.Text.Json;
using System.IO;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private sealed class ProblemClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class MemoryTrackingCache : IShipmentTrackingCacheStore
    {
        private readonly Dictionary<string, string> _snapshots = [];
        public Task<ShipmentTrackingCache> LoadAsync(string context, CancellationToken ct = default) =>
            Task.FromResult(_snapshots.TryGetValue(context, out var json) ? JsonSerializer.Deserialize<ShipmentTrackingCache>(json)! : new() { AccountContext = context });
        public Task SaveAsync(string context, ShipmentTrackingCache cache, CancellationToken ct = default)
        { _snapshots[context] = JsonSerializer.Serialize(cache); return Task.CompletedTask; }
    }
    private static MarketplaceOrder ProblemOrder(string id, DateTimeOffset created, string number = "") => Order() with
    {
        Key = Order().Key with { OrderId = id }, Number = id, CreatedAt = created, Status = "Прийнято",
        ReceiptIds = [], Shipments = number.Length == 0 ? [] : [new("nova_poshta", number)]
    };
    private static DashboardTrackingSource ProblemTracking(ProblemClock clock) => new()
    {
        Read = (orders, _) => Task.FromResult(new ShipmentTrackingFetchResult(orders.SelectMany(order => order.Shipments.Select(shipment =>
            new ShipmentTrackingStatus(ShippingCarrier.NovaPoshta, shipment.TrackingNumber, "Прибув у відділення", "Нова пошта", clock.Now)
                { StatusCode = "7", ArrivedAt = clock.Now.AddDays(-6), PaidStorageStartsOn = DateRangeBuilder.KyivDate(clock.Now).AddDays(1) })).ToArray(), []))
    };

    private static async Task ProblemPeriodDefaultsAsync()
    {
        var fixture = new Fixture(); var clock = new ProblemClock();
        var today = DateRangeBuilder.KyivDate(clock.Now);
        var oldestIncluded = DateRangeBuilder.ForLocalDates(today.AddDays(-13), today.AddDays(-13)).From;
        fixture.Source.Orders = [ProblemOrder("recent", clock.Now.AddDays(-2)), ProblemOrder("boundary", oldestIncluded),
            ProblemOrder("older", oldestIncluded.AddTicks(-1)), ProblemOrder("archive", clock.Now.AddYears(-2))];
        var (main, workspace) = CreateTrackingDashboard(fixture, clock: clock);
        try
        {
            await main.RefreshAllAsync();
            workspace.RefreshProblems(clock.Now);
            Equal("Останні 14 днів", workspace.ProblemPeriod);
            True(workspace.ProblemOrders.Cast<MarketplaceOrderRowViewModel>().Select(r => r.Number).ToHashSet().SetEquals(["recent", "boundary"]));
            var fetches = fixture.Source.FetchCalls;
            workspace.ProblemPeriod = "За весь час";
            Equal(4, workspace.ProblemOrders.Cast<object>().Count());
            workspace.ProblemSearch = "archive";
            Equal("archive", workspace.ProblemOrders.Cast<MarketplaceOrderRowViewModel>().Single().Number);
            workspace.ProblemSearch = ""; workspace.ProblemPeriod = "Останні 14 днів";
            Equal(2, workspace.ProblemOrders.Cast<object>().Count());
            Equal(fetches, fixture.Source.FetchCalls); Equal(4, workspace.AllOrderRows.Count);
            workspace.RefreshProblems(clock.Now.AddDays(1));
            Equal("recent", workspace.ProblemOrders.Cast<MarketplaceOrderRowViewModel>().Single().Number);
        }
        finally { main.CancelPendingOperations(); }
    }

    private static async Task ProblemTrackingThirtyMinutesAsync()
    {
        var fixture = new Fixture(); var clock = new ProblemClock(); var tracking = ProblemTracking(clock);
        fixture.Source.Orders = [ProblemOrder("recent", clock.Now.AddDays(-3), "20400554610503"),
            ProblemOrder("old", clock.Now.AddDays(-25), "20400554484754")];
        await fixture.Cache.SaveAsync(new(fixture.Source.Orders, []));
        var (main, workspace) = CreateTrackingDashboard(fixture, tracking, clock: clock);
        try
        {
            main.SelectedTabIndex = 3;
            await main.PrepareOrdersAsync();
            await main.RefreshShipmentTrackingAsync();
            Equal(1, tracking.Calls); Equal("recent", tracking.Requests.Single().Single().Number);
            workspace.ProblemPeriod = "За весь час";
            await main.RefreshShipmentTrackingAsync();
            Equal(1, tracking.Calls);
            clock.Now = clock.Now.AddMinutes(29).AddSeconds(59);
            await main.RefreshShipmentTrackingAsync(); Equal(1, tracking.Calls);
            clock.Now = clock.Now.AddSeconds(1);
            await main.RefreshShipmentTrackingAsync(); Equal(2, tracking.Calls);
            True(tracking.Requests.Last().Select(o => o.Number).ToHashSet().SetEquals(["recent", "old"]));
            tracking.Read = (_, _) => Task.FromResult(new ShipmentTrackingFetchResult([], ["offline"]));
            await main.RefreshShipmentTrackingAsync(force: true); Equal(3, tracking.Calls);
            clock.Now = clock.Now.AddMinutes(29);
            await main.RefreshShipmentTrackingAsync(); Equal(3, tracking.Calls);
            await main.RefreshShipmentTrackingAsync(force: true); Equal(4, tracking.Calls);
            Equal(0, fixture.Source.FetchCalls);
        }
        finally { main.CancelPendingOperations(); }
    }

    private static async Task ProblemTrackingCachedRestartAsync()
    {
        var fixture = new Fixture(); var clock = new ProblemClock(); var cache = new MemoryTrackingCache();
        fixture.Source.Orders = [ProblemOrder("cached", clock.Now.AddDays(-4), "20400554610503")];
        await fixture.Cache.SaveAsync(new(fixture.Source.Orders, []));
        var first = CreateTrackingDashboard(fixture, ProblemTracking(clock), trackingCache: cache, clock: clock);
        first.Main.SelectedTabIndex = 3;
        await first.Main.PrepareOrdersAsync();
        await first.Main.RefreshShipmentTrackingAsync();
        first.Main.CancelPendingOperations();
        clock.Now = clock.Now.AddMinutes(10);
        var restartedSource = ProblemTracking(clock);
        var second = CreateTrackingDashboard(fixture, restartedSource, trackingCache: cache, clock: clock);
        try
        {
            second.Main.SelectedTabIndex = 3;
            await second.Main.PrepareOrdersAsync();
            await second.Main.RefreshShipmentTrackingAsync();
            Equal(0, restartedSource.Calls);
            True(second.Workspace.AllOrderRows.Single().ShipmentLinks.Single().TrackingStatus == "Прибув у відділення");
            True(second.Workspace.ProblemOrders.Cast<MarketplaceOrderRowViewModel>().Single().ProblemReason.Contains("платне з"));
            clock.Now = clock.Now.AddMinutes(20);
            await second.Main.RefreshShipmentTrackingAsync(); Equal(1, restartedSource.Calls);
        }
        finally { second.Main.CancelPendingOperations(); }
        var differentSource = ProblemTracking(clock);
        var different = CreateTrackingDashboard(fixture, differentSource, trackingCache: cache, cacheContext: "other-account", clock: clock);
        try
        {
            different.Main.SelectedTabIndex = 3;
            await different.Main.PrepareOrdersAsync();
            await different.Main.RefreshShipmentTrackingAsync(); Equal(1, differentSource.Calls);
        }
        finally { different.Main.CancelPendingOperations(); }
    }

    private static async Task ProblemCacheManualUpgradeAsync()
    {
        var fixture = new Fixture(); var clock = new ProblemClock(); var cache = new MemoryTrackingCache();
        var order = ProblemOrder("cached", clock.Now.AddDays(-4), "20400554610503");
        await fixture.Cache.SaveAsync(new([order], []));
        await cache.SaveAsync("synthetic-scope", new() { AccountContext = "synthetic-scope", Attempts =
            [new(ShippingCarrier.NovaPoshta, "20400554610503", order.Key, "", clock.Now)] });
        var contextGate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = ProblemTracking(clock);
        var (main, _) = CreateTrackingDashboard(fixture, source, trackingCache: cache, clock: clock, contextProvider: ct => contextGate.Task.WaitAsync(ct));
        try
        {
            main.SelectedTabIndex = 3;
            await main.PrepareOrdersAsync();
            var automatic = main.RefreshShipmentTrackingAsync();
            var manual = main.RefreshShipmentTrackingAsync(force: true);
            True(ReferenceEquals(automatic, manual), "Startup should coalesce while preserving a later manual request.");
            contextGate.TrySetResult("synthetic-scope");
            await manual;
            Equal(1, source.Calls);
        }
        finally { contextGate.TrySetCanceled(); main.CancelPendingOperations(); }
    }

    private static async Task ProblemCacheContextRecoveryAsync()
    {
        var fixture = new Fixture(); var clock = new ProblemClock(); var cache = new MemoryTrackingCache();
        var order = ProblemOrder("cached", clock.Now.AddDays(-4), "20400554610503");
        await fixture.Cache.SaveAsync(new([order], []));
        var contextReads = 0;
        var source = ProblemTracking(clock);
        var (main, _) = CreateTrackingDashboard(fixture, source, trackingCache: cache, clock: clock, contextProvider: _ =>
            ++contextReads == 1 ? Task.FromException<string>(new IOException("Temporary local read failure")) : Task.FromResult("synthetic-scope"));
        try
        {
            main.SelectedTabIndex = 3;
            await main.PrepareOrdersAsync();
            await main.RefreshShipmentTrackingAsync();
            await main.RefreshShipmentTrackingAsync();
            True(contextReads >= 2);
            Equal(1, source.Calls);
            var saved = await cache.LoadAsync("synthetic-scope");
            Equal(1, saved.Statuses.Count); Equal(1, saved.Attempts.Count);
        }
        finally { main.CancelPendingOperations(); }
    }

    private static async Task ProblemManualRefreshRangeAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [ProblemOrder("order", DateTimeOffset.UtcNow.AddDays(-2))];
        var waybillSource = new TrackingWaybillSource([]);
        using var waybills = new WaybillsViewModel(waybillSource);
        var (main, _) = CreateTrackingDashboard(fixture, waybills: waybills);
        try
        {
            main.DateFrom = DateRangeBuilder.TodayKyiv.AddMonths(-6); main.DateTo = DateRangeBuilder.TodayKyiv;
            main.SelectedTabIndex = 3;
            await main.RefreshAllAsync();
            var today = DateOnly.FromDateTime(DateRangeBuilder.TodayKyiv);
            Equal(today.AddDays(-13), fixture.ReceiptSource.LastFrom);
            Equal(today, fixture.ReceiptSource.LastTo);
            Equal(MarketplaceSyncService.BuildRange(today.AddDays(-13), today, 0), fixture.Source.LastRange);
            Equal(0, waybillSource.Calls);
            Equal(DateRangeBuilder.TodayKyiv.AddMonths(-6), main.DateFrom);
        }
        finally { main.CancelPendingOperations(); }
    }
}
