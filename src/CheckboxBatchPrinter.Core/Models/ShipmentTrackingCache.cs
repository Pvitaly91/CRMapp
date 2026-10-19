namespace CheckboxBatchPrinter.Core.Models;

// Only status metadata is persisted. Carrier responses, credentials and recipient details are excluded.
public sealed record ShipmentTrackingCacheAttempt(ShippingCarrier Carrier, string TrackingNumber,
    OrderKey Order, string ShipmentId, DateTimeOffset AttemptedAtUtc);

public sealed record ShipmentTrackingCache
{
    public int Schema { get; init; } = 1;
    public string AccountContext { get; init; } = "";
    public IReadOnlyList<ShipmentTrackingStatus> Statuses { get; init; } = [];
    public IReadOnlyList<ShipmentTrackingCacheAttempt> Attempts { get; init; } = [];
}
