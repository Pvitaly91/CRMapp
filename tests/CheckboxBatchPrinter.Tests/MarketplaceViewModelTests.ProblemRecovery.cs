using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task ProblemCompletedShipmentRecoveryAsync()
    {
        var fixture = new Fixture();
        var clock = new ProblemClock { Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero) };
        var cache = new MemoryTrackingCache();
        fixture.Settings.Market.Connections.Add(new()
            { Id = "rz-test", Marketplace = MarketplaceKind.Rozetka, Enabled = true, Name = "Synthetic Rozetka" });
        var completed = ProblemOrder("completed", clock.Now.AddDays(-8), "20400000000101") with
            { Status = "Виконано", SourceStatus = "delivered" };
        var sellerCompleted = completed with
        {
            Key = new(MarketplaceKind.Rozetka, "rz-test", "102"), Number = "seller-completed", SourceStatusGroup = 2,
            Shipments = [new("nova_poshta", "20400000000102")]
        };
        var active = ProblemOrder("active", clock.Now.AddDays(-8), "20400000000103");
        var partial = ProblemOrder("partial", clock.Now.AddDays(-8)) with
            { Shipments = [new("nova_poshta", "20400000000104"), new("nova_poshta", "20400000000105")] };
        fixture.Source.Orders = [completed, active, partial]; fixture.Rozetka.Orders = [sellerCompleted];
        await fixture.Cache.SaveAsync(new(fixture.Source.Orders.Concat(fixture.Rozetka.Orders).ToArray(), []));
        var all = fixture.Source.Orders.Concat(fixture.Rozetka.Orders).ToArray();
        var old = clock.Now.AddDays(-5);
        await cache.SaveAsync("synthetic-scope", new()
        {
            AccountContext = "synthetic-scope",
            Statuses = all.SelectMany(order => order.Shipments.Select(shipment => new ShipmentTrackingStatus(
                ShippingCarrier.NovaPoshta, shipment.TrackingNumber, "Прибув у відділення", "Нова пошта", old)
                { StatusCode = "7", ArrivedAt = old, PaidStorageStartsOn = DateRangeBuilder.KyivDate(old).AddDays(1) })).ToArray(),
            // Recent activity for another parcel must not block these old identities.
            Attempts = [new(ShippingCarrier.NovaPoshta, "20400000000999", completed.Key, "", clock.Now)]
        });
        var tracking = new DashboardTrackingSource
        {
            Read = (orders, _) => Task.FromResult(new ShipmentTrackingFetchResult(orders.SelectMany(order => order.Shipments.Select(shipment =>
                new ShipmentTrackingStatus(ShippingCarrier.NovaPoshta, shipment.TrackingNumber,
                    shipment.TrackingNumber.EndsWith("105") ? "Прибув у відділення" : "Відправлення отримано", "Нова пошта", clock.Now)
                {
                    StatusCode = shipment.TrackingNumber.EndsWith("105") ? "7" : "9",
                    ArrivedAt = old, PaidStorageStartsOn = DateRangeBuilder.KyivDate(old).AddDays(1)
                })).ToArray(), []))
        };
        var (main, workspace) = CreateTrackingDashboard(fixture, tracking, trackingCache: cache, clock: clock);
        try
        {
            main.SelectedTabIndex = 3;
            await main.PrepareOrdersAsync();
            await main.RefreshShipmentTrackingAsync();
            Equal(1, tracking.Calls);
            True(tracking.Requests.Single().Select(order => order.Key).ToHashSet().SetEquals(all.Select(order => order.Key)),
                "Completed marketplace orders with stale pickup observations must still be checked by their TTNs.");
            Equal("partial", workspace.ProblemOrders.Cast<MarketplaceOrderRowViewModel>().Single().Number);
            True(workspace.AllOrderRows.Where(row => row.Key != partial.Key).All(row => !row.ProblemTrackingPending));
            clock.Now = clock.Now.AddMinutes(30);
            await main.RefreshShipmentTrackingAsync();
            Equal(2, tracking.Calls);
            Equal("20400000000105", tracking.Requests.Last().Single().Shipments.Single().TrackingNumber);
        }
        finally { main.CancelPendingOperations(); }

        var restartedSource = new DashboardTrackingSource();
        var restarted = CreateTrackingDashboard(fixture, restartedSource, trackingCache: cache, clock: clock);
        try
        {
            restarted.Main.SelectedTabIndex = 3;
            await restarted.Main.PrepareOrdersAsync();
            await restarted.Main.RefreshShipmentTrackingAsync();
            Equal(0, restartedSource.Calls);
            Equal("partial", restarted.Workspace.ProblemOrders.Cast<MarketplaceOrderRowViewModel>().Single().Number);
            True(restarted.Workspace.AllOrderRows.Where(row => row.Key != partial.Key).All(row => !row.ProblemTrackingPending));
        }
        finally { restarted.Main.CancelPendingOperations(); }
    }

    private static async Task ProblemManualTrackingCompletionAsync()
    {
        var fixture = new Fixture();
        var clock = new ProblemClock { Now = DateTimeOffset.UtcNow };
        var order = ProblemOrder("delivered-after-refresh", clock.Now.AddDays(-4), "20400000000201");
        fixture.Source.Orders = [order]; fixture.ReceiptSource.Rows = [];
        await fixture.Cache.SaveAsync(new([order], []));
        var gate = new TaskCompletionSource<ShipmentTrackingFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracking = new DashboardTrackingSource { Read = (_, ct) => gate.Task.WaitAsync(ct) };
        var (main, workspace) = CreateTrackingDashboard(fixture, tracking, clock: clock);
        try
        {
            main.SelectedTabIndex = 3;
            await main.PrepareOrdersAsync();
            var refresh = main.RefreshAllAsync();
            await tracking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            True(!refresh.IsCompleted, "Problem refresh must await carrier verification before reporting completion.");
            gate.SetResult(new([new(ShippingCarrier.NovaPoshta, "20400000000201", "Відправлення отримано", "Нова пошта", clock.Now)
                { StatusCode = "9" }], []));
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(0, workspace.ProblemOrders.Cast<object>().Count());
            True(!main.IsRefreshingAll && main.RefreshAllStatus.Contains("Оновлення завершено"));
            await main.RefreshShipmentTrackingAsync();
            Equal(1, tracking.Calls);
        }
        finally { main.CancelPendingOperations(); }
    }

    private static async Task ProblemCalendarFilterAsync()
    {
        var fixture = new Fixture();
        var clock = new ProblemClock { Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero) };
        var day = new DateOnly(2026, 10, 25);
        var range = DateRangeBuilder.ForLocalDates(day, day);
        fixture.Source.Orders = [ProblemOrder("before", range.From.AddTicks(-1)), ProblemOrder("first", range.From),
            ProblemOrder("last", range.ToExclusive.AddTicks(-1)), ProblemOrder("after", range.ToExclusive)];
        await fixture.Cache.SaveAsync(new(fixture.Source.Orders, []));
        var (main, workspace) = CreateTrackingDashboard(fixture, clock: clock);
        try
        {
            await main.PrepareOrdersAsync();
            main.SelectedTabIndex = 3;
            workspace.ProblemDateFrom = workspace.ProblemDateTo = new(2026, 10, 25);
            workspace.ProblemPeriod = "За календарем";
            workspace.RefreshProblems(clock.Now);
            True(workspace.ProblemOrders.Cast<MarketplaceOrderRowViewModel>().Select(row => row.Number).ToHashSet().SetEquals(["first", "last"]));
            True(workspace.ProblemSummary.Contains("25.10.2026"));
            Equal(0, fixture.Source.FetchCalls);
            workspace.ProblemDateFrom = new(2026, 10, 26);
            True(!workspace.HasValidProblemDateRange && workspace.ProblemDateRangeHint.Length > 0);
            Equal(0, workspace.ProblemOrders.Cast<object>().Count());
            True(!main.RefreshAllCommand.CanExecute(null));
            await main.RefreshAllAsync(); Equal(0, fixture.Source.FetchCalls);
            workspace.ProblemDateFrom = null;
            True(workspace.ProblemDateRangeHint.Contains("обидві дати"));
            workspace.ProblemDateFrom = new(2026, 10, 24);
            Equal(3, workspace.ProblemOrders.Cast<object>().Count());
            workspace.ProblemPeriod = "За весь час";
            Equal(4, workspace.ProblemOrders.Cast<object>().Count());
            Equal(0, fixture.Settings.AppSaves); Equal(0, fixture.Settings.MarketplaceSaves);
        }
        finally { main.CancelPendingOperations(); }
    }

    private static async Task ProblemCalendarRefreshRangeAsync()
    {
        var fixture = new Fixture();
        var from = new DateOnly(2026, 9, 23); var to = new DateOnly(2026, 10, 3);
        fixture.Source.Orders = [ProblemOrder("custom-range", new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero))];
        var waybillSource = new TrackingWaybillSource([]);
        using var waybills = new WaybillsViewModel(waybillSource);
        var (main, workspace) = CreateTrackingDashboard(fixture, waybills: waybills);
        try
        {
            var topFrom = main.DateFrom; var topTo = main.DateTo;
            main.SelectedTabIndex = 3;
            workspace.ProblemPeriod = "За календарем";
            workspace.ProblemDateFrom = from.ToDateTime(TimeOnly.MinValue);
            workspace.ProblemDateTo = to.ToDateTime(TimeOnly.MinValue);
            await main.RefreshAllAsync();
            Equal(from, fixture.ReceiptSource.LastFrom); Equal(to, fixture.ReceiptSource.LastTo);
            Equal(MarketplaceSyncService.BuildRange(from, to, 0), fixture.Source.LastRange);
            Equal(0, waybillSource.Calls);
            Equal(topFrom, main.DateFrom); Equal(topTo, main.DateTo);
            Equal("custom-range", workspace.ProblemOrders.Cast<MarketplaceOrderRowViewModel>().Single().Number);
        }
        finally { main.CancelPendingOperations(); }
    }
}
