namespace CheckboxBatchPrinter.Core.Models;

// Carrier documents exist independently of marketplace orders and receipt links.
public sealed record WaybillRecord(ShippingCarrier Carrier, string ConnectionId, string ConnectionName,
    string DocumentId, string TrackingNumber, DateTime? CreatedLocal, string Status,
    string Recipient, string City, decimal? DeclaredValue, int? Places, bool IsDeleted = false)
{
    public DateTimeOffset RetrievedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    // Cabinet/API print markers describe the provider's form history, independently of local
    // Windows submission. Null means no supported print marker was supplied by the carrier.
    public bool? CabinetPrinted { get; init; }
    public string CabinetLastPrint { get; init; } = "";
    // Operation-local proof of the account used to read this document; never displayed or persisted.
    public string CredentialFingerprint { get; init; } = "";
}

public sealed record WaybillPrintRequest(WaybillRecord Waybill, IReadOnlyList<string> OrderNumbers);
public sealed record PreparedWaybillBatch(LabelPrintBatch Batch, IReadOnlyDictionary<string, string> CredentialFingerprints);

public sealed record WaybillSourceStatus(ShippingCarrier Carrier, string ConnectionName, bool Complete,
    int Count, string Message);

public sealed record WaybillFetchResult(IReadOnlyList<WaybillRecord> Documents,
    IReadOnlyList<WaybillSourceStatus> Sources);
