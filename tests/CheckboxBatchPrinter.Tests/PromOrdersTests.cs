using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class PromOrdersTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> All { get; } =
    [
        ("Prom pages overlap safely, deduplicate and use UTC dates", PaginationAsync),
        ("Prom exclusive cursor and short pages are fully scanned", ExclusiveCursorAsync),
        ("Prom normalizes documented detail fields without guessing currency or receipts", DetailAsync),
        ("Prom preserves unknown amounts and tolerates null optional data", NullAndMoneyAsync),
        ("Prom preserves partial data on repeated pages, HTTP errors and page limits", IncompleteAsync),
        ("Prom cancellation interrupts paging and is not reported as an empty success", CancellationAsync),
        ("Prom rejects invalid response envelopes and order identifiers", InvalidContractAsync)
    ];

    private static MarketplaceConnection Connection => new()
    {
        Id = "synthetic-prom-store", Marketplace = MarketplaceKind.Prom, Name = "Тестовий магазин", Enabled = true
    };
    private static MarketplaceCredentials Credentials => new("synthetic-test-token");
    private static MarketplaceRange Range => new(
        new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.FromHours(3)),
        new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.FromHours(3)));

    private static async Task PaginationAsync()
    {
        var paths = new List<string>();
        using var http = Http((request, _) =>
        {
            Assert(request.Method == HttpMethod.Get, "Only GET may be sent to Prom.");
            Assert(request.RequestUri!.Host == "my.prom.ua", "Wrong API host.");
            Assert(request.Headers.Authorization?.Scheme == "Bearer"
                && request.Headers.Authorization.Parameter == Credentials.Token, "Missing bearer authorization.");
            Assert(request.Headers.GetValues("X-LANGUAGE").Single() == "uk", "Missing Ukrainian locale.");
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            Assert(query.Contains("date_from=2026-09-22T21:00:00", StringComparison.Ordinal), "From date is not UTC.");
            Assert(query.Contains("date_to=2026-09-23T21:00:00", StringComparison.Ordinal), "End date is not UTC.");
            Assert(query.Contains("sort_dir=desc", StringComparison.Ordinal), "Missing deterministic sort direction.");
            paths.Add(query);
            return Task.FromResult(Json(paths.Count switch
            {
                1 => Page(103, 102),
                2 => Page(102, 101),
                3 => Page(101),
                _ => throw new InvalidOperationException("Unexpected page request.")
            }));
        });
        var result = await Client(http, pageSize: 2).FetchAsync(Connection, Credentials, Range);
        Assert(result.Complete, result.Message);
        Assert(result.Orders.Select(o => o.Key.OrderId).SequenceEqual(new[] { "103", "102", "101" }), "Duplicate or skipped order.");
        Assert(paths[1].Contains("last_id=102", StringComparison.Ordinal) && paths[2].Contains("last_id=101", StringComparison.Ordinal), "Incorrect cursor.");
    }

    private static async Task ExclusiveCursorAsync()
    {
        var calls = 0;
        using var http = Http((_, _) => Task.FromResult(Json(++calls switch
        {
            1 => Page(103),
            2 => Page(102),
            3 => Page(101),
            4 => Page(),
            _ => throw new InvalidOperationException("Unexpected request.")
        })));
        var result = await Client(http).FetchAsync(Connection, Credentials, Range);
        Assert(result.Complete && result.Orders.Count == 3 && calls == 4, "Short page was wrongly considered the end.");
    }

    private static async Task DetailAsync()
    {
        const string detail = """
        {"order": {
          "id": 101, "date_created": "2026-09-23T12:34:56.588791+03:00",
          "client_first_name": "Тест", "client_second_name": "Тестович", "client_last_name": "Приклад",
          "phone": "+380000000000", "price": "201.25", "full_price": "250.25", "delivery_cost": 49,
          "status": "custom-123", "status_name": "Власний статус", "payment_option": {"id": 1, "name": "Пром-оплата"},
          "payment_data": {"type": "evopay", "status": "paid"},
          "delivery_option": {"id": 2, "name": "Нова пошта"}, "delivery_address": "Тестове відділення",
          "delivery_provider_data": {"provider": "nova_poshta", "declaration_number": "20400000000000"},
          "products": [{"id": 55, "name": "Тестовий товар", "sku": "SYNTH-01", "quantity": 1.25, "price": "161.00", "total_price": "201.25"}],
          "receipt_url": "https://check.checkbox.ua/test-unverified", "receipt_id": "497f6eca-6276-4993-bfeb-53cbbbba6f08"
        }}
        """;
        using var http = Http((request, _) =>
        {
            Assert(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v1/orders/101", "Unexpected detail operation.");
            return Task.FromResult(Json(detail));
        });
        var order = await Client(http).GetOrderAsync(Connection, Credentials, "101") ?? throw new Exception("Order missing.");
        Assert(order.Key == new OrderKey(MarketplaceKind.Prom, Connection.Id, "101"), "Lost account context.");
        Assert(order.Total == 201.25m && order.RawTotal == "201.25" && order.Currency == "", "Money was scaled or currency guessed.");
        Assert(order.CreatedAt?.Offset == TimeSpan.FromHours(3), "Date offset lost.");
        Assert(order.Buyer?.Name == "Приклад Тест Тестович" && order.Recipient is null, "Buyer/recipient invented.");
        Assert(order.Items.Single().Quantity == 1.25m && order.Items.Single().UnitPrice == 161.00m, "Item units were scaled.");
        Assert(order.DeliveryCost == 49m && order.TrackingDisplay == "20400000000000", "Shipment not mapped.");
        Assert(order.PaymentStatus == "paid" && order.PaymentMethod == "Пром-оплата", "Payment not mapped.");
        Assert(order.SourceStatus == "custom-123" && order.Status == "Власний статус", "Source status was lost.");
        Assert(order.ReceiptIds.Count == 0 && order.FiscalReceiptNumbers.Count == 0 && order.FiscalReceiptUrls.Count == 0,
            "Undocumented receipt reference was trusted.");
    }

    private static async Task NullAndMoneyAsync()
    {
        var calls = 0;
        using var http = Http((_, _) => Task.FromResult(Json(++calls == 1 ? """
        {"orders": [
          {"id": 104, "date_created": "2026-09-23T10:00:00Z", "price": "1 234,50 грн", "products": null, "payment_data": null, "delivery_provider_data": null},
          {"id": 103, "date_created": "2026-09-23T10:00:00Z", "price": null},
          {"id": 102, "date_created": "2026-09-23T10:00:00Z", "price": "1234.50", "products": [{"name": "Дробова кількість", "quantity": 0.125, "price": "24.80"}]},
          {"id": 101, "date_created": "2026-09-24T00:00:00Z", "price": "20.00"}
        ]}
        """ : Page())));
        var result = await Client(http).FetchAsync(Connection, Credentials, Range);
        Assert(result.Complete && result.Orders.Count == 3, "Nullable fields or local range filter failed.");
        var unknown = result.Orders.Single(o => o.Number == "104");
        Assert(unknown.Total is null && unknown.RawTotal == "1 234,50 грн" && unknown.Currency == "", "Unknown money was guessed.");
        Assert(unknown.Items.Count == 0 && unknown.Shipments.Count == 0 && unknown.Buyer is null && unknown.Recipient is null, "Null fields were not tolerated.");
        Assert(result.Orders.Single(o => o.Number == "103").Total is null, "Null total became zero.");
        var numeric = result.Orders.Single(o => o.Number == "102");
        Assert(numeric.Total == 1234.50m && numeric.Items.Single().Quantity == 0.125m, "Decimal precision was lost.");
    }

    private static async Task IncompleteAsync()
    {
        using (var http = Http((_, _) => Task.FromResult(Json(Page(103, 102)))))
        {
            var result = await Client(http).FetchAsync(Connection, Credentials, Range);
            Assert(!result.Complete && result.Orders.Count == 2 && result.Message.Length > 0, "Repeated page reported complete.");
        }
        var calls = 0;
        using (var http = Http((_, _) => Task.FromResult(++calls == 1 ? Json(Page(103, 102)) :
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("private-api-body") })))
        {
            var result = await Client(http).FetchAsync(Connection, Credentials, Range);
            Assert(!result.Complete && result.Orders.Count == 2, "HTTP error erased previously read orders.");
            Assert(!result.Message.Contains("private-api-body", StringComparison.Ordinal)
                && !result.Message.Contains(Credentials.Token, StringComparison.Ordinal), "Sensitive error body leaked.");
        }
        using (var http = Http((_, _) => Task.FromResult(Json(Page(103, 102)))))
        {
            var result = await Client(http, maxPages: 1).FetchAsync(Connection, Credentials, Range);
            Assert(!result.Complete && result.Orders.Count == 2 && result.Message.Contains("сторінок", StringComparison.Ordinal), "Page guard reported complete.");
        }
        calls = 0;
        using (var http = Http((_, _) => Task.FromResult(Json(++calls == 1
            ? """{"orders":[{"id":101,"date_created":"not-a-date","price":"10.00"}]}""" : Page()))))
        {
            var result = await Client(http).FetchAsync(Connection, Credentials, Range);
            Assert(!result.Complete && result.Orders.Single().CreatedAt is null && result.Orders.Single().RawCreatedAt == "not-a-date", "Unknown date lost or treated as complete range.");
        }
    }

    private static async Task CancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        using var http = Http((_, _) =>
        {
            calls++;
            cancellation.Cancel();
            return Task.FromResult(Json(Page(103, 102)));
        });
        try
        {
            await Client(http).FetchAsync(Connection, Credentials, Range, cancellation.Token);
            throw new Exception("Cancellation was swallowed.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Assert(calls == 1, "Cancellation did not stop requests.");
    }

    private static async Task InvalidContractAsync()
    {
        foreach (var invalid in new[] { "{", "{}", "{\"orders\":null}", "{\"orders\":[{\"id\":null}]}" })
        {
            using var http = Http((_, _) => Task.FromResult(Json(invalid)));
            var result = await Client(http).FetchAsync(Connection, Credentials, Range);
            Assert(!result.Complete && result.Message.Length > 0, "Invalid response treated as no orders.");
        }
        var calls = 0;
        using var client = Http((_, _) => { calls++; return Task.FromResult(Json(Page())); });
        try
        {
            await Client(client).GetOrderAsync(Connection, Credentials, "../set_status");
            throw new Exception("Unsafe order identifier accepted.");
        }
        catch (ArgumentException) { }
        Assert(calls == 0, "Invalid identifier reached HTTP.");
    }

    private static PromOrdersClient Client(HttpClient http, int pageSize = 100, int maxPages = 1000) =>
        new(new MarketplaceHttpTransport(http, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }), pageSize, maxPages);

    private static HttpClient Http(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new StubHandler(send));
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static string Page(params long[] ids) => JsonSerializer.Serialize(new
    {
        orders = ids.Select(id => new { id, date_created = "2026-09-23T10:00:00Z", price = "10.00" })
    });
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
