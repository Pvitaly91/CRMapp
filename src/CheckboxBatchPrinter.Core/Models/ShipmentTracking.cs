namespace CheckboxBatchPrinter.Core.Models;

// Informational carrier tracking, separate from marketplace status and label/receipt submission.
// Public tracking responses can contain personal details; only these status fields are retained.
public sealed record ShipmentTrackingStatus(ShippingCarrier Carrier, string TrackingNumber, string Status,
    string Source, DateTimeOffset CheckedAtUtc, string ShipmentId = "")
{
    // Seller-only status is scoped to the account/order that supplied the verified document.
    public OrderKey? Order { get; init; }
    public string StatusCode { get; init; } = "";
    public DateTimeOffset? ArrivedAt { get; init; }
    public DateOnly? PaidStorageStartsOn { get; init; }
}

public sealed record ShipmentTrackingFetchResult(IReadOnlyList<ShipmentTrackingStatus> Statuses,
    IReadOnlyList<string> Errors);
