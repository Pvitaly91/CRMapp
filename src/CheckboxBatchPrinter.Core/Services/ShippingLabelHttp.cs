using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

// Dedicated transport: no credential-bearing URL is logged or exposed through exceptions.
public sealed class ShippingLabelHttp(HttpClient client, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public const int MaximumBytes = 20 * 1024 * 1024;
    public static void Validate(HttpRequestMessage request)
    {
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("Недозволений маршрут доставки.");
        var allowed = uri.Host switch
        {
            "rz-delivery.rozetka.ua" => request.Method == HttpMethod.Get &&
                (uri.AbsolutePath == "/api/track/label" || Regex.IsMatch(uri.AbsolutePath, @"^/api/track/[0-9A-Za-z-]+$")),
            "api-seller.rozetka.com.ua" => request.Method == HttpMethod.Post && uri.AbsolutePath == "/sites" ||
                request.Method == HttpMethod.Get && uri.AbsolutePath is "/ttns/ttn-list" or "/ttns/ttn-print/zebra",
            _ => false
        };
        if (!allowed) throw new InvalidOperationException("Дозволено лише читання наявних накладних та їхніх копій.");
    }
    public async Task<byte[]> SendAsync(Func<HttpRequestMessage> factory, string expectedType, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = factory();
            Validate(request);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and < 400)
                    throw new ShippingLabelException(LabelFailureKind.AccessDenied, "Перенаправлення етикетки відхилено для захисту токена.");
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new ShippingLabelException(LabelFailureKind.AccessDenied, "Немає доступу до етикетки. Перевірте локальне підключення та права на саме цю накладну.");
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new ShippingLabelException(LabelFailureKind.MissingDocument, "Документ накладної не знайдено в цьому підключенні.");
                if ((int)response.StatusCode >= 500 || (int)response.StatusCode == 429)
                {
                    if (attempt >= 2) throw new ShippingLabelException(LabelFailureKind.Temporary, "API доставки тимчасово недоступне.");
                    var wait = response.Headers.RetryAfter?.Delta ??
                        (response.Headers.RetryAfter?.Date is { } retryAt ? retryAt - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(attempt + 1));
                    // Do not shorten server delay; excessive delay requires a new explicit request.
                    if (wait > TimeSpan.FromSeconds(45)) throw new ShippingLabelException(LabelFailureKind.Temporary, "API просить повторити пізніше.");
                    await (delay ?? Task.Delay)(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw new ShippingLabelException(LabelFailureKind.InvalidDocument, "API відхилило читання накладної.");
                if (response.Content.Headers.ContentType?.MediaType != expectedType)
                    throw new ShippingLabelException(LabelFailureKind.InvalidDocument, "API повернуло не очікуваний PDF/JSON. Документ не друкуватиметься.");
                if (response.Content.Headers.ContentLength > MaximumBytes) throw TooLarge();
                using var buffer = new MemoryStream();
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                var chunk = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + count > MaximumBytes) throw TooLarge();
                    await buffer.WriteAsync(chunk.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                }
                return buffer.ToArray();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new ShippingLabelException(LabelFailureKind.Temporary, "Перевищено час читання етикетки."); }
            catch (HttpRequestException)
            { throw new ShippingLabelException(LabelFailureKind.Temporary, "Немає з’єднання з API доставки."); }
        }
    }
    private static ShippingLabelException TooLarge() => new(LabelFailureKind.InvalidDocument, "Етикетка перевищує обмеження 20 MiB.");
    public static void ValidatePdf(byte[] bytes)
    {
        if (bytes.Length is < 8 or > MaximumBytes || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            throw new ShippingLabelException(LabelFailureKind.InvalidDocument, "Отриманий документ не є PDF.");
    }
}

public sealed class OfficialShippingLabelSource(ShippingLabelHttp http, IShippingSettingsStore settings,
    IMarketplaceSettingsStore marketplaceSettings, IMarketplaceSecretStore marketplaceSecrets) : IShippingLabelSource
{
    public async Task<ShippingLabelDocument> GetAsync(ShipmentReference shipment, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shipment.TrackingNumber)) throw new ShippingLabelException(LabelFailureKind.MissingDocument, "Накладну не створено.");
        if (shipment.Carrier == ShippingCarrier.Unknown) throw new ShippingLabelException(LabelFailureKind.UnsupportedFormat, "Перевізник не визначений документованими полями доставки.");
        var config = await settings.LoadAsync(cancellationToken);
        return shipment.Carrier == ShippingCarrier.RozetkaDelivery
            ? await ReadDeliveryAsync(shipment, config, cancellationToken)
            : await ReadNovaPoshtaViaSellerAsync(shipment, config, cancellationToken);
    }
    private async Task<ShippingLabelDocument> ReadDeliveryAsync(ShipmentReference shipment, ShippingLabelSettings config, CancellationToken ct)
    {
        var token = config.RozetkaDeliveryToken;
        if (string.IsNullOrWhiteSpace(token)) throw new ShippingLabelException(LabelFailureKind.ConnectionRequired, "Потрібен окремий токен Rozetka Delivery у налаштуваннях наклейок.");
        var id = shipment.ShipmentId.Length > 0 ? shipment.ShipmentId : shipment.TrackingNumber;
        if (!Regex.IsMatch(id, @"^[0-9A-Za-z-]{4,80}$")) throw new ShippingLabelException(LabelFailureKind.InvalidDocument, "Некоректний ідентифікатор відправлення.");
        using var details = await JsonAsync("https://rz-delivery.rozetka.ua/api/track/" + Uri.EscapeDataString(id), token, ct);
        CheckDeliverySuccess(details.RootElement);
        var data = Field(details.RootElement, "data");
        var actualId = Text(data, "id");
        var partnerTtn = Text(data, "carrier_track_num");
        if (actualId != id || (shipment.TrackingNumber != actualId && shipment.TrackingNumber != partnerTtn))
            throw new ShippingLabelException(LabelFailureKind.InvalidDocument, "Деталі доставки відповідають іншій ТТН. Друк заблоковано.");
        // One request per verified shipment: Swagger promises a PDF but no per-page mapping for a multi-ID response.
        using var label = await JsonAsync("https://rz-delivery.rozetka.ua/api/track/label?id=" + Uri.EscapeDataString(actualId), token, ct);
        CheckDeliverySuccess(label.RootElement);
        byte[] pdf;
        try { pdf = Convert.FromBase64String(Text(Field(label.RootElement, "data"), "label")); }
        catch (FormatException) { throw new ShippingLabelException(LabelFailureKind.InvalidDocument, "Некоректна base64-етикетка."); }
        return Document(shipment, config.RozetkaDeliveryConnectionId, actualId, pdf, Places(data, "places"));
    }
    private async Task<ShippingLabelDocument> ReadNovaPoshtaViaSellerAsync(ShipmentReference shipment, ShippingLabelSettings config, CancellationToken ct)
    {
        // Verified official Seller API alternative. A direct NP endpoint is deliberately not guessed.
        var connections = await marketplaceSettings.LoadAsync(ct);
        var connectionId = config.NovaPoshtaSellerConnectionId.Length > 0 ? config.NovaPoshtaSellerConnectionId
            : shipment.Order.Marketplace == MarketplaceKind.Rozetka ? shipment.Order.ConnectionId : "";
        if (!connections.Connections.Any(c => c.Id == connectionId && c.Enabled && c.Marketplace == MarketplaceKind.Rozetka))
            throw new ShippingLabelException(LabelFailureKind.ConnectionRequired,
                "Потрібне підключення Rozetka Seller з доступом до цієї ТТН Нової пошти. Прямий NP API ще не підключено: його актуальний контракт потребує перевірки.");
        var credentials = await marketplaceSecrets.LoadAsync(connectionId, ct);
        if (credentials is null || string.IsNullOrWhiteSpace(credentials.Login) || credentials.Password.Length == 0)
            throw new ShippingLabelException(LabelFailureKind.ConnectionRequired, "Налаштуйте підключення Rozetka Seller локально.");
        var body = JsonSerializer.Serialize(new { username = credentials.Login.Trim(), password = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.Password)) });
        using var auth = JsonDocument.Parse(await http.SendAsync(() => new(HttpMethod.Post, "https://api-seller.rozetka.com.ua/sites")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") }, "application/json", ct));
        CheckSellerSuccess(auth.RootElement);
        var token = Text(Field(auth.RootElement, "content"), "access_token");
        if (string.IsNullOrWhiteSpace(token)) throw new ShippingLabelException(LabelFailureKind.AccessDenied, "Seller API не повернуло токен.");
        using var found = await JsonAsync("https://api-seller.rozetka.com.ua/ttns/ttn-list?ttn=" + Uri.EscapeDataString(shipment.TrackingNumber), token, ct);
        CheckSellerSuccess(found.RootElement);
        var list = Field(Field(found.RootElement, "content"), "ttn_list");
        var matches = list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Where(x => Text(x, "IntDocNumber") == shipment.TrackingNumber).ToArray() : [];
        if (matches.Length != 1) throw new ShippingLabelException(matches.Length == 0 ? LabelFailureKind.MissingDocument : LabelFailureKind.InvalidDocument,
            "Немає однозначного доступного документа для цієї ТТН у Seller API. Наявність номера в Prom не гарантує доступу.");
        var actualRef = Text(matches[0], "Ref");
        if (!Guid.TryParse(actualRef, out _) || shipment.ShipmentId.Length > 0 && shipment.ShipmentId != actualRef)
            throw new ShippingLabelException(LabelFailureKind.InvalidDocument, "Не підтверджено document Ref ТТН.");
        var pdf = await http.SendAsync(() => Request("https://api-seller.rozetka.com.ua/ttns/ttn-print/zebra?ttnNumbers%5B%5D=" + Uri.EscapeDataString(shipment.TrackingNumber), token), "application/pdf", ct);
        return Document(shipment, connectionId, actualRef, pdf, Places(matches[0], "SeatsAmount"));
    }
    private static ShippingLabelDocument Document(ShipmentReference shipment, string connection, string id, byte[] pdf, int places)
    {
        ShippingLabelHttp.ValidatePdf(pdf);
        return new(shipment, connection, id, pdf, Convert.ToHexString(SHA256.HashData(pdf)), DateTimeOffset.UtcNow, places)
        { Source = shipment.Carrier == ShippingCarrier.NovaPoshta ? "RozetkaSeller.Zebra" : "RozetkaDelivery.Label" };
    }
    private async Task<JsonDocument> JsonAsync(string url, string token, CancellationToken ct) =>
        JsonDocument.Parse(await http.SendAsync(() => Request(url, token), "application/json", ct));
    private static HttpRequestMessage Request(string url, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Content-Language", "uk");
        return request;
    }
    private static void CheckDeliverySuccess(JsonElement root)
    {
        if (Field(root, "statusCode").ToString() != "0")
            throw new ShippingLabelException(LabelFailureKind.AccessDenied, "Rozetka Delivery не надало документ; перевірте токен та доступ.");
    }
    private static void CheckSellerSuccess(JsonElement root)
    {
        if (Field(root, "success").ValueKind != JsonValueKind.True)
            throw new ShippingLabelException(LabelFailureKind.AccessDenied, "Seller API не надало документ; перевірте права.");
    }
    private static int Places(JsonElement data, string name) => int.TryParse(Text(data, name), out var value) && value is > 0 and <= 100 ? value : 0;
    private static JsonElement Field(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) ? field : default;
    private static string Text(JsonElement value, string key) => Field(value, key).ValueKind is JsonValueKind.String or JsonValueKind.Number ? Field(value, key).ToString() : "";
}
