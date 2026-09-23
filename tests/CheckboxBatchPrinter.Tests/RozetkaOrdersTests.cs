using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class RozetkaOrdersTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("Rozetka documented auth, complete pagination, money and distinct buyer/recipient", PaginationAsync),
        ("Rozetka detail hydration and optional fiscal link are read-only", DetailAsync),
        ("Rozetka optional PRRO denial preserves the order and rejects unknown UUID inference", PrroUnavailableAsync),
        ("Rozetka null fields remain unknown and fiscal number is not a receipt UUID", NullFieldsAsync),
        ("Rozetka page failure preserves partial results", PartialAsync),
        ("Rozetka bounded reauthentication handles HTTP and JSON expiry", ReauthenticationAsync),
        ("Rozetka repeated pages and missing metadata cannot claim completion", InvalidPaginationAsync),
        ("Rozetka connection check rejects a success envelope without orders", ConnectionShapeAsync),
        ("Rozetka cancellation propagates", CancellationAsync)
    ];

    private static MarketplaceConnection Connection => new()
        { Id = "rz-store-one", Marketplace = MarketplaceKind.Rozetka, Name = "Тестовий магазин" };
    private static MarketplaceCredentials Credentials => new(Login: "test-seller", Password: "demo-пароль");
    private static MarketplaceRange Range => new(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(3)),
        new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.FromHours(3)));
    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Login(string token = "test-session") =>
        Json(new { success = true, content = new { access_token = token, permissions = new[] { "prro_access" } } });
    private static object Row(int id, string status = "1") => new
    {
        id, created = "2026-09-23 10:00:00", changed = "2026-09-23 11:00:00", status,
        cost_with_discount = "100.10", user = new { contact_fio = "Покупець" }, purchases = Array.Empty<object>()
    };
    private static HttpResponseMessage Page(int current, int count, params object[] orders) => Json(new
        { success = true, content = new { orders, _meta = new { currentPage = current, pageCount = count, perPage = 20 } } });

    private static async Task PaginationAsync()
    {
        var authRequests = 0;
        var pages = new List<int>();
        var handler = new Handler(async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Equal("/sites", request.RequestUri!.AbsolutePath);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Equal("test-seller", body.RootElement.GetProperty("username").GetString());
                Equal("demo-пароль", Encoding.UTF8.GetString(Convert.FromBase64String(body.RootElement.GetProperty("password").GetString()!)));
                authRequests++;
                return Login();
            }
            Equal("test-session", request.Headers.Authorization!.Parameter);
            var query = request.RequestUri!.Query;
            True(query.Contains("types=1") && query.Contains("created_from=2026-09-01") && query.Contains("created_to=2026-09-23"));
            Equal("/orders/search", request.RequestUri.AbsolutePath);
            var page = query.Contains("page=3") ? 3 : query.Contains("page=2") ? 2 : 1;
            pages.Add(page);
            if (page == 1) return Page(1, 3, new
            {
                id = 100, created = "2026-09-23 10:00:00", cost_with_discount = "123.45", amount = "120.00", amount_with_discount = "110.00",
                status = 2, status_data = new { name_uk = "Обробляється" },
                user_title = new { full_name = "Покупець А" }, user_phone = "test-buyer-phone",
                delivery = new { recipient_title = "Одержувач Б", recipient_phone = "test-recipient-phone", delivery_service_name = "Тест доставка", cost = "13.45" },
                ttn = "TTN-TEST-1", carrier = new { carrier_track_num = "TTN-TEST-2", carrier_inner_id = 4 },
                payment = new { payment_method_name = "Картка", payment_status = new { title = "Сплачено" } },
                purchases = new[] { new { item_name = "Товар", quantity = 2, price_with_discount = "55.00", cost_with_discount = "110.00", item = new { article = "SKU-test" } } }
            });
            return page == 2 ? Page(2, 3, Row(101)) : Page(3, 3, Row(101, "3"), Row(102));
        });
        using var http = new HttpClient(handler);
        var result = await new RozetkaOrdersClient(new MarketplaceHttpTransport(http)).FetchAsync(Connection, Credentials, Range);
        True(result.Complete); Equal(1, authRequests); Equal(3, pages.Count); Equal(3, result.Orders.Count);
        var first = result.Orders.Single(o => o.Number == "100");
        Equal("Покупець А", first.Buyer!.Name); Equal("Одержувач Б", first.Recipient!.Name);
        Equal(123.45m, first.Total); Equal(10m, first.Discount); Equal(13.45m, first.DeliveryCost);
        Equal("SKU-test", first.Items.Single().Sku); Equal(2m, first.Items.Single().Quantity); Equal(55m, first.Items.Single().UnitPrice);
        Equal("Картка", first.PaymentMethod); Equal("Сплачено", first.PaymentStatus);
        Equal(2, first.Shipments.Count); Equal(TimeSpan.FromHours(3), first.CreatedAt!.Value.Offset);
        Equal("3", result.Orders.Single(o => o.Number == "101").SourceStatus);
    }

    private static async Task DetailAsync()
    {
        var paths = new List<string>();
        var handler = new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            if (request.Method == HttpMethod.Post) return Task.FromResult(Login());
            True(request.Method == HttpMethod.Get);
            if (path == "/orders/search") return Task.FromResult(Page(1, 1, new { id = 100, created = "2026-09-23 10:00:00" }));
            if (path == "/orders/100") return Task.FromResult(Json(new
            {
                success = true, content = new
                {
                    id = 100, user = new { contact_fio = "Покупець" }, purchases = Array.Empty<object>(),
                    prro = new { prro_receipt_status = 1, prro_receipt_fiscal_code = "TEST-FISCAL" }
                }
            }));
            Equal("/prro/receipt/100", path); Equal("?type=link", request.RequestUri.Query);
            return Task.FromResult(Json(new { success = true, content = new { url = "https://kasa.example/c/TEST-FISCAL" } }));
        });
        using var http = new HttpClient(handler);
        var client = new RozetkaOrdersClient(new MarketplaceHttpTransport(http));
        var list = await client.FetchAsync(Connection, Credentials, Range);
        True(list.Complete); Equal("Покупець", list.Orders.Single().Buyer!.Name);
        // Missing details must not erase an existing list-only date.
        True(list.Orders.Single().CreatedAt.HasValue);
        var detail = await client.GetOrderAsync(Connection, Credentials, "100");
        Equal("TEST-FISCAL", detail!.FiscalReceiptNumbers.Single());
        Equal("https://kasa.example/c/TEST-FISCAL", detail.FiscalReceiptUrls.Single());
        Equal(0, detail.ReceiptIds.Count);
        True(paths.All(p => p is "/sites" or "/orders/search" or "/orders/100" or "/prro/receipt/100"));
    }

    private static async Task NullFieldsAsync()
    {
        var handler = new Handler(request => Task.FromResult(request.Method == HttpMethod.Post ? Login() : Json(new
        {
            success = true, content = new
            {
                id = 100, created = "2026-10-25 03:30:00", user = (object?)null, delivery = (object?)null,
                purchases = (object?)null, cost_with_discount = (string?)null,
                prro = new { prro_receipt_status = 0, prro_receipt_fiscal_code = "00000000-0000-0000-0000-000000000001" }
            }
        })));
        using var http = new HttpClient(handler);
        var order = await new RozetkaOrdersClient(new MarketplaceHttpTransport(http)).GetOrderAsync(Connection, Credentials, "100");
        True(order!.Buyer is null && order.Recipient is null && order.Total is null);
        True(order.CreatedAt is null); // Ambiguous Kyiv DST time is not guessed.
        Equal("2026-10-25 03:30:00", order.RawCreatedAt); Equal("", order.Currency);
        Equal(0, order.Items.Count); Equal(0, order.ReceiptIds.Count); Equal(1, order.FiscalReceiptNumbers.Count);
    }

    private static async Task PrroUnavailableAsync()
    {
        var reads = 0;
        var handler = new Handler(request =>
        {
            if (request.Method == HttpMethod.Post) return Task.FromResult(Login());
            reads++;
            if (request.RequestUri!.AbsolutePath == "/orders/100") return Task.FromResult(Json(new
            {
                success = true, content = new
                {
                    id = 100, created = "2026-09-23 11:00:00", user = new { contact_fio = "Покупець" },
                    prro = new { prro_receipt_status = 1, prro_receipt_fiscal_code = "FISCAL-TEST" },
                    comment = "00000000-0000-0000-0000-000000000001"
                }
            }));
            Equal("/prro/receipt/100", request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        });
        using var http = new HttpClient(handler);
        var result = await new RozetkaOrdersClient(new MarketplaceHttpTransport(http)).GetOrderAsync(Connection, Credentials, "100");
        Equal("100", result!.Number); Equal("Покупець", result.Buyer!.Name); Equal(2, reads);
        Equal(0, result.FiscalReceiptUrls.Count); Equal(0, result.ReceiptIds.Count);
    }

    private static async Task PartialAsync()
    {
        var handler = new Handler(request => Task.FromResult(request.Method == HttpMethod.Post ? Login() :
            request.RequestUri!.Query.Contains("page=1") ? Page(1, 2, Row(100)) :
            new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var http = new HttpClient(handler);
        var result = await new RozetkaOrdersClient(new MarketplaceHttpTransport(http)).FetchAsync(Connection, Credentials, Range);
        True(!result.Complete); Equal(1, result.Orders.Count); True(result.Message.Length > 0);
    }

    private static async Task ReauthenticationAsync()
    {
        foreach (var jsonExpiry in new[] { false, true })
        {
            var loginCalls = 0;
            var reads = 0;
            var handler = new Handler(request =>
            {
                if (request.Method == HttpMethod.Post) { loginCalls++; return Task.FromResult(Login("token-" + loginCalls)); }
                reads++;
                if (reads == 1) return Task.FromResult(jsonExpiry
                    ? Json(new { success = false, errors = new { code = 1018 } })
                    : new HttpResponseMessage(HttpStatusCode.Unauthorized));
                Equal("token-2", request.Headers.Authorization!.Parameter);
                return Task.FromResult(Page(1, 1, Row(100)));
            });
            using var http = new HttpClient(handler);
            var result = await new RozetkaOrdersClient(new MarketplaceHttpTransport(http)).FetchAsync(Connection, Credentials, Range);
            True(result.Complete); Equal(2, loginCalls); Equal(2, reads);
        }
        var logins = 0;
        var alwaysExpired = new Handler(request =>
        {
            if (request.Method == HttpMethod.Post) { logins++; return Task.FromResult(Login()); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });
        using var deniedHttp = new HttpClient(alwaysExpired);
        var denied = await new RozetkaOrdersClient(new MarketplaceHttpTransport(deniedHttp)).FetchAsync(Connection, Credentials, Range);
        True(!denied.Complete); Equal(2, logins);
    }

    private static async Task InvalidPaginationAsync()
    {
        foreach (var missingMeta in new[] { false, true })
        {
            var handler = new Handler(request => Task.FromResult(request.Method == HttpMethod.Post ? Login() :
                missingMeta ? Json(new { success = true, content = new { orders = new[] { Row(100) } } }) :
                Page(request.RequestUri!.Query.Contains("page=2") ? 2 : 1, 3, Row(100))));
            using var http = new HttpClient(handler);
            var result = await new RozetkaOrdersClient(new MarketplaceHttpTransport(http)).FetchAsync(Connection, Credentials, Range);
            True(!result.Complete);
        }
    }

    private static async Task CancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var handler = new Handler(_ => throw new InvalidOperationException("No requests after cancellation expected."));
        using var http = new HttpClient(handler);
        try
        {
            await new RozetkaOrdersClient(new MarketplaceHttpTransport(http)).FetchAsync(Connection, Credentials, Range, cancellation.Token);
            throw new InvalidOperationException("Cancellation was swallowed.");
        }
        catch (OperationCanceledException) { }
    }

    private static async Task ConnectionShapeAsync()
    {
        var handler = new Handler(request => Task.FromResult(request.Method == HttpMethod.Post ? Login() :
            Json(new { success = true, content = new { message = "unsupported" } })));
        using var http = new HttpClient(handler);
        try
        {
            await new RozetkaOrdersClient(new MarketplaceHttpTransport(http)).TestConnectionAsync(Connection, Credentials);
            throw new InvalidOperationException("Malformed list accepted as valid credentials/read access.");
        }
        catch (System.IO.InvalidDataException) { }
    }

    private static void True(bool value) { if (!value) throw new InvalidOperationException("Assertion failed."); }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return respond(request);
        }
    }
}
