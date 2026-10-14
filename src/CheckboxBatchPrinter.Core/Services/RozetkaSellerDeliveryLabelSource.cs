using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

// Seller's delivery module reads only existing documents. Each PDF request contains exactly one
// verified primary TTN because the API does not document a mapping of batch-PDF pages to numbers.
public static class RozetkaSellerDeliveryReadOnlyContract
{
    public const string ListPath = "/delivery-rozetka/ttn-list";
    public const string PrintPath = "/delivery-rozetka/ttn-print-batch";
    public const int MaximumPages = 10;
    public static bool ValidPrimary(string value) => Regex.IsMatch(value, @"\ARMP-[0-9]{1,20}\z");
    public static bool ValidOrderId(string value) => Regex.IsMatch(value, @"\A[1-9][0-9]{0,18}\z") &&
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    public static bool ValidRoute(HttpRequestMessage request)
    {
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != "https" || uri.Host != "api-seller.rozetka.com.ua" ||
            !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) return false;
        if (uri.AbsolutePath == ListPath && request.Method == HttpMethod.Get && request.Content is null)
            return ValidListQuery(uri);
        if (uri.AbsolutePath != PrintPath || request.Method != HttpMethod.Post || uri.Query.Length != 0 ||
            request.Content is not StringContent || request.Content.Headers.ContentType?.MediaType != "application/json" ||
            request.Content.Headers.ContentLength is not (>= 1 and <= 4096)) return false;
        try
        {
            using var json = JsonDocument.Parse(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != 1 || properties[0].Name != "track_numbers" ||
                properties[0].Value.ValueKind != JsonValueKind.Array || properties[0].Value.GetArrayLength() != 1) return false;
            var number = properties[0].Value[0];
            return number.ValueKind == JsonValueKind.String && ValidPrimary(number.GetString()!);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        { return false; }
    }

    private static bool ValidListQuery(Uri uri)
    {
        if (uri.Query.Length is < 1 or > 512) return false;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in uri.Query[1..].Split('&'))
        {
            var pair = segment.Split('=', 2);
            if (pair.Length != 2 || !values.TryAdd(Uri.UnescapeDataString(pair[0]), Uri.UnescapeDataString(pair[1]))) return false;
        }
        return values.Count is 3 or 4 && values.Keys.All(k => k is "order_id" or "page" or "pageSize" or "ttn") &&
            values.TryGetValue("order_id", out var order) && ValidOrderId(order) &&
            values.TryGetValue("page", out var pageText) && int.TryParse(pageText, NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page is >= 1 and <= MaximumPages &&
            values.TryGetValue("pageSize", out var sizeText) && int.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size is >= 1 and <= 100 &&
            (!values.TryGetValue("ttn", out var ttn) || ValidPrimary(ttn));
    }
}

public sealed class RozetkaSellerDeliveryLabelSource(ShippingLabelHttp http, IMarketplaceSettingsStore settings,
    IMarketplaceSecretStore secrets, IReadOnlyList<ShipmentReference>? selectedShipments = null) : IShippingLabelSource
{
    public const string PartnerSource = "Rozetka.carrier.carrier_track_num";
    private const string Host = "https://api-seller.rozetka.com.ua";
    private readonly IReadOnlyList<ShipmentReference>? _selectedShipments = selectedShipments?.ToArray();
    // Never retained by the application-wide source: cache only within one frozen preparation.
    private readonly Dictionary<(string Connection, string Credentials, string Order, string Row, string Primary), byte[]> _batchPdfs = [];
    private (string Connection, string Credentials, string Order)? _batchOrder;
    private long _batchPdfBytes;

    public static bool IsSellerDeliveryShipment(ShipmentReference shipment) =>
        shipment.Order is { Marketplace: MarketplaceKind.Rozetka } &&
        (shipment.Carrier == ShippingCarrier.RozetkaDelivery || shipment.Source == PartnerSource);

    public async Task<ShippingLabelDocument> GetAsync(ShipmentReference shipment, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSellerDeliveryShipment(shipment) || shipment.Order is not { } order)
            throw Invalid("Для цього джерела потрібна наявна накладна замовлення Rozetka.");
        if (!RozetkaSellerDeliveryReadOnlyContract.ValidOrderId(order.OrderId) ||
            !Regex.IsMatch(shipment.TrackingNumber, @"\A[0-9A-Za-z-]{4,80}\z"))
            throw Invalid("Некоректний номер замовлення або накладної Rozetka.");
        var config = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(order.ConnectionId) ||
            config.Connections.Count(c => c.Id == order.ConnectionId && c.Enabled && c.Marketplace == MarketplaceKind.Rozetka) != 1)
            throw Missing("Увімкніть підключення Rozetka Seller цього замовлення у налаштуваннях маркетплейсів.");
        MarketplaceCredentials? credentials;
        try { credentials = await secrets.LoadAsync(order.ConnectionId, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException)
        { throw Missing("Не вдалося прочитати збережені облікові дані Rozetka Seller. Збережіть їх повторно у налаштуваннях."); }
        if (!RozetkaApiCredentials.ValidToken(credentials?.Token))
            throw Missing(RozetkaApiCredentials.SetupMessage);
        var token = RozetkaApiCredentials.RequireToken(credentials);
        cancellationToken.ThrowIfCancellationRequested();

        var record = await LookupAsync(shipment, token, cancellationToken).ConfigureAwait(false);
        var proof = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var batchOrder = (order.ConnectionId, proof, order.OrderId);
        if (_selectedShipments is not null && _batchOrder != batchOrder)
        {
            _batchPdfs.Clear(); _batchPdfBytes = 0; _batchOrder = batchOrder;
        }
        var cacheKey = (order.ConnectionId, proof, order.OrderId, record.Id, record.Primary);
        if (_selectedShipments is null || !_batchPdfs.TryGetValue(cacheKey, out var pdf))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var printBody = JsonSerializer.Serialize(new { track_numbers = new[] { record.Primary } });
            pdf = await http.SendAsync(() => Request(HttpMethod.Post, RozetkaSellerDeliveryReadOnlyContract.PrintPath, token, printBody),
                "application/pdf", cancellationToken).ConfigureAwait(false);
            ShippingLabelHttp.ValidatePdf(pdf);
            if (_selectedShipments is not null)
            {
                if (_batchPdfBytes + pdf.Length > 80L * 1024 * 1024) { _batchPdfs.Clear(); _batchPdfBytes = 0; }
                _batchPdfs[cacheKey] = pdf; _batchPdfBytes += pdf.Length;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var covered = (_selectedShipments ?? [shipment]).Where(s => s.Order == order &&
            (s.ShipmentId.Length == 0 || s.ShipmentId == record.Id || s.ShipmentId == record.Primary) &&
            (s.TrackingNumber == record.Primary && s.Carrier == ShippingCarrier.RozetkaDelivery ||
             s.Source == PartnerSource && record.Partner.Length > 0 && s.TrackingNumber == record.Partner))
            .Append(shipment).Distinct().ToArray();
        return new(shipment, order.ConnectionId, record.Primary, pdf.ToArray(), Convert.ToHexString(SHA256.HashData(pdf)),
            DateTimeOffset.UtcNow, shipment.ExpectedPlaces is > 0 and <= 100 ? shipment.ExpectedPlaces : 0)
        { Source = "RozetkaSeller.Delivery", CoveredShipments = Array.AsReadOnly(covered) };
    }

    private async Task<DeliveryRecord> LookupAsync(ShipmentReference shipment, string token, CancellationToken ct)
    {
        var order = shipment.Order!;
        var requestedPrimary = RozetkaSellerDeliveryReadOnlyContract.ValidPrimary(shipment.TrackingNumber);
        var rows = new List<DeliveryRecord>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        int? total = null, pages = null, perPage = null;
        for (var page = 1; page <= RozetkaSellerDeliveryReadOnlyContract.MaximumPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var path = RozetkaSellerDeliveryReadOnlyContract.ListPath + "?order_id=" + order.OrderId + "&page=" + page.ToString(CultureInfo.InvariantCulture) + "&pageSize=100" +
                (requestedPrimary ? "&ttn=" + shipment.TrackingNumber : "");
            using var response = await JsonAsync(() => Request(HttpMethod.Get, path, token), ct).ConfigureAwait(false);
            CheckSuccess(response.RootElement);
            var content = Field(response.RootElement, "content");
            var models = Field(content, "models");
            var meta = Field(content, "_meta");
            var current = Integer(meta, "currentPage", 1, RozetkaSellerDeliveryReadOnlyContract.MaximumPages);
            var pageCount = Integer(meta, "pageCount", 0, RozetkaSellerDeliveryReadOnlyContract.MaximumPages);
            var totalCount = Integer(meta, "totalCount", 0, RozetkaSellerDeliveryReadOnlyContract.MaximumPages * 100);
            var size = Integer(meta, "perPage", 1, 100);
            if (models.ValueKind != JsonValueKind.Array || current != page || models.GetArrayLength() > size ||
                pageCount != (totalCount == 0 ? 0 : (totalCount + size - 1) / size) && !(totalCount == 0 && pageCount == 1) ||
                page > Math.Max(pageCount, 1) ||
                total is not null && total != totalCount || pages is not null && pages != pageCount || perPage is not null && perPage != size)
                throw Invalid("Seller API повернуло неповний або суперечливий список накладних. Оновіть дані.");
            total = totalCount; pages = pageCount; perPage = size;
            foreach (var model in models.EnumerateArray())
            {
                var id = PositiveId(model, "id");
                var orderId = PositiveId(model, "order_id");
                var primary = Text(model, "ttn");
                var partnerField = Field(model, "carrier_track_num");
                if (id.Length == 0 || orderId.Length == 0 || primary.Length == 0 || !seenIds.Add(id) ||
                    partnerField.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String))
                    throw Invalid("Seller API повернуло некоректні або повторні дані накладної.");
                rows.Add(new(id, orderId, primary, Text(model, "carrier_track_num")));
            }
            if (page >= Math.Max(pageCount, 1)) break;
        }
        if (rows.Count != total) throw Invalid("Не отримано всі сторінки накладних цього замовлення. Друк заблоковано.");
        var matching = rows.Where(r => r.Primary == shipment.TrackingNumber ||
            shipment.Source == PartnerSource && r.Partner.Length > 0 && r.Partner == shipment.TrackingNumber).ToArray();
        if (matching.Length == 0) throw new ShippingLabelException(LabelFailureKind.MissingDocument, "Накладну не знайдено у підключенні Rozetka Seller цього замовлення.");
        if (matching.Length != 1 || matching[0].OrderId != order.OrderId ||
            !RozetkaSellerDeliveryReadOnlyContract.ValidPrimary(matching[0].Primary) ||
            shipment.ShipmentId.Length > 0 && shipment.ShipmentId != matching[0].Id && shipment.ShipmentId != matching[0].Primary)
            throw Invalid("Не підтверджено однозначну належність накладної цьому замовленню Rozetka. Друк заблоковано.");
        return matching[0];
    }

    private async Task<JsonDocument> JsonAsync(Func<HttpRequestMessage> factory, CancellationToken ct)
    {
        try { return JsonDocument.Parse(await http.SendAsync(factory, "application/json", ct).ConfigureAwait(false)); }
        catch (JsonException) { throw Invalid("Seller API повернуло некоректну JSON-відповідь. Друк заблоковано."); }
    }
    private static HttpRequestMessage Request(HttpMethod method, string path, string token, string? body = null)
    {
        var request = new HttpRequestMessage(method, Host + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Content-Language", "uk");
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }
    private static void CheckSuccess(JsonElement root)
    {
        if (Field(root, "success").ValueKind != JsonValueKind.True)
            throw new ShippingLabelException(LabelFailureKind.AccessDenied, "Rozetka Seller відхилило доступ до накладної. Перевірте облікові дані та права продавця.");
    }
    private static int Integer(JsonElement value, string key, int minimum, int maximum)
    {
        var text = Text(value, key);
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < minimum || number > maximum)
            throw Invalid("Seller API не підтвердило повноту списку накладних.");
        return number;
    }
    private static string PositiveId(JsonElement value, string key)
    {
        var text = Text(value, key);
        return RozetkaSellerDeliveryReadOnlyContract.ValidOrderId(text) ? text : "";
    }
    private static JsonElement Field(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) ? field : default;
    private static string Text(JsonElement value, string key) => Field(value, key).ValueKind is JsonValueKind.String or JsonValueKind.Number ? Field(value, key).ToString() : "";
    private static ShippingLabelException Missing(string message) => new(LabelFailureKind.ConnectionRequired, message) { MissingCredentials = true };
    private static ShippingLabelException Invalid(string message) => new(LabelFailureKind.InvalidDocument, message);
    private sealed record DeliveryRecord(string Id, string OrderId, string Primary, string Partner);
}
