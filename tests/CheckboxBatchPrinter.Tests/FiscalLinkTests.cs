using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class FiscalLinkTests
{
    private const string ReceiptId = "f1000000-0000-0000-0000-000000000001";
    private const string OtherId = "f1000000-0000-0000-0000-000000000002";
    private const string Organization = "f2000000-0000-0000-0000-000000000001";
    private const string Code = "00Ab_Cd-123"; // Case, symbols and leading zeroes are significant.
    private const string Url = "https://api.checkbox.ua/api/v1/receipts/" + ReceiptId + "/html?simple=true&show_buttons=false";
    private static readonly MarketplaceConnection Connection = new()
        { Id = "f3000000000000000000000000000001", Marketplace = MarketplaceKind.Rozetka, Enabled = true };
    private static readonly MarketplaceCredentials Credentials = new(Login: "synthetic", Password: "synthetic");
    private static readonly MarketplaceRange Range = MarketplaceSyncService.BuildRange(new(2026,9,24),new(2026,9,24),0);
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("fiscal API-to-matcher: Rozetka list PRRO and new-order URL link automatically", ListToMatcherAsync),
        ("fiscal API-to-matcher: absent detail PRRO uses read-only status, not no-receipt guess", DetailStatusAsync),
        ("fiscal API-to-matcher: null detail PRRO preserves list key case and zeroes", NullExpansionAsync),
        ("fiscal sync: repeated updates and inaccessible optional PRRO retain typed evidence", RefreshPreservesAsync),
        ("fiscal API-to-matcher: contradictory UUID and fiscal code are conflicts", ConflictingKeysAsync),
        ("fiscal URL parser: exact host route and formatting query only, no external navigation", UrlValidationAsync),
        ("fiscal API-to-matcher: foreign URL and bare fiscal code cannot prove a link", UntrustedAndBareAsync),
        ("fiscal API-to-verifier-to-matcher: code plus organization and unique Checkbox lookup", FiscalCodeLookupAsync),
        ("fiscal verifier: empty, denied, collision and changed account never manufacture proof", LookupFailureAsync),
        ("fiscal verifier: failures count toward request bound and duplicate keys do not retry", LookupBoundAsync),
        ("fiscal verifier: cashier change during HTTP read discards new and previous proof", AccountChangesDuringLookupAsync),
        ("fiscal matching: same serial or code on another cash register is not a link", ContextIsolationAsync),
        ("fiscal matching: independent sale/return documents and manual decisions survive", MultipleAndManualAsync),
        ("fiscal cache: raw evidence persists but account verification does not", SerializationAsync),
        ("fiscal Prom adapter: schema-only response or arbitrary comment UUID stays unlinked", PromReadShapeAsync)
    ];

    private static object Row(string code = Code, bool complete = false) => new
    {
        id = 100, created = "2026-09-24 10:00:00", cost_with_discount = "100.00",
        prro = new { prro_receipt_status = 1, prro_receipt_service_name = "checkbox", prro_receipt_fiscal_code = code },
        user = complete ? new { contact_fio = "Synthetic" } : null
    };
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Login() => Json(new { success = true, content = new { access_token = "fake-session", permissions = new[] { "prro_access" } } });
    private static HttpResponseMessage Page(object row) => Json(new { success = true, content = new { orders = new[] { row }, _meta = new { currentPage = 1, pageCount = 1 } } });
    private static HttpResponseMessage Detail(object? prro = null) => Json(new { success = true, content = new { id = 100, purchases = Array.Empty<object>(), prro, user = new { contact_fio = "Synthetic" }, ttn = "SYNTHETIC-TTN" } });
    private static ReceiptRecord Receipt(string id = ReceiptId, string code = Code, string cash = "000123", string type = "SELL", string organization = Organization) => new()
    { Id = id, FiscalCode = code, CashRegisterFiscalNumber = cash, OrganizationId = organization, Type = type,
        Serial = 42, TotalSumMinor = 10000, FiscalDate = new DateTimeOffset(2026,9,24,12,0,0,TimeSpan.FromHours(3)) };
    private static ReceiptOrderMatch Match(MarketplaceOrder order, ReceiptRecord? receipt = null,
        IReadOnlyList<ReceiptOrderDecision>? decisions = null, IReadOnlyList<ReceiptRecord>? scope = null, string account = "account")
    {
        receipt ??= Receipt();
        return new ReceiptOrderMatchingService().Match(receipt, account, [order], decisions ?? [], true, receiptScope: scope ?? [receipt]);
    }

    internal static async Task<MarketplaceOrder> Fetch(string url = Url, string code = Code, object? detailPrro = null)
    {
        var paths = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            Equal("api-seller.rozetka.com.ua", request.RequestUri!.Host);
            var path = request.RequestUri.AbsolutePath; paths.Add(path);
            if (path == "/sites") { Equal(HttpMethod.Post, request.Method); return Login(); }
            Equal(HttpMethod.Get, request.Method); Equal("fake-session", request.Headers.Authorization?.Parameter);
            return path switch
            {
                "/orders/search" => Page(Row(code)),
                "/orders/100" => Detail(detailPrro),
                "/prro/receipt/100" => Json(new { success = true, content = new { url } }),
                _ => throw new InvalidOperationException("Unexpected synthetic route")
            };
        }));
        var result = await new RozetkaOrdersClient(new MarketplaceHttpTransport(http)).FetchAsync(Connection, Credentials, Range);
        True(result.Complete); True(paths.Contains("/prro/receipt/100"));
        return result.Orders.Single();
    }
    private static async Task ListToMatcherAsync()
    {
        var order = await Fetch();
        Equal(0, order.ReceiptIds.Count); Equal(2, order.FiscalReferences.Count);
        True(order.FiscalReferences.All(r => r.Order == order.Key && r.Source.Length > 0));
        var match = Match(order); Equal(ReceiptLinkState.Exact, match.State); Equal("За посиланням на чек", match.Explanation);
    }
    private static async Task DetailStatusAsync()
    {
        var paths = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath; paths.Add(path);
            if (path == "/sites") return Login();
            Equal(HttpMethod.Get, request.Method);
            return path switch
            {
                "/orders/100" => Detail(),
                "/prro/receipt-status/100" => Json(new { success = true, content = new { status = 1 } }),
                "/prro/receipt/100" => Json(new { success = true, content = new { url = Url } }),
                _ => throw new InvalidOperationException()
            };
        }));
        var order = await new RozetkaOrdersClient(new MarketplaceHttpTransport(http)).GetOrderAsync(Connection, Credentials, "100");
        True(paths.SequenceEqual(["/sites", "/orders/100", "/prro/receipt-status/100", "/prro/receipt/100"]));
        Equal(ReceiptLinkState.Exact, Match(order!).State);
    }
    private static async Task NullExpansionAsync()
    {
        foreach (var detail in new object?[] { null, new { prro_receipt_status = 1, prro_receipt_fiscal_code = (string?)null } })
        {
            var order = await Fetch(detailPrro: detail);
            Equal(Code, order.FiscalReceiptNumbers.Single());
            Equal(ReceiptLinkState.Exact, Match(order).State);
        }
    }
    private static async Task RefreshPreservesAsync()
    {
        var phase = 0;
        using var http = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/sites" => Login(),
            "/orders/search" => Page(phase == 0 ? Row() : new { id = 100, created = "2026-09-24 10:00:00" }),
            "/orders/100" => Detail(),
            "/prro/receipt-status/100" => Json(new { success = true, content = new { status = 1 } }),
            "/prro/receipt/100" => phase == 0 ? Json(new { success = true, content = new { url = Url } }) : new(HttpStatusCode.Forbidden),
            _ => throw new InvalidOperationException()
        }));
        var cache = new Cache();
        var sync = new MarketplaceSyncService([new RozetkaOrdersClient(new MarketplaceHttpTransport(http))], new Secrets(), cache);
        var config = new MarketplaceSettings { Connections = [Connection] };
        var first = await sync.SynchronizeAsync(config, Range, []);
        Equal(ReceiptLinkState.Exact, Match(first.Orders.Single()).State);
        phase = 1;
        var second = await sync.SynchronizeAsync(config, Range, []);
        Equal(2, second.Orders.Single().FiscalReferences.Count);
        Equal(ReceiptLinkState.Exact, Match(second.Orders.Single()).State);
        True(second.Orders.Single().FiscalDataStatus.Length > 0);
    }
    private static async Task ConflictingKeysAsync()
    {
        var order = await Fetch(code: "OTHER-CODE");
        Equal(ReceiptLinkState.Conflict, Match(order).State);
        Equal(ReceiptLinkState.Conflict, Match(order, Receipt(OtherId, "OTHER-CODE")).State);
        // List vs detail contradictory immutable assertions must both survive.
        var changed = await Fetch(detailPrro: new { prro_receipt_status = 1, prro_receipt_fiscal_code = "OTHER-CODE" });
        True(changed.FiscalReferences.Count(r => r.Kind == FiscalDocumentKeyKind.FiscalCode) == 2);
        Equal(ReceiptLinkState.Conflict, Match(changed).State);
    }
    private static Task UrlValidationAsync()
    {
        True(CheckboxReceiptReference.TryParseUrl(Url + "&organization_id=" + Organization, out var parsed));
        Equal(ReceiptId, parsed.ReceiptId); Equal(Organization, parsed.OrganizationId);
        foreach (var invalid in new[]
        {
            Url.Replace("https:","http:"), Url.Replace("api.checkbox.ua","api.checkbox.ua.evil.test"),
            Url.Replace("api.checkbox.ua","evil.test@api.checkbox.ua"), Url.Replace("api.checkbox.ua","api.checkbox.ua:444"),
            Url + "&redirect=https://evil.test", Url + "#" + OtherId, Url + "&simple=false",
            "https://api.checkbox.ua/?receipt_id=" + ReceiptId, Url.Replace("/api/", "/other/../api/"),
            "https://check.checkbox.ua/" + ReceiptId, "comment " + ReceiptId
        }) True(!CheckboxReceiptReference.TryParseUrl(invalid, out _));
        return Task.CompletedTask;
    }
    private static async Task UntrustedAndBareAsync()
    {
        var order = await Fetch(url: "https://unknown.example/" + ReceiptId);
        True(Match(order).State != ReceiptLinkState.Exact);
        True(order.FiscalReferences.All(r => r.Kind != FiscalDocumentKeyKind.CheckboxReceiptUrl));
        True(order.FiscalDataStatus.Length > 0);
    }
    private static async Task FiscalCodeLookupAsync()
    {
        const string code = Code; // Documented HTML alias is exactly 11 characters.
        var url = $"https://api.checkbox.ua/api/v1/receipts/{code}/html?organization_id={Organization}&simple=1";
        var order = await Fetch(url, code);
        var settings = new Settings(); var account = PrintAccountContext.Create(await settings.LoadAsync());
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++; Equal(HttpMethod.Get, request.Method); Equal("api.checkbox.ua", request.RequestUri!.Host);
            True(request.RequestUri.Query.Contains("self_receipts=true") && request.RequestUri.Query.Contains("fiscal_code=" + code));
            True(!request.RequestUri.Query.Contains("from_date")); // Lookup is exact, not limited to UI dates.
            Equal("fake-checkbox", request.Headers.Authorization?.Parameter);
            return ReceiptPage(Receipt(code: code));
        }));
        var verifier = new FiscalReferenceVerifier(new CheckboxApiClient(http, new Auth(), new Logger()), settings);
        var verified = (await verifier.VerifyAsync([order], account)).Single();
        Equal(1, calls); // Code and code URL share one lookup.
        var match = Match(verified, Receipt(code: code), account: account);
        Equal(ReceiptLinkState.Exact, match.State); Equal("За фіскальним номером", match.Explanation);
        True(Match(verified, Receipt(code: code), account: "another-account").State != ReceiptLinkState.Exact);
    }
    private static async Task LookupFailureAsync()
    {
        const string code = Code;
        var order = await Fetch($"https://api.checkbox.ua/api/v1/receipts/{code}/html?organization_id={Organization}", code);
        var settings = new Settings(); var account = PrintAccountContext.Create(await settings.LoadAsync());
        foreach (var mode in new[] { 0, 1, 2, 3 })
        {
            using var http = new HttpClient(new Handler(_ => mode switch
            {
                0 => ReceiptPage(),
                1 => new(HttpStatusCode.Forbidden),
                2 => ReceiptPage(Receipt(code: code), Receipt(OtherId, code)),
                _ => ReceiptPage(Receipt(code: code, organization: OtherId))
            }));
            var verifier = new FiscalReferenceVerifier(new CheckboxApiClient(http, new Auth(), new Logger()), settings);
            var verified = (await verifier.VerifyAsync([order], account)).Single();
            True(Match(verified, Receipt(code: code), account: account).State != ReceiptLinkState.Exact);
            True(verified.FiscalReferences.All(r => r.VerifiedReceiptId.Length == 0));
            True(verified.FiscalDataStatus.Length > 0);
        }
    }
    private static async Task ContextIsolationAsync()
    {
        var original = await Fetch(url: "https://unknown.example/receipt");
        var code = original.FiscalReferences.Single() with { CashRegisterFiscalNumber = "000123" };
        var order = original with { FiscalReferences = [code] };
        Equal(ReceiptLinkState.Exact, Match(order).State);
        True(Match(order, Receipt(cash: "000456")).State != ReceiptLinkState.Exact);
        True(Match(order, Receipt(code: Code.ToLowerInvariant())).State != ReceiptLinkState.Exact);
        Equal(ReceiptLinkState.Conflict, Match(order, scope: [Receipt(), Receipt(OtherId)]).State);
        // Same serial is not a fiscal number, nor is the cash-register fiscal number.
        True(Match(original, Receipt(code: "42")).State != ReceiptLinkState.Exact);
        True(Match(original, Receipt(code: "000123")).State != ReceiptLinkState.Exact);
    }

    private static async Task LookupBoundAsync()
    {
        var source = await Fetch();
        var settings = new Settings(); var account = PrintAccountContext.Create(await settings.LoadAsync());
        var references = Enumerable.Range(0, 101).Select(index => new FiscalDocumentReference(FiscalDocumentKeyKind.FiscalCode,
            "F" + index.ToString("D10"), "synthetic bounded fiscal source", source.Key, "Checkbox", "doc-" + index,
            OrganizationId: Organization)).ToArray();
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            calls++;
            // 400 is permanent, avoiding transient/authorization retry in the underlying client.
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }));
        var verifier = new FiscalReferenceVerifier(new CheckboxApiClient(http, new Auth(), new Logger()), settings);
        var result = await verifier.VerifyAsync([source with { FiscalReferences = references.Concat(references).ToArray() }], account);
        Equal(100, calls); True(result.Single().FiscalReferences.All(r => r.VerifiedReceiptId.Length == 0));
        calls = 0;
        await verifier.VerifyAsync([source with { FiscalReferences = references }], "different-account");
        Equal(0, calls);
    }
    private static async Task AccountChangesDuringLookupAsync()
    {
        var order = await Fetch($"https://api.checkbox.ua/api/v1/receipts/{Code}/html?organization_id={Organization}");
        var settings = new Settings();
        var account = PrintAccountContext.Create(await settings.LoadAsync());
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            calls++;
            settings.Login = "another-synthetic-cashier";
            return ReceiptPage(Receipt());
        }));
        var verifier = new FiscalReferenceVerifier(new CheckboxApiClient(http, new Auth(), new Logger()), settings);
        var result = (await verifier.VerifyAsync([order], account)).Single();
        Equal(1, calls);
        True(result.FiscalReferences.All(r => r.VerifiedReceiptId.Length == 0));
        True(result.FiscalDataStatus.Contains("Касира змінено"));
        True(Match(result, account: account).State != ReceiptLinkState.Exact);
        var stale = order with { FiscalReferences = order.FiscalReferences.Select(r => r with
            { VerifiedReceiptId = ReceiptId, VerifiedAccountContext = account }).ToArray() };
        var rejected = (await verifier.VerifyAsync([stale], account)).Single();
        Equal(1, calls); // Already changed account: no further network request.
        True(rejected.FiscalReferences.All(r => r.VerifiedReceiptId.Length == 0));
    }

    private static async Task MultipleAndManualAsync()
    {
        var sale = await Fetch();
        var returned = await Fetch(url: Url.Replace(ReceiptId, OtherId), code: "RETURN-CODE");
        var both = sale with { FiscalReferences = sale.FiscalReferences.Concat(returned.FiscalReferences.Select(r => r with { DocumentId = "return" })).ToArray() };
        Equal(ReceiptLinkState.Exact, Match(both).State);
        Equal(ReceiptLinkState.Exact, Match(both, Receipt(OtherId,"RETURN-CODE",type:"RETURN")).State);
        True(Match(sale, Receipt(OtherId,"RETURN-CODE",type:"RETURN")).State != ReceiptLinkState.Exact);
        var rejected = new ReceiptOrderDecision { AccountContext = "account", ReceiptId = ReceiptId, RejectedOrders = [sale.Key] };
        True(Match(sale, decisions: [rejected]).State != ReceiptLinkState.Exact);
        var conflict = await Fetch(code: "MISMATCH");
        var manual = new ReceiptOrderDecision { AccountContext = "account", ReceiptId = ReceiptId, ConfirmedOrder = sale.Key };
        Equal(ReceiptLinkState.Manual, Match(conflict, decisions: [manual]).State);
    }
    private static async Task SerializationAsync()
    {
        var order = await Fetch();
        var original = order with { FiscalReferences = order.FiscalReferences.Select(r => r with { VerifiedReceiptId = ReceiptId, VerifiedAccountContext = "account" }).ToArray() };
        var json = JsonSerializer.Serialize(original);
        True(!json.Contains("VerifiedReceiptId") && !json.Contains("VerifiedAccountContext"));
        var restored = JsonSerializer.Deserialize<MarketplaceOrder>(json)!;
        Equal(2, restored.FiscalReferences.Count); Equal(ReceiptLinkState.Exact, Match(restored).State);
        True(restored.FiscalReferences.All(r => r.VerifiedReceiptId.Length == 0));
    }
    private static async Task PromReadShapeAsync()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Equal(HttpMethod.Get, request.Method); Equal("my.prom.ua", request.RequestUri!.Host);
            return Json(new { order = new { id = 100, date_created = "2026-09-24T10:00:00+03:00", price = "100.00", client_notes = Url, receipt_id = ReceiptId } });
        }));
        // A name alone does not establish semantics: no real Prom response/documented read field proved receipt_id.
        var order = await new PromOrdersClient(new MarketplaceHttpTransport(http)).GetOrderAsync(
            new() { Id = Connection.Id, Marketplace = MarketplaceKind.Prom }, new(Token: "fake-prom"), "100");
        Equal(0, order!.FiscalReferences.Count); Equal(ReceiptLinkState.Candidates, Match(order).State);
    }

    private static HttpResponseMessage ReceiptPage(params ReceiptRecord[] receipts) => Json(new { results = receipts.Select(r => new
        { id = r.Id, fiscal_code = r.FiscalCode, organization_id = r.OrganizationId, type = r.Type, cash_register = new { fiscal_number = r.CashRegisterFiscalNumber } }).ToArray() });
    private static void True(bool condition) { if (!condition) throw new Exception("Fiscal assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(reply(request)); } }
    private sealed class Cache : IMarketplaceCacheStore
    { private MarketplaceSnapshot _value = new([], []); public Task<MarketplaceSnapshot> LoadAsync(int days, CancellationToken cancellationToken = default) => Task.FromResult(_value); public Task SaveAsync(MarketplaceSnapshot value, CancellationToken cancellationToken = default) { _value = value; return Task.CompletedTask; } }
    private sealed class Secrets : IMarketplaceSecretStore
    { public Task<MarketplaceCredentials?> LoadAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<MarketplaceCredentials?>(Credentials); public Task SaveAsync(string id, MarketplaceCredentials value, CancellationToken cancellationToken = default) => throw new NotSupportedException(); }
    private sealed class Settings : ISettingsService
    { public string Login { get; set; } = "synthetic"; public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings { Login = Login }); public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default) => throw new NotSupportedException(); }
    private sealed class Auth : IAuthenticationService
    { public bool HasStoredCredentials => true; public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) => Task.FromResult("fake-checkbox"); public void InvalidateToken() { } public Task SignInAndStoreAsync(string login, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException(); }
    private sealed class Logger : IAppLogger
    { public string LogDirectory => ""; public void Info(string operation, string? receiptId = null, int? httpStatus = null, string? printStatus = null) { } public void Error(string operation, Exception exception, string? receiptId = null, int? httpStatus = null, string? printStatus = null) { } }
}
