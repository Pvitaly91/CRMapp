using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private sealed class DashboardTrackingSource : IShipmentTrackingService
    {
        public int Calls;
        public readonly List<MarketplaceOrder[]> Requests = [];
        public Func<IReadOnlyList<MarketplaceOrder>, CancellationToken, Task<ShipmentTrackingFetchResult>>? Read;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken LastToken;
        public Task<ShipmentTrackingFetchResult> FetchAsync(IReadOnlyList<MarketplaceOrder> orders, CancellationToken ct = default)
        {
            Calls++; LastToken = ct;
            var snapshot = orders.Select(order => order with { Shipments = order.Shipments.ToArray() }).ToArray();
            Requests.Add(snapshot); Entered.TrySetResult();
            return Read is null ? Task.FromResult(TrackingSuccess(snapshot, "Доставляється")) : Read(snapshot, ct);
        }
    }

    private static ShipmentTrackingFetchResult TrackingSuccess(IReadOnlyList<MarketplaceOrder> orders, string state)
    {
        var statuses = orders.SelectMany(order => ShippingCarrierNames.ForOrder(order).Select(shipment =>
            new ShipmentTrackingStatus(shipment.Carrier, shipment.TrackingNumber, state, "Synthetic parcel tracking", DateTimeOffset.UtcNow,
                shipment.ShipmentId) { Order = shipment.Carrier == ShippingCarrier.RozetkaDelivery ? order.Key : null }))
            .DistinctBy(status => (status.Carrier, status.TrackingNumber, status.Order)).ToArray();
        return new(statuses, []);
    }

    private static (MainViewModel Main, MarketplaceWorkspaceViewModel Workspace) CreateTrackingDashboard(Fixture fixture,
        IShipmentTrackingService? tracking = null, WaybillsViewModel? waybills = null, IShippingSettingsStore? shippingSettings = null,
        IShipmentTrackingCacheStore? trackingCache = null, string cacheContext = "synthetic-scope", TimeProvider? clock = null,
        Func<CancellationToken, Task<string>>? contextProvider = null)
    {
        var sync = new MarketplaceSyncService([fixture.Source, fixture.Rozetka], fixture.Settings, fixture.Cache);
        var workspace = new MarketplaceWorkspaceViewModel(fixture.Settings, sync, fixture.Links, fixture.Details,
            fixture.Dialogs, autoLinkEnabled: false);
        var main = new MainViewModel(fixture.ReceiptSource, fixture.Images, fixture.Settings, fixture.Authentication,
            fixture.History, fixture.Printer, fixture.Dialogs, new Logger(), workspace, waybills: waybills,
            waybillShippingSettings: shippingSettings, shipmentTracking: tracking, shipmentTrackingCache: trackingCache,
            shipmentTrackingCacheContext: contextProvider ?? (_ => Task.FromResult(cacheContext)), shipmentTrackingClock: clock)
            { DateFrom = new(2026, 9, 23), DateTo = new(2026, 9, 23) };
        return (main, workspace);
    }

    private static async Task ShipmentTrackingDoesNotBlockPrintingAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders =
        [
            Order([ReceiptOne]) with { Shipments = [new("Нова пошта", "20400000000001")] },
            Order([ReceiptTwo]) with { Key = Order().Key with { OrderId = "42" }, Number = "SECOND", Shipments = [new("Нова пошта", "20400000000002")] }
        ];
        var gate = new TaskCompletionSource<ShipmentTrackingFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracking = new DashboardTrackingSource { Read = (_, ct) => gate.Task.WaitAsync(ct) };
        var (main, workspace) = CreateTrackingDashboard(fixture, tracking);
        try
        {
            await main.RefreshAllAsync().WaitAsync(TimeSpan.FromSeconds(4));
            var first = main.RefreshShipmentTrackingAsync(force: true);
            var repeated = main.RefreshShipmentTrackingAsync(force: true);
            True(ReferenceEquals(first, repeated), "Repeated queued status requests must share a single current flight.");
            await tracking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Equal(1, tracking.Calls); Equal(2, tracking.Requests.Single().Length);
            True(!first.IsCompleted && !main.IsBusy && main.RefreshAllCommand.CanExecute(null),
                "Slow tracking held the main refresh/print workflow busy.");
            True(workspace.AllOrderRows.All(row => row.ShipmentLinks.Single().TrackingStatus.Contains("не завантажено")));

            main.SelectedTabIndex = 0;
            main.SelectAllCommand.Execute(null);
            var expected = main.ReceiptsView.Cast<ReceiptRowViewModel>().Select(row => row.Id).ToArray();
            await ExecuteAsync(main.PrintSelectedCommand);
            Equal(3, fixture.Printer.Calls);
            True(fixture.Printer.BatchIds.SequenceEqual(expected));
            True(!first.IsCompleted && !main.IsBusy, "Receipt printing waited for parcel tracking.");

            gate.TrySetResult(TrackingSuccess(tracking.Requests.Single(), "Доставляється"));
            await first.WaitAsync(TimeSpan.FromSeconds(4));
            True(workspace.AllOrderRows.All(row => row.ShipmentLinks.Single().TrackingStatus == "Доставляється"));
            await main.RefreshShipmentTrackingAsync();
            Equal(1, tracking.Calls);
            Equal(0, fixture.Settings.AppSaves); Equal(0, fixture.Settings.MarketplaceSaves);
        }
        finally { gate.TrySetCanceled(); main.CancelPendingOperations(); }
    }

    private static async Task ShipmentTrackingFailureKeepsLastStatusAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [Order() with { Shipments = [new("Нова пошта", "20400000000001")] }];
        var tracking = new DashboardTrackingSource();
        var (main, workspace) = CreateTrackingDashboard(fixture, tracking);
        try
        {
            await main.RefreshAllAsync();
            await main.RefreshShipmentTrackingAsync(force: true).WaitAsync(TimeSpan.FromSeconds(4));
            var row = workspace.AllOrderRows.Single();
            Equal("Доставляється", row.ShipmentLinks.Single().TrackingStatus);
            tracking.Read = (_, _) => Task.FromResult(new ShipmentTrackingFetchResult([], ["Synthetic source offline"]));
            await main.RefreshShipmentTrackingAsync(force: true).WaitAsync(TimeSpan.FromSeconds(4));
            Equal("Доставляється", row.ShipmentLinks.Single().TrackingStatus);
            True(row.ShipmentLinks.Single().TrackingStatusToolTip.Contains("попередній"),
                "A failed refresh represented the old parcel status as current.");

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tracking.Read = async (_, ct) =>
            {
                entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new Exception("Canceled tracking source continued.");
            };
            using var cancel = new CancellationTokenSource();
            var pending = main.RefreshShipmentTrackingAsync(force: true, cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(4)); cancel.Cancel();
            await pending.WaitAsync(TimeSpan.FromSeconds(4));
            Equal("Доставляється", row.ShipmentLinks.Single().TrackingStatus);
            True(row.ShipmentLinks.Single().TrackingStatusToolTip.Contains("попередній") && !main.IsBusy);
            Equal(0, fixture.Dialogs.Errors.Count); Equal(0, fixture.Printer.Calls);
        }
        finally { main.CancelPendingOperations(); }
    }

    private static async Task ShipmentTrackingLateOldTtnAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [Order() with { Shipments = [new("Нова пошта", "20400000000001")] }];
        var oldResult = new TaskCompletionSource<ShipmentTrackingFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracking = new DashboardTrackingSource
        {
            Read = (orders, _) => orders[0].Shipments[0].TrackingNumber == "20400000000001"
                ? oldResult.Task : Task.FromResult(TrackingSuccess(orders, "Отримано нову посилку"))
        };
        var (main, workspace) = CreateTrackingDashboard(fixture, tracking);
        try
        {
            await main.RefreshAllAsync();
            var oldFlight = main.RefreshShipmentTrackingAsync(force: true);
            await tracking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            var oldToken = tracking.LastToken;
            var row = workspace.AllOrderRows.Single();
            var oldShipment = row.Model.Shipments.Single();
            var replacement = row.Model with { Shipments = [oldShipment with { TrackingNumber = "20400000000002" }] };
            row.Update(replacement, main.Receipts.ToArray(), []);
            await main.RefreshShipmentTrackingAsync(force: true).WaitAsync(TimeSpan.FromSeconds(4));
            True(oldToken.IsCancellationRequested);
            Equal("20400000000002", row.ShipmentLinks.Single().TrackingNumber);
            Equal("Отримано нову посилку", row.ShipmentLinks.Single().TrackingStatus);
            True(row.TrackingStatusFor(oldShipment) is null);
            oldResult.TrySetResult(TrackingSuccess(tracking.Requests[0], "Старий стан іншої ТТН"));
            await oldFlight.WaitAsync(TimeSpan.FromSeconds(4));
            Equal("Отримано нову посилку", row.ShipmentLinks.Single().TrackingStatus);
            Equal(2, tracking.Calls); Equal(0, fixture.Dialogs.Errors.Count);
        }
        finally { oldResult.TrySetCanceled(); main.CancelPendingOperations(); }
    }

    private static async Task ShipmentTrackingSettingsResetAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [Order() with { Shipments = [new("Нова пошта", "20400000000001")] }];
        var oldResult = new TaskCompletionSource<ShipmentTrackingFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracking = new DashboardTrackingSource();
        tracking.Read = async (orders, ct) =>
        {
            if (tracking.Calls == 1) return await oldResult.Task; // An old provider deliberately ignores cancellation.
            newEntered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new Exception("Canceled new credentials scope continued.");
        };
        var (main, workspace) = CreateTrackingDashboard(fixture, tracking);
        // Production retains the marketplace archive while updating a receipt scope. The manual
        // fixture mode deliberately clears it on settings completion when the orders tab is inactive.
        main.CoordinatorManaged = true;
        workspace.CoordinatorManaged = true;
        try
        {
            await main.RefreshAllAsync();
            var oldFlight = main.RefreshShipmentTrackingAsync(force: true);
            await tracking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            var oldToken = tracking.LastToken;
            await ExecuteAsync(main.SettingsCommand);
            True(oldToken.IsCancellationRequested, "Opening/saving settings did not invalidate the old tracking context.");
            oldResult.TrySetResult(TrackingSuccess(tracking.Requests[0], "Стан із попередніх облікових даних"));
            await oldFlight.WaitAsync(TimeSpan.FromSeconds(4));
            await newEntered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            True(workspace.AllOrderRows.Single().ShipmentLinks.Single().TrackingStatus.Contains("не завантажено"),
                "A late lookup from the old settings context repopulated the dashboard.");
            True(!main.IsBusy); Equal(0, fixture.Dialogs.Errors.Count);
        }
        finally { oldResult.TrySetCanceled(); main.CancelPendingOperations(); }
    }

    private sealed class TrackingWaybillSource(IReadOnlyList<WaybillRecord> documents) : IWaybillService
    {
        public int Calls;
        public Task<WaybillFetchResult> FetchAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(new WaybillFetchResult(documents, [])); }
    }
    private sealed class TrackingShippingSettings(ShippingLabelSettings settings) : IShippingSettingsStore
    {
        public Task<ShippingLabelSettings> LoadAsync(CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(settings); }
        public Task SaveAsync(ShippingLabelSettings value, CancellationToken ct = default) => throw new Exception("Tracking changed shipping settings.");
    }
    private static async Task ShipmentTrackingWaybillReuseAsync()
    {
        var expectedRef = Guid.Parse("60000000-0000-0000-0000-000000000001");
        var wrongRef = Guid.Parse("60000000-0000-0000-0000-000000000002");
        var fixture = new Fixture();
        fixture.Source.Orders = [Order() with
        {
            Shipments =
            [
                new("Нова пошта", "20400000000001") { ShipmentId = expectedRef.ToString() },
                new("Нова пошта", "20400000000002"),
                new("Нова пошта", "20400000000003"),
                new("Нова пошта", "20400000000004") { ShipmentId = expectedRef.ToString() },
                new("Rozetka Delivery", "RMP-005") { ShipmentId = "rd-5" },
                new("Нова пошта", "20400000000006")
            ]
        }];
        WaybillRecord Document(ShippingCarrier carrier, string account, string id, string number, string state) =>
            new(carrier, account, "Same account name", id, number, new DateTime(2026, 9, 23, 12, 0, 0), state, "", "", null, null);
        var source = new TrackingWaybillSource(
        [
            Document(ShippingCarrier.NovaPoshta, "np-alias", expectedRef.ToString(), "20400000000001", "Отримано"),
            Document(ShippingCarrier.NovaPoshta, "other-account", "np-other", "20400000000002", "WRONG_ACCOUNT"),
            Document(ShippingCarrier.NovaPoshta, "np-main", "np-c1", "20400000000003", "Їде"),
            Document(ShippingCarrier.NovaPoshta, "np-main", "np-c2", "20400000000003", "Отримано"),
            Document(ShippingCarrier.NovaPoshta, "np-main", wrongRef.ToString(), "20400000000004", "WRONG_REFERENCE"),
            Document(ShippingCarrier.RozetkaDelivery, "rd-account", "rd-5", "RMP-005", "Передано кур’єру"),
            Document(ShippingCarrier.RozetkaDelivery, "rd-account", "rd-6", "20400000000006", "WRONG_CARRIER")
        ]);
        using var waybills = new WaybillsViewModel(source);
        var settings = new TrackingShippingSettings(new()
        {
            NovaPoshtaConnections = [new("np-main", "Same account name", "synthetic_np_key"),
                new("np-alias", "Same account name", "synthetic_np_key"), new("other-account", "Same account name", "different_np_key")],
            NovaPoshtaStoreBindings = [new("prom-test", "np-main")]
        });
        var (main, workspace) = CreateTrackingDashboard(fixture, waybills: waybills, shippingSettings: settings);
        try
        {
            await main.RefreshAllAsync().WaitAsync(TimeSpan.FromSeconds(4));
            await main.UpdateWaybillOrderAssociationsAsync();
            var links = workspace.AllOrderRows.Single().ShipmentLinks.ToDictionary(link => link.TrackingNumber);
            Equal("Отримано", links["20400000000001"].TrackingStatus);
            Equal("Передано кур’єру", links["RMP-005"].TrackingStatus);
            foreach (var number in new[] { "20400000000002", "20400000000003", "20400000000004", "20400000000006" })
                True(links[number].TrackingStatus.Contains("не завантажено"),
                    "Tracking reused a wrong account/carrier/Ref or ambiguous carrier record.");
            True(links["20400000000001"].TrackingStatusToolTip.Contains("список накладних"));
            waybills.SearchText = "no-row-has-this-text";
            Equal(0, waybills.View.Cast<object>().Count());
            await main.UpdateWaybillOrderAssociationsAsync();
            Equal("Отримано", workspace.AllOrderRows.Single().ShipmentLinks.First().TrackingStatus);
            Equal(1, source.Calls); Equal(0, fixture.Printer.Calls);
        }
        finally { main.CancelPendingOperations(); }
    }
}
