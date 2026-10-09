namespace CheckboxBatchPrinter.Core.Models;

// Carrier documents exist independently of marketplace orders and receipt links.
public sealed record WaybillRecord(ShippingCarrier Carrier, string ConnectionId, string ConnectionName,
    string DocumentId, string TrackingNumber, DateTime? CreatedLocal, string Status,
    string Recipient, string City, decimal? DeclaredValue, int? Places, bool IsDeleted = false);

public sealed record WaybillSourceStatus(ShippingCarrier Carrier, string ConnectionName, bool Complete,
    int Count, string Message);

public sealed record WaybillFetchResult(IReadOnlyList<WaybillRecord> Documents,
    IReadOnlyList<WaybillSourceStatus> Sources);
