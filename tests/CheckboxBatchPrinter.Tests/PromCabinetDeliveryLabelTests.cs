using System.Net;
using System.Net.Http;
using System.Text;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;
using CheckboxBatchPrinter.Services;
using static CheckboxBatchPrinter.Tests.ShippingLabelsTests;

namespace CheckboxBatchPrinter.Tests;

internal static class PromCabinetDeliveryLabelTests
{
    private const string ConnectionId = "synthetic-prom-cabinet", OrderId = "433580034", OptionId = "16954954";
    private const string Tracking = "PRM-811362308";
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("Prom cabinet accepts verified Prom and numeric Bigl labels without any carrier or API key", Success),
        ("Prom cabinet read contract restricts exact HTTPS host path and bounded order delivery identifiers", Routes),
        ("Prom cabinet validates frozen store carrier order and delivery option before session access", Identity),
        ("Prom cabinet rejects account and request changes or redirects without inspecting or retrying", ResponseIdentity),
        ("Prom cabinet rejects authentication errors throttling server failures HTML JSON ZIP and oversized PDFs", ResponseFailures),
        ("Prom cabinet requires exact visible TTN single-page100mm layout and rejects numeric substrings", PdfIdentity),
        ("Prom cabinet cancellation and disabled store during download stop before accepting document", Interrupted),
        ("Prom cabinet source survives batch scoping and never silently falls back to Delivery API", Routing),
        ("Prom cabinet printed history retains the Prom order and account identity", History),
        ("Prom delivery option numeric and string IDs survive adapter snapshot and malformed IDs stay absent", DeliveryOptionMapping),
        ("STA real PDFium verifies Prom cabinet labels and rejects hidden wrong or multisheet PDFs", () => MarketplaceViewModelTests.StaAsync(RealPdf))
    ];

    private sealed class Store : IMarketplaceSettingsStore, IShippingSettingsStore, IMarketplaceSecretStore
    {
        public MarketplaceSettings Market = new() { Connections = [new() { Id = ConnectionId, Marketplace = MarketplaceKind.Prom, Enabled = true }] };
        public Task<MarketplaceSettings> LoadAsync(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Task.FromResult(Market); }
        public Task SaveAsync(MarketplaceSettings value, CancellationToken ct = default) => throw new Exception("Label source mutated marketplace settings.");
        Task<ShippingLabelSettings> IShippingSettingsStore.LoadAsync(CancellationToken ct) => throw new Exception("Prom cabinet read carrier credentials.");
        public Task SaveAsync(ShippingLabelSettings value, CancellationToken ct = default) => throw new Exception("Label source changed carrier credentials.");
        public Task<MarketplaceCredentials?> LoadAsync(string id, CancellationToken ct = default) => throw new Exception("Prom cabinet requested public API credentials.");
        public Task SaveAsync(string id, MarketplaceCredentials value, CancellationToken ct = default) => throw new Exception("Label source changed API credentials.");
    }
    private sealed class Session : IPromCabinetSession
    {
        public readonly List<PromCabinetLabelRequest> Requests = [];
        public Func<PromCabinetLabelRequest, CancellationToken, PromCabinetLabelResponse>? Override;
        public Task<PromCabinetLabelResponse> ReadLabelAsync(PromCabinetLabelRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Requests.Add(request);
            return Task.FromResult(Override?.Invoke(request, ct) ?? Reply(request));
        }
    }
    private sealed class Inspector : ILabelPdfInspector
    {
        public int Calls;
        public LabelPdfInspection Result = new([new(1, 100, 100, "TTN " + Tracking)]);
        public Task<LabelPdfInspection> InspectAsync(byte[] bytes, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(Result); }
    }
    private sealed class Fixture
    {
        public readonly Store Store = new();
        public readonly Session Session = new();
        public readonly Inspector Inspector = new();
        public PromCabinetDeliveryLabelSource Source => new(Store, Session, Inspector);
    }
    private static ShipmentReference Shipment(string tracking = Tracking) => new(new(MarketplaceKind.Prom, ConnectionId, OrderId),
        OrderId, ShippingCarrier.RozetkaDelivery, tracking) { DeliveryServiceId = OptionId };
    private static PromCabinetLabelResponse Reply(PromCabinetLabelRequest request) => new(request.ConnectionId, request.RequestUri,
        request.RequestUri, 200, "application/pdf", SyntheticShippingPdf.Create(pageTexts: [request.TrackingNumber]));
    private static async Task Reject(Func<Task> action, LabelFailureKind kind)
    {
        try { await action(); throw new Exception("Invalid Prom cabinet label was accepted."); }
        catch (ShippingLabelException ex) { Check(ex.Kind == kind, "Unexpected label failure: " + ex.Kind); }
    }

    private static async Task Success()
    {
        foreach (var tracking in new[] { Tracking, "201288134129" })
        {
            var f = new Fixture(); f.Inspector.Result = new([new(1, 99.99, 99.99, "TTN " + tracking)]);
            var document = await f.Source.GetAsync(Shipment(tracking));
            Check(document.ConnectionId == ConnectionId && document.Shipment == Shipment(tracking) && document.ExpectedPages == 1 &&
                document.Source == PromCabinetDeliveryLabelSource.SourceName && document.Pdf.Length > 0 && document.Fingerprint.Length == 64,
                "Prom or Bigl label lost identity or content.");
            var request = f.Session.Requests.Single();
            Check(request.RequestUri.AbsoluteUri == "https://my.prom.ua/cabinet/delivery/print_shipment_pdf?order_id=" + OrderId +
                "&delivery_option_id=" + OptionId && request.TrackingNumber == tracking && request.ConnectionId == ConnectionId,
                "Cabinet request did not use the frozen order's own delivery option.");
        }
    }
    private static Task Routes()
    {
        var request = new PromCabinetLabelRequest(ConnectionId, OrderId, OptionId, Tracking);
        Check(PromCabinetLabelReadOnlyContract.ValidUri(request.RequestUri), "Known cabinet route rejected.");
        foreach (var url in new[]
        {
            "http://my.prom.ua/cabinet/delivery/print_shipment_pdf?order_id=1&delivery_option_id=2",
            "https://my.prom.ua.evil.invalid/cabinet/delivery/print_shipment_pdf?order_id=1&delivery_option_id=2",
            "https://other:secret@my.prom.ua/cabinet/delivery/print_shipment_pdf?order_id=1&delivery_option_id=2",
            "https://my.prom.ua:444/cabinet/delivery/print_shipment_pdf?order_id=1&delivery_option_id=2",
            "https://my.prom.ua/cabinet/delivery/create_declaration?order_id=1&delivery_option_id=2",
            "https://my.prom.ua/cabinet/delivery/print_shipment_pdf?order_id=1&delivery_option_id=2&extra=1",
            "https://my.prom.ua/cabinet/delivery/print_shipment_pdf?order_id=1&delivery_option_id=2#extra",
            "https://my.prom.ua/cabinet/delivery/print_shipment_pdf?order_id=01&delivery_option_id=2",
            "https://my.prom.ua/cabinet/delivery/print_shipment_pdf?order_id=1&delivery_option_id=9223372036854775808",
            "https://my.prom.ua/cabinet/delivery/print_shipment_pdf?order_id=1&order_id=2"
        }) Check(!PromCabinetLabelReadOnlyContract.ValidUri(new(url)), "Unsafe cabinet route accepted: " + url);
        foreach (var invalid in new[] { request with { OrderId = "1&other=2" }, request with { DeliveryOptionId = "" },
            request with { ConnectionId = " " }, request with { TrackingNumber = "../../label" } })
            Check(!PromCabinetLabelReadOnlyContract.ValidRequest(invalid), "Invalid cabinet identity accepted.");
        return Task.CompletedTask;
    }
    private static async Task Identity()
    {
        foreach (var invalid in new[] { Shipment() with { Order = null }, Shipment() with { Order = Shipment().Order! with { Marketplace = MarketplaceKind.Rozetka } },
            Shipment() with { Carrier = ShippingCarrier.NovaPoshta }, Shipment() with { DeliveryServiceId = "" },
            Shipment() with { TrackingNumber = "12" }, Shipment() with { Order = Shipment().Order! with { OrderId = "../x" } } })
        {
            var f = new Fixture(); await Reject(() => f.Source.GetAsync(invalid), LabelFailureKind.InvalidDocument);
            Check(f.Session.Requests.Count == 0, "Invalid identity contacted cabinet.");
        }
        var multi = new Fixture(); await Reject(() => multi.Source.GetAsync(Shipment() with { ExpectedPlaces = 2 }), LabelFailureKind.UnsupportedFormat);
        Check(multi.Session.Requests.Count == 0, "Known unsupported multi-place shipment fetched a label.");
        foreach (var mode in new[] { "missing", "disabled", "foreign", "duplicate", "kind" })
        {
            var f = new Fixture(); var c = f.Store.Market.Connections.Single();
            if (mode == "missing") f.Store.Market.Connections.Clear();
            if (mode == "disabled") c.Enabled = false;
            if (mode == "foreign") c.Id = "another-store";
            if (mode == "duplicate") f.Store.Market.Connections.Add(c);
            if (mode == "kind") c.Marketplace = MarketplaceKind.Rozetka;
            await Reject(() => f.Source.GetAsync(Shipment()), LabelFailureKind.ConnectionRequired);
            Check(f.Session.Requests.Count == 0, "Unconfigured store contacted cabinet.");
        }
    }
    private static async Task ResponseIdentity()
    {
        foreach (var mode in new[] { "account", "request", "final", "redirect" })
        {
            var f = new Fixture(); f.Session.Override = (r, _) =>
            {
                var response = Reply(r); var other = new Uri("https://my.prom.ua/cabinet/delivery/print_shipment_pdf?order_id=999&delivery_option_id=" + OptionId);
                return mode switch { "account" => response with { ConnectionId = "foreign" }, "request" => response with { RequestUri = other },
                    "final" => response with { FinalUri = other }, _ => response with { FinalUri = new("https://my.prom.ua/login") } };
            };
            await Reject(() => f.Source.GetAsync(Shipment()), LabelFailureKind.InvalidDocument);
            Check(f.Session.Requests.Count == 1 && f.Inspector.Calls == 0, "Mismatched response retried or inspected.");
        }
    }
    private static async Task ResponseFailures()
    {
        foreach (var status in new[] { 401, 403, 404, 429, 500, 302 })
        {
            var f = new Fixture(); f.Session.Override = (r, _) => Reply(r) with { StatusCode = status };
            await Reject(() => f.Source.GetAsync(Shipment()), status is 401 or 403 ? LabelFailureKind.ConnectionRequired :
                status == 404 ? LabelFailureKind.MissingDocument : status is 429 or 500 ? LabelFailureKind.Temporary : LabelFailureKind.InvalidDocument);
            Check(f.Session.Requests.Count == 1 && f.Inspector.Calls == 0, "Failure caused retries or inspection.");
        }
        foreach (var type in new[] { "text/html", "application/json", "application/zip", "" })
        {
            var f = new Fixture(); f.Session.Override = (r, _) => Reply(r) with { ContentType = type };
            await Reject(() => f.Source.GetAsync(Shipment()), LabelFailureKind.UnsupportedFormat);
            Check(f.Inspector.Calls == 0, "Non-PDF content reached inspector.");
        }
        foreach (var bytes in new[] { Encoding.ASCII.GetBytes("<html>Log in</html>"), new byte[PromCabinetLabelReadOnlyContract.MaximumBytes + 1] })
        {
            var f = new Fixture(); f.Session.Override = (r, _) => Reply(r) with { Content = bytes };
            await Reject(() => f.Source.GetAsync(Shipment()), LabelFailureKind.InvalidDocument);
            Check(f.Inspector.Calls == 0, "Invalid or oversized bytes reached inspector.");
        }
    }
    private static async Task PdfIdentity()
    {
        foreach (var text in new[] { "another TTN PRM-111111111", "", "X" + Tracking, Tracking + "1", "0" + "201288134129", "201288134129" + "0" })
        {
            var f = new Fixture(); f.Inspector.Result = new([new(1, 100, 100, text)]);
            await Reject(() => f.Source.GetAsync(Shipment(text.Contains("201288") ? "201288134129" : Tracking)), LabelFailureKind.InvalidDocument);
        }
        foreach (var pages in new IReadOnlyList<LabelPdfPageInfo>[] { [], [new(1, 100, 100, Tracking), new(2, 100, 100, Tracking)],
            [new(1, 210, 297, Tracking)], [new(1, double.NaN, 100, Tracking)], [new(2, 100, 100, Tracking)] })
        {
            var f = new Fixture(); f.Inspector.Result = new(pages);
            await Reject(() => f.Source.GetAsync(Shipment()), LabelFailureKind.UnsupportedFormat);
        }
    }
    private static async Task Interrupted()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); var f = new Fixture();
        try { await f.Source.GetAsync(Shipment(), canceled.Token); throw new Exception("Canceled label fetched."); } catch (OperationCanceledException) { }
        Check(f.Session.Requests.Count == 0, "Canceled read contacted cabinet.");
        using var mid = new CancellationTokenSource(); f.Session.Override = (r, _) => { mid.Cancel(); return Reply(r); };
        try { await f.Source.GetAsync(Shipment(), mid.Token); throw new Exception("Canceled response accepted."); } catch (OperationCanceledException) { }
        Check(f.Inspector.Calls == 0, "Canceled response inspected.");
        var changed = new Fixture(); changed.Session.Override = (r, _) => { changed.Store.Market.Connections.Single().Enabled = false; return Reply(r); };
        await Reject(() => changed.Source.GetAsync(Shipment()), LabelFailureKind.ConnectionRequired);
    }
    private sealed class NoNetwork : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new Exception("Prom label attempted Delivery/Seller API fallback."); }
    private static async Task Routing()
    {
        var f = new Fixture(); using var http = new HttpClient(new NoNetwork());
        var source = new OfficialShippingLabelSource(new(http), f.Store, f.Store, f.Store, promCabinet: f.Source);
        var scoped = source.CreateBatchSource([Shipment()]);
        Check((await scoped.GetAsync(Shipment())).Source == PromCabinetDeliveryLabelSource.SourceName, "Batch lost cabinet routing.");
        f.Session.Override = (r, _) => Reply(r) with { StatusCode = 403 };
        await Reject(() => scoped.GetAsync(Shipment()), LabelFailureKind.ConnectionRequired);
        Check(f.Session.Requests.Count == 2, "Cabinet error retried or changed route.");
    }
    private static async Task History()
    {
        var f = new Fixture(); var document = await f.Source.GetAsync(Shipment());
        var page = new ShippingLabelPage(document.Shipment, document.DocumentId, document.Fingerprint, 1, 100, 100, [1])
            { ConnectionId = ConnectionId, Source = document.Source };
        var history = new MemoryHistory(); var backend = new MemoryBackend();
        var result = await new LabelPrintCoordinator(history, backend).SubmitAsync(new("synthetic-prom-batch", [page], Settings));
        Check(result.Pages.Single().Order == Shipment().Order && result.Pages.Single().ConnectionId == ConnectionId,
            "Prom cabinet print lost order/account history.");
    }
    private sealed class MemoryHistory : ILabelHistoryStore
    {
        public Task<IReadOnlyList<LabelPrintAttempt>> LoadAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LabelPrintAttempt>>([]);
        public Task SaveAsync(LabelPrintAttempt attempt, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class MemoryBackend : ILabelBatchBackend
    {
        public async Task<int?> SubmitAsync(LabelPrintBatch batch, string name, Func<Task> before, Func<int, Task> created, CancellationToken ct = default)
        { await before(); await created(1); return 1; }
    }
    private sealed class ResponseHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
    private static async Task DeliveryOptionMapping()
    {
        foreach (var value in new[] { "16954954", "\"16954954\"", "\"0016954954\"", "0", "-1", "1.5", "true", "null", "{}", "\"1&x=2\"" })
        {
            var json = "{\"order\":{\"id\":" + OrderId + ",\"delivery_option\":{\"id\":" + value + ",\"name\":\"rozetka_delivery\"}," +
                "\"delivery_provider_data\":{\"provider\":\"rozetka_delivery\",\"declaration_number\":\"" + Tracking + "\"}}}";
            using var http = new HttpClient(new ResponseHandler(json));
            var client = new PromOrdersClient(new MarketplaceHttpTransport(http)); var store = new Store();
            var order = await client.GetOrderAsync(store.Market.Connections.Single(), new("synthetic-public-api-token"), OrderId);
            Check(order is not null, "Synthetic Prom order missing.");
            var expected = value.Contains("16954954", StringComparison.Ordinal) ? OptionId : "";
            Check(order!.Shipments.Single().DeliveryServiceId == expected && ShippingCarrierNames.ForOrder(order).Single().DeliveryServiceId == expected,
                "Delivery option ID lost or guessed from malformed data: " + value);
        }
    }
    private static async Task RealPdf()
    {
        foreach (var tracking in new[] { Tracking, "201288134129" })
        {
            var f = new Fixture(); var source = new PromCabinetDeliveryLabelSource(f.Store, f.Session, new WindowsLabelPdfInspector());
            var doc = await source.GetAsync(Shipment(tracking));
            var pages = await new WindowsShippingLabelRenderer().RenderAsync(doc, 203, 203);
            Check(pages.Count == 1 && pages.Single().Source == PromCabinetDeliveryLabelSource.SourceName && pages.Single().Png.Length > 0,
                "Verified cabinet PDF did not render.");
        }
        foreach (var mode in new[] { "hidden", "wrong", "multi", "a4" })
        {
            var f = new Fixture(); f.Session.Override = (r, _) => Reply(r) with
            {
                Content = SyntheticShippingPdf.Create(widthMm: mode == "a4" ? 210 : 100, heightMm: mode == "a4" ? 297 : 100,
                    pages: mode == "multi" ? 2 : 1, pageTexts: mode == "multi" ? [Tracking, Tracking] : [mode == "wrong" ? "PRM-111111111" : Tracking],
                    textOutsidePage: mode == "hidden")
            };
            var source = new PromCabinetDeliveryLabelSource(f.Store, f.Session, new WindowsLabelPdfInspector());
            await Reject(() => source.GetAsync(Shipment()), mode is "multi" or "a4" ? LabelFailureKind.UnsupportedFormat : LabelFailureKind.InvalidDocument);
        }
    }
}
