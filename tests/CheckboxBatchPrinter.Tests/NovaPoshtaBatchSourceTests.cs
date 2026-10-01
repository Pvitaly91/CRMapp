using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;
using CheckboxBatchPrinter.Services;
using static CheckboxBatchPrinter.Tests.ShippingLabelsTests;

namespace CheckboxBatchPrinter.Tests;

internal static class NovaPoshtaBatchSourceTests
{
    private const string FirstTtn = "20400000000001", SecondTtn = "20400000000002", OtherTtn = "20400000000003";
    private const string FirstRef = "10000000-0000-0000-0000-000000000001";
    private static readonly NovaPoshtaConnection Connection = new("synthetic-np-selected", "Synthetic selected NP", "synthetic_batch_np_key");
    private static readonly NovaPoshtaConnection ForeignConnection = new("synthetic-np-other", "Synthetic other NP", "synthetic_batch_other_key");
    private static MarketplaceOrder FirstOrder => Order("NP-first", ShippingCarrier.NovaPoshta, FirstTtn);
    private static MarketplaceOrder SecondOrder => Order("NP-second", ShippingCarrier.NovaPoshta, SecondTtn);
    private static ShipmentReference Shipment(MarketplaceOrder order) => ShippingCarrierNames.ForOrder(order).Single();

    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("STA direct NP packet uses one bulk GET and downloads fresh PDF for the next packet", () => MarketplaceViewModelTests.StaAsync(OneGetPerPacket)),
        ("STA direct NP bulk maps reversed and repeated TTN pages without dropping places", () => MarketplaceViewModelTests.StaAsync(PageMapping)),
        ("STA direct NP bulk download is isolated by key account and explicitly selected shop", () => MarketplaceViewModelTests.StaAsync(ScopedAccountChanges)),
        ("STA direct NP bulk ignores existing manual approval and does not record new session proof", () => MarketplaceViewModelTests.StaAsync(TwoLabels)),
        ("STA direct NP manual approval cannot bypass a wrong fresh bulk PDF", () => MarketplaceViewModelTests.StaAsync(ChangedApprovedPdf)),
        ("STA direct NP wrong missing unknown and unsupported bulk pages block the entire packet", () => MarketplaceViewModelTests.StaAsync(InvalidPages)),
        ("STA direct NP bulk retains unknown places and rejects missing known expected places", () => MarketplaceViewModelTests.StaAsync(ExpectedPlaces)),
        ("STA direct NP cancelled bulk GET stops without list lookup fallback or partial packet", () => MarketplaceViewModelTests.StaAsync(Cancellation)),
        ("STA direct NP bulk HTTP429 and HTTP500 stop once without fallback or retry", () => MarketplaceViewModelTests.StaAsync(TransportFailure)),
        ("STA direct NP rotated key downloads one fresh PDF without modifying manual evidence", () => MarketplaceViewModelTests.StaAsync(Rotation)),
        ("STA direct NP single read uses one GET typed TTN identity and never claims a verified Ref", () => MarketplaceViewModelTests.StaAsync(BindingAndRef))
    ];

    private enum ResponseKind { Valid, WrongNumber, UnknownNumber, MissingSecond, MultiplePlaces, UnsupportedSize, RateLimited, ServerError }
    private sealed record Request(string Method, string Key, string[] Ttns);
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => response(request, ct);
    }
    private sealed class Store : IShippingSettingsStore
    {
        public ShippingLabelSettings Config = new()
        {
            Print = Settings, NovaPoshtaConnections = [ForeignConnection, Connection],
            NovaPoshtaStoreBindings = [new("synthetic-other-prom", ForeignConnection.Id), new(FirstOrder.Key.ConnectionId, Connection.Id)]
        };
        public int Loads, Saves;
        public Task<ShippingLabelSettings> LoadAsync(CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Loads++; return Task.FromResult(Config); }
        public Task SaveAsync(ShippingLabelSettings value, CancellationToken ct = default)
        { Saves++; throw new Exception("Label preparation tried to save settings."); }
    }
    private sealed class NeverSource : IShippingLabelSource
    {
        public Task<ShippingLabelDocument> GetAsync(ShipmentReference shipment, CancellationToken ct = default) =>
            throw new Exception("Direct NP preparation contacted a fallback account.");
    }
    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _http;
        private bool _manualDiagnostic;
        public readonly Store Store = new();
        public readonly List<Request> Requests = [];
        public readonly NovaPoshtaVerifiedSession Session = new();
        public readonly NovaPoshtaReadOnlyClient Client;
        public readonly WindowsLabelPdfInspector Inspector = new();
        public readonly IShippingLabelSource Source;
        public ResponseKind Result;
        public bool WrongFirstPdf;
        public CancellationTokenSource? CancelOnGet;
        public NovaPoshtaConnection Selected => Store.Config.NovaPoshtaConnections.Single(c => c.Id ==
            Store.Config.NovaPoshtaStoreBindings.Single(b => b.MarketplaceConnectionId == FirstOrder.Key.ConnectionId).NovaPoshtaConnectionId);

        public Fixture()
        {
            _http = new(new Handler(Respond));
            Client = new(NovaPoshtaRequestPolicyTests.FastTransport(_http));
            Source = new ConfiguredShippingLabelSource(Store, new NovaPoshtaDirectLabelSource(Store, Client, Inspector), new NeverSource());
            Check(Requests.Count == 0, "Constructing the source started NP requests.");
        }
        public void Dispose() => _http.Dispose();
        public async Task ApproveFirst()
        {
            _manualDiagnostic = true;
            try
            {
                var today = DateOnly.FromDateTime(DateRangeBuilder.TodayKyiv);
                var result = await new NovaPoshtaDiagnostics(Client, Inspector).CheckAsync(Selected, Shipment(FirstOrder), new(today.AddDays(-6), today));
                Session.Accept(Selected, result);
            }
            finally { _manualDiagnostic = false; Requests.Clear(); }
        }
        public Task<LabelPrintBatch> Prepare(CancellationToken ct = default, params MarketplaceOrder[] orders) =>
            new LabelBatchPreparation(Source, new WindowsShippingLabelRenderer()).PrepareAsync(
                orders.Length == 0 ? [FirstOrder, SecondOrder] : orders, Settings, Geometry, ct);

        private async Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri ?? throw new Exception("NP request has no URI.");
            if (request.Method == HttpMethod.Post)
            {
                Check(_manualDiagnostic && uri.AbsoluteUri == NovaPoshtaReadOnlyContract.EntryPoint,
                    "Printing scanned getDocumentList instead of requesting the selected marking PDF.");
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var root = body.RootElement;
                var key = root.GetProperty("apiKey").GetString()!;
                Check(key == Selected.ApiKey, "Manual diagnostic used another account key.");
                Requests.Add(new("POST", key, []));
                var firstPage = root.GetProperty("methodProperties").GetProperty("Page").GetString() == "1";
                return List(firstPage ? [new { IntDocNumber = FirstTtn, Ref = FirstRef, SeatsAmount = 1, DeletionMark = "0" }] : []);
            }
            Check(request.Method == HttpMethod.Get && uri.Host == "my.novaposhta.ua", "Unexpected NP request.");
            var segments = Uri.UnescapeDataString(uri.AbsolutePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
            Check(segments.Length == 10 && segments[0] == "orders" && segments[1] == "printMarking100x100" &&
                segments[2] is "orders" or "orders[]", "Unexpected marking URL shape.");
            var numbers = segments[3].Split(',');
            Check(numbers.Length > 0 && numbers.All(n => n is FirstTtn or SecondTtn) && numbers.Distinct().Count() == numbers.Length,
                "Marking request contains an unselected or duplicate TTN.");
            Check(segments[2] == (numbers.Length > 1 ? "orders" : "orders[]"), "Single and bulk marking route forms were mixed.");
            var keyValue = segments[^1];
            Check(Store.Config.NovaPoshtaConnections.Any(c => c.ApiKey == keyValue), "PDF used an unconfigured key.");
            Requests.Add(new("GET", keyValue, numbers));
            if (CancelOnGet is not null) { CancelOnGet.Cancel(); ct.ThrowIfCancellationRequested(); }
            if (Result == ResponseKind.RateLimited) return new((HttpStatusCode)429);
            if (Result == ResponseKind.ServerError) return new(HttpStatusCode.InternalServerError);
            var texts = _manualDiagnostic ? new[] { FirstTtn }
                : WrongFirstPdf ? new[] { OtherTtn, SecondTtn }
                : Result == ResponseKind.WrongNumber ? new[] { FirstTtn, OtherTtn }
                : Result == ResponseKind.UnknownNumber ? new[] { FirstTtn, "synthetic label without tracking identity" }
                : Result == ResponseKind.MissingSecond ? new[] { FirstTtn }
                : Result == ResponseKind.MultiplePlaces ? new[] { SecondTtn, FirstTtn, SecondTtn }
                : numbers.Reverse().ToArray();
            var pdf = SyntheticShippingPdf.Create(widthMm: Result == ResponseKind.UnsupportedSize ? 110 : 100,
                pages: texts.Length, pageTexts: texts);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(pdf) { Headers = { ContentType = new("application/pdf") } } };
        }
    }
    private static HttpResponseMessage List(params object[] rows) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { success = true, data = rows, errors = Array.Empty<string>() }), Encoding.UTF8, "application/json") };
    private static void NotApproved(NovaPoshtaVerifiedSession session, NovaPoshtaConnection connection, string ttn)
    {
        try { session.Require(connection, ttn); throw new Exception("Preparation silently recorded manual session approval."); }
        catch (ShippingLabelException ex) { Check(ex.Kind == LabelFailureKind.ConnectionRequired, "Session Require contract changed."); }
    }
    private static async Task<ShippingLabelException> Failure(Task operation)
    {
        try { await operation; throw new Exception("Invalid bulk label returned a complete or partial batch."); }
        catch (ShippingLabelException ex)
        {
            Check(!ex.ToString().Contains(Connection.ApiKey) && !ex.ToString().Contains(ForeignConnection.ApiKey), "Failure disclosed a key.");
            return ex;
        }
    }
    private static void OneBulkGet(Fixture fixture, string? key = null)
    {
        Check(fixture.Requests.Count == 1 && fixture.Requests[0].Method == "GET" &&
            fixture.Requests[0].Key == (key ?? Connection.ApiKey) && fixture.Requests[0].Ttns.SequenceEqual([FirstTtn, SecondTtn]),
            "Selected packet must issue exactly one bulk GET, without a list lookup or per-TTN request.");
    }

    private static async Task OneGetPerPacket()
    {
        using var fixture = new Fixture();
        for (var packet = 0; packet < 2; packet++)
        {
            fixture.Requests.Clear();
            var batch = await fixture.Prepare();
            Check(batch.Pages.Count == 2 && batch.SelectedOrderCount == 2, "The selected packet lost a label or order.");
            OneBulkGet(fixture);
            Check(fixture.Store.Saves == 0, "Preparation persisted operation-local PDF or metadata.");
            NotApproved(fixture.Session, Connection, FirstTtn); NotApproved(fixture.Session, Connection, SecondTtn);
        }
        fixture.Requests.Clear();
        var repeated = Enumerable.Range(0, 120).Select(i => Shipment(Order("NP-repeat-" + i,
            ShippingCarrier.NovaPoshta, i % 2 == 0 ? FirstTtn : SecondTtn))).ToArray();
        var scoped = ((IShippingLabelBatchSource)fixture.Source).CreateBatchSource(repeated);
        await scoped.GetAsync(Shipment(FirstOrder));
        await scoped.GetAsync(Shipment(SecondOrder));
        OneBulkGet(fixture);
    }
    private static async Task PageMapping()
    {
        using var fixture = new Fixture { Result = ResponseKind.MultiplePlaces };
        var scoped = ((IShippingLabelBatchSource)fixture.Source).CreateBatchSource([Shipment(FirstOrder), Shipment(SecondOrder)]);
        var first = await scoped.GetAsync(Shipment(FirstOrder));
        var second = await scoped.GetAsync(Shipment(SecondOrder));
        OneBulkGet(fixture);
        Check(first.SourcePageNumbers.SequenceEqual([2]) && second.SourcePageNumbers.SequenceEqual([1, 3]) &&
            first.Pdf.SequenceEqual(second.Pdf), "Source document lost the original bulk page indices or shared PDF.");
        var renderer = new WindowsShippingLabelRenderer();
        var firstPages = await renderer.RenderAsync(first, 203, 203);
        var secondPages = await renderer.RenderAsync(second, 203, 203);
        Check(firstPages.Select(p => p.PageNumber).SequenceEqual([1]) && secondPages.Select(p => p.PageNumber).SequenceEqual([1, 2]) &&
            firstPages.All(p => p.Shipment.TrackingNumber == FirstTtn) && secondPages.All(p => p.Shipment.TrackingNumber == SecondTtn),
            "Renderer did not keep all matching places with local per-TTN numbering.");
    }
    private static async Task TwoLabels()
    {
        foreach (var manuallyApproved in new[] { false, true })
        {
            using var fixture = new Fixture();
            if (manuallyApproved) await fixture.ApproveFirst();
            var batch = await fixture.Prepare();
            Check(batch.Pages.Select(p => p.Shipment.TrackingNumber).SequenceEqual([FirstTtn, SecondTtn]) &&
                batch.Pages.Select(p => p.DocumentId).SequenceEqual(["ttn:" + FirstTtn, "ttn:" + SecondTtn]),
                "Bulk labels lost selected order or incorrectly claimed verified document Refs.");
            OneBulkGet(fixture);
            Check(fixture.Store.Saves == 0, "Preparation changed saved settings.");
            if (manuallyApproved) Check(fixture.Session.Require(Connection, FirstTtn).DocumentRef == FirstRef, "Preparation rewrote manual evidence.");
            else NotApproved(fixture.Session, Connection, FirstTtn);
            NotApproved(fixture.Session, Connection, SecondTtn);
        }
    }
    private static async Task ChangedApprovedPdf()
    {
        using var fixture = new Fixture(); await fixture.ApproveFirst(); fixture.WrongFirstPdf = true;
        var error = await Failure(fixture.Prepare());
        Check(error.Kind == LabelFailureKind.InvalidDocument, "Manual approval allowed a wrong fresh bulk PDF.");
        OneBulkGet(fixture);
        Check(fixture.Session.Require(Connection, FirstTtn).DocumentRef == FirstRef, "Rejected bulk PDF changed prior manual proof.");
        NotApproved(fixture.Session, Connection, SecondTtn);
    }
    private static async Task InvalidPages()
    {
        foreach (var result in new[] { ResponseKind.WrongNumber, ResponseKind.UnknownNumber, ResponseKind.MissingSecond, ResponseKind.UnsupportedSize })
        {
            using var fixture = new Fixture { Result = result };
            var error = await Failure(fixture.Prepare());
            Check(error.Kind is LabelFailureKind.InvalidDocument or LabelFailureKind.UnsupportedFormat, "Invalid bulk page had an unrelated failure kind.");
            if (result == ResponseKind.MissingSecond) Check(error.Message.Contains(SecondTtn, StringComparison.Ordinal),
                "Missing selected TTN was not identified in the bulk packet error.");
            OneBulkGet(fixture);
            NotApproved(fixture.Session, Connection, FirstTtn); NotApproved(fixture.Session, Connection, SecondTtn);
        }
    }
    private static async Task ExpectedPlaces()
    {
        using var fixture = new Fixture { Result = ResponseKind.MultiplePlaces };
        var second = SecondOrder with { Shipments = [SecondOrder.Shipments[0] with { Places = 2 }] };
        var batch = await fixture.Prepare(default, FirstOrder, second);
        Check(batch.Pages.Select(p => p.Shipment.TrackingNumber).SequenceEqual([FirstTtn, SecondTtn, SecondTtn]),
            "Known multi-place marking pages were discarded or interleaved incorrectly.");
        OneBulkGet(fixture);
        fixture.Requests.Clear(); fixture.Result = ResponseKind.Valid;
        var error = await Failure(fixture.Prepare(default, FirstOrder, second));
        Check(error.Kind == LabelFailureKind.InvalidDocument, "Missing known expected place was accepted.");
        OneBulkGet(fixture);
        fixture.Requests.Clear(); fixture.Result = ResponseKind.MultiplePlaces;
        Check((await fixture.Prepare()).Pages.Count == 3, "Unknown expected place count dropped returned PDF pages.");
        OneBulkGet(fixture);
    }
    private static async Task Cancellation()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource(); fixture.CancelOnGet = cancellation;
        try { await fixture.Prepare(cancellation.Token); throw new Exception("Cancelled preparation returned a batch."); }
        catch (OperationCanceledException) { }
        OneBulkGet(fixture);
        NotApproved(fixture.Session, Connection, FirstTtn); NotApproved(fixture.Session, Connection, SecondTtn);
    }
    private static async Task TransportFailure()
    {
        foreach (var result in new[] { ResponseKind.RateLimited, ResponseKind.ServerError })
        {
            using var fixture = new Fixture { Result = result };
            var error = await Failure(fixture.Prepare());
            Check(error.Kind == LabelFailureKind.Temporary, "Transient bulk failure had an unrelated failure kind.");
            OneBulkGet(fixture);
            if (result == ResponseKind.RateLimited)
            {
                Check(error.RetryAtUtc is not null && fixture.Client.GetRetryDelay(Connection) > TimeSpan.Zero, "HTTP429 lost the shared cooldown.");
                await Failure(fixture.Source.GetAsync(Shipment(SecondOrder)));
                OneBulkGet(fixture);
            }
            NotApproved(fixture.Session, Connection, SecondTtn);
        }
    }
    private static async Task Rotation()
    {
        using var fixture = new Fixture(); await fixture.ApproveFirst();
        var rotated = Connection with { ApiKey = "synthetic_batch_rotated_key" };
        fixture.Store.Config = fixture.Store.Config with { NovaPoshtaConnections = [ForeignConnection, rotated] };
        Check((await fixture.Prepare()).Pages.Count == 2, "Rotated key lost the selected packet.");
        OneBulkGet(fixture, rotated.ApiKey);
        NotApproved(fixture.Session, rotated, FirstTtn); NotApproved(fixture.Session, rotated, SecondTtn);
        Check(fixture.Session.Require(Connection, FirstTtn).DocumentRef == FirstRef, "Key rotation rewrote old manual evidence.");
    }
    private static async Task BindingAndRef()
    {
        using var fixture = new Fixture();
        var missingShop = Shipment(FirstOrder) with { Order = FirstOrder.Key with { ConnectionId = "synthetic-unbound-prom" } };
        await Failure(fixture.Source.GetAsync(missingShop));
        Check(fixture.Requests.Count == 0, "Missing binding contacted another NP account.");
        var document = await fixture.Source.GetAsync(Shipment(SecondOrder) with { ShipmentId = FirstRef });
        Check(document.DocumentId == "ttn:" + SecondTtn && document.ConnectionId == Connection.Id &&
            fixture.Requests.Count == 1 && fixture.Requests[0].Method == "GET" && fixture.Requests[0].Ttns.SequenceEqual([SecondTtn]),
            "Single source read scanned lists, invented a verified Ref, or contacted another account.");
        NotApproved(fixture.Session, Connection, SecondTtn);
    }
    private static async Task ScopedAccountChanges()
    {
        foreach (var rotateKey in new[] { true, false })
        {
            using var fixture = new Fixture();
            var scoped = ((IShippingLabelBatchSource)fixture.Source).CreateBatchSource([Shipment(FirstOrder), Shipment(SecondOrder)]);
            await scoped.GetAsync(Shipment(FirstOrder));
            var changed = rotateKey ? Connection with { ApiKey = "synthetic_rotated_during_packet" }
                : Connection with { Id = "synthetic_rebound_during_packet" };
            fixture.Store.Config = fixture.Store.Config with
            {
                NovaPoshtaConnections = [ForeignConnection, changed],
                NovaPoshtaStoreBindings = [new(FirstOrder.Key.ConnectionId, changed.Id)]
            };
            var document = await scoped.GetAsync(Shipment(SecondOrder));
            Check(document.ConnectionId == changed.Id && fixture.Requests.Count == 2 && fixture.Requests.All(r => r.Method == "GET") &&
                fixture.Requests[1].Key == changed.ApiKey, "Packet download leaked across key change or explicit account rebinding.");
        }
        using var separate = new Fixture();
        var secondShop = SecondOrder with { Key = SecondOrder.Key with { ConnectionId = "synthetic-other-prom" } };
        var batch = await separate.Prepare(default, FirstOrder, secondShop);
        Check(batch.Pages.Select(p => p.ConnectionId).SequenceEqual([Connection.Id, ForeignConnection.Id]) && separate.Requests.Count == 2 &&
            separate.Requests.Select(r => r.Key).SequenceEqual([Connection.ApiKey, ForeignConnection.ApiKey]) &&
            separate.Requests.Select(r => r.Ttns.Single()).SequenceEqual([FirstTtn, SecondTtn]),
            "Selected different NP accounts were combined into one key or expanded to unselected accounts.");
    }
}
