using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IShipmentTrackingService? _shipmentTracking;
    private readonly Dictionary<(ShippingCarrier Carrier, string Number, OrderKey? Order), ShipmentTrackingStatus> _liveTracking = [];
    private readonly Dictionary<(ShippingCarrier Carrier, string Number, OrderKey? Order), ShipmentTrackingStatus> _waybillTracking = [];
    private readonly HashSet<(ShippingCarrier Carrier, string Number, OrderKey? Order)> _trackingFailures = [];
    private readonly Dictionary<(ShippingCarrier Carrier, string Number, OrderKey Order, string ShipmentId), DateTimeOffset> _trackingAttempts = [];
    private CancellationTokenSource? _trackingCancellation;
    private Task? _trackingTask;
    private string _trackingScope = "";
    private long _trackingGeneration;
    private static readonly TimeSpan TrackingCacheAge = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ProblemRefreshInterval = TimeSpan.FromMinutes(30);
    private DateTimeOffset? _trackingAutomaticAttemptAt;

    public void QueueShipmentTrackingRefresh(bool force = false)
    {
        if (_shipmentTracking is null || Marketplace is null || IsBusy || IsPrinting || IsRefreshingAll) return;
        _ = RefreshShipmentTrackingAsync(force);
    }

    public Task RefreshShipmentTrackingAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (_shipmentTracking is null || Marketplace is null) return Task.CompletedTask;
        if (_trackingCacheStore is not null && !_trackingCacheReady)
            return RestoreCacheAndRefreshTrackingAsync(force, cancellationToken);
        var now = _trackingClock.GetUtcNow();
        if (IsProblemOrdersTab && !force && _trackingAutomaticAttemptAt is { } last && now >= last && now - last < ProblemRefreshInterval)
        {
            PublishShipmentTrackingStatuses();
            return _trackingTask is { IsCompleted: false } ? _trackingTask : Task.CompletedTask;
        }
        var cacheAge = IsProblemOrdersTab ? ProblemRefreshInterval : TrackingCacheAge;
        var visible = (IsProblemOrdersTab ? Marketplace.ProblemScopeRows.Where(row => !row.Model.IsCanceled &&
                !ProblemOrderRules.IsCompletedOrder(row.Model)) : Marketplace.Orders.Cast<MarketplaceOrderRowViewModel>())
            .Select(row => row.Model).ToArray();
        var requested = visible.Select(order => order with { Shipments = order.Shipments.Where(shipment =>
        {
            var status = ResolveTracking(order.Key, shipment);
            var identity = (ShippingCarrierNames.FromDocumentedDeliveryField(shipment.Carrier), shipment.TrackingNumber.Trim(), order.Key, shipment.ShipmentId);
            return !string.IsNullOrWhiteSpace(shipment.TrackingNumber) &&
                (force || (status is null || now - status.CheckedAtUtc >= cacheAge || status.CheckedAtUtc > now ||
                    IsProblemOrdersTab && status.Carrier == ShippingCarrier.NovaPoshta && status.StatusCode.Length == 0) &&
                    (!_trackingAttempts.TryGetValue(identity, out var attempted) || attempted > now ||
                        now - attempted >= ProblemRefreshInterval));
        }).ToArray() }).Where(order => order.Shipments.Count > 0).ToArray();
        PublishShipmentTrackingStatuses();
        if (requested.Length == 0) return Task.CompletedTask;
        var scope = string.Join("\n", requested.SelectMany(order => ShippingCarrierNames.ForOrder(order)
            .Select(shipment => $"{order.Key.Marketplace}|{order.Key.ConnectionId}|{order.Key.OrderId}|{shipment.Carrier}|{shipment.TrackingNumber}|{shipment.ShipmentId}"))
            .OrderBy(value => value, StringComparer.Ordinal));
        if (_trackingTask is { IsCompleted: false } && scope == _trackingScope) return _trackingTask;
        _trackingCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(45));
        _trackingCancellation = cancellation;
        _trackingScope = scope;
        var generation = ++_trackingGeneration;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _trackingTask = completion.Task;
        _ = RunShipmentTrackingAsync(requested, generation, cancellation, completion);
        return completion.Task;
    }

    private async Task RunShipmentTrackingAsync(MarketplaceOrder[] requested, long generation,
        CancellationTokenSource cancellation, TaskCompletionSource completion)
    {
        try
        {
            await Task.Delay(200, cancellation.Token); // Coalesce source/view changes, never fetch from a cell getter.
            _trackingAutomaticAttemptAt = _trackingClock.GetUtcNow();
            foreach (var order in requested)
                foreach (var shipment in order.Shipments)
                    _trackingAttempts[(ShippingCarrierNames.FromDocumentedDeliveryField(shipment.Carrier), shipment.TrackingNumber.Trim(),
                        order.Key, shipment.ShipmentId)] = _trackingClock.GetUtcNow();
            await SaveTrackingCacheAsync(generation, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var result = await _shipmentTracking!.FetchAsync(requested, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != _trackingGeneration) return;
            foreach (var order in requested)
                foreach (var shipment in order.Shipments)
                {
                    var identity = (ShippingCarrierNames.FromDocumentedDeliveryField(shipment.Carrier), shipment.TrackingNumber.Trim(), (OrderKey?)order.Key);
                    _trackingFailures.Add(identity);
                    _trackingAttempts[(identity.Item1, identity.Item2, order.Key, shipment.ShipmentId)] = _trackingClock.GetUtcNow();
                }
            foreach (var status in result.Statuses)
            {
                // Do not publish unsolicited identities from an adapter, even into an otherwise loaded row.
                if (!requested.Any(order => (status.Order is null || status.Order == order.Key) && order.Shipments.Any(shipment =>
                    ShippingCarrierNames.FromDocumentedDeliveryField(shipment.Carrier) == status.Carrier &&
                    shipment.TrackingNumber.Trim() == status.TrackingNumber))) continue;
                _liveTracking[(status.Carrier, status.TrackingNumber, status.Order)] = status;
                foreach (var order in requested.Where(order => status.Order is null || status.Order == order.Key))
                    _trackingFailures.Remove((status.Carrier, status.TrackingNumber, order.Key));
            }
            PublishShipmentTrackingStatuses();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (generation == _trackingGeneration) MarkTrackingFailure(requested);
        }
        catch (Exception exception)
        {
            if (generation == _trackingGeneration)
            {
                MarkTrackingFailure(requested);
            }
            _logger.Error("shipment.tracking.refresh", exception);
        }
        finally
        {
            await SaveTrackingCacheAsync(generation);
            if (ReferenceEquals(_trackingCancellation, cancellation)) _trackingCancellation = null;
            cancellation.Dispose();
            completion.TrySetResult();
            if (ReferenceEquals(_trackingTask, completion.Task)) _trackingTask = null;
        }
    }

    private void MarkTrackingFailure(IEnumerable<MarketplaceOrder> requested)
    {
        foreach (var order in requested)
            foreach (var shipment in order.Shipments)
            {
                var identity = (ShippingCarrierNames.FromDocumentedDeliveryField(shipment.Carrier), shipment.TrackingNumber.Trim(), (OrderKey?)order.Key);
                _trackingFailures.Add(identity);
                _trackingAttempts[(identity.Item1, identity.Item2, order.Key, shipment.ShipmentId)] = _trackingClock.GetUtcNow();
            }
        PublishShipmentTrackingStatuses();
    }

    private ShipmentTrackingStatus? ResolveTracking(OrderKey order, OrderShipment shipment)
    {
        var carrier = ShippingCarrierNames.FromDocumentedDeliveryField(shipment.Carrier);
        var number = shipment.TrackingNumber.Trim();
        var resolved = new[] { _liveTracking.GetValueOrDefault((carrier, number, order)),
            _liveTracking.GetValueOrDefault((carrier, number, null)), _waybillTracking.GetValueOrDefault((carrier, number, order)) }
            .OfType<ShipmentTrackingStatus>().Where(status => status.ShipmentId.Length == 0 || shipment.ShipmentId.Length == 0 ||
                status.ShipmentId == shipment.ShipmentId).MaxBy(status => status.CheckedAtUtc);
        // A list refresh can repeat the same state without storage fields. Preserve the dated
        // tracking observation (including its real timestamp), but never carry dates across states.
        var detailed = _liveTracking.GetValueOrDefault((carrier, number, null));
        return carrier == ShippingCarrier.NovaPoshta && detailed is not null && resolved?.Status == detailed.Status &&
            resolved.StatusCode.Length == 0 ? detailed : resolved;
    }

    private void ImportWaybillTrackingStatuses(ShippingLabelSettings settings)
    {
        _waybillTracking.Clear();
        if (Marketplace is null || Waybills is null) return;
        var documents = Waybills.Rows.Select(row => row.Model).ToArray();
        var byNumber = documents.GroupBy(document => (document.Carrier, document.TrackingNumber))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var byId = documents.Where(document => document.Carrier == ShippingCarrier.RozetkaDelivery)
            .GroupBy(document => document.DocumentId).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var row in Marketplace.AllOrderRows)
            foreach (var shipment in row.Model.Shipments)
            {
                var carrier = ShippingCarrierNames.FromDocumentedDeliveryField(shipment.Carrier);
                var number = shipment.TrackingNumber.Trim();
                var candidates = byNumber.GetValueOrDefault((carrier, number)) ?? [];
                if (carrier == ShippingCarrier.RozetkaDelivery && shipment.ShipmentId.Length > 0)
                    candidates = candidates.Concat(byId.GetValueOrDefault(shipment.ShipmentId) ?? []).Distinct().ToArray();
                var matching = candidates.Where(document => WaybillOrderAssociations.Find(document,
                    [row.Model with { Shipments = [shipment] }], settings).Count > 0).ToArray();
                if (matching.Length == 0) continue;
                var cabinet = matching.Where(document => document.Carrier == ShippingCarrier.NovaPoshta && document.CabinetPrinted.HasValue).ToArray();
                if (cabinet.Length > 0 && cabinet.Select(document => document.CabinetPrinted).Distinct().Count() == 1)
                {
                    var print = cabinet.MaxBy(document => document.RetrievedAtUtc)!;
                    row.UpdateCabinetPrintStatuses([new(shipment, print.CabinetPrinted, "Кабінет НП", print.CabinetLastPrint, print.RetrievedAtUtc)]);
                }
                var tracking = matching.Where(document => !string.IsNullOrWhiteSpace(document.Status)).ToArray();
                if (tracking.Length == 0 || tracking.Select(document => document.Status).Distinct(StringComparer.Ordinal).Count() != 1) continue;
                var latest = tracking.MaxBy(document => document.RetrievedAtUtc)!;
                _waybillTracking[(carrier, number, row.Key)] = new(carrier, number, latest.Status,
                    ShippingCarrierNames.Display(carrier) + " · список накладних", latest.RetrievedAtUtc, shipment.ShipmentId)
                    { Order = row.Key };
            }
        PublishShipmentTrackingStatuses();
    }

    private void PublishShipmentTrackingStatuses()
    {
        if (Marketplace is null) return;
        var now = _trackingClock.GetUtcNow();
        foreach (var row in Marketplace.AllOrderRows)
            row.UpdateTrackingStatuses(row.Model.Shipments.Select(shipment =>
            {
                var status = ResolveTracking(row.Key, shipment);
                var carrier = ShippingCarrierNames.FromDocumentedDeliveryField(shipment.Carrier);
                var identity = (carrier, shipment.TrackingNumber.Trim(), (OrderKey?)row.Key);
                if (status is null)
                    return _trackingFailures.Contains(identity) ? new OrderShipmentTrackingStatus(shipment,
                        "Статус недоступний", ShippingCarrierNames.Display(carrier),
                        _trackingAttempts.GetValueOrDefault((carrier, shipment.TrackingNumber.Trim(), row.Key, shipment.ShipmentId))) : null;
                return new OrderShipmentTrackingStatus(shipment, status.Status, status.Source, status.CheckedAtUtc,
                    now - status.CheckedAtUtc >= (IsProblemOrdersTab ? ProblemRefreshInterval : TrackingCacheAge) ||
                        _trackingFailures.Contains((carrier, shipment.TrackingNumber.Trim(), row.Key)))
                    { Details = status };
            }).OfType<OrderShipmentTrackingStatus>());
        Marketplace.RefreshProblems(now);
    }

    private void ResetShipmentTracking()
    {
        ++_trackingGeneration;
        _trackingCancellation?.Cancel();
        _trackingCacheLoadCancellation?.Cancel();
        _trackingCacheReady = false; _trackingCacheContext = ""; _trackingStartupTask = null; _trackingAutomaticAttemptAt = null;
        _trackingCancellation = null; _trackingTask = null; _trackingScope = "";
        _liveTracking.Clear(); _waybillTracking.Clear(); _trackingFailures.Clear(); _trackingAttempts.Clear();
        if (Marketplace is not null) foreach (var row in Marketplace.AllOrderRows) row.ClearCabinetPrintStatuses();
        PublishShipmentTrackingStatuses();
    }
}
