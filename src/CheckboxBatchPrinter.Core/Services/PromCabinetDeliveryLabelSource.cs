using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

// A browser session belongs to one existing Prom connection. Its implementation must never
// export cookies to callers, follow redirects, use API bearer credentials or log response bodies.
public interface IPromCabinetSession
{
    Task<PromCabinetLabelResponse> ReadLabelAsync(PromCabinetLabelRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record PromCabinetLabelRequest(string ConnectionId, string OrderId,
    string DeliveryOptionId, string TrackingNumber)
{
    public Uri RequestUri => PromCabinetLabelReadOnlyContract.CreateUri(this);
}

// Content is operation-local. Do not serialize or retain it in application settings/cache/logs.
public sealed record PromCabinetLabelResponse(string ConnectionId, Uri RequestUri, Uri FinalUri,
    int StatusCode, string ContentType, byte[] Content)
{
    public override string ToString() => $"Prom cabinet label response: HTTP {StatusCode}";
}

public static class PromCabinetLabelReadOnlyContract
{
    public const string Host = "my.prom.ua";
    public const string PrintPath = "/cabinet/delivery/print_shipment_pdf";
    public const int MaximumBytes = 20 * 1024 * 1024;

    public static bool ValidRequest(PromCabinetLabelRequest request) =>
        !string.IsNullOrWhiteSpace(request.ConnectionId) && request.ConnectionId.Length <= 200 &&
        request.ConnectionId.All(c => !char.IsControl(c)) &&
        ValidId(request.OrderId) && ValidId(request.DeliveryOptionId) &&
        Regex.IsMatch(request.TrackingNumber, @"\A(?:[A-Z]{2,4}-[0-9]{6,20}|[0-9]{10,20})\z");

    public static Uri CreateUri(PromCabinetLabelRequest request)
    {
        if (!ValidRequest(request)) throw Invalid("Некоректні дані замовлення або способу доставки Prom.");
        return new Uri($"https://{Host}{PrintPath}?order_id={request.OrderId}&delivery_option_id={request.DeliveryOptionId}");
    }

    public static bool ValidUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host != Host || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Fragment.Length != 0 || uri.AbsolutePath != PrintPath) return false;
        var parts = uri.Query.TrimStart('?').Split('&');
        if (parts.Length != 2 || !parts[0].StartsWith("order_id=", StringComparison.Ordinal) ||
            !parts[1].StartsWith("delivery_option_id=", StringComparison.Ordinal)) return false;
        return ValidId(parts[0][9..]) && ValidId(parts[1][19..]);
    }

    private static bool ValidId(string value) => Regex.IsMatch(value, @"\A[1-9][0-9]{0,18}\z") &&
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    private static ShippingLabelException Invalid(string text) => new(LabelFailureKind.InvalidDocument, text);
}

// Bigl orders use the same Prom connection and verified cabinet print route. This source reads
// an already-created label; it never creates, updates or guesses a shipment identifier.
public sealed class PromCabinetDeliveryLabelSource(IMarketplaceSettingsStore settings,
    IPromCabinetSession session, ILabelPdfInspector inspector) : IShippingLabelSource
{
    public const string SourceName = "PromCabinet.Delivery";
    public static bool IsPromDeliveryShipment(ShipmentReference shipment) =>
        shipment.Order is { Marketplace: MarketplaceKind.Prom } && shipment.Carrier == ShippingCarrier.RozetkaDelivery;

    public async Task<ShippingLabelDocument> GetAsync(ShipmentReference shipment,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsPromDeliveryShipment(shipment) || shipment.Order is not { } order)
            throw Invalid("Для кабінету Prom потрібна накладна Rozetka Delivery замовлення Prom або Bigl.");
        if (string.IsNullOrWhiteSpace(shipment.DeliveryServiceId))
            throw Invalid("У замовленні немає ID способу доставки Prom. Оновіть замовлення перед друком накладної.");
        var request = new PromCabinetLabelRequest(order.ConnectionId, order.OrderId,
            shipment.DeliveryServiceId, shipment.TrackingNumber);
        var requestUri = request.RequestUri;
        if (shipment.ExpectedPlaces > 1)
            throw Unsupported("Друк багатомісної накладної через кабінет Prom ще не підтримується.");
        await RequireConnectionAsync(order.ConnectionId, cancellationToken).ConfigureAwait(false);
        var response = await session.ReadLabelAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (response.ConnectionId != order.ConnectionId || response.RequestUri != requestUri ||
            response.FinalUri != requestUri || !PromCabinetLabelReadOnlyContract.ValidUri(response.FinalUri))
            throw Invalid("Кабінет Prom повернув документ іншого запиту або перенаправлення. Увійдіть у потрібний магазин повторно.");
        if (response.StatusCode is 401 or 403)
            throw new ShippingLabelException(LabelFailureKind.ConnectionRequired,
                "Кабінет Prom не дозволив читання накладної. Увійдіть у відповідний магазин у налаштуваннях маркетплейсів.")
            { AppliesToBatch = true, MissingCredentials = true };
        if (response.StatusCode == 404)
            throw new ShippingLabelException(LabelFailureKind.MissingDocument, "Кабінет Prom не знайшов готову накладну цього замовлення.");
        if (response.StatusCode == 429 || response.StatusCode >= 500)
            throw new ShippingLabelException(LabelFailureKind.Temporary,
                "Кабінет Prom тимчасово не надав накладну. Спробуйте пізніше.") { AppliesToBatch = true };
        if (response.StatusCode != 200)
            throw Invalid("Кабінет Prom не повернув готову накладну. Перевірте її доступність у замовленні.");
        if (!string.Equals(response.ContentType.Split(';', 2)[0].Trim(), "application/pdf", StringComparison.OrdinalIgnoreCase))
            throw Unsupported("Кабінет Prom повернув сторінку входу, повідомлення або інший формат замість PDF. Перевірте вхід і накладну в кабінеті.");
        ShippingLabelHttp.ValidatePdf(response.Content);
        var pdf = response.Content.ToArray();
        var inspection = await inspector.InspectAsync(pdf, cancellationToken).ConfigureAwait(false);
        ValidateInspection(inspection, shipment.TrackingNumber);
        await RequireConnectionAsync(order.ConnectionId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new(shipment, order.ConnectionId, order.OrderId + ":" + shipment.DeliveryServiceId + ":" + shipment.TrackingNumber,
            pdf, Convert.ToHexString(SHA256.HashData(pdf)), DateTimeOffset.UtcNow, 1)
        { Source = SourceName };
    }

    private async Task RequireConnectionAsync(string connectionId, CancellationToken ct)
    {
        var config = await settings.LoadAsync(ct).ConfigureAwait(false);
        if (config.Connections.Count(c => c.Id == connectionId && c.Enabled && c.Marketplace == MarketplaceKind.Prom) != 1)
            throw new ShippingLabelException(LabelFailureKind.ConnectionRequired,
                "Увімкніть підключення Prom цього замовлення та увійдіть у його кабінет у налаштуваннях маркетплейсів.")
            { MissingCredentials = true, AppliesToBatch = true };
    }

    private static void ValidateInspection(LabelPdfInspection inspection, string tracking)
    {
        if (inspection.Pages.Count != 1)
            throw Unsupported("Для кабінету Prom підтримується одна сторінка накладної 100 × 100 мм. Багатосторінковий документ не передано на друк.");
        var page = inspection.Pages[0];
        if (page.Number != 1 || !double.IsFinite(page.WidthMm) || !double.IsFinite(page.HeightMm) ||
            Math.Abs(page.WidthMm - 100) > 0.8 || Math.Abs(page.HeightMm - 100) > 0.8)
            throw Unsupported("Накладна Prom має непідтримуваний розмір сторінки; потрібен PDF 100 × 100 мм.");
        // Never match a prefix/suffix of another identifier. Whitespace around a PRM hyphen is
        // typographic only; numeric TTNs must remain one complete run of digits.
        var numberPattern = Regex.Escape(tracking).Replace("-", @"\s*-\s*", StringComparison.Ordinal);
        if (!Regex.IsMatch(page.Text, @"(?<![0-9A-Za-z-])" + numberPattern + @"(?![0-9A-Za-z-])",
                RegexOptions.CultureInvariant))
            throw Invalid("ТТН у PDF кабінету Prom не збігається з вибраним замовленням або не підтверджена текстовим шаром. Друк зупинено.");
    }

    private static ShippingLabelException Invalid(string message) => new(LabelFailureKind.InvalidDocument, message);
    private static ShippingLabelException Unsupported(string message) => new(LabelFailureKind.UnsupportedFormat, message);
}
