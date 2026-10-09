using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

// Proof stays in memory and binds a live carrier document to the exact account that read it.
public static class WaybillAccountProof
{
    public static string ScopeKey(ShippingCarrier carrier, string connectionId) =>
        ((int)carrier).ToString(CultureInfo.InvariantCulture) + ":" + connectionId.Length + ":" + connectionId;
    public static string Create(ShippingCarrier carrier, string connectionId, string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ScopeKey(carrier, connectionId) + ":" + secret)));
}

// Direct carrier documents never borrow marketplace order credentials or synthesize an order.
public sealed class WaybillLabelPreparation(IShippingSettingsStore settingsStore, ShippingLabelHttp http,
    NovaPoshtaReadOnlyClient novaPoshta, ILabelPdfInspector inspector, IShippingLabelRenderer renderer) : IWaybillLabelPreparation
{
    public async Task<PreparedWaybillBatch> PrepareAsync(IReadOnlyList<WaybillPrintRequest> documents,
        LabelPrintSettings settings, LabelPrinterGeometry geometry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        settings.Validate();
        if (documents.Count is 0 or > 100) throw new InvalidOperationException("Виберіть від 1 до 100 накладних для друку.");
        var requests = documents.Select(r => new WaybillPrintRequest(r.Waybill with { },
            Array.AsReadOnly(r.OrderNumbers.Distinct(StringComparer.Ordinal).ToArray()))).ToArray();
        var config = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        config = config with { NovaPoshtaConnections = config.NovaPoshtaConnections.ToArray() };
        var proofs = new Dictionary<string, string>(StringComparer.Ordinal);
        // Validate the complete selection before any carrier requests, so an old row cannot switch accounts.
        foreach (var request in requests)
        {
            var row = request.Waybill;
            ValidateIdentity(row);
            var secret = Secret(config, row.Carrier, row.ConnectionId);
            var proof = WaybillAccountProof.Create(row.Carrier, row.ConnectionId, secret);
            if (row.CredentialFingerprint.Length == 0 || row.CredentialFingerprint != proof)
                throw Stale();
            proofs[WaybillAccountProof.ScopeKey(row.Carrier, row.ConnectionId)] = proof;
        }
        var selected = requests.GroupBy(r => (r.Waybill.Carrier, r.Waybill.ConnectionId, r.Waybill.DocumentId))
            .Select(group =>
            {
                var first = group.First();
                if (group.Any(r => r.Waybill.TrackingNumber != first.Waybill.TrackingNumber || r.Waybill.Places != first.Waybill.Places))
                    throw Invalid("Вибір містить суперечливі дані однієї накладної. Оновіть список.");
                return new WaybillPrintRequest(first.Waybill, group.SelectMany(r => r.OrderNumbers).Distinct(StringComparer.Ordinal).ToArray());
            }).ToArray();
        if (selected.GroupBy(r => (r.Waybill.Carrier, r.Waybill.ConnectionId, r.Waybill.TrackingNumber))
            .Any(group => group.Select(r => r.Waybill.DocumentId).Distinct(StringComparer.Ordinal).Skip(1).Any()))
            throw Invalid("Один номер накладної має різні ідентифікатори в цьому акаунті. Оновіть список.");
        var fetched = new Dictionary<(ShippingCarrier, string, string), ShippingLabelDocument>();
        foreach (var group in selected.GroupBy(r => (r.Waybill.Carrier, r.Waybill.ConnectionId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Key.Carrier == ShippingCarrier.NovaPoshta)
            {
                var connection = config.NovaPoshtaConnections.Single(c => c.Id == group.Key.ConnectionId);
                var shipments = group.Select(Shipment).ToArray();
                var pdf = await novaPoshta.ReadMarkingsAsync(connection, shipments, cancellationToken).ConfigureAwait(false);
                var inspection = await inspector.InspectAsync(pdf, cancellationToken).ConfigureAwait(false);
                var mapped = NovaPoshtaBulkLabels.Map(connection.Id, shipments, pdf, inspection);
                foreach (var request in group)
                {
                    var shipment = Shipment(request);
                    fetched[Identity(request.Waybill)] = mapped[shipment.TrackingNumber] with
                    { Shipment = shipment, ExpectedPages = shipment.ExpectedPlaces };
                }
            }
            else
                foreach (var request in group)
                    fetched[Identity(request.Waybill)] = await DeliveryAsync(request, config, cancellationToken).ConfigureAwait(false);
        }
        var pages = new List<ShippingLabelPage>();
        var seenPages = new HashSet<(ShippingCarrier, string, string, int)>();
        long rasterBytes = 0;
        double decodedBytes = 0;
        foreach (var request in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = fetched[Identity(request.Waybill)];
            var rendered = await renderer.RenderAsync(document, geometry.DpiX, geometry.DpiY, cancellationToken).ConfigureAwait(false);
            if (rendered.Count == 0 || document.ExpectedPages > rendered.Count)
                throw Invalid("Документ не має етикеток для всіх місць вибраної накладної.");
            if (document.SourcePageNumbers.Count > 0 && rendered.Count != document.SourcePageNumbers.Count)
                throw Invalid("Не всі перевірені сторінки накладної підготовлено до друку.");
            if (rendered.Select(p => p.PageNumber).Distinct().Count() != rendered.Count)
                throw Invalid("Рендерер повторив сторінку накладної.");
            foreach (var page in rendered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (page.Shipment != document.Shipment || page.DocumentId != document.DocumentId ||
                    page.Fingerprint != document.Fingerprint || page.ConnectionId != document.ConnectionId || page.PageNumber < 1)
                    throw Invalid("Сторінка не відповідає перевіреній накладній.");
                if (!seenPages.Add((page.Shipment.Carrier, page.ConnectionId, page.DocumentId, page.PageNumber))) continue;
                _ = LabelPageLayout.Place(page, settings, geometry);
                rasterBytes += page.Png.Length;
                decodedBytes += Math.Ceiling(page.WidthMm * geometry.DpiX / 25.4) * Math.Ceiling(page.HeightMm * geometry.DpiY / 25.4) * 4;
                if (decodedBytes > 256L * 1024 * 1024) throw new InvalidOperationException("Растери пакета перевищують 256 МіБ. Виберіть менший пакет.");
                if (pages.Count >= 250 || rasterBytes > 80L * 1024 * 1024) throw new InvalidOperationException("Пакет перевищує 250 сторінок або 80 MiB.");
                pages.Add(page with { Png = page.Png.ToArray() });
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(new(Guid.NewGuid().ToString("N"), Array.AsReadOnly(pages.ToArray()), settings with { })
        {
            IsWaybillBatch = true, SelectedWaybillCount = selected.Length,
            RasterDpiX = geometry.DpiX, RasterDpiY = geometry.DpiY
        }, new ReadOnlyDictionary<string, string>(proofs));
    }

    public async Task ValidateAsync(PreparedWaybillBatch prepared, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!prepared.Batch.IsWaybillBatch || prepared.Batch.Pages.Count == 0 || prepared.CredentialFingerprints.Count == 0)
            throw Invalid("Пакет накладних порожній або не перевірений.");
        var config = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var group in prepared.Batch.Pages.GroupBy(p => (p.Shipment.Carrier, p.ConnectionId)))
        {
            var scope = WaybillAccountProof.ScopeKey(group.Key.Carrier, group.Key.ConnectionId);
            if (!prepared.CredentialFingerprints.TryGetValue(scope, out var previous) ||
                previous != WaybillAccountProof.Create(group.Key.Carrier, group.Key.ConnectionId, Secret(config, group.Key.Carrier, group.Key.ConnectionId)))
                throw Stale();
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<ShippingLabelDocument> DeliveryAsync(WaybillPrintRequest request, ShippingLabelSettings config, CancellationToken ct)
    {
        var shipment = Shipment(request);
        var id = request.Waybill.DocumentId;
        try
        {
            using var details = await JsonAsync("https://rz-delivery.rozetka.ua/api/track/" + Uri.EscapeDataString(id), config.RozetkaDeliveryToken, ct).ConfigureAwait(false);
            DeliverySuccess(details.RootElement);
            var data = Field(details.RootElement, "data");
            if (Text(data, "id") != id || shipment.TrackingNumber != id && Text(data, "carrier_track_num") != shipment.TrackingNumber)
                throw Invalid("Деталі доставки належать іншій накладній.");
            if (Deleted(data)) throw new ShippingLabelException(LabelFailureKind.MissingDocument, "Вибрана накладна Rozetka Delivery видалена.");
            var placesText = Text(data, "places");
            var places = placesText.Length == 0 ? request.Waybill.Places ?? 0 :
                int.TryParse(placesText, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is > 0 and <= 1000 ? number : throw Invalid("Невідома кількість місць накладної.");
            if (request.Waybill.Places is { } previous && places != previous) throw Stale();
            using var label = await JsonAsync("https://rz-delivery.rozetka.ua/api/track/label?id=" + Uri.EscapeDataString(id), config.RozetkaDeliveryToken, ct).ConfigureAwait(false);
            DeliverySuccess(label.RootElement);
            byte[] pdf;
            try { pdf = Convert.FromBase64String(Text(Field(label.RootElement, "data"), "label")); }
            catch (FormatException) { throw Invalid("Накладна містить некоректний PDF."); }
            ShippingLabelHttp.ValidatePdf(pdf);
            var inspection = await inspector.InspectAsync(pdf, ct).ConfigureAwait(false);
            if (inspection.Pages.Count == 0 || inspection.Pages.Count < places || inspection.Pages.Count > 100)
                throw Invalid("Накладна не містить етикеток усіх місць або перевищує 100 сторінок.");
            return new(shipment with { ExpectedPlaces = places }, config.RozetkaDeliveryConnectionId, id, pdf,
                Convert.ToHexString(SHA256.HashData(pdf)), DateTimeOffset.UtcNow, places)
            { Source = "RozetkaDelivery.Label", SourcePageNumbers = Array.AsReadOnly(inspection.Pages.Select(p => p.Number).ToArray()) };
        }
        catch (JsonException) { throw Invalid("API доставки повернуло неочікувані дані накладної."); }
    }
    private async Task<JsonDocument> JsonAsync(string url, string token, CancellationToken ct) =>
        JsonDocument.Parse(await http.SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("Content-Language", "uk");
            return request;
        }, "application/json", ct).ConfigureAwait(false));
    private static (ShippingCarrier, string, string) Identity(WaybillRecord row) => (row.Carrier, row.ConnectionId, row.DocumentId);
    private static ShipmentReference Shipment(WaybillPrintRequest request) => new(null, string.Join(", ", request.OrderNumbers),
        request.Waybill.Carrier, request.Waybill.TrackingNumber,
        request.Waybill.DocumentId.StartsWith("ttn:", StringComparison.Ordinal) ? "" : request.Waybill.DocumentId,
        "Carrier.Waybills", request.Waybill.Places ?? 0) { CarrierConnectionId = request.Waybill.ConnectionId };
    private static string Secret(ShippingLabelSettings config, ShippingCarrier carrier, string connectionId)
    {
        var key = carrier switch
        {
            ShippingCarrier.NovaPoshta => config.NovaPoshtaConnections.Where(c => c.Id == connectionId).ToArray() is { Length: 1 } connections ? connections[0].ApiKey : throw Stale(),
            ShippingCarrier.RozetkaDelivery when config.RozetkaDeliveryConnectionId == connectionId => config.RozetkaDeliveryToken,
            _ => throw Stale()
        };
        if (string.IsNullOrWhiteSpace(key)) throw Stale();
        if (carrier == ShippingCarrier.NovaPoshta && !NovaPoshtaReadOnlyContract.ValidKey(key)) throw Stale();
        return key;
    }
    private static void ValidateIdentity(WaybillRecord row)
    {
        if (row.IsDeleted) throw new ShippingLabelException(LabelFailureKind.MissingDocument, "Видалену накладну не можна друкувати.");
        if (string.IsNullOrWhiteSpace(row.ConnectionId) || string.IsNullOrWhiteSpace(row.DocumentId) || row.Places is <= 0 or > 1000)
            throw Invalid("Некоректні дані накладної. Оновіть список.");
        if (row.Carrier == ShippingCarrier.NovaPoshta)
        {
            if (!Regex.IsMatch(row.TrackingNumber, @"\A[0-9]{14}\z") ||
                row.DocumentId != "ttn:" + row.TrackingNumber && (!Guid.TryParse(row.DocumentId, out var id) || id == Guid.Empty))
                throw Invalid("Некоректний номер або ідентифікатор накладної Нової пошти.");
        }
        else if (row.Carrier != ShippingCarrier.RozetkaDelivery || !Regex.IsMatch(row.DocumentId, @"\A[0-9A-Za-z-]{1,80}\z"))
            throw Invalid("Невідомий перевізник або ідентифікатор накладної.");
    }
    private static JsonElement Field(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field : default;
    private static string Text(JsonElement value, string name) => Field(value, name).ValueKind is JsonValueKind.Number or JsonValueKind.String ? Field(value, name).ToString() : "";
    private static bool Deleted(JsonElement data) => Field(data, "is_deleted") switch
    {
        { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.True } => true,
        var flag when flag.ValueKind is JsonValueKind.String or JsonValueKind.Number && flag.ToString() == "0" => false,
        var flag when flag.ValueKind is JsonValueKind.String or JsonValueKind.Number && flag.ToString() == "1" => true,
        _ => throw Invalid("Невідома ознака видалення накладної.")
    };
    private static void DeliverySuccess(JsonElement root)
    {
        if (Field(root, "statusCode").ToString() != "0")
            throw new ShippingLabelException(LabelFailureKind.AccessDenied, "Rozetka Delivery не надало накладну. Перевірте доступ до цього акаунта.");
    }
    private static ShippingLabelException Invalid(string message) => new(LabelFailureKind.InvalidDocument, message);
    private static ShippingLabelException Stale() => new(LabelFailureKind.ConnectionRequired,
        "Підключення або дані накладної змінилися. Оновіть «Усі накладні» та підготуйте друк повторно.");
}
