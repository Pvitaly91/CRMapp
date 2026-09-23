using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

/// <summary>Documented Seller API operations only; no order/PRRO mutation endpoints.</summary>
public sealed class RozetkaOrdersClient(MarketplaceHttpTransport transport) : IMarketplaceOrdersClient
{
    private const string BaseUrl = "https://api-seller.rozetka.com.ua";
    private const string ListExpand = "delivery,user,purchases,status_data,prro,carrier";
    private const string DetailExpand = "delivery,user,purchases,item_details,status_data";
    private const int PageLimit = 1000;
    private static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("FLE Standard Time");
    public MarketplaceKind Marketplace => MarketplaceKind.Rozetka;

    public async Task TestConnectionAsync(MarketplaceConnection connection, MarketplaceCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        Validate(connection, credentials);
        var session = await LoginAsync(credentials, cancellationToken);
        using var json = await ReadAsync("/orders/search?page=1&types=1", session, credentials, cancellationToken);
        if (Field(Field(json.RootElement, "content"), "orders").ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Rozetka не повернула список замовлень для перевірки доступу.");
    }

    public async Task<OrdersFetchResult> FetchAsync(MarketplaceConnection connection, MarketplaceCredentials credentials,
        MarketplaceRange range, CancellationToken cancellationToken = default)
    {
        Validate(connection, credentials);
        if (range.ToExclusive <= range.From) throw new ArgumentException("Некоректний діапазон дат замовлень.");
        var orders = new Dictionary<string, MarketplaceOrder>(StringComparer.Ordinal);
        var complete = true;
        var warning = "Rozetka: дати без часового поясу трактуються як Europe/Kyiv; API не документує пояс.";
        try
        {
            var session = await LoginAsync(credentials, cancellationToken);
            var from = TimeZoneInfo.ConvertTime(range.From, Kyiv).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var to = TimeZoneInfo.ConvertTime(range.ToExclusive.AddTicks(-1), Kyiv).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            for (var page = 1; page <= PageLimit; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = $"/orders/search?page={page}&types=1&sort=-id&created_from={from}&created_to={to}&expand={ListExpand}";
                using var json = await ReadAsync(path, session, credentials, cancellationToken);
                var content = Field(json.RootElement, "content");
                var rows = Field(content, "orders");
                if (rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Неочікуваний формат списку Rozetka.");
                var meta = Field(content, "_meta");
                var currentPage = Integer(meta, "currentPage");
                var pageCount = Integer(meta, "pageCount");
                var idsOnPage = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in rows.EnumerateArray())
                {
                    var order = ParseOrder(row, connection);
                    if (order.CreatedAt is null)
                    {
                        complete = false;
                        warning = "Частина замовлень Rozetka має невизначену дату; повноту діапазону не підтверджено.";
                    }
                    var previous = orders.GetValueOrDefault(order.Key.OrderId);
                    var isNew = previous is null;
                    orders[order.Key.OrderId] = MarketplaceFiscalEvidence.Preserve(order, previous);
                    if (isNew) idsOnPage.Add(order.Key.OrderId);
                    var data = row;
                    // DetailExpand does not document prro. Keep list PRRO data even if details omit/null it.
                    if (!row.TryGetProperty("purchases", out _) ||
                        (!row.TryGetProperty("user_title", out _) && !row.TryGetProperty("user", out _)))
                    {
                        try
                        {
                            using var detail = await ReadAsync($"/orders/{order.Key.OrderId}?expand={DetailExpand}",
                                session, credentials, cancellationToken);
                            data = Merge(row, Field(detail.RootElement, "content"));
                            orders[order.Key.OrderId] = ParseOrder(data, connection);
                        }
                        catch (Exception exception) when (IsRecoverable(exception, cancellationToken))
                        {
                            complete = false;
                            warning = "Частина деталей Rozetka недоступна; отримані замовлення збережені.";
                        }
                    }
                    var fiscal = await ReadFiscalUrlAsync(data, order.Key.OrderId, session, credentials, cancellationToken);
                    orders[order.Key.OrderId] = MarketplaceFiscalEvidence.Preserve(
                        ParseOrder(data, connection, fiscal.Url) with { FiscalDataStatus = fiscal.Status },
                        MarketplaceFiscalEvidence.Preserve(order, previous));
                }
                if (currentPage != page || pageCount is null or < 0)
                    throw new InvalidDataException("Rozetka не повернула достовірну пагінацію.");
                if (pageCount == 0 && rows.GetArrayLength() != 0)
                    throw new InvalidDataException("Суперечлива пагінація Rozetka.");
                if (page >= pageCount) return new(orders.Values.ToArray(), complete, warning);
                if (idsOnPage.Count == 0) throw new InvalidDataException("Rozetka повторила сторінку замовлень.");
            }
            return new(orders.Values.ToArray(), false, "Досягнуто межу 1000 сторінок Rozetka; синхронізація неповна.");
        }
        catch (Exception exception) when (IsRecoverable(exception, cancellationToken))
        {
            return new(orders.Values.ToArray(), false, "Не вдалося отримати всі замовлення Rozetka; показано отриману частину.");
        }
    }

    public async Task<MarketplaceOrder?> GetOrderAsync(MarketplaceConnection connection, MarketplaceCredentials credentials,
        string orderId, CancellationToken cancellationToken = default)
    {
        Validate(connection, credentials);
        if (string.IsNullOrWhiteSpace(orderId) || !orderId.All(char.IsAsciiDigit))
            throw new ArgumentException("ID замовлення Rozetka має містити лише цифри.");
        var session = await LoginAsync(credentials, cancellationToken);
        using var detail = await ReadAsync($"/orders/{orderId}?expand={DetailExpand}", session, credentials, cancellationToken);
        var data = Field(detail.RootElement, "content");
        if (Text(data, "id") != orderId) throw new InvalidDataException("Rozetka повернула інше замовлення.");
        var fiscal = await ReadFiscalUrlAsync(data, orderId, session, credentials, cancellationToken);
        return ParseOrder(data, connection, fiscal.Url) with { FiscalDataStatus = fiscal.Status };
    }

    private async Task<(string? Url, string Status)> ReadFiscalUrlAsync(JsonElement data, string orderId,
        Session session, MarketplaceCredentials credentials, CancellationToken cancellationToken)
    {
        if (!session.CanReadPrro) return (null, "Немає права prro_access для читання посилання. Це не означає, що чека немає.");
        try
        {
            var status = Integer(Field(data, "prro"), "prro_receipt_status");
            if (status is null)
            {
                // Missing expansion is unknown, not status=0. This GET cannot issue a new receipt.
                using var state = await ReadAsync($"/prro/receipt-status/{orderId}", session, credentials, cancellationToken);
                status = Integer(Field(state.RootElement, "content"), "status");
            }
            if (status == 0) return (null, "Rozetka повідомляє: чек ще не фіскалізований.");
            if (status != 1) return (null, "Статус PRRO невідомий; наявність чека не встановлена.");
            using var receipt = await ReadAsync($"/prro/receipt/{orderId}?type=link", session, credentials, cancellationToken);
            var candidate = Text(Field(receipt.RootElement, "content"), "url");
            // Keep a safe display-only reference even when its domain/format is not evidence.
            // Never follow it; only the strict Checkbox parser can turn it into an exact key.
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
                uri.UserInfo.Length == 0 && !candidate.Any(char.IsControl))
                return (candidate, CheckboxReceiptReference.TryParseUrl(candidate, out _) ? "" : "Формат посилання PRRO не підтверджений як Checkbox; автоприв’язку за ним не виконано.");
            return (null, "PRRO не повернув перевіреного посилання на чек.");
        }
        catch (Exception exception) when (IsRecoverable(exception, cancellationToken))
        { return (null, "Читання наявного PRRO-документа недоступне; це не означає, що чека немає."); }
    }

    private async Task<Session> LoginAsync(MarketplaceCredentials credentials, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            username = credentials.Login.Trim(),
            password = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.Password))
        });
        var text = await transport.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/sites")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }, Marketplace, cancellationToken);
        using var json = JsonDocument.Parse(text);
        EnsureSuccess(json.RootElement);
        var content = Field(json.RootElement, "content");
        var token = Text(content, "access_token");
        if (token.Length == 0 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
            throw new InvalidDataException("Rozetka не повернула коректний токен авторизації.");
        var permissions = Field(content, "permissions");
        return new Session(token, permissions.ValueKind == JsonValueKind.Array &&
            permissions.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.String && p.GetString() == "prro_access"));
    }

    private async Task<JsonDocument> ReadAsync(string path, Session session, MarketplaceCredentials credentials,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var text = await transport.SendAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
                    request.Headers.TryAddWithoutValidation("Content-Language", "uk");
                    return request;
                }, Marketplace, cancellationToken);
                var json = JsonDocument.Parse(text);
                try { EnsureSuccess(json.RootElement); return json; }
                catch { json.Dispose(); throw; }
            }
            catch (Exception exception) when (attempt == 0 && !session.Reauthenticated && IsUnauthorized(exception))
            {
                session.Reauthenticated = true;
                var fresh = await LoginAsync(credentials, cancellationToken);
                session.Token = fresh.Token;
                session.CanReadPrro = fresh.CanReadPrro;
            }
        }
    }

    private static MarketplaceOrder ParseOrder(JsonElement row, MarketplaceConnection connection, string? receiptUrl = null)
    {
        var id = Text(row, "id");
        if (id.Length == 0 || !id.All(char.IsAsciiDigit)) throw new InvalidDataException("Замовлення Rozetka без коректного ID.");
        var user = Field(row, "user");
        var title = Field(row, "user_title");
        var delivery = Field(row, "delivery");
        var payment = Field(row, "payment");
        var paymentStatus = Field(payment, "payment_status");
        if (paymentStatus.ValueKind != JsonValueKind.Object) paymentStatus = Field(row, "status_payment");
        var status = Field(row, "status_data");
        var items = new List<OrderItem>();
        var purchases = Field(row, "purchases");
        if (purchases.ValueKind == JsonValueKind.Array)
            foreach (var item in purchases.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object)
                    items.Add(new(Text(item, "item_name"), Text(Field(item, "item"), "article"),
                        Money(item, "quantity"), Money(item, "price_with_discount") ?? Money(item, "price"),
                        Money(item, "cost_with_discount") ?? Money(item, "cost")));
        var buyerName = First(Text(title, "full_name"), Text(user, "contact_fio"),
            string.Join(" ", new[] { Text(title, "last_name"), Text(title, "first_name"), Text(title, "second_name") }.Where(s => s.Length > 0)));
        var buyerPhone = Text(row, "user_phone");
        var recipientName = First(Text(delivery, "recipient_title"),
            string.Join(" ", new[] { Text(delivery, "recipient_last_name"), Text(delivery, "recipient_first_name"),
                Text(delivery, "recipient_second_name") }.Where(s => s.Length > 0)));
        var recipientPhone = Text(delivery, "recipient_phone");
        var carrier = Text(delivery, "delivery_service_name");
        var destination = string.Join(", ", new[] { Text(Field(delivery, "city"), "city_name"),
            Text(Field(delivery, "city"), "title"), Text(delivery, "place_street"), Text(delivery, "place_house"),
            Text(delivery, "place_number") }.Where(s => s.Length > 0).Distinct());
        var shipments = new List<OrderShipment>();
        var ttn = Text(row, "ttn");
        if (ttn.Length > 0 || carrier.Length > 0) shipments.Add(new(carrier, ttn, destination));
        var extraCarrier = Field(row, "carrier");
        var extraTtn = Text(extraCarrier, "carrier_track_num");
        if (extraTtn.Length > 0 && extraTtn != ttn) shipments.Add(new(Text(extraCarrier, "carrier_inner_id"), extraTtn, destination));
        var amount = Money(row, "amount");
        var discountedAmount = Money(row, "amount_with_discount");
        var rawTotal = First(Text(row, "cost_with_discount"), Text(row, "cost"), Text(row, "amount"));
        var fiscalNumber = Text(Field(row, "prro"), "prro_receipt_fiscal_code");
        var key = new OrderKey(MarketplaceKind.Rozetka, connection.Id, id);
        return new MarketplaceOrder
        {
            Key = key, StoreName = connection.Name, Number = id,
            CreatedAt = Date(row, "created"), UpdatedAt = Date(row, "changed"), RawCreatedAt = Text(row, "created"),
            Status = First(Text(status, "title"), Text(status, "name_uk"), Text(status, "name"), Text(row, "status")),
            SourceStatus = Text(row, "status"),
            PaymentStatus = First(Text(paymentStatus, "title"), Text(paymentStatus, "name"), Text(row, "payment_status")),
            PaymentMethod = First(Text(payment, "payment_method_name"), Text(row, "payment_type_name"), Text(row, "payment_type_title")),
            Buyer = buyerName.Length + buyerPhone.Length == 0 ? null : new(buyerName, buyerPhone),
            Recipient = recipientName.Length + recipientPhone.Length == 0 ? null : new(recipientName, recipientPhone),
            Total = ParseMoney(rawTotal), RawTotal = rawTotal, Currency = "", DeliveryMethod = carrier,
            Discount = amount.HasValue && discountedAmount.HasValue ? amount.Value - discountedAmount.Value : null,
            DeliveryCost = Money(delivery, "cost"), Items = items, Shipments = shipments,
            ItemsComplete = purchases.ValueKind == JsonValueKind.Array && purchases.GetArrayLength() > 0 && items.Count == purchases.GetArrayLength(),
            FiscalReceiptNumbers = fiscalNumber.Length == 0 ? [] : [fiscalNumber],
            FiscalReceiptUrls = receiptUrl is null ? [] : [receiptUrl], ReceiptIds = [],
            FiscalReferences = CheckboxReceiptReference.FromRozetka(key, fiscalNumber,
                Text(Field(row, "prro"), "prro_receipt_service_name"), receiptUrl)
        };
    }

    private static JsonElement Merge(JsonElement list, JsonElement detail)
    {
        if (detail.ValueKind != JsonValueKind.Object || Text(list, "id") != Text(detail, "id"))
            throw new InvalidDataException("Некоректні деталі замовлення Rozetka.");
        var values = list.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        foreach (var property in detail.EnumerateObject())
        {
            if (property.Name == "prro" && values.TryGetValue("prro", out var prior) && prior.ValueKind == JsonValueKind.Object)
            {
                var fiscal = prior.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
                if (property.Value.ValueKind == JsonValueKind.Object)
                    foreach (var part in property.Value.EnumerateObject())
                        if (part.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) && part.Value.ToString().Length > 0)
                            fiscal[part.Name] = part.Value.Clone();
                values["prro"] = JsonSerializer.SerializeToElement(fiscal);
            }
            else values[property.Name] = property.Value.Clone();
        }
        return JsonSerializer.SerializeToElement(values);
    }

    private static void Validate(MarketplaceConnection connection, MarketplaceCredentials credentials)
    {
        if (connection.Marketplace != MarketplaceKind.Rozetka) throw new ArgumentException("Очікується підключення Rozetka.");
        if (string.IsNullOrWhiteSpace(connection.Id) || string.IsNullOrWhiteSpace(credentials.Login) || credentials.Password.Length == 0)
            throw new InvalidOperationException("Вкажіть логін та пароль кабінету продавця Rozetka у локальних налаштуваннях.");
    }
    private static void EnsureSuccess(JsonElement root)
    {
        if (Field(root, "success").ValueKind == JsonValueKind.True) return;
        if (Integer(Field(root, "errors"), "code") is 1018 or 1020) throw new RozetkaTokenException();
        throw new InvalidDataException("Rozetka відхилила запит. Перевірте підключення та права доступу.");
    }
    private static bool IsUnauthorized(Exception exception) => exception is RozetkaTokenException ||
        exception is MarketplaceApiException { StatusCode: HttpStatusCode.Unauthorized } ||
        exception is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized };
    private static bool IsRecoverable(Exception exception, CancellationToken token) => !token.IsCancellationRequested &&
        exception is MarketplaceApiException or HttpRequestException or JsonException or InvalidDataException or RozetkaTokenException or TaskCanceledException;
    private static JsonElement Field(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) ? value : default;
    private static string Text(JsonElement element, string name)
    {
        var value = Field(element, name);
        return value.ValueKind switch { JsonValueKind.String => value.GetString() ?? "", JsonValueKind.Number => value.GetRawText(), _ => "" };
    }
    private static string First(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
    private static decimal? ParseMoney(string value) => decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
        CultureInfo.InvariantCulture, out var amount) ? amount : null;
    private static decimal? Money(JsonElement element, string name) => ParseMoney(Text(element, name));
    private static int? Integer(JsonElement element, string name) => int.TryParse(Text(element, name), NumberStyles.None,
        CultureInfo.InvariantCulture, out var number) ? number : null;
    private static DateTimeOffset? Date(JsonElement element, string name)
    {
        var value = Text(element, name);
        // Seller API documents no offset. Keep raw text and never use the PC's local time zone.
        if (DateTime.TryParseExact(value, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            if (Kyiv.IsInvalidTime(local) || Kyiv.IsAmbiguousTime(local)) return null;
            return new DateTimeOffset(local, Kyiv.GetUtcOffset(local));
        }
        if ((value.EndsWith('Z') || (value.Length >= 6 && value[^3] == ':' && value[^6] is '+' or '-')) &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset)) return offset;
        return null;
    }
    private sealed class Session(string token, bool canReadPrro)
    {
        public string Token = token;
        public bool CanReadPrro = canReadPrro;
        public bool Reauthenticated;
    }
    private sealed class RozetkaTokenException : Exception;
}
