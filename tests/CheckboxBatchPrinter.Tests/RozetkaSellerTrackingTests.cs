using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class RozetkaSellerTrackingTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("Seller tracking shares one paginated scan across identical account aliases and binds exact orders", BulkAliasesAsync),
        ("Seller tracking never scans missing dates disabled accounts or unrelated shipment carriers", SelectionScopeAsync),
        ("Seller tracking rejects incomplete conflicting and malformed account responses without publishing partial statuses", InvalidResponsesAsync),
        ("Seller tracking uses only the explicit status title and never infers it from IDs or partner fields", EmptyStatusAsync),
        ("Seller tracking isolates unavailable accounts and does not expose API bodies or secrets", AccountIsolationAsync),
        ("Seller tracking rejects revoked API tokens once without cabinet login fallback", RevokedTokenAsync),
        ("Seller tracking cancels before or during pagination without publishing stale partial data", CancellationAsync),
        ("Seller tracking route permits only bounded read-only dates pagination and status expansion", ReadOnlyRouteAsync)
    ];

    private static readonly DateTimeOffset Now = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private static MarketplaceOrder Order(string connection, string id, string ttn, DateTimeOffset? created = null) => new()
    {
        Key = new(MarketplaceKind.Rozetka, connection, id), Number = id,
        CreatedAt = created ?? new DateTimeOffset(2026, 9, 22, 23, 30, 0, TimeSpan.Zero),
        Shipments = [new("rozetka_delivery", ttn) { Source = "Rozetka.ttn", ShipmentId = "source-" + ttn }]
    };
    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Page(int page, int total, int size, params object[] rows) => Json(new
    {
        success = true, content = new { models = rows,
            _meta = new { currentPage = page, totalCount = total, perPage = size, pageCount = total == 0 ? 0 : (total + size - 1) / size } }
    });
    private static object Row(int id, int order, string ttn, object? status, string extId = "unrelated") => new
    { id, order_id = order, ttn, delivery_status_title = status, delivery_status_id = "40040", ext_id = extId, status_updated_at = "2026-10-12 10:00:00" };

    private static async Task BulkAliasesAsync()
    {
        var store = new Stores();
        store.Add("alias", " synthetic-main ");
        var calls = new List<string>();
        var handler = new Handler((request, _) =>
        {
            calls.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            Equal(HttpMethod.Get, request.Method); Equal("Bearer", request.Headers.Authorization!.Scheme);
            Equal("synthetic-main", request.Headers.Authorization!.Parameter);
            var query = Query(request.RequestUri!);
            Equal("2026-09-23", query["date_from"]); Equal("2026-10-12", query["date_to"]);
            Equal("100", query["pageSize"]); Equal("delivery_status_title", query["expand"]);
            return Task.FromResult(query["page"] == "1" ? Page(1, 3, 2,
                Row(1, 100, "RMP-100", "Готовий до видачі"), Row(2, 900, "UNK-900", "Не використовувати", "RMP-101"))
                : Page(2, 3, 2, Row(3, 101, "RMP-101", "  Посилку видано  ")));
        });
        using var client = new HttpClient(handler);
        var selected = new[] { Order("primary", "100", "RMP-100", new(2026, 9, 23, 23, 30, 0, TimeSpan.Zero)),
            Order("alias", "100", "RMP-100"), Order("alias", "101", "RMP-101") };
        var result = await new RozetkaSellerTrackingService(new(client), store, store, new Clock()).FetchAsync(selected);
        Equal(3, result.Statuses.Count); Equal(0, result.Errors.Count);
        Equal(0, calls.Count(c => c == "POST /sites")); Equal(2, calls.Count(c => c == "GET /delivery-rozetka/ttn-list"));
        Check(result.Statuses.Select(s => s.Order).ToHashSet().SetEquals(selected.Select(o => o.Key)), "Account aliases lost their exact selected order identities.");
        Check(result.Statuses.All(s => s.Carrier == ShippingCarrier.RozetkaDelivery && s.Source == "Rozetka Seller" && s.CheckedAtUtc == Now), "Incorrect tracking proof/source.");
        Equal("Посилку видано", result.Statuses.Single(s => s.TrackingNumber == "RMP-101").Status);
        Check(result.Statuses.Where(s => s.TrackingNumber == "RMP-100").All(s => s.Status == "Готовий до видачі"), "A partner ext_id changed the primary TTN status.");
        Check(result.Statuses.All(s => s.ShipmentId == "source-" + s.TrackingNumber), "Shipment identity was inferred from the unrelated Seller row ID.");
        Equal(0, store.Writes);
    }

    private static async Task SelectionScopeAsync()
    {
        foreach (var mode in new[] { "disabled", "unknown-account", "missing-date", "wrong-carrier", "prom", "invalid-ttn", "future-date", "missing-credentials", "control-token" })
        {
            var store = new Stores();
            var order = Order("primary", "100", "RMP-100");
            if (mode == "disabled") store.Config.Connections[0].Enabled = false;
            if (mode == "unknown-account") order = order with { Key = order.Key with { ConnectionId = "unconfigured" } };
            if (mode == "missing-date") order = order with { CreatedAt = null };
            if (mode == "future-date") order = order with { CreatedAt = Now.AddDays(2) };
            if (mode == "wrong-carrier") order = order with { Shipments = [new("nova_poshta", "20400000000001")] };
            if (mode == "prom") order = order with { Key = order.Key with { Marketplace = MarketplaceKind.Prom } };
            if (mode == "invalid-ttn") order = order with { Shipments = [new("rozetka_delivery", "20400000000001")] };
            if (mode == "missing-credentials") store.Credentials["primary"] = new(Login: "seller-main", Password: "synthetic-password");
            if (mode == "control-token") store.Credentials["primary"] = new(Token: "synthetic-main\n");
            var calls = 0;
            using var client = new HttpClient(new Handler((_, _) => { calls++; throw new InvalidOperationException("No HTTP read is justified for " + mode); }));
            var result = await new RozetkaSellerTrackingService(new(client), store, store, new Clock()).FetchAsync([order]);
            Equal(0, calls); Equal(0, result.Statuses.Count); Equal(0, store.Writes);
        }
        var datedStore = new Stores();
        using var datedClient = new HttpClient(new Handler((request, _) => Task.FromResult(Page(1, 1, 100, Row(1, 100, "RMP-100", "Доставляється")))));
        var mixed = await new RozetkaSellerTrackingService(new(datedClient), datedStore, datedStore, new Clock()).FetchAsync([
            Order("primary", "100", "RMP-100"), Order("primary", "101", "RMP-101") with { CreatedAt = null }]);
        Equal(1, mixed.Statuses.Count); Check(mixed.Errors.Count > 0, "Undated order was silently included in the bounded account scan.");
    }

    private static async Task InvalidResponsesAsync()
    {
        foreach (var mode in new[] { "page-denied", "wrong-order", "duplicate-pair", "duplicate-id", "malformed-status", "missing-meta", "oversize", "changed-meta", "repeated-page" })
        {
            var store = new Stores();
            var reads = 0;
            using var client = new HttpClient(new Handler((request, _) =>
            {
                Equal(HttpMethod.Get, request.Method);
                reads++;
                HttpResponseMessage response = mode switch
                {
                    "wrong-order" => Page(1, 1, 100, Row(1, 999, "RMP-100", "Wrong order")),
                    "duplicate-pair" => Page(1, 2, 100, Row(1, 100, "RMP-100", "A"), Row(2, 100, "RMP-100", "A")),
                    "duplicate-id" => Page(1, 2, 100, Row(1, 100, "RMP-100", "A"), Row(1, 101, "RMP-101", "B")),
                    "malformed-status" => Page(1, 1, 100, Row(1, 100, "RMP-100", new { title = "Not a string" })),
                    "missing-meta" => Json(new { success = true, content = new { models = new[] { Row(1, 100, "RMP-100", "A") } } }),
                    "oversize" => Page(1, 1001, 100, Row(1, 100, "RMP-100", "A")),
                    _ when reads == 1 => Page(1, 2, 1, Row(1, 100, "RMP-100", "Ready first page")),
                    "page-denied" => new(HttpStatusCode.Forbidden),
                    "changed-meta" => Page(2, 3, 1, Row(2, 101, "RMP-101", "B")),
                    "repeated-page" => Page(2, 2, 1, Row(1, 100, "RMP-100", "Ready first page")),
                    _ => throw new InvalidOperationException("Unexpected malformed fixture " + mode)
                };
                return Task.FromResult(response);
            }));
            var result = await new RozetkaSellerTrackingService(new(client), store, store, new Clock())
                .FetchAsync([Order("primary", "100", "RMP-100"), Order("primary", "101", "RMP-101")]);
            Equal(0, result.Statuses.Count); Check(result.Errors.Count > 0, mode + " refreshed a partial or contradictory account scan.");
            Equal(0, store.Writes);
        }
    }

    private static async Task EmptyStatusAsync()
    {
        var store = new Stores();
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(
            Page(1, 3, 100, Row(1, 100, "RMP-100", ""), Row(2, 101, "RMP-101", null), Row(3, 102, "RMP-102", " ")))));
        var result = await new RozetkaSellerTrackingService(new(client), store, store, new Clock()).FetchAsync([
            Order("primary", "100", "RMP-100"), Order("primary", "101", "RMP-101"), Order("primary", "102", "RMP-102")]);
        Equal(0, result.Statuses.Count); Equal(0, result.Errors.Count);
    }

    private static async Task AccountIsolationAsync()
    {
        var store = new Stores(); store.Add("other", "other-token");
        var calls = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            calls++; Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(request.Headers.Authorization!.Parameter == "synthetic-main" ? Page(1, 1, 100, Row(1, 100, "RMP-100", "В дорозі")) :
                Json(new { success = false, content = new { login = "other-seller", secret = "other-password", token = "other-token", recipient = "Do not publish" } }));
        }));
        var result = await new RozetkaSellerTrackingService(new(client), store, store, new Clock()).FetchAsync([
            Order("primary", "100", "RMP-100"), Order("other", "101", "RMP-101")]);
        Equal(2, calls); Equal(1, result.Statuses.Count); Equal(1, result.Errors.Count);
        Equal("primary", result.Statuses.Single().Order!.ConnectionId);
        Check(!string.Join(" ", result.Errors).Contains("other-seller") && !string.Join(" ", result.Errors).Contains("other-password") &&
            !string.Join(" ", result.Errors).Contains("other-token") && !string.Join(" ", result.Errors).Contains("Do not publish"), "Raw API account or recipient data leaked into the error.");
    }

    private static async Task CancellationAsync()
    {
        foreach (var cancelBefore in new[] { true, false })
        {
            var store = new Stores(); using var cancellation = new CancellationTokenSource();
            var reads = 0;
            if (cancelBefore) cancellation.Cancel();
            using var client = new HttpClient(new Handler((request, token) =>
            {
                Equal(HttpMethod.Get, request.Method);
                reads++;
                if (reads == 2) { cancellation.Cancel(); return Task.FromCanceled<HttpResponseMessage>(token); }
                return Task.FromResult(Page(1, 2, 1, Row(1, 100, "RMP-100", "Partial first page")));
            }));
            try
            {
                await new RozetkaSellerTrackingService(new(client), store, store, new Clock()).FetchAsync([
                    Order("primary", "100", "RMP-100"), Order("primary", "101", "RMP-101")], cancellation.Token);
                throw new InvalidOperationException("Cancellation must propagate, not produce a fresh partial tracking result.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            Equal(cancelBefore ? 0 : 2, reads); Equal(0, store.Writes);
        }
    }

    private static async Task RevokedTokenAsync()
    {
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
        {
            var store = new Stores(); var calls = 0;
            using var client = new HttpClient(new Handler((request, _) =>
            {
                calls++;
                Equal(HttpMethod.Get, request.Method);
                Equal("synthetic-main", request.Headers.Authorization!.Parameter);
                return Task.FromResult(new HttpResponseMessage(status));
            }));
            var result = await new RozetkaSellerTrackingService(new(client), store, store, new Clock())
                .FetchAsync([Order("primary", "100", "RMP-100")]);
            Equal(1, calls); Equal(0, result.Statuses.Count); Equal(1, result.Errors.Count);
            Check(!result.Errors[0].Contains("synthetic-main"), "Revoked API token leaked into the user-facing error.");
        }
    }

    private static Task ReadOnlyRouteAsync()
    {
        const string valid = "https://api-seller.rozetka.com.ua/delivery-rozetka/ttn-list?page=1&pageSize=100&date_from=2026-09-23&date_to=2026-10-12&expand=delivery_status_title";
        using (var request = new HttpRequestMessage(HttpMethod.Get, valid))
        { Check(RozetkaSellerTrackingReadOnlyContract.ValidRoute(request), "Verified read-only tracking list rejected."); ShippingLabelHttp.Validate(request); }
        foreach (var invalid in new[]
        {
            valid.Replace("page=1", "page=11"), valid.Replace("pageSize=100", "pageSize=101"), valid + "&page=2",
            valid + "&order_id=100", valid + "&token=synthetic", valid.Replace("date_from=2026-09-23", "date_from=2026-10-13"),
            valid.Replace("date_to=2026-10-12", "date_to=2026-02-30"), valid.Replace("expand=delivery_status_title", "expand=order,receiver"),
            valid.Replace("api-seller.rozetka.com.ua", "evil.invalid"), valid.Replace("https://", "http://"), valid + "#fragment",
            valid.Replace("ttn-list", "ttn-create"), valid.Replace("2026-09-23", "2000-01-01")
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, invalid);
            Check(!RozetkaSellerTrackingReadOnlyContract.ValidRoute(request), "Expanded or unbounded tracking route was accepted.");
        }
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, valid);
            Check(!RozetkaSellerTrackingReadOnlyContract.ValidRoute(request), "A write method was treated as a tracking read.");
        }
        return Task.CompletedTask;
    }

    private static Dictionary<string, string> Query(Uri uri) => uri.Query[1..].Split('&').Select(p => p.Split('=', 2))
        .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]), StringComparer.Ordinal);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> read) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); ShippingLabelHttp.Validate(request); return read(request, cancellationToken); }
    }
    private sealed class Stores : IMarketplaceSettingsStore, IMarketplaceSecretStore
    {
        public MarketplaceSettings Config { get; } = new();
        public Dictionary<string, MarketplaceCredentials> Credentials { get; } = [];
        public int Writes;
        public Stores() => Add("primary", "synthetic-main");
        public void Add(string id, string token)
        { Config.Connections.Add(new() { Id = id, Marketplace = MarketplaceKind.Rozetka, Enabled = true }); Credentials[id] = new(Token: token, Login: "legacy-seller", Password: "legacy-password"); }
        public Task<MarketplaceSettings> LoadAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Config); }
        public Task<MarketplaceCredentials?> LoadAsync(string connectionId, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult<MarketplaceCredentials?>(Credentials.GetValueOrDefault(connectionId)); }
        public Task SaveAsync(MarketplaceSettings settings, CancellationToken cancellationToken = default) { Writes++; return Task.CompletedTask; }
        public Task SaveAsync(string connectionId, MarketplaceCredentials credentials, CancellationToken cancellationToken = default) { Writes++; return Task.CompletedTask; }
    }
}
