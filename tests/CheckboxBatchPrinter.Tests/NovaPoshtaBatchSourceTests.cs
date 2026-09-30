using System.Globalization;
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
    private const string FirstTtn = "20400000000001", SecondTtn = "20400000000002";
    private const string FirstRef = "10000000-0000-0000-0000-000000000001", SecondRef = "10000000-0000-0000-0000-000000000002";
    private static readonly NovaPoshtaConnection Connection = new("synthetic-np-selected", "Synthetic selected NP", "synthetic_batch_np_key");
    private static readonly NovaPoshtaConnection ForeignConnection = new("synthetic-np-other", "Synthetic other NP", "synthetic_batch_other_key");
    private static MarketplaceOrder FirstOrder => Order("NP-first", ShippingCarrier.NovaPoshta, FirstTtn);
    private static MarketplaceOrder SecondOrder => Order("NP-second", ShippingCarrier.NovaPoshta, SecondTtn);
    private static ShipmentReference Shipment(MarketplaceOrder order) => ShippingCarrierNames.ForOrder(order).Single();
    private static NovaPoshtaSearchPeriod RecentPeriod
    {
        get
        {
            var today = DateOnly.FromDateTime(DateRangeBuilder.TodayKyiv);
            return new(today.AddDays(-6), today);
        }
    }

    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("STA direct NP packet shares one account scan and discards metadata before the next packet", () => MarketplaceViewModelTests.StaAsync(OneScanPerPacket)),
        ("STA direct NP shared scan still checks the second TTN deletion identity and Ref", () => MarketplaceViewModelTests.StaAsync(SharedLookupFailures)),
        ("STA direct NP packet lookup is isolated after key rotation or account rebinding", () => MarketplaceViewModelTests.StaAsync(ScopedAccountChanges)),
        ("STA direct NP batch prepares two selected Prom TTNs when only the first was manually approved", () => MarketplaceViewModelTests.StaAsync(TwoLabels)),
        ("STA direct NP existing manual approval still rejects a changed fresh PDF before the second lookup", () => MarketplaceViewModelTests.StaAsync(ChangedApprovedPdf)),
        ("STA direct NP automatic preparation rejects wrong unknown multi-place and unsupported second labels", () => MarketplaceViewModelTests.StaAsync(InvalidSecond)),
        ("STA direct NP missing or incomplete second lookup cannot request its PDF or return a partial batch", () => MarketplaceViewModelTests.StaAsync(UnprovenLookup)),
        ("STA direct NP cancellation during the second lookup prevents its marking request", () => MarketplaceViewModelTests.StaAsync(Cancellation)),
        ("STA direct NP second lookup HTTP429 stops the batch and preserves shared cooldown without PDF retry", () => MarketplaceViewModelTests.StaAsync(RateLimit)),
        ("STA direct NP key rotation requires fresh bounded lookup and never silently approves a new session", () => MarketplaceViewModelTests.StaAsync(Rotation)),
        ("STA direct NP fresh preparation retains exact shop binding and shipment Ref guards", () => MarketplaceViewModelTests.StaAsync(BindingAndRef))
    ];

    private enum SecondResult { Valid, WrongNumber, UnknownNumber, MultiplePlaces, UnknownPlaces, UnsupportedSize, Missing, Incomplete, RateLimited, Deleted, UnknownDeletion, ConflictingRef }
    private sealed record Request(string Method, string Key, int Page = 0, string Ttn = "");
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => response(request, ct);
    }
    private sealed class Store : IShippingSettingsStore
    {
        public ShippingLabelSettings Config = new()
        {
            Print = Settings,
            NovaPoshtaConnections = [ForeignConnection, Connection],
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
        public readonly Store Store = new();
        public readonly List<Request> Requests = [];
        public readonly NovaPoshtaVerifiedSession Session = new();
        public readonly NovaPoshtaReadOnlyClient Client;
        public readonly WindowsLabelPdfInspector Inspector = new();
        public readonly IShippingLabelSource Source;
        public SecondResult Result;
        public bool WrongFirstPdf;
        public CancellationTokenSource? CancelOnSecondPage;
        public NovaPoshtaConnection Selected => Store.Config.NovaPoshtaConnections.Single(c => c.Id ==
            Store.Config.NovaPoshtaStoreBindings.Single(b => b.MarketplaceConnectionId == FirstOrder.Key.ConnectionId).NovaPoshtaConnectionId);

        public Fixture()
        {
            _http = new(new Handler(Respond));
            Client = new(NovaPoshtaRequestPolicyTests.FastTransport(_http));
            Source = new ConfiguredShippingLabelSource(Store, new NovaPoshtaDirectLabelSource(Store, Client, Inspector, Session), new NeverSource());
            Check(Requests.Count == 0, "Constructing the source started NP requests.");
        }
        public void Dispose() => _http.Dispose();
        public async Task ApproveFirst()
        {
            var result = await new NovaPoshtaDiagnostics(Client, Inspector).CheckAsync(Selected, Shipment(FirstOrder), RecentPeriod);
            Session.Accept(Selected, result);
            Requests.Clear();
        }
        public Task<LabelPrintBatch> Prepare(CancellationToken ct = default) =>
            new LabelBatchPreparation(Source, new WindowsShippingLabelRenderer()).PrepareAsync([FirstOrder, SecondOrder], Settings, Geometry, ct);

        private async Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri ?? throw new Exception("NP request has no URI.");
            if (request.Method == HttpMethod.Post)
            {
                Check(uri.AbsoluteUri == NovaPoshtaReadOnlyContract.EntryPoint, "Wrong NP lookup endpoint.");
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var root = body.RootElement; var props = root.GetProperty("methodProperties");
                var key = root.GetProperty("apiKey").GetString()!;
                Check(key == Selected.ApiKey && key != ForeignConnection.ApiKey, "Lookup used another shop/account key.");
                var period = RecentPeriod;
                Check(props.GetProperty("DateTimeFrom").GetString() == period.From.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) &&
                    props.GetProperty("DateTimeTo").GetString() == period.To.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) &&
                    !props.GetProperty("GetFullList").GetBoolean(), "Preparation did not use the bounded recent seven Kyiv days.");
                var page = int.Parse(props.GetProperty("Page").GetString()!, CultureInfo.InvariantCulture);
                Requests.Add(new("POST", key, page));
                if (page == 2 && CancelOnSecondPage is not null)
                {
                    CancelOnSecondPage.Cancel();
                    ct.ThrowIfCancellationRequested();
                }
                if (page == 2 && Result == SecondResult.RateLimited) return new((HttpStatusCode)429);
                if (page == 2 && Result != SecondResult.Incomplete) return List();
                if (Result == SecondResult.ConflictingRef) return List(Row(FirstTtn, FirstRef), Row(SecondTtn, SecondRef), Row(SecondTtn, FirstRef));
                return Result == SecondResult.Missing ? List(Row(FirstTtn, FirstRef))
                    : List(Row(FirstTtn, FirstRef), Row(SecondTtn, SecondRef,
                        Result == SecondResult.MultiplePlaces ? 2 : Result == SecondResult.UnknownPlaces ? null : 1,
                        Result == SecondResult.Deleted ? "1" : Result == SecondResult.UnknownDeletion ? "unexpected" : "0"));
            }
            Check(request.Method == HttpMethod.Get && uri.Host == "my.novaposhta.ua", "Unexpected NP request.");
            var path = uri.AbsolutePath;
            var ttn = path.Split('/', StringSplitOptions.RemoveEmptyEntries)[3];
            Check(ttn is FirstTtn or SecondTtn, "Marking request used an unexpected TTN.");
            var pdfKey = path.Split('/')[^1];
            Check(pdfKey == Selected.ApiKey && pdfKey != ForeignConnection.ApiKey, "PDF used another shop/account key.");
            Requests.Add(new("GET", pdfKey, Ttn: ttn));
            var second = ttn == SecondTtn;
            var texts = !second && WrongFirstPdf ? new[] { SecondTtn }
                : second && Result == SecondResult.WrongNumber ? new[] { FirstTtn }
                : second && Result == SecondResult.UnknownNumber ? new[] { "synthetic label without tracking identity" }
                : second && Result == SecondResult.MultiplePlaces ? new[] { SecondTtn, SecondTtn } : new[] { ttn };
            var pdf = SyntheticShippingPdf.Create(widthMm: second && Result == SecondResult.UnsupportedSize ? 110 : 100,
                pages: texts.Length, pageTexts: texts);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(pdf) { Headers = { ContentType = new("application/pdf") } } };
        }
    }
    private static object Row(string ttn, string reference, int? places = 1, string deletion = "0") =>
        new { IntDocNumber = ttn, Ref = reference, SeatsAmount = places, DeletionMark = deletion };
    private static HttpResponseMessage List(params object[] rows) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { success = true, data = rows, errors = Array.Empty<string>() }), Encoding.UTF8, "application/json") };
    private static void NotApproved(NovaPoshtaVerifiedSession session, NovaPoshtaConnection connection, string ttn)
    {
        try { session.Require(connection, ttn); throw new Exception("Preparation silently recorded manual session approval."); }
        catch (ShippingLabelException ex) { Check(ex.Kind == LabelFailureKind.ConnectionRequired, "Session Require contract changed."); }
    }
    private static async Task<ShippingLabelException> Failure(Task operation)
    {
        try { await operation; throw new Exception("Invalid second label returned a complete/partial batch."); }
        catch (ShippingLabelException ex)
        {
            Check(!ex.ToString().Contains(Connection.ApiKey) && !ex.ToString().Contains(ForeignConnection.ApiKey), "Failure disclosed a key.");
            return ex;
        }
    }

    private static async Task TwoLabels()
    {
        using var fixture = new Fixture();
        await fixture.ApproveFirst();
        var batch = await fixture.Prepare();
        Check(batch.SelectedOrderCount == 2 && batch.Pages.Select(p => p.Shipment.TrackingNumber).SequenceEqual([FirstTtn, SecondTtn]) &&
            batch.Pages.Select(p => p.DocumentId).SequenceEqual([FirstRef, SecondRef]), "Two selected labels lost order/identity.");
        Check(fixture.Requests.Select(r => (r.Method, r.Page, r.Ttn)).SequenceEqual(
            [("GET", 0, FirstTtn), ("POST", 1, ""), ("POST", 2, ""), ("GET", 0, SecondTtn)]),
            "Existing evidence was rescanned or a fresh PDF was requested before complete lookup.");
        Check(fixture.Session.Require(Connection, FirstTtn).DocumentRef == FirstRef && fixture.Store.Saves == 0, "Existing manual approval/settings changed.");
        NotApproved(fixture.Session, Connection, SecondTtn);
        fixture.Requests.Clear();
        await fixture.Source.GetAsync(Shipment(SecondOrder));
        Check(fixture.Requests.Count == 3 && fixture.Requests[0].Method == "POST", "Fresh verification was cached as manual approval.");
        NotApproved(fixture.Session, Connection, SecondTtn);
    }
    private static async Task InvalidSecond()
    {
        foreach (var result in new[] { SecondResult.WrongNumber, SecondResult.UnknownNumber, SecondResult.MultiplePlaces, SecondResult.UnknownPlaces, SecondResult.UnsupportedSize })
        {
            using var fixture = new Fixture(); await fixture.ApproveFirst(); fixture.Result = result;
            var error = await Failure(fixture.Prepare());
            Check(error.Kind == LabelFailureKind.InvalidDocument && fixture.Requests.Count(r => r.Method == "GET" && r.Ttn == SecondTtn) == 1,
                "Unsafe second document passed or its PDF was retried.");
            NotApproved(fixture.Session, Connection, SecondTtn);
        }
    }
    private static async Task ChangedApprovedPdf()
    {
        using var fixture = new Fixture(); await fixture.ApproveFirst(); fixture.WrongFirstPdf = true;
        var error = await Failure(fixture.Prepare());
        Check(error.Kind == LabelFailureKind.InvalidDocument && fixture.Requests.Count == 1 && fixture.Requests[0].Method == "GET" &&
            fixture.Requests[0].Ttn == FirstTtn, "Manual approval bypassed the fresh PDF recheck or allowed a second lookup.");
        NotApproved(fixture.Session, Connection, SecondTtn);
    }
    private static async Task UnprovenLookup()
    {
        foreach (var result in new[] { SecondResult.Missing, SecondResult.Incomplete })
        {
            using var fixture = new Fixture(); await fixture.ApproveFirst(); fixture.Result = result;
            await Failure(fixture.Prepare());
            Check(fixture.Requests.Count(r => r.Method == "POST") == 2 && fixture.Requests.All(r => r.Method != "GET" || r.Ttn == FirstTtn),
                "Missing/incomplete second lookup requested a PDF or continued scanning.");
            NotApproved(fixture.Session, Connection, SecondTtn);
        }
    }
    private static async Task Cancellation()
    {
        using var fixture = new Fixture(); await fixture.ApproveFirst();
        using var cancellation = new CancellationTokenSource(); fixture.CancelOnSecondPage = cancellation;
        try { await fixture.Prepare(cancellation.Token); throw new Exception("Cancelled preparation returned a batch."); }
        catch (OperationCanceledException) { }
        Check(fixture.Requests.Count == 3 && fixture.Requests.All(r => r.Method != "GET" || r.Ttn == FirstTtn), "Cancellation requested another PDF.");
        NotApproved(fixture.Session, Connection, SecondTtn);
    }
    private static async Task RateLimit()
    {
        using var fixture = new Fixture(); await fixture.ApproveFirst(); fixture.Result = SecondResult.RateLimited;
        var error = await Failure(fixture.Prepare());
        Check(error.Kind == LabelFailureKind.Temporary && error.RetryAtUtc is not null && fixture.Client.GetRetryDelay(Connection) > TimeSpan.Zero &&
            fixture.Requests.Count == 3 && fixture.Requests.All(r => r.Method != "GET" || r.Ttn == FirstTtn), "HTTP429 retried, lost cooldown or requested second PDF.");
        await Failure(fixture.Source.GetAsync(Shipment(SecondOrder)));
        Check(fixture.Requests.Count == 3, "A new caller bypassed the shared NP cooldown.");
        NotApproved(fixture.Session, Connection, SecondTtn);
    }
    private static async Task Rotation()
    {
        using var fixture = new Fixture(); await fixture.ApproveFirst();
        var rotated = Connection with { ApiKey = "synthetic_batch_rotated_key" };
        fixture.Store.Config = fixture.Store.Config with { NovaPoshtaConnections = [ForeignConnection, rotated] };
        var batch = await fixture.Prepare();
        Check(batch.Pages.Count == 2 && fixture.Requests.Count == 4 && fixture.Requests[0].Method == "POST" &&
            fixture.Requests.All(r => r.Key == rotated.ApiKey), "Rotated key reused old session evidence or another account.");
        NotApproved(fixture.Session, rotated, FirstTtn); NotApproved(fixture.Session, rotated, SecondTtn);
        Check(fixture.Session.Require(Connection, FirstTtn).DocumentRef == FirstRef, "Key rotation rewrote the prior manual approval.");
    }
    private static async Task BindingAndRef()
    {
        using var fixture = new Fixture();
        var missingShop = Shipment(FirstOrder) with { Order = FirstOrder.Key with { ConnectionId = "synthetic-unbound-prom" } };
        await Failure(fixture.Source.GetAsync(missingShop));
        Check(fixture.Requests.Count == 0, "Missing binding contacted another NP account.");
        var badRef = Shipment(SecondOrder) with { ShipmentId = FirstRef };
        var error = await Failure(fixture.Source.GetAsync(badRef));
        Check(error.Kind == LabelFailureKind.InvalidDocument && fixture.Requests.Count == 1 && fixture.Requests.All(r => r.Method == "POST"),
            "Fresh verification dropped the shipment Ref guard or requested marking.");
        NotApproved(fixture.Session, Connection, SecondTtn);
    }

    private static async Task OneScanPerPacket()
    {
        using var fixture = new Fixture();
        for (var packet = 0; packet < 2; packet++)
        {
            fixture.Requests.Clear();
            var batch = await fixture.Prepare();
            Check(batch.Pages.Count == 2 && fixture.Requests.Select(r => (r.Method, r.Page, r.Ttn)).SequenceEqual(
                [("POST", 1, ""), ("POST", 2, ""), ("GET", 0, FirstTtn), ("GET", 0, SecondTtn)]),
                "A packet rescanned one account for each TTN or reused a previous packet's evidence.");
            Check(fixture.Store.Saves == 0, "Preparation persisted lookup metadata.");
            NotApproved(fixture.Session, Connection, FirstTtn); NotApproved(fixture.Session, Connection, SecondTtn);
        }
    }

    private static async Task SharedLookupFailures()
    {
        foreach (var result in new[] { SecondResult.Missing, SecondResult.Deleted, SecondResult.UnknownDeletion, SecondResult.ConflictingRef })
        {
            using var fixture = new Fixture { Result = result };
            var error = await Failure(fixture.Prepare());
            Check(error.Message.Contains(SecondTtn) && fixture.Requests.Count(r => r.Method == "POST") == 2 &&
                fixture.Requests.Count(r => r.Method == "GET") == 1 && fixture.Requests.All(r => r.Method != "GET" || r.Ttn == FirstTtn),
                "Shared metadata bypassed a second-document failure or repeated the account scan.");
        }
        using var refFixture = new Fixture();
        var scoped = ((IShippingLabelBatchSource)refFixture.Source).CreateBatchSource();
        await scoped.GetAsync(Shipment(FirstOrder));
        var refError = await Failure(scoped.GetAsync(Shipment(SecondOrder) with { ShipmentId = FirstRef }));
        Check(refError.Kind == LabelFailureKind.InvalidDocument && refFixture.Requests.Count == 3,
            "A shared scan bypassed the selected second shipment's Ref.");
    }

    private static async Task ScopedAccountChanges()
    {
        foreach (var rotateKey in new[] { true, false })
        {
            using var fixture = new Fixture();
            var scoped = ((IShippingLabelBatchSource)fixture.Source).CreateBatchSource();
            await scoped.GetAsync(Shipment(FirstOrder));
            var changed = rotateKey ? Connection with { ApiKey = "synthetic_rotated_during_packet" }
                : Connection with { Id = "synthetic_rebound_during_packet" };
            fixture.Store.Config = fixture.Store.Config with
            {
                NovaPoshtaConnections = [ForeignConnection, changed],
                NovaPoshtaStoreBindings = [new(FirstOrder.Key.ConnectionId, changed.Id)]
            };
            var document = await scoped.GetAsync(Shipment(SecondOrder));
            Check(document.ConnectionId == changed.Id && fixture.Requests.Count == 6 &&
                fixture.Requests.Count(r => r.Method == "POST") == 4 && fixture.Requests.Skip(3).All(r => r.Key == changed.ApiKey),
                "Packet metadata leaked across a key change or explicit account rebinding.");
        }
    }
}
