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
        ("Rozetka locker delivery name routes the primary TTN while unknown partner carrier IDs stay unresolved", LockerDeliveryNameAsync),
        ("Rozetka detail hydration and optional fiscal link are read-only", DetailAsync),
        ("Rozetka optional PRRO denial preserves the order and rejects unknown UUID inference", PrroUnavailableAsync),
        ("Rozetka null fields remain unknown and fiscal number is not a receipt UUID", NullFieldsAsync),
        ("Rozetka cancellation uses the order status group and rejects unrelated status fields", CanceledStatusGroupAsync),
        ("Rozetka numeric cancellation fallback supports old caches without affecting Prom", CanceledStatusLegacyCacheAsync),
        ("Rozetka detail status updates invalidate a stale list status group", CanceledStatusDetailAsync),
        ("Rozetka page failure preserves partial results", PartialAsync),
        ("Rozetka bounded reauthentication handles HTTP and JSON expiry", ReauthenticationAsync),
        ("Rozetka repeated pages and missing metadata cannot claim completion", InvalidPaginationAsync),
        ("Rozetka connection check rejects a success envelope without orders", ConnectionShapeAsync),
        ("Rozetka repeated refresh reuses only confirmed UUIDs while order fields stay fresh", FiscalCacheReuseAsync),
        ("Rozetka changed fiscal assertions, dates and credentials invalidate UUID reuse", FiscalCacheInvalidationAsync),
        ("Rozetka canceled orders remain present without stale fiscal inference", FiscalCacheCanceledAsync),
        ("Rozetka failed and code-only fiscal reads are retried on the next refresh", FiscalCacheNegativeAsync),
        ("Rozetka successful fiscal cache evicts older documents at its bounded limit", FiscalCacheBoundedAsync),
        ("Rozetka API rows outside the requested dates do not trigger hydration", OutOfRangeHydrationAsync),
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
                delivery = new { recipient_title = "Одержувач Б", recipient_phone = "test-recipient-phone", delivery_service_name = "Нова пошта", delivery_service_id = 99, cost = "13.45" },
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
        Equal(ShippingCarrier.NovaPoshta, ShippingCarrierNames.ForOrder(first).First().Carrier);
        Equal("99", first.Shipments.First().DeliveryServiceId);
        Equal("TTN-TEST-2", first.Shipments.Last().TrackingNumber);
        Equal(ShippingCarrier.Unknown, ShippingCarrierNames.ForOrder(first).Last().Carrier); // Unverified numeric carrier ID is never guessed.
        Equal("3", result.Orders.Single(o => o.Number == "101").SourceStatus);
    }

    private static async Task LockerDeliveryNameAsync()
    {
        var names = new[] { "Нова Пошта (поштомати)", " НОВА ПОШТА (ПОШТОМАТИ) ", "43660", "Unknown service" };
        var handler = new Handler(request =>
        {
            if (request.Method == HttpMethod.Post) return Task.FromResult(Login());
            Equal("/orders/search", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Page(1, 1, names.Select((name, index) => new
            {
                id = 100 + index, created = "2026-09-23 10:00:00", status = 1,
                cost_with_discount = "100.00", purchases = Array.Empty<object>(), user = new { contact_fio = "Synthetic buyer" },
                delivery = new { delivery_service_name = name, delivery_service_id = 43660 },
                ttn = "20400000000001", carrier = new { carrier_inner_id = 43660, carrier_track_num = "20400000000002" }
            }).Cast<object>().ToArray()));
        });
        using var http = new HttpClient(handler);
        var result = await new RozetkaOrdersClient(new MarketplaceHttpTransport(http)).FetchAsync(Connection, Credentials, Range);
        True(result.Complete); Equal(4, result.Orders.Count);
        for (var index = 0; index < names.Length; index++)
        {
            var order = result.Orders.Single(o => o.Number == (100 + index).ToString());
            Equal(names[index], order.Shipments.First().Carrier);
            Equal("Rozetka.ttn", order.Shipments.First().Source);
            Equal("43660", order.Shipments.First().DeliveryServiceId);
            Equal("Rozetka.carrier.carrier_track_num", order.Shipments.Last().Source);
            Equal("43660", order.Shipments.Last().Carrier);
            var restored = JsonSerializer.Deserialize<MarketplaceOrder>(JsonSerializer.Serialize(order))!;
            var shipments = ShippingCarrierNames.ForOrder(restored);
            Equal(index < 2 ? ShippingCarrier.NovaPoshta : ShippingCarrier.Unknown, shipments.First().Carrier);
            Equal(ShippingCarrier.Unknown, shipments.Last().Carrier);
            Equal("20400000000001", shipments.First().TrackingNumber);
            Equal("Rozetka.ttn", shipments.First().Source);
        }
    }

    private static async Task DetailAsync()
    {
        var paths = new List<string>();
        var formats = new List<string>();
        var handler = new Handler(request =>
        {
            Equal("api-seller.rozetka.com.ua", request.RequestUri!.Host); // Returned display URL is never followed.
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
            Equal("/prro/receipt/100", path); True(request.RequestUri.Query is "?type=link" or "?type=html");
            formats.Add(request.RequestUri.Query);
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
        True(detail.FiscalDataStatus.Length > 0);
        True(formats.SequenceEqual(["?type=link", "?type=html", "?type=link", "?type=html"]));
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

    private static async Task CanceledStatusGroupAsync()
    {
        static object StatusRow(int id, object? code, object? details) => new
        {
            id, created = "2026-09-23 10:00:00", status = code, status_data = details,
            user = new { contact_fio = "Покупець" }, purchases = Array.Empty<object>(),
            payment = new { payment_status = new { title = "Скасовано" } }
        };
        var rows = new[]
        {
            StatusRow(100, 999, new { id = 999, status_group = 3, title = "Нова причина відмови" }),
            StatusRow(101, 45, new { id = 45, status_group = 1, title = "Скасовано" }),
            StatusRow(102, 45, new { id = 45, status_group = 2, title = "Скасовано" }),
            StatusRow(103, null, new { id = 45, status_group = 3, title = "Причина відмови" }),
            StatusRow(104, 45, new { id = 45, status_group = new { value = 3 } }),
            StatusRow(105, 45, new { id = 45, title = "Причина відмови" }),
            StatusRow(106, 45, new { id = 45, status_group = 99 }),
            StatusRow(107, 999, new { id = 999, status_group = "не число" }),
            StatusRow(108, 1, new { id = 1, status = 0, title = "Обробляється" }),
            StatusRow(109, 1, new { id = 45, status_group = 3, title = "Застарілі дані" }),
            StatusRow(110, 45, new { id = 1, status_group = 1, title = "Застарілі дані" }),
            StatusRow(111, "45", new { id = 45, status_group = "3" })
        };
        var handler = new Handler(request =>
        {
            if (request.Method == HttpMethod.Post) return Task.FromResult(Login());
            Equal("/orders/search", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Page(1, 1, rows));
        });
        using var http = new HttpClient(handler);
        var result = await new RozetkaOrdersClient(new MarketplaceHttpTransport(http)).FetchAsync(Connection, Credentials, Range);
        True(result.Complete); Equal(rows.Length, result.Orders.Count);
        var expectedCanceled = new HashSet<string> { "100", "103", "104", "105", "106", "110", "111" };
        foreach (var order in result.Orders)
            Equal(expectedCanceled.Contains(order.Number), order.IsCanceled);
        Equal<int?>(3, result.Orders.Single(o => o.Number == "100").SourceStatusGroup);
        Equal("45", result.Orders.Single(o => o.Number == "103").SourceStatus);
        foreach (var id in new[] { "104", "105", "107", "108", "109", "110" })
            Equal<int?>(null, result.Orders.Single(o => o.Number == id).SourceStatusGroup);
        Equal<int?>(3, result.Orders.Single(o => o.Number == "111").SourceStatusGroup);
    }

    private static Task CanceledStatusLegacyCacheAsync()
    {
        // Old cache entries contain the provider code but no status_group or localized cancellation label.
        int[] canceledCodes = [10, 11, 12, 13, 16, 17, 18, 19, 20, 24, 25, 28, 29, 30, 31, 32,
            33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 49, 50, 51, 71, 72, 75, 77, 85, 88];
        foreach (var code in canceledCodes)
        {
            var oldJson = JsonSerializer.Serialize(new
            {
                Key = new OrderKey(MarketplaceKind.Rozetka, Connection.Id, "100"),
                SourceStatus = code.ToString(System.Globalization.CultureInfo.InvariantCulture), Status = "Причина відмови"
            });
            var restored = JsonSerializer.Deserialize<MarketplaceOrder>(oldJson)!;
            Equal<int?>(null, restored.SourceStatusGroup); True(restored.IsCanceled);
            True(JsonSerializer.Deserialize<MarketplaceOrder>(JsonSerializer.Serialize(restored))!.IsCanceled);
            True(!(restored with { Key = restored.Key with { Marketplace = MarketplaceKind.Prom } }).IsCanceled);
        }
        foreach (var code in new[] { "0", "1", "2", "3", "6", "7", "14", "15", "23", "48", "999", "not-a-code" })
        {
            var active = new MarketplaceOrder
            {
                Key = new(MarketplaceKind.Rozetka, Connection.Id, "100"), SourceStatus = code,
                Status = "Обробляється", PaymentStatus = "Скасовано"
            };
            True(!active.IsCanceled);
        }
        foreach (var group in new[] { 1, 2, 3 })
        {
            var grouped = new MarketplaceOrder
            {
                Key = new(MarketplaceKind.Rozetka, Connection.Id, "100"),
                SourceStatus = "45", SourceStatusGroup = group, Status = "Скасовано"
            };
            var restored = JsonSerializer.Deserialize<MarketplaceOrder>(JsonSerializer.Serialize(grouped))!;
            Equal<int?>(group, restored.SourceStatusGroup); Equal(group == 3, restored.IsCanceled);
        }
        return Task.CompletedTask;
    }

    private static async Task CanceledStatusDetailAsync()
    {
        // Exercise complete list expansions, expansions without IDs, and a detail
        // response that supplies its new code only through status_data.id.
        foreach (var shape in new[] { "id", "no-id", "detail-expansion" })
        {
            var reopened = false;
            var detailReads = 0;
            object listStatus = shape == "no-id"
                ? new { status_group = 1, title = "Обробляється" }
                : new { id = 1, status_group = 1, title = "Обробляється" };
            var handler = new Handler(request =>
            {
                if (request.Method == HttpMethod.Post) return Task.FromResult(Login());
                if (request.RequestUri!.AbsolutePath == "/orders/search") return Task.FromResult(Page(1, 1, new
                {
                    id = 100, created = "2026-09-23 10:00:00", status = 1, status_data = listStatus
                }));
                Equal("/orders/100", request.RequestUri!.AbsolutePath); detailReads++;
                if (reopened) return Task.FromResult(Json(new { success = true, content = new { id = 100, status = 1,
                    status_data = new { id = 1, status_group = 1 },
                    user = new { contact_fio = "Покупець" }, purchases = Array.Empty<object>() } }));
                if (shape == "detail-expansion") return Task.FromResult(Json(new { success = true, content = new
                {
                    id = 100, status_data = new { id = 45, status_group = 3 },
                    user = new { contact_fio = "Покупець" }, purchases = Array.Empty<object>() } }));
                // Detail changes only the code; the list's old group must not override it.
                return Task.FromResult(Json(new { success = true, content = new { id = 100, status = 45,
                    user = new { contact_fio = "Покупець" }, purchases = Array.Empty<object>() } }));
            });
            using var http = new HttpClient(handler);
            var client = new RozetkaOrdersClient(new MarketplaceHttpTransport(http));
            var result = await client.FetchAsync(Connection, Credentials, Range);
            True(result.Complete); Equal(1, detailReads);
            var canceled = result.Orders.Single();
            True(canceled.IsCanceled); Equal("45", canceled.SourceStatus);
            Equal<int?>(shape == "detail-expansion" ? 3 : null, canceled.SourceStatusGroup);
            reopened = true;
            var active = await client.GetOrderAsync(Connection, Credentials, "100");
            Equal(2, detailReads); True(!active!.IsCanceled); Equal<int?>(1, active.SourceStatusGroup);
        }
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

    private static object FiscalRow(int id = 100, int prroStatus = 1, string code = "FISCAL-TEST", string changed = "2026-09-23 11:00:00",
        string status = "1", string amount = "100.10", string tracking = "TTN-TEST", string provider = "Checkbox", string organization = "") => new
    {
        id, created = "2026-09-23 10:00:00", changed, status, cost_with_discount = amount, ttn = tracking,
        user = new { contact_fio = "Покупець" }, purchases = Array.Empty<object>(),
        prro = new { prro_receipt_status = prroStatus, prro_receipt_fiscal_code = code, prro_receipt_service_name = provider, organization_id = organization }
    };

    private const string FiscalUuidUrl = "https://api.checkbox.ua/api/v1/receipts/00000000-0000-0000-0000-000000000100/html";

    private static async Task FiscalCacheReuseAsync()
    {
        var pages = 0;
        var fiscalReads = 0;
        var handler = new Handler(request =>
        {
            if (request.Method == HttpMethod.Post) return Task.FromResult(Login());
            if (request.RequestUri!.AbsolutePath == "/orders/search")
            {
                pages++;
                return Task.FromResult(Page(1, 1, Enumerable.Range(100, 20).Select(id => FiscalRow(id,
                    amount: pages == 1 ? "100.10" : "125.20", tracking: pages == 1 ? "OLD-TTN" : "NEW-TTN")).ToArray()));
            }
            fiscalReads++;
            return Task.FromResult(request.RequestUri.Query == "?type=link"
                ? Json(new { success = false, errors = new { code = 15005 } })
                : Json(new { success = true, content = new { url = FiscalUuidUrl } }));
        });
        using var http = new HttpClient(handler);
        var client = new RozetkaOrdersClient(new MarketplaceHttpTransport(http));
        for (var refresh = 0; refresh < 4; refresh++)
        {
            var result = await client.FetchAsync(Connection, Credentials, Range);
            True(result.Complete); Equal(20, result.Orders.Count);
            True(result.Orders.All(o => o.FiscalReceiptUrls.Single() == FiscalUuidUrl));
            if (refresh > 0)
            {
                True(result.Orders.All(o => o.Total == 125.20m));
                True(result.Orders.All(o => o.Shipments.Single().TrackingNumber == "NEW-TTN"));
            }
        }
        Equal(4, pages); Equal(40, fiscalReads); // Formerly 160 PRRO requests for the same immutable documents.
    }

    private static async Task FiscalCacheInvalidationAsync()
    {
        object row = FiscalRow();
        var reads = 0;
        var permissions = true;
        var handler = new Handler(request =>
        {
            if (request.Method == HttpMethod.Post) return Task.FromResult(permissions ? Login() :
                Json(new { success = true, content = new { access_token = "test-session", permissions = Array.Empty<string>() } }));
            if (request.RequestUri!.AbsolutePath == "/orders/search") return Task.FromResult(Page(1, 1, row));
            reads++;
            return Task.FromResult(Json(new { success = true, content = new { url = FiscalUuidUrl } }));
        });
        using var http = new HttpClient(handler);
        var client = new RozetkaOrdersClient(new MarketplaceHttpTransport(http));
        async Task Refresh(int expected, MarketplaceCredentials? credentials = null, MarketplaceConnection? connection = null)
        {
            True((await client.FetchAsync(connection ?? Connection, credentials ?? Credentials, Range)).Complete);
            Equal(expected, reads);
        }
        await Refresh(1); await Refresh(1);
        row = FiscalRow(code: "NEW-FISCAL"); await Refresh(2); await Refresh(2);
        row = FiscalRow(code: "NEW-FISCAL", changed: "2026-09-23 12:00:00"); await Refresh(3);
        row = FiscalRow(code: "NEW-FISCAL", changed: "2026-09-23 12:00:00", provider: "Changed provider"); await Refresh(4);
        row = FiscalRow(code: "NEW-FISCAL", changed: "2026-09-23 12:00:00", provider: "Changed provider", organization: "changed-context"); await Refresh(5);
        await Refresh(6, Credentials with { Password = "different-demo-password" });
        await Refresh(7, Credentials with { Login = "different-demo-seller" });
        await Refresh(8, connection: new() { Id = "other-connection", Marketplace = MarketplaceKind.Rozetka });
        row = FiscalRow(prroStatus: 0); await Refresh(8);
        row = FiscalRow(code: "NEW-FISCAL", changed: "2026-09-23 12:00:00", provider: "Changed provider", organization: "changed-context"); await Refresh(9);
        permissions = false;
        var denied = await client.FetchAsync(Connection, Credentials, Range);
        Equal(0, denied.Orders.Single().FiscalReceiptUrls.Count); Equal(9, reads);
        permissions = true; await Refresh(10);
    }

    private static async Task FiscalCacheCanceledAsync()
    {
        var canceled = false;
        var denied = false;
        var reads = 0;
        var handler = new Handler(request =>
        {
            if (request.Method == HttpMethod.Post) return Task.FromResult(Login());
            if (request.RequestUri!.AbsolutePath == "/orders/search")
                return Task.FromResult(Page(1, 1, FiscalRow(status: canceled ? "canceled" : "1")));
            reads++;
            return Task.FromResult(denied ? new HttpResponseMessage(HttpStatusCode.Forbidden) :
                Json(new { success = true, content = new { url = FiscalUuidUrl } }));
        });
        using var http = new HttpClient(handler);
        var client = new RozetkaOrdersClient(new MarketplaceHttpTransport(http));
        var active = (await client.FetchAsync(Connection, Credentials, Range)).Orders.Single();
        Equal(FiscalUuidUrl, active.FiscalReceiptUrls.Single());
        canceled = true; denied = true;
        var unavailable = (await client.FetchAsync(Connection, Credentials, Range)).Orders.Single();
        True(unavailable.IsCanceled); Equal(0, unavailable.FiscalReceiptUrls.Count); Equal(2, reads);
        denied = false;
        var confirmed = (await client.FetchAsync(Connection, Credentials, Range)).Orders.Single();
        True(confirmed.IsCanceled); Equal(FiscalUuidUrl, confirmed.FiscalReceiptUrls.Single()); Equal(3, reads);
        var again = (await client.FetchAsync(Connection, Credentials, Range)).Orders.Single();
        True(again.IsCanceled); Equal(FiscalUuidUrl, again.FiscalReceiptUrls.Single()); Equal(3, reads);
    }

    private static async Task FiscalCacheNegativeAsync()
    {
        foreach (var url in new[] { "", "https://api.checkbox.ua/api/v1/receipts/ABC12345678/html", "https://kasa.example/display" })
        {
            var reads = 0;
            var handler = new Handler(request =>
            {
                if (request.Method == HttpMethod.Post) return Task.FromResult(Login());
                if (request.RequestUri!.AbsolutePath == "/orders/search") return Task.FromResult(Page(1, 1, FiscalRow()));
                reads++;
                return Task.FromResult(url.Length == 0 ? new HttpResponseMessage(HttpStatusCode.Forbidden) :
                    Json(new { success = true, content = new { url } }));
            });
            using var http = new HttpClient(handler);
            var client = new RozetkaOrdersClient(new MarketplaceHttpTransport(http));
            await client.FetchAsync(Connection, Credentials, Range);
            var first = reads; True(first > 0);
            await client.FetchAsync(Connection, Credentials, Range);
            Equal(first * 2, reads);
        }
    }

    private static async Task FiscalCacheBoundedAsync()
    {
        var phase = 0;
        var reads = 0;
        var handler = new Handler(request =>
        {
            if (request.Method == HttpMethod.Post) return Task.FromResult(Login());
            if (request.RequestUri!.AbsolutePath == "/orders/search")
                return Task.FromResult(Page(1, 1, phase == 0
                    ? Enumerable.Range(100, 1024).Select(id => FiscalRow(id)).ToArray()
                    : new[] { FiscalRow(phase switch { 2 => 1124, 4 => 101, _ => 100 }) }));
            reads++;
            return Task.FromResult(Json(new { success = true, content = new { url = FiscalUuidUrl } }));
        });
        using var http = new HttpClient(handler);
        var client = new RozetkaOrdersClient(new MarketplaceHttpTransport(http));
        foreach (var expected in new[] { 1024, 1024, 1025, 1025, 1026 })
        {
            True((await client.FetchAsync(Connection, Credentials, Range)).Complete);
            Equal(expected, reads); phase++;
        }
    }

    private static async Task OutOfRangeHydrationAsync()
    {
        foreach (var malformedPage in new[] { false, true })
        {
            var handler = new Handler(request =>
            {
                if (request.Method == HttpMethod.Post) return Task.FromResult(Login());
                Equal("/orders/search", request.RequestUri!.AbsolutePath);
                return Task.FromResult(Page(1, malformedPage ? 2 : 1,
                    new { id = 99, created = "2026-08-31 23:59:59" },
                    new { id = 100, created = "2026-09-24 00:00:00" }));
            });
            using var http = new HttpClient(handler);
            var result = await new RozetkaOrdersClient(new MarketplaceHttpTransport(http)).FetchAsync(Connection, Credentials, Range);
            Equal(!malformedPage, result.Complete); Equal(0, result.Orders.Count);
        }
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
            // Existing pagination scenarios have no issued fiscal document. New fiscal tests
            // exercise the actual status/link responses independently, not this convenience default.
            if (request.RequestUri!.AbsolutePath.StartsWith("/prro/receipt-status/", StringComparison.Ordinal))
                return Task.FromResult(Json(new { success = true, content = new { status = 0 } }));
            return respond(request);
        }
    }
}
