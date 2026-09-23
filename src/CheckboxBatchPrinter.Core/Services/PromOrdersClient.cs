using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

/// <summary>Read-only adapter for the published Prom v1 Orders contract.</summary>
public sealed class PromOrdersClient : IMarketplaceOrdersClient
{
    private const string BaseUrl = "https://my.prom.ua/api/v1/";
    private readonly MarketplaceHttpTransport _transport;
    private readonly int _pageSize;
    private readonly int _maxPages;

    public PromOrdersClient(MarketplaceHttpTransport transport, int pageSize = 100, int maxPages = 1000)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (pageSize < 2) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (maxPages < 1) throw new ArgumentOutOfRangeException(nameof(maxPages));
        _transport = transport;
        _pageSize = pageSize;
        _maxPages = maxPages;
    }

    public MarketplaceKind Marketplace => MarketplaceKind.Prom;

    public async Task TestConnectionAsync(MarketplaceConnection connection, MarketplaceCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ValidateConnection(connection, credentials);
        var json = await SendAsync("orders/list?limit=1&sort_dir=desc", credentials, cancellationToken);
        using var document = JsonDocument.Parse(json);
        _ = ReadOrders(document.RootElement);
    }

    public async Task<OrdersFetchResult> FetchAsync(MarketplaceConnection connection, MarketplaceCredentials credentials,
        MarketplaceRange range, CancellationToken cancellationToken = default)
    {
        var orders = new Dictionary<string, MarketplaceOrder>(StringComparer.Ordinal);
        var seenIds = new HashSet<long>();
        long? cursor = null;
        var malformedDates = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateConnection(connection, credentials);
            if (range.From >= range.ToExclusive)
                return new OrdersFetchResult([], false, "Prom: некоректний діапазон дат.");

            var query = "orders/list?limit=" + _pageSize.ToString(CultureInfo.InvariantCulture)
                + "&sort_dir=desc&date_from=" + FormatUtc(range.From)
                + "&date_to=" + FormatUtc(range.ToExclusive);
            for (var page = 0; page < _maxPages; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = query + (cursor.HasValue ? "&last_id=" + cursor.Value.ToString(CultureInfo.InvariantCulture) : "");
                var json = await SendAsync(path, credentials, cancellationToken);
                using var document = JsonDocument.Parse(json);
                var values = ReadOrders(document.RootElement);
                if (values.GetArrayLength() == 0) return Completed();

                var minimumId = long.MaxValue;
                var addedIds = 0;
                var onlyCursorBoundary = cursor.HasValue;
                foreach (var value in values.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var orderId = ReadId(value);
                    if (cursor.HasValue && orderId > cursor.Value)
                        return Incomplete("Prom повернув сторінку за межами запитаного курсора. Завантаження неповне.");
                    minimumId = Math.Min(minimumId, orderId);
                    onlyCursorBoundary &= orderId == cursor;
                    if (!seenIds.Add(orderId)) continue;
                    addedIds++;
                    var order = ParseOrder(value, connection);
                    if (!order.CreatedAt.HasValue)
                    {
                        malformedDates = true;
                        orders[order.Key.OrderId] = order;
                    }
                    else if (order.CreatedAt >= range.From && order.CreatedAt < range.ToExclusive)
                        orders[order.Key.OrderId] = order;
                }

                // The published last_id wording permits an inclusive boundary. Keep the overlap
                // and deduplicate, so an exclusive implementation cannot make us skip an ID.
                if (addedIds == 0)
                {
                    if (onlyCursorBoundary && values.GetArrayLength() == 1) return Completed();
                    return Incomplete("Prom повторює сторінку замовлень. Завантаження неповне.");
                }
                if (cursor.HasValue && minimumId >= cursor.Value)
                    return Incomplete("Prom не просуває курсор замовлень. Завантаження неповне.");
                cursor = minimumId;
            }
            return Incomplete("Досягнуто обмеження сторінок Prom. Звузьте діапазон дат; завантаження неповне.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (MarketplaceApiException exception) { return Incomplete(exception.Message); }
        catch (OperationCanceledException) { return Incomplete("Prom: перевищено час очікування. Завантаження неповне."); }
        catch (HttpRequestException) { return Incomplete("Prom: не вдалося отримати всі замовлення через помилку мережі."); }
        catch (JsonException) { return Incomplete("Prom повернув некоректну відповідь. Завантаження неповне."); }
        catch (InvalidDataException) { return Incomplete("Prom повернув непідтримувану структуру даних. Завантаження неповне."); }
        catch (ArgumentException) { return Incomplete("Prom: перевірте налаштування підключення."); }

        OrdersFetchResult Completed() => malformedDates
            ? Incomplete("Prom: частина замовлень має невідому дату; повноту діапазону не підтверджено.")
            : new OrdersFetchResult(orders.Values.ToArray(), true);
        OrdersFetchResult Incomplete(string message) => new(orders.Values.ToArray(), false, message);
    }

    public async Task<MarketplaceOrder?> GetOrderAsync(MarketplaceConnection connection, MarketplaceCredentials credentials,
        string orderId, CancellationToken cancellationToken = default)
    {
        ValidateConnection(connection, credentials);
        if (!long.TryParse(orderId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new ArgumentException("Prom: некоректний ідентифікатор замовлення.", nameof(orderId));
        var json = await SendAsync("orders/" + id.ToString(CultureInfo.InvariantCulture), credentials, cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (!TryProperty(document.RootElement, "order", out var value) || value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Prom: відповідь не містить замовлення.");
        if (ReadId(value) != id) throw new InvalidDataException("Prom: відповідь містить інше замовлення.");
        return ParseOrder(value, connection);
    }

    private Task<string> SendAsync(string path, MarketplaceCredentials credentials, CancellationToken cancellationToken) =>
        _transport.SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Token.Trim());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("X-LANGUAGE", "uk");
            return request;
        }, Marketplace, cancellationToken);

    private static void ValidateConnection(MarketplaceConnection connection, MarketplaceCredentials credentials)
    {
        if (connection.Marketplace != MarketplaceKind.Prom || string.IsNullOrWhiteSpace(connection.Id)
            || string.IsNullOrWhiteSpace(credentials.Token) || credentials.Token.Any(char.IsControl))
            throw new ArgumentException("Prom: вкажіть токен і підключення магазину.");
    }

    private static JsonElement ReadOrders(JsonElement root)
    {
        if (!TryProperty(root, "orders", out var orders) || orders.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Prom: відповідь не містить списку замовлень.");
        return orders;
    }

    private static long ReadId(JsonElement order)
    {
        if (TryProperty(order, "id", out var id) && id.ValueKind == JsonValueKind.Number
            && id.TryGetInt64(out var number) && number > 0) return number;
        throw new InvalidDataException("Prom: відсутній коректний ідентифікатор замовлення.");
    }

    private static MarketplaceOrder ParseOrder(JsonElement order, MarketplaceConnection connection)
    {
        var id = ReadId(order).ToString(CultureInfo.InvariantCulture);
        var payment = Property(order, "payment_data");
        var delivery = Property(order, "delivery_provider_data");
        var deliveryOption = Property(order, "delivery_option");
        var rawTotal = Text(order, "price");
        var rawCreatedAt = Text(order, "date_created");
        var items = new List<OrderItem>();
        if (TryProperty(order, "products", out var products) && products.ValueKind == JsonValueKind.Array)
            foreach (var product in products.EnumerateArray())
                if (product.ValueKind == JsonValueKind.Object)
                    items.Add(new OrderItem(Text(product, "name"), Text(product, "sku"),
                        DecimalValue(Property(product, "quantity")), DecimalValue(Property(product, "price")),
                        DecimalValue(Property(product, "total_price"))));

        var buyerName = string.Join(" ", new[] { Text(order, "client_last_name"), Text(order, "client_first_name"),
            Text(order, "client_second_name") }.Where(part => part.Length > 0));
        var phone = Text(order, "phone");
        var tracking = Text(delivery, "declaration_number");
        var rawStatus = Text(order, "status");
        var statusName = Text(order, "status_name");
        return new MarketplaceOrder
        {
            Key = new OrderKey(MarketplaceKind.Prom, connection.Id, id),
            StoreName = connection.Name,
            Number = id,
            CreatedAt = ParseDate(rawCreatedAt),
            RawCreatedAt = rawCreatedAt,
            Status = statusName.Length == 0 ? rawStatus : statusName,
            SourceStatus = rawStatus,
            PaymentStatus = Text(payment, "status"),
            Buyer = buyerName.Length == 0 && phone.Length == 0 ? null : new OrderPerson(buyerName, phone),
            // The GET contract has no distinct recipient or currency field; do not invent either.
            Recipient = null,
            Total = DecimalValue(Property(order, "price")),
            RawTotal = rawTotal,
            Currency = "",
            PaymentMethod = Text(Property(order, "payment_option"), "name"),
            DeliveryMethod = Text(deliveryOption, "name"),
            DeliveryCost = DecimalValue(Property(order, "delivery_cost")),
            Items = items,
            ItemsComplete = products.ValueKind == JsonValueKind.Array && products.GetArrayLength() > 0 &&
                items.Count == products.GetArrayLength() && products.EnumerateArray().All(p =>
                    !TryProperty(p, "discount_types", out var discounts) || discounts.ValueKind == JsonValueKind.Null ||
                        (discounts.ValueKind == JsonValueKind.Array && discounts.GetArrayLength() == 0)),
            Shipments = tracking.Length == 0 ? [] :
                [new OrderShipment(Text(delivery, "provider"), tracking, Text(order, "delivery_address"))],
            ReceiptIds = [],
            SellerUrl = null
        };
    }

    private static string FormatUtc(DateTimeOffset value) =>
        Uri.EscapeDataString(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture));

    private static DateTimeOffset? ParseDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    // Monetary strings are major-unit decimal text, not Checkbox's integer kopecks. A currency
    // suffix, comma, thousands separator or unknown syntax remains unknown rather than guessed.
    private static decimal? DecimalValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(),
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number)) return number;
        return null;
    }

    private static bool TryProperty(JsonElement value, string name, out JsonElement property)
    {
        property = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out property);
    }

    private static JsonElement Property(JsonElement value, string name) => TryProperty(value, name, out var property) ? property : default;

    private static string Text(JsonElement value, string name) => Property(value, name) is var property
        && property.ValueKind == JsonValueKind.String ? property.GetString()?.Trim() ?? "" : "";
}
