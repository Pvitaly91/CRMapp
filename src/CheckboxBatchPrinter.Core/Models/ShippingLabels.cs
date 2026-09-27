namespace CheckboxBatchPrinter.Core.Models;

public enum ShippingCarrier { Unknown, NovaPoshta, RozetkaDelivery }
public enum LabelSubmissionState { NotSubmitted, SubmissionUnknown, Submitted }
public enum LabelFailureKind { MissingDocument, AccessDenied, ConnectionRequired, Temporary, UnsupportedFormat, InvalidDocument }
public sealed class ShippingLabelException(LabelFailureKind kind, string message) : Exception(message)
{ public LabelFailureKind Kind { get; } = kind; }

// None of these identities is an order/receipt ID inferred by basket matching.
public sealed record ShipmentReference(OrderKey Order, string OrderNumber, ShippingCarrier Carrier,
    string TrackingNumber, string ShipmentId = "", string Source = "", int ExpectedPlaces = 0);
public sealed record ShippingLabelDocument(ShipmentReference Shipment, string ConnectionId, string DocumentId,
    byte[] Pdf, string Fingerprint, DateTimeOffset RetrievedAtUtc, int ExpectedPages = 0)
{ public string Source { get; init; } = ""; public string Format { get; init; } = "application/pdf"; }
public sealed record ShippingLabelPage(ShipmentReference Shipment, string DocumentId, string Fingerprint,
    int PageNumber, double WidthMm, double HeightMm, byte[] Png)
{ public string ConnectionId { get; init; } = ""; public string Source { get; init; } = ""; }
public sealed record LabelPrintBatch(string Id, IReadOnlyList<ShippingLabelPage> Pages, LabelPrintSettings Settings)
{ public int SelectedOrderCount { get; init; } public double RasterDpiX { get; init; } public double RasterDpiY { get; init; } }
public sealed record LabelPrintAttempt(string AttemptId, string JobName, string PrinterName, DateTimeOffset TimeUtc,
    LabelSubmissionState State, IReadOnlyList<LabelAttemptPage> Pages, int? WindowsJobId = null);
public sealed record LabelAttemptPage(ShippingCarrier Carrier, string ConnectionId, string TrackingNumber,
    string DocumentId, int PageNumber, string Fingerprint);

public sealed record LabelPrintSettings
{
    public string PrinterName { get; init; } = "";
    public string DriverFormat { get; init; } = "";
    public double WidthMm { get; init; } = 100;
    public double HeightMm { get; init; } = 100;
    public bool Landscape { get; init; }
    public double ScalePercent { get; init; } = 100;
    public double OffsetXmm { get; init; }
    public double OffsetYmm { get; init; }
    public int Copies { get; init; } = 1;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PrinterName)) throw new InvalidOperationException("Виберіть принтер наклейок.");
        if (!double.IsFinite(WidthMm) || !double.IsFinite(HeightMm) || WidthMm is < 20 or > 300 || HeightMm is < 20 or > 400 ||
            !double.IsFinite(ScalePercent) || ScalePercent is < 10 or > 100 || Copies is < 1 or > 20 ||
            !double.IsFinite(OffsetXmm) || !double.IsFinite(OffsetYmm) || Math.Abs(OffsetXmm) > 50 || Math.Abs(OffsetYmm) > 50)
            throw new InvalidOperationException("Некоректний формат, масштаб, зміщення або кількість копій наклейок.");
    }
}
public sealed record ShippingLabelSettings
{
    public LabelPrintSettings Print { get; init; } = new();
    // Stored together through DPAPI, never in AppSettings/marketplace settings JSON.
    public string RozetkaDeliveryToken { get; init; } = "";
    public string RozetkaDeliveryConnectionId { get; init; } = Guid.NewGuid().ToString("N");
    public string NovaPoshtaSellerConnectionId { get; init; } = "";
    public IReadOnlyList<NovaPoshtaConnection> NovaPoshtaConnections { get; init; } = [];
    public IReadOnlyList<NovaPoshtaStoreBinding> NovaPoshtaStoreBindings { get; init; } = [];
}

// Lives exclusively in channel-specific DPAPI shipping settings, never marketplace JSON.
public sealed record NovaPoshtaConnection(string Id, string Name, string ApiKey)
{ public override string ToString() => Name; }
public sealed record NovaPoshtaStoreBinding(string MarketplaceConnectionId, string NovaPoshtaConnectionId);
public sealed record NovaPoshtaSearchPeriod(DateOnly From, DateOnly To)
{
    public void Validate()
    {
        if (To < From || To.DayNumber - From.DayNumber > 6)
            throw new InvalidOperationException("Виберіть період перевірки ТТН від 1 до 7 днів. Розширення — окрема явна перевірка.");
    }
}
public sealed record NovaPoshtaLookup(string TrackingNumber, string DocumentRef, int? Places, bool Complete, int PagesRead, string Message, bool Found = true);
public enum LabelIdentityCheck { Unconfirmed, Verified, Contradiction }
// Text is operation-local; do not serialize, cache, log or display raw PDF text.
public sealed record LabelPdfPageInfo(int Number, double WidthMm, double HeightMm, string Text)
{ public override string ToString() => $"Сторінка {Number}: {WidthMm:0.##} × {HeightMm:0.##} мм"; }
public sealed record LabelPdfInspection(IReadOnlyList<LabelPdfPageInfo> Pages);
public sealed record NovaPoshtaDiagnosticResult(NovaPoshtaLookup Lookup, ShippingLabelDocument? Document,
    LabelPdfInspection? Inspection, LabelIdentityCheck Identity, bool AllPlacesVerified, string Message)
{
    public bool CanUseInBatch => Lookup.Complete && Lookup.Found && Document is not null && Identity == LabelIdentityCheck.Verified && AllPlacesVerified &&
        (Lookup.DocumentRef.Length == 0 || Guid.TryParse(Lookup.DocumentRef, out _)) && Inspection is { Pages.Count: > 0 } &&
        Inspection.Pages.All(p => Math.Abs(p.WidthMm - 100) <= 0.8 && Math.Abs(p.HeightMm - 100) <= 0.8);
}

public static class ShippingCarrierNames
{
    public static ShippingCarrier FromDocumentedDeliveryField(string value) => value.Trim().ToLowerInvariant() switch
    {
        "nova_poshta" or "нова пошта" or "новая почта" or "nova poshta" => ShippingCarrier.NovaPoshta,
        "rozetka delivery" or "rozetka_delivery" or "доставка rozetka" or "rozetka" => ShippingCarrier.RozetkaDelivery,
        _ => ShippingCarrier.Unknown
    };
    public static string Display(ShippingCarrier carrier) => carrier switch
    {
        ShippingCarrier.NovaPoshta => "Нова пошта",
        ShippingCarrier.RozetkaDelivery => "Rozetka Delivery",
        _ => "Невідомий перевізник"
    };
    public static IReadOnlyList<ShipmentReference> ForOrder(MarketplaceOrder order) => order.Shipments
        .Where(s => !string.IsNullOrWhiteSpace(s.TrackingNumber))
        .Select(s => new ShipmentReference(order.Key, order.Number, FromDocumentedDeliveryField(s.Carrier),
            s.TrackingNumber.Trim(), s.ShipmentId, s.Source, s.Places))
        .DistinctBy(s => (s.Carrier, s.TrackingNumber, s.ShipmentId)).ToArray();
}
