using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;
using CheckboxBatchPrinter.Services;
using CheckboxBatchPrinter.ViewModels;
using static CheckboxBatchPrinter.Tests.ShippingLabelsTests;

namespace CheckboxBatchPrinter.Tests;

internal static class SellerRozetkaDeliveryLabelTests
{
    private const string Account = "synthetic-selected-seller", OtherAccount = "synthetic-other-seller";
    private const string Login = "selected-seller@example.invalid", Password = "synthetic-selected-password";
    private const string Token = "synthetic-selected-seller-token", DeliveryToken = "synthetic-delivery-token";
    private const string OrderId = "908483548", Tracking = "RMP-537811951", Partner = "723-6008725";

    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("Rozetka Delivery order label uses its exact Seller login without a Delivery key", SellerRoute),
        ("Seller Delivery partner TTN resolves only through its owned primary RMP document", PartnerRoute),
        ("Seller Delivery rejects missing disabled changed-kind and incomplete accounts before HTTP", Accounts),
        ("Seller Delivery verifies order identity and rejects missing ambiguous or contradictory documents", Identity),
        ("Seller Delivery checks every list page before printing and rejects duplicate or incomplete pagination", Pagination),
        ("Seller Delivery requires module_octopus and does not disclose authentication failures", Permissions),
        ("Seller Delivery rejects unauthorized redirected non-PDF and malformed API replies", Responses),
        ("Seller Delivery cancellation stops before HTTP and during authentication list or PDF", Cancellation),
        ("Seller Delivery read-only allowlist excludes writes unbounded queries and unsafe print bodies", ReadOnlyPolicy),
        ("Seller Delivery preserves Prom and independent Delivery-token routing", LegacyScope),
        ("STA Seller Delivery renders all supplied pages and retains Seller account history", () => MarketplaceViewModelTests.StaAsync(AllPages)),
        ("STA Seller Delivery known missing places and unsupported PDF layout block the entire batch", () => MarketplaceViewModelTests.StaAsync(InvalidBatch)),
        ("STA Seller Delivery primary and partner references print one canonical document", () => MarketplaceViewModelTests.StaAsync(AliasBatch))
    ];

    private sealed class Store : IShippingSettingsStore, IMarketplaceSettingsStore, IMarketplaceSecretStore
    {
        public ShippingLabelSettings Shipping = new() { Print = Settings, RozetkaDeliveryToken = "", NovaPoshtaSellerConnectionId = OtherAccount };
        public MarketplaceSettings Market = new()
        {
            Connections = [new() { Id = OtherAccount, Marketplace = MarketplaceKind.Rozetka, Enabled = true },
                new() { Id = Account, Marketplace = MarketplaceKind.Rozetka, Enabled = true }]
        };
        public readonly Dictionary<string, MarketplaceCredentials?> Credentials = new(StringComparer.Ordinal)
        {
            [Account] = new(Login: " " + Login + " ", Password: Password),
            [OtherAccount] = new(Login: "foreign@example.invalid", Password: "synthetic-foreign-password")
        };
        public readonly List<string> SecretReads = [];
        public Exception? SecretError;
        Task<ShippingLabelSettings> IShippingSettingsStore.LoadAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(Shipping); }
        Task<MarketplaceSettings> IMarketplaceSettingsStore.LoadAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(Market); }
        public Task<MarketplaceCredentials?> LoadAsync(string connectionId, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); SecretReads.Add(connectionId); if (SecretError is not null) throw SecretError; return Task.FromResult(Credentials.GetValueOrDefault(connectionId)); }
        public Task SaveAsync(ShippingLabelSettings value, CancellationToken ct) => throw new Exception("Label retrieval saved shipping settings.");
        public Task SaveAsync(MarketplaceSettings value, CancellationToken ct) => throw new Exception("Label retrieval changed seller accounts.");
        public Task SaveAsync(string id, MarketplaceCredentials value, CancellationToken ct) => throw new Exception("Label retrieval changed a seller secret.");
    }

    private sealed record Read(string Method, string Host, string Path, string Query, string Body, string? Token);
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }
    private sealed class Fixture : IDisposable
    {
        public readonly Store Store = new();
        public readonly List<Read> Reads = [];
        public readonly OfficialShippingLabelSource Source;
        private readonly HttpClient _client;
        public string[] Permissions = ["module_octopus"];
        public bool AuthSuccess = true;
        public Func<int, object>? ListPage;
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? Override;
        public byte[] PdfBytes = SyntheticShippingPdf.Create();
        public Fixture()
        {
            _client = new(new Handler(Respond));
            Source = new(new(_client, (_, ct) => Task.CompletedTask), Store, Store, Store);
        }
        public void Dispose() => _client.Dispose();
        public async Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Reads.Add(new(request.Method.Method, uri.Host, uri.AbsolutePath, uri.Query, body, request.Headers.Authorization?.Parameter));
            if (Override is not null && await Override(request, ct) is { } overridden) return overridden;
            Check(uri.Host == "api-seller.rozetka.com.ua", "Seller label contacted another service.");
            switch (uri.AbsolutePath)
            {
                case "/sites":
                    Check(request.Method == HttpMethod.Post && request.Headers.Authorization is null, "Seller authentication used another service token.");
                    using (var json = JsonDocument.Parse(body))
                    {
                        Check(json.RootElement.GetProperty("username").GetString() == Login &&
                            Encoding.UTF8.GetString(Convert.FromBase64String(json.RootElement.GetProperty("password").GetString()!)) == Password,
                            "Seller label used credentials from another configured account or changed the password.");
                    }
                    return Json(new { success = AuthSuccess, content = new { access_token = Token, permissions = Permissions } });
                case "/delivery-rozetka/ttn-list":
                    Check(request.Method == HttpMethod.Get && request.Headers.Authorization?.Parameter == Token, "List read did not use the selected Seller token.");
                    var query = Query(uri);
                    var page = int.TryParse(query.GetValueOrDefault("page"), out var parsedPage) ? parsedPage : 0;
                    Check(query.GetValueOrDefault("order_id") == OrderId && page >= 1,
                        "List lookup scanned another order or omitted an explicit bounded page.");
                    return Json(ListPage?.Invoke(page) ?? List([Model()], 1, 1, 1));
                case "/delivery-rozetka/ttn-print-batch":
                    Check(request.Method == HttpMethod.Post && request.Headers.Authorization?.Parameter == Token, "PDF did not use the selected Seller token.");
                    using (var json = JsonDocument.Parse(body))
                    {
                        Check(json.RootElement.EnumerateObject().Count() == 1 && json.RootElement.GetProperty("track_numbers").EnumerateArray()
                            .Select(item => item.GetString()).SequenceEqual([Tracking]), "PDF request added an unselected or partner TTN.");
                    }
                    return Pdf(PdfBytes);
                default: throw new Exception("Unexpected Seller mutation or guessed label route.");
            }
        }
    }
    private static object Model(string tracking = Tracking, string order = OrderId, string? partner = Partner, int id = 316, string? external = "external-id-316") =>
        new { id, ext_id = external, ttn = tracking, order_id = long.Parse(order), carrier_track_num = partner };
    private static object List(object[] rows, int page = 1, int pageCount = 1, int total = 1, int perPage = 100) =>
        new { success = true, content = new { models = rows, _meta = new { totalCount = total, pageCount, currentPage = page, perPage } } };
    private static object[] OtherModels(int count) => Enumerable.Range(0, count)
        .Select(index => Model("RMP-" + (600000000 + index), partner: null, id: 10000 + index)).ToArray();
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Pdf(byte[] bytes) => new(HttpStatusCode.OK)
    { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("application/pdf") } } };
    private static HttpResponseMessage Octet(byte[] bytes) => new(HttpStatusCode.OK)
    { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("application/octet-stream") } } };
    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2)).ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1]), StringComparer.Ordinal);
    private static ShipmentReference Shipment(string tracking = Tracking, string id = "", int places = 0) =>
        new(new(MarketplaceKind.Rozetka, Account, OrderId), OrderId, ShippingCarrier.RozetkaDelivery, tracking, id,
            Source: tracking == Partner ? "Rozetka.carrier.carrier_track_num" : "", ExpectedPlaces: places);
    private static MarketplaceOrder SellerOrder(params OrderShipment[] shipments) => new()
    { Key = new(MarketplaceKind.Rozetka, Account, OrderId), Number = OrderId, Shipments = shipments };
    private static OrderShipment OrderShipment(string tracking = Tracking, int places = 0) => new("rozetka_delivery", tracking) { Places = places };
    private static async Task<ShippingLabelException> Failure(Task operation, params LabelFailureKind[] allowed)
    {
        try { await operation; throw new Exception("Invalid Seller label was accepted."); }
        catch (ShippingLabelException ex)
        {
            Check(allowed.Contains(ex.Kind), "Seller failure was classified incorrectly.");
            Check(!ex.ToString().Contains(Password) && !ex.ToString().Contains(Token) && !ex.ToString().Contains(Login), "Seller failure exposed authentication data.");
            return ex;
        }
    }
    private static void NoPrint(Fixture fixture) => Check(fixture.Reads.All(read => read.Path != "/delivery-rozetka/ttn-print-batch"), "PDF was requested before ownership and list completeness were proved.");

    private static async Task SellerRoute()
    {
        using var fixture = new Fixture();
        var shipment = Shipment();
        var document = await fixture.Source.GetAsync(shipment);
        Check(document.Shipment == shipment && document.ConnectionId == Account && document.DocumentId == Tracking && document.Source == "RozetkaSeller.Delivery",
            "Seller delivery document lost its selected account or shipment identity.");
        Check(document.ExpectedPages == 0 && document.Pdf.SequenceEqual(fixture.PdfBytes) && document.Fingerprint == Convert.ToHexString(SHA256.HashData(document.Pdf)),
            "Seller delivery guessed a place count or changed the official PDF.");
        Check(fixture.Store.SecretReads.SequenceEqual([Account]) && fixture.Reads.Select(read => read.Path).SequenceEqual(
            ["/sites", "/delivery-rozetka/ttn-list", "/delivery-rozetka/ttn-print-batch"]), "Seller label consulted a fallback account or Delivery key.");
    }
    private static async Task PartnerRoute()
    {
        using var fixture = new Fixture();
        var shipment = Shipment(Partner);
        var document = await fixture.Source.GetAsync(shipment);
        Check(document.Shipment == shipment && document.DocumentId == Tracking, "Partner tracking was mistaken for the primary Seller TTN.");
        var query = Query(new Uri("https://api-seller.rozetka.com.ua" + fixture.Reads[1].Path + fixture.Reads[1].Query));
        Check(!query.ContainsKey("ttn"), "Partner number was sent as an undocumented primary-TTN filter.");
        using var external = new Fixture { ListPage = _ => List([Model(partner: null, external: Partner)]) };
        await Failure(external.Source.GetAsync(shipment), LabelFailureKind.MissingDocument, LabelFailureKind.InvalidDocument);
        NoPrint(external);
        using var unproven = new Fixture();
        await Failure(unproven.Source.GetAsync(shipment with { Source = "" }), LabelFailureKind.MissingDocument);
        NoPrint(unproven);
    }
    private static async Task Accounts()
    {
        foreach (var kind in new[] { "removed", "disabled", "prom", "duplicate-id", "missing-secret", "unreadable-secret", "corrupted-secret", "login", "password", "control-login", "control-password" })
        {
            using var fixture = new Fixture();
            var connection = fixture.Store.Market.Connections.Single(c => c.Id == Account);
            if (kind == "removed") fixture.Store.Market.Connections.Remove(connection);
            if (kind == "disabled") connection.Enabled = false;
            if (kind == "prom") connection.Marketplace = MarketplaceKind.Prom;
            if (kind == "duplicate-id") fixture.Store.Market.Connections.Add(new() { Id = Account, Marketplace = MarketplaceKind.Rozetka, Enabled = true });
            if (kind == "missing-secret") fixture.Store.Credentials[Account] = null;
            if (kind == "unreadable-secret") fixture.Store.SecretError = new System.IO.IOException("Synthetic protected store failure.");
            if (kind == "corrupted-secret") fixture.Store.SecretError = new CryptographicException("Synthetic DPAPI failure.");
            if (kind == "login") fixture.Store.Credentials[Account] = new(Login: " ", Password: Password);
            if (kind == "password") fixture.Store.Credentials[Account] = new(Login: Login, Password: "");
            if (kind == "control-login") fixture.Store.Credentials[Account] = new(Login: Login + "\n", Password: Password);
            if (kind == "control-password") fixture.Store.Credentials[Account] = new(Login: Login, Password: Password + "\n");
            await Failure(fixture.Source.GetAsync(Shipment()), LabelFailureKind.ConnectionRequired, LabelFailureKind.InvalidDocument);
            Check(fixture.Reads.Count == 0 && fixture.Store.SecretReads.All(id => id == Account), "Invalid Seller configuration contacted an API or another store.");
        }
    }
    private static async Task Identity()
    {
        foreach (var kind in new[] { "wrong-order", "wrong-ttn", "missing", "duplicate", "bad-primary", "bad-order-id", "bad-shipment-id" })
        {
            using var fixture = new Fixture();
            fixture.ListPage = _ => kind switch
            {
                "wrong-order" => List([Model(order: "908483549")]),
                "wrong-ttn" => List([Model(tracking: "RMP-537811952", partner: null)]),
                "missing" => List([], total: 0),
                "duplicate" => List([Model(), Model(id: 317)], total: 2),
                "bad-primary" => List([Model(tracking: "PRM-537811951")]),
                _ => List([Model()])
            };
            var shipment = kind == "bad-order-id" ? Shipment() with { Order = new(MarketplaceKind.Rozetka, Account, "908483548&all=1") }
                : kind == "bad-shipment-id" ? Shipment(id: "different-seller-document") : Shipment();
            await Failure(fixture.Source.GetAsync(shipment), LabelFailureKind.MissingDocument, LabelFailureKind.InvalidDocument);
            NoPrint(fixture);
        }
    }
    private static async Task Pagination()
    {
        using (var fixture = new Fixture())
        {
            fixture.ListPage = page => page == 1 ? List(OtherModels(100), 1, 2, 101) : List([Model()], 2, 2, 101);
            var document = await fixture.Source.GetAsync(Shipment(Partner));
            Check(document.DocumentId == Tracking && fixture.Reads.Count(read => read.Path == "/delivery-rozetka/ttn-list") == 2,
                "Seller label stopped before the page containing the document.");
        }
        foreach (var kind in new[] { "duplicate-on-next-page", "repeated-page", "changed-count", "missing-meta", "too-many-pages" })
        {
            using var fixture = new Fixture();
            fixture.ListPage = page => kind switch
            {
                "duplicate-on-next-page" => List(page == 1 ? [Model(), .. OtherModels(99)] : [Model(id: 317)], page, 2, 101),
                "repeated-page" => List([Model(), .. OtherModels(99)], 1, 2, 101),
                "changed-count" => List(page == 1 ? OtherModels(100) : [Model()], page, page == 1 ? 2 : 1, page == 1 ? 101 : 1),
                "missing-meta" => new { success = true, content = new { models = new[] { Model() } } },
                _ => List([Model()], 1, 1001, 1001)
            };
            await Failure(fixture.Source.GetAsync(Shipment(Partner)), LabelFailureKind.InvalidDocument);
            NoPrint(fixture);
        }
    }
    private static async Task Permissions()
    {
        foreach (var kind in new[] { "absent", "substring", "success-false", "no-token", "permissions-wrong-type" })
        {
            using var fixture = new Fixture();
            if (kind == "absent") fixture.Permissions = ["order_read"];
            if (kind == "substring") fixture.Permissions = ["module_octopus_edit"];
            if (kind == "success-false") fixture.AuthSuccess = false;
            if (kind == "no-token" || kind == "permissions-wrong-type") fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(
                request.RequestUri!.AbsolutePath != "/sites" ? null : kind == "no-token"
                    ? Json(new { success = true, content = new { access_token = "", permissions = new[] { "module_octopus" } } })
                    : Json(new { success = true, content = new { access_token = Token, permissions = "module_octopus" } }));
            await Failure(fixture.Source.GetAsync(Shipment()), LabelFailureKind.AccessDenied, LabelFailureKind.InvalidDocument);
            Check(fixture.Reads.Count == 1, "Seller label continued after authentication or missing permission.");
        }
    }
    private static async Task Responses()
    {
        using (var octet = new Fixture())
        {
            octet.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.EndsWith("ttn-print-batch") ? Octet(octet.PdfBytes) : null);
            var document = await octet.Source.GetAsync(Shipment());
            Check(document.Pdf.SequenceEqual(octet.PdfBytes) && document.DocumentId == Tracking, "Valid Seller PDF returned as octet-stream was rejected or substituted.");
        }
        foreach (var kind in new[] { "401-auth", "403-list", "redirect", "list-error", "bad-json", "html", "false-pdf", "broken-pdf", "octet-html", "octet-truncated" })
        {
            using var fixture = new Fixture();
            fixture.Override = (request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                HttpResponseMessage? result = kind switch
                {
                    "401-auth" when path == "/sites" => new(HttpStatusCode.Unauthorized),
                    "403-list" when path.EndsWith("ttn-list") => new(HttpStatusCode.Forbidden),
                    "redirect" when path.EndsWith("ttn-print-batch") => new(HttpStatusCode.Redirect) { Headers = { Location = new("https://evil.invalid/") } },
                    "list-error" when path.EndsWith("ttn-list") => Json(new { success = false, errors = new[] { Password, Token } }),
                    "bad-json" when path.EndsWith("ttn-list") => new(HttpStatusCode.OK) { Content = new StringContent("{broken", Encoding.UTF8, "application/json") },
                    "html" when path.EndsWith("ttn-print-batch") => new(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>", Encoding.UTF8, "text/html") },
                    "false-pdf" when path.EndsWith("ttn-print-batch") => Pdf("<html>login</html>"u8.ToArray()),
                    "broken-pdf" when path.EndsWith("ttn-print-batch") => Pdf([]),
                    "octet-html" when path.EndsWith("ttn-print-batch") => Octet("<html>login</html>"u8.ToArray()),
                    "octet-truncated" when path.EndsWith("ttn-print-batch") => Octet("%PDF"u8.ToArray()),
                    _ => null
                };
                return Task.FromResult(result);
            };
            await Failure(fixture.Source.GetAsync(Shipment()), LabelFailureKind.AccessDenied, LabelFailureKind.InvalidDocument);
            Check(fixture.Reads.Count <= 3, "Invalid Seller request retried or scanned other accounts.");
            if (kind is "401-auth" or "403-list" or "list-error" or "bad-json") NoPrint(fixture);
        }
        using (var novaPoshta = new Fixture())
        {
            const string tracking = "20400000000001";
            novaPoshta.Store.Shipping = novaPoshta.Store.Shipping with { NovaPoshtaSellerConnectionId = Account };
            novaPoshta.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath switch
            {
                "/ttns/ttn-list" => Json(new { success = true, content = new { ttn_list = new[] { new { Ref = "10000000-0000-0000-0000-000000000001", IntDocNumber = tracking, SeatsAmount = 1 } } } }),
                "/ttns/ttn-print/zebra" => Octet(novaPoshta.PdfBytes),
                _ => null
            });
            await Failure(novaPoshta.Source.GetAsync(Shipment(tracking) with { Carrier = ShippingCarrier.NovaPoshta }), LabelFailureKind.InvalidDocument);
        }
        using (var delivery = new Fixture())
        {
            delivery.Store.Shipping = delivery.Store.Shipping with { RozetkaDeliveryToken = DeliveryToken };
            delivery.Override = (_, _) => Task.FromResult<HttpResponseMessage?>(Octet(Encoding.UTF8.GetBytes("{\"statusCode\":0,\"data\":{\"id\":\"00123456\",\"places\":1}}")));
            await Failure(delivery.Source.GetAsync(new ShipmentReference(null, "", ShippingCarrier.RozetkaDelivery, "00123456")), LabelFailureKind.InvalidDocument);
        }
    }
    private static async Task Cancellation()
    {
        foreach (var stage in new[] { "before", "/sites", "/delivery-rozetka/ttn-list", "/delivery-rozetka/ttn-print-batch" })
        {
            using var fixture = new Fixture();
            using var cancellation = new CancellationTokenSource();
            if (stage == "before") cancellation.Cancel();
            fixture.Override = (request, ct) =>
            {
                if (request.RequestUri!.AbsolutePath == stage) { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
                return Task.FromResult<HttpResponseMessage?>(null);
            };
            try { await fixture.Source.GetAsync(Shipment(), cancellation.Token); throw new Exception("Canceled Seller label returned a document."); }
            catch (OperationCanceledException) { Check(cancellation.IsCancellationRequested, "Seller timeout was mistaken for caller cancellation."); }
            Check(fixture.Reads.Count == (stage == "before" ? 0 : stage == "/sites" ? 1 : stage.EndsWith("ttn-list") ? 2 : 3), "Cancellation continued into another stage.");
        }
    }
    private static Task ReadOnlyPolicy()
    {
        const string host = "https://api-seller.rozetka.com.ua";
        const string print = "/delivery-rozetka/ttn-print-batch";
        const string list = "/delivery-rozetka/ttn-list?order_id=908483548&page=1&pageSize=100";
        using (var request = new HttpRequestMessage(HttpMethod.Get, host + list)) ShippingLabelHttp.Validate(request);
        using (var request = new HttpRequestMessage(HttpMethod.Post, host + print) { Content = new StringContent("{\"track_numbers\":[\"RMP-537811951\"]}", Encoding.UTF8, "application/json") })
            ShippingLabelHttp.Validate(request);
        foreach (var route in new[] { host + "/delivery-rozetka/ttn-create", host + "/delivery-rozetka/ttn-update", host + print + "?format=zebra",
            "https://evil.invalid" + print, "http://api-seller.rozetka.com.ua" + print, host + ":444" + print, host + print + "#fragment" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = new StringContent("{\"track_numbers\":[\"RMP-537811951\"]}", Encoding.UTF8, "application/json") };
            RejectRoute(request);
        }
        foreach (var query in new[] { list + "&token=secret", list + "&order_id=1", list.Replace("page=1", "page=0"),
            list.Replace("pageSize=100", "pageSize=101"), list.Replace("order_id=908483548", "order_id=wrong"), list + "&ttn=PRM-1" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, host + query); RejectRoute(request);
        }
        foreach (var body in new[] { "{}", "{\"track_numbers\":null}", "{\"track_numbers\":\"RMP-537811951\"}", "{\"track_numbers\":[\"PRM-1\"]}",
            "{\"track_numbers\":[\"RMP-537811951\",\"RMP-537811951\"]}", "{\"track_numbers\":[\"RMP-537811951\"],\"format\":\"zebra\"}",
            "{\"track_numbers\":[\"RMP-537811951\"],\"track_numbers\":[\"RMP-2\"]}", "{\"track_numbers\":[\"RMP-" + new string('1', 20000) + "\"]}", "{broken" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, host + print) { Content = new StringContent(body, Encoding.UTF8, "application/json") }; RejectRoute(request);
        }
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Patch, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, host + print) { Content = new StringContent("{\"track_numbers\":[\"RMP-537811951\"]}", Encoding.UTF8, "application/json") }; RejectRoute(request);
        }
        return Task.CompletedTask;
    }
    private static void RejectRoute(HttpRequestMessage request)
    {
        try { ShippingLabelHttp.Validate(request); throw new Exception("Unsafe Seller route or request body was accepted."); }
        catch (InvalidOperationException) { }
    }
    private static async Task LegacyScope()
    {
        foreach (var withOrder in new[] { true, false })
        {
            using var fixture = new Fixture();
            fixture.Store.Shipping = fixture.Store.Shipping with { RozetkaDeliveryToken = DeliveryToken, RozetkaDeliveryConnectionId = "legacy-delivery-account" };
            fixture.Override = (request, _) =>
            {
                Check(request.RequestUri!.Host == "rz-delivery.rozetka.ua" && request.Headers.Authorization?.Parameter == DeliveryToken,
                    "Prom or independent Delivery label was sent to Seller.");
                return Task.FromResult<HttpResponseMessage?>(request.RequestUri.AbsolutePath.EndsWith("/label")
                    ? Json(new { statusCode = 0, data = new { label = Convert.ToBase64String(fixture.PdfBytes) } })
                    : Json(new { statusCode = 0, data = new { id = "00123456", places = 1 } }));
            };
            var shipment = new ShipmentReference(withOrder ? new(MarketplaceKind.Prom, "prom", "1") : null, withOrder ? "1" : "", ShippingCarrier.RozetkaDelivery, "00123456");
            var document = await fixture.Source.GetAsync(shipment);
            Check(document.ConnectionId == "legacy-delivery-account" && fixture.Store.SecretReads.Count == 0 && fixture.Reads.Count == 2,
                "Legacy Delivery account or routing was changed.");
        }
    }
    private static async Task AllPages()
    {
        using var fixture = new Fixture { PdfBytes = SyntheticShippingPdf.Create(pages: 3) };
        var order = SellerOrder(OrderShipment(places: 2));
        var batch = await new LabelBatchPreparation(fixture.Source, new WindowsShippingLabelRenderer()).PrepareAsync([order], Settings, Geometry);
        Check(batch.Pages.Select(page => page.PageNumber).SequenceEqual([1, 2, 3]) && batch.Pages.All(page => page.ConnectionId == Account && page.DocumentId == Tracking),
            "Seller label discarded an extra official PDF page or changed account history identity.");
        var history = new History();
        var attempt = await new LabelPrintCoordinator(history, new Backend()).SubmitAsync(batch);
        Check(attempt.State == LabelSubmissionState.Submitted && attempt.Pages.Count == 3 && attempt.Pages.All(page => page.ConnectionId == Account && page.DocumentId == Tracking),
            "Seller account or all-place history was lost before a synthetic transmission.");
    }
    private static async Task InvalidBatch()
    {
        foreach (var unsupported in new[] { false, true })
        {
            using var fixture = new Fixture { PdfBytes = unsupported ? SyntheticShippingPdf.Create(210, 297) : SyntheticShippingPdf.Create() };
            var order = SellerOrder(OrderShipment(places: unsupported ? 0 : 2));
            await Failure(new LabelBatchPreparation(fixture.Source, new WindowsShippingLabelRenderer()).PrepareAsync([order], Settings, Geometry),
                unsupported ? LabelFailureKind.UnsupportedFormat : LabelFailureKind.InvalidDocument);
        }
    }
    private static async Task AliasBatch()
    {
        foreach (var carrier in new[] { "unknown", "nova_poshta" })
        {
            using var fixture = new Fixture { PdfBytes = SyntheticShippingPdf.Create(pages: 2) };
            var order = SellerOrder(OrderShipment(), new(carrier, Partner) { Source = "Rozetka.carrier.carrier_track_num" });
            var batch = await new LabelBatchPreparation(fixture.Source, new WindowsShippingLabelRenderer()).PrepareAsync([order], Settings, Geometry);
            Check(batch.Pages.Count == 2 && batch.Pages.Select(page => page.PageNumber).SequenceEqual([1, 2]) && batch.Pages.All(page => page.DocumentId == Tracking),
                "Primary and partner TTNs duplicated the same Seller label pages.");
            Check(fixture.Reads.Count(read => read.Path.EndsWith("ttn-print-batch")) == 1, "Primary and partner aliases downloaded the same PDF twice in one frozen batch.");
            var history = new History();
            var attempt = await new LabelPrintCoordinator(history, new Backend()).SubmitAsync(batch);
            Check(attempt.Pages.Count == 4 && attempt.Pages.All(page => page.DocumentId == Tracking && page.ConnectionId == Account && page.Order == order.Key) &&
                attempt.Pages.Count(page => page.TrackingNumber == Tracking) == 2 && attempt.Pages.Count(page => page.TrackingNumber == Partner) == 2,
                "Canonical Seller document did not preserve every verified alias in history.");
            var row = new MarketplaceOrderRowViewModel(order);
            row.ApplyLabelHistory(history.Attempts);
            Check(row.HasPrintedLabel && row.HasLabelSubmission, "Verified partner alias left the successfully printed order eligible for another label batch.");
            row.ApplyLabelHistory([attempt with { State = LabelSubmissionState.SubmissionUnknown }]);
            Check(row.HasLabelSubmission && !row.HasPrintedLabel, "Unknown transmission was marked fully printed or lost duplicate protection.");
            void RejectForeignHistory(IReadOnlyList<LabelPrintAttempt> records)
            {
                foreach (var key in new[] { new OrderKey(MarketplaceKind.Rozetka, OtherAccount, OrderId),
                    new OrderKey(MarketplaceKind.Rozetka, Account, "908483549"), new OrderKey(MarketplaceKind.Prom, Account, OrderId) })
                {
                    var foreign = new MarketplaceOrderRowViewModel(order with { Key = key });
                    foreign.ApplyLabelHistory(records);
                    Check(!foreign.HasPrintedLabel && !foreign.HasLabelSubmission, "Another seller account or order inherited print history from matching primary and partner TTNs.");
                }
            }
            RejectForeignHistory([attempt]);
            RejectForeignHistory([attempt with { State = LabelSubmissionState.SubmissionUnknown }]);
            var legacy = new MarketplaceOrderRowViewModel(order);
            legacy.ApplyLabelHistory([attempt with { Pages = attempt.Pages.Select(page => page with { Order = null }).ToArray() }]);
            Check(legacy.HasPrintedLabel && legacy.HasLabelSubmission, "Existing carrier-only history stopped restoring after Seller order identity was added.");
            const string foreignAccount = "synthetic-foreign-seller";
            fixture.Store.Credentials[OtherAccount] = fixture.Store.Credentials[Account];
            fixture.Store.Market.Connections.Add(new() { Id = foreignAccount, Marketplace = MarketplaceKind.Rozetka, Enabled = true });
            fixture.Store.Credentials[foreignAccount] = new(Login: "different-account@example.invalid", Password: "synthetic-different-password");
            async Task CheckCanonicalHistory(IReadOnlyList<LabelPrintAttempt> records)
            {
                var deduplication = await MarketplaceOrderDeduplication.ResolveAsync(fixture.Store.Market, fixture.Store);
                var canonical = deduplication.NormalizeKey(order.Key);
                Check(canonical.ConnectionId == OtherAccount, "Fixture did not change the canonical identity after an equivalent connection was added.");
                var duplicate = new MarketplaceOrderRowViewModel(order with { Key = canonical });
                duplicate.ApplyLabelHistory(records, deduplication.NormalizeKey);
                Check(duplicate.HasPrintedLabel && duplicate.HasLabelSubmission, "An equivalent Seller connection lost existing print history when its canonical identity changed.");
                foreach (var key in new[] { new OrderKey(MarketplaceKind.Rozetka, foreignAccount, OrderId),
                    new OrderKey(MarketplaceKind.Rozetka, OtherAccount, "908483549"), new OrderKey(MarketplaceKind.Prom, OtherAccount, OrderId) })
                {
                    var foreign = new MarketplaceOrderRowViewModel(order with { Key = key });
                    foreign.ApplyLabelHistory(records, deduplication.NormalizeKey);
                    Check(!foreign.HasPrintedLabel && !foreign.HasLabelSubmission, "Connection normalization allowed a different seller, order or marketplace to inherit history.");
                }
                Check(records.SelectMany(record => record.Pages).All(page => page.Order == order.Key), "History normalization rewrote original stored Seller identities.");
            }
            await CheckCanonicalHistory([attempt]);
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cbp-seller-delivery-history-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "labels.dpapi");
            try
            {
                await new DpapiLabelHistoryStore(path).SaveAsync(attempt);
                var restarted = new MarketplaceOrderRowViewModel(order);
                var restored = await new DpapiLabelHistoryStore(path).LoadAsync();
                Check(restored.Single().Pages.All(page => page.Order == order.Key), "Protected print history lost the Seller order/account identity.");
                restarted.ApplyLabelHistory(restored);
                Check(restarted.HasPrintedLabel && restarted.HasLabelSubmission, "Restart lost the printed Seller partner aliases.");
                RejectForeignHistory(restored);
                await CheckCanonicalHistory(restored);
            }
            finally { System.IO.File.Delete(path); System.IO.Directory.Delete(directory); }
        }
    }
    private sealed class Backend : ILabelBatchBackend
    {
        public async Task<int?> SubmitAsync(LabelPrintBatch batch, string name, Func<Task> beforeTransfer, Func<int, Task> jobCreated, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); await beforeTransfer(); await jobCreated(901); return 901; }
    }
}
