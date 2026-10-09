using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;
using static CheckboxBatchPrinter.Tests.ShippingLabelsTests;

namespace CheckboxBatchPrinter.Tests;

internal static class WaybillLabelPreparationTests
{
    private const string Key = "synthetic_independent_np_key", Token = "synthetic_independent_rd_token";
    private const string First = "20400000000001", Second = "20400000000002";
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("independent waybill print keeps mixed carrier documents, all places and actual order captions", Mixed),
        ("independent NP waybill print uses exact accounts and does not deduplicate pages across accounts", Accounts),
        ("independent waybill print deduplicates one document and preserves original bulk PDF page mapping", Deduplication),
        ("independent waybill source proof rejects old, switched, removed or unconfigured accounts before HTTP", StaleSelection),
        ("independent waybill print rechecks credential rotation immediately before transfer", Revalidation),
        ("independent waybill print rejects selected and freshly deleted RD documents without label download", Deleted),
        ("independent waybill print rejects mismatched RD IDs, place counts and unreadable documents", DeliveryErrors),
        ("independent waybill print rejects unselected NP PDF pages, missing places and unsupported formats", NovaPoshtaErrors),
        ("independent waybill print enforces document, page, layout and decoded raster limits", Limits),
        ("independent waybill cancellation stops preparation before and during carrier HTTP", Cancellation),
        ("independent waybill print rejects forged renderer shipment and page metadata", RendererErrors),
        ("live waybill enumeration supplies account proof for both carriers without secrets", SourceProof)
    ];
    private sealed class Store(ShippingLabelSettings config) : IShippingSettingsStore
    {
        public ShippingLabelSettings Config = config;
        public Task<ShippingLabelSettings> LoadAsync(CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(Config); }
        public Task SaveAsync(ShippingLabelSettings _, CancellationToken ct = default) => throw new Exception("Preparation changed settings.");
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> read) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return read(request, ct); }
    }
    private sealed class Inspector : ILabelPdfInspector
    {
        private readonly Dictionary<string, LabelPdfInspection> _fixtures = new(StringComparer.Ordinal);
        public byte[] Pdf(params string[] texts)
        {
            var pdf = SyntheticShippingPdf.Create(pages: texts.Length, pageTexts: texts);
            _fixtures[Hash(pdf)] = new(texts.Select((text, index) => new LabelPdfPageInfo(index + 1, 100, 100, text)).ToArray());
            return pdf;
        }
        public void Change(byte[] pdf, LabelPdfInspection inspection) => _fixtures[Hash(pdf)] = inspection;
        public Task<LabelPdfInspection> InspectAsync(byte[] pdf, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(_fixtures[Hash(pdf)]); }
    }
    private sealed class Renderer : IShippingLabelRenderer
    {
        public readonly List<ShippingLabelDocument> Documents = [];
        public Func<ShippingLabelDocument, IReadOnlyList<ShippingLabelPage>>? Override;
        public Task<IReadOnlyList<ShippingLabelPage>> RenderAsync(ShippingLabelDocument document, double dpiX, double dpiY, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Documents.Add(document);
            return Task.FromResult(Override?.Invoke(document) ?? Pages(document));
        }
        public static IReadOnlyList<ShippingLabelPage> Pages(ShippingLabelDocument document) => document.SourcePageNumbers
            .Select((sourcePage, index) => new ShippingLabelPage(document.Shipment, document.DocumentId, document.Fingerprint,
                index + 1, 100, 100, [(byte)sourcePage]) { ConnectionId = document.ConnectionId, Source = document.Source }).ToArray();
    }
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private static ShippingLabelSettings Config => new()
    { Print = Settings, NovaPoshtaConnections = [new("np", "NP", Key)], RozetkaDeliveryConnectionId = "rd", RozetkaDeliveryToken = Token };
    private static WaybillRecord Np(string number = First, string connection = "np", string key = Key, int places = 1) =>
        new(ShippingCarrier.NovaPoshta, connection, "NP", "ttn:" + number, number, new(2026, 10, 9), "", "", "", null, places)
        { CredentialFingerprint = WaybillAccountProof.Create(ShippingCarrier.NovaPoshta, connection, key) };
    private static WaybillRecord Rd(string id = "rd-001", int places = 1) =>
        new(ShippingCarrier.RozetkaDelivery, "rd", "RD", id, id, new(2026, 10, 9), "", "", "", null, places)
        { CredentialFingerprint = WaybillAccountProof.Create(ShippingCarrier.RozetkaDelivery, "rd", Token) };
    private static WaybillPrintRequest Request(WaybillRecord row, params string[] orders) => new(row, orders);
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Pdf(byte[] pdf) => new(HttpStatusCode.OK)
    { Content = new ByteArrayContent(pdf) { Headers = { ContentType = new("application/pdf") } } };
    private static WaybillLabelPreparation Service(Store store, HttpClient client, Inspector inspector, Renderer renderer)
    {
        var transport = NovaPoshtaRequestPolicyTests.FastTransport(client);
        return new(store, transport, new(transport), inspector, renderer);
    }
    private static async Task<ShippingLabelException> Failure(Task operation, LabelFailureKind? kind = null)
    {
        try { await operation; throw new Exception("Invalid independent waybill allowed."); }
        catch (ShippingLabelException error)
        {
            Check(kind is null || error.Kind == kind, "Waybill error misclassified.");
            Check(!error.ToString().Contains(Key) && !error.ToString().Contains(Token) && !error.ToString().Contains("https://"), "Waybill error exposed credentials.");
            return error;
        }
    }
    private static async Task Mixed()
    {
        var store = new Store(Config); var inspector = new Inspector(); var renderer = new Renderer();
        var npPdf = inspector.Pdf(First + " PLACE 1", Second + " PLACE 1", First + " PLACE 2");
        var rdPdf = inspector.Pdf("RD PLACE 1", "RD PLACE 2");
        var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "my.novaposhta.ua")
            {
                Check(request.RequestUri.AbsolutePath.Contains("orders/" + First + "," + Second + "/") && request.Headers.Authorization is null, "Wrong NP bulk scope.");
                return Task.FromResult(Pdf(npPdf));
            }
            Check(request.Headers.Authorization?.Parameter == Token, "RD borrowed a marketplace token.");
            return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("/label") ?
                Json(new { statusCode = 0, data = new { label = Convert.ToBase64String(rdPdf) } }) :
                Json(new { statusCode = 0, data = new { id = "rd-001", places = 2, is_deleted = false } }));
        });
        using var client = new HttpClient(handler); var service = Service(store, client, inspector, renderer);
        var prepared = await service.PrepareAsync([Request(Np(places: 2), "P-001"), Request(Np(Second)), Request(Rd(places: 2), "R-002")], Settings, Geometry);
        Check(prepared.Batch.IsWaybillBatch && prepared.Batch.SelectedWaybillCount == 3 && prepared.Batch.SelectedOrderCount == 0 &&
            prepared.Batch.Pages.Count == 5 && handler.Calls == 3, "Carrier batch lost documents or extra places.");
        Check(prepared.Batch.Pages.All(p => p.Shipment.Order is null) &&
            prepared.Batch.Pages[0].Shipment.OrderNumber == "P-001" && prepared.Batch.Pages[^1].Shipment.OrderNumber == "R-002", "An order was synthesized or linked caption lost.");
        Check(renderer.Documents[0].SourcePageNumbers.SequenceEqual([1, 3]) && renderer.Documents[1].SourcePageNumbers.SequenceEqual([2]), "Bulk source pages were reordered or truncated.");
        await service.ValidateAsync(prepared);
    }
    private static async Task Accounts()
    {
        var store = new Store(Config with { NovaPoshtaConnections = [new("np", "NP", Key), new("other", "Other NP", "synthetic_other_key")] });
        var inspector = new Inspector(); var renderer = new Renderer(); var pdf = inspector.Pdf(First); var keys = new List<string>();
        var handler = new Handler((request, _) => { keys.Add(request.RequestUri!.AbsolutePath.Split('/')[^1]); return Task.FromResult(Pdf(pdf)); });
        using var client = new HttpClient(handler);
        var prepared = await Service(store, client, inspector, renderer).PrepareAsync([Request(Np()), Request(Np(connection: "other", key: "synthetic_other_key"))], Settings, Geometry);
        Check(keys.SequenceEqual([Key, "synthetic_other_key"]) && prepared.Batch.Pages.Count == 2 && prepared.Batch.Pages.Select(p => p.ConnectionId).Distinct().Count() == 2,
            "Print searched accounts or deduplicated another account's page.");
    }
    private static async Task Deduplication()
    {
        var inspector = new Inspector(); var renderer = new Renderer(); var pdf = inspector.Pdf(First, Second, First);
        var handler = new Handler((_, _) => Task.FromResult(Pdf(pdf))); using var client = new HttpClient(handler);
        var result = await Service(new(Config), client, inspector, renderer).PrepareAsync(
            [Request(Np(places: 2), "A"), Request(Np(places: 2), "B"), Request(Np(Second))], Settings, Geometry);
        Check(handler.Calls == 1 && result.Batch.SelectedWaybillCount == 2 && result.Batch.Pages.Count == 3 &&
            result.Batch.Pages[0].Shipment.OrderNumber == "A, B" && result.Batch.Pages.Select(p => p.PageNumber).SequenceEqual([1, 2, 1]), "Duplicate document printed or linked captions disappeared.");
        Check(renderer.Documents[0].SourcePageNumbers.SequenceEqual([1, 3]) && renderer.Documents[0].Pdf.SequenceEqual(pdf), "Original page mapping lost.");
        await Failure(Service(new(Config), client, inspector, renderer).PrepareAsync(
            [Request(Np()), Request(Np() with { DocumentId = "10000000-0000-0000-0000-000000000001" })], Settings, Geometry), LabelFailureKind.InvalidDocument);
        Check(handler.Calls == 1, "Conflicting carrier document references reached HTTP.");
    }
    private static async Task StaleSelection()
    {
        foreach (var change in new[] { "missing-proof", "rotated", "removed", "blank-key", "wrong-account" })
        {
            var store = new Store(Config); var row = Np();
            if (change == "missing-proof") row = row with { CredentialFingerprint = "" };
            if (change == "rotated") store.Config = store.Config with { NovaPoshtaConnections = [new("np", "NP", "new_secret")] };
            if (change == "removed") store.Config = store.Config with { NovaPoshtaConnections = [] };
            if (change == "blank-key") store.Config = store.Config with { NovaPoshtaConnections = [new("np", "NP", "")] };
            if (change == "wrong-account") row = row with { ConnectionId = "other" };
            var handler = new Handler((_, _) => throw new Exception("Stale selection reached HTTP.")); using var client = new HttpClient(handler);
            await Failure(Service(store, client, new(), new()).PrepareAsync([Request(row)], Settings, Geometry), LabelFailureKind.ConnectionRequired);
            Check(handler.Calls == 0, "Stale account was queried.");
        }
    }
    private static async Task Revalidation()
    {
        var store = new Store(Config); var inspector = new Inspector(); var pdf = inspector.Pdf(First);
        var handler = new Handler((_, _) => Task.FromResult(Pdf(pdf))); using var client = new HttpClient(handler);
        var service = Service(store, client, inspector, new()); var prepared = await service.PrepareAsync([Request(Np())], Settings, Geometry);
        store.Config = store.Config with { NovaPoshtaConnections = [new("np", "NP", "rotated_key")] };
        await Failure(service.ValidateAsync(prepared), LabelFailureKind.ConnectionRequired);
        Check(handler.Calls == 1, "Credential revalidation read another carrier PDF.");
    }
    private static async Task Deleted()
    {
        foreach (var stale in new[] { false, true })
        {
            var handler = new Handler((_, _) => Task.FromResult(Json(new { statusCode = 0, data = new { id = "rd-001", places = 1, is_deleted = true } })));
            using var client = new HttpClient(handler);
            await Failure(Service(new(Config), client, new(), new()).PrepareAsync([Request(Rd() with { IsDeleted = stale })], Settings, Geometry), LabelFailureKind.MissingDocument);
            Check(handler.Calls == (stale ? 0 : 1), "Deleted document label was downloaded.");
        }
    }
    private static async Task DeliveryErrors()
    {
        foreach (var error in new[] { "id", "places", "invalid-pdf", "missing-pages", "deletion-format", "denied" })
        {
            var inspector = new Inspector(); var pdf = inspector.Pdf("RD ONE");
            var handler = new Handler((request, _) => Task.FromResult(error == "denied" ? new(HttpStatusCode.Forbidden) :
                request.RequestUri!.AbsolutePath.EndsWith("/label") ? Json(new { statusCode = 0, data = new { label = Convert.ToBase64String(error == "invalid-pdf" ? "html-error"u8.ToArray() : pdf) } }) :
                Json(new { statusCode = 0, data = new { id = error == "id" ? "other" : "rd-001", places = error is "places" or "missing-pages" ? 2 : 1,
                    is_deleted = error == "deletion-format" ? "unknown" : "0" } })));
            using var client = new HttpClient(handler);
            await Failure(Service(new(Config), client, inspector, new()).PrepareAsync([Request(Rd(places: error == "missing-pages" ? 2 : 1))], Settings, Geometry));
            Check(handler.Calls <= 2, "RD error triggered an account search or alternative route.");
        }
    }
    private static async Task NovaPoshtaErrors()
    {
        foreach (var error in new[] { "other-ttn", "missing-place", "format" })
        {
            var inspector = new Inspector(); var pdf = inspector.Pdf(error == "other-ttn" ? Second : First);
            if (error == "format") inspector.Change(pdf, new([new(1, 210, 297, First)]));
            var handler = new Handler((_, _) => Task.FromResult(Pdf(pdf))); using var client = new HttpClient(handler); var renderer = new Renderer();
            await Failure(Service(new(Config), client, inspector, renderer).PrepareAsync([Request(Np(places: error == "missing-place" ? 2 : 1))], Settings, Geometry), LabelFailureKind.InvalidDocument);
            Check(handler.Calls == 1 && renderer.Documents.Count == 0, "Invalid NP response was rendered or searched individually.");
        }
    }
    private static async Task Limits()
    {
        var inspector = new Inspector(); var pdf = inspector.Pdf(First); var handler = new Handler((_, _) => Task.FromResult(Pdf(pdf))); using var client = new HttpClient(handler);
        var service = Service(new(Config), client, inspector, new());
        foreach (var count in new[] { 0, 101 })
        {
            try { await service.PrepareAsync(Enumerable.Repeat(Request(Np()), count).ToArray(), Settings, Geometry); throw new Exception("Document bound bypassed."); }
            catch (InvalidOperationException) { }
        }
        Check(handler.Calls == 0, "Out-of-bound selection sent HTTP.");
        foreach (var limit in new[] { "pages", "decoded", "bytes", "layout" })
        {
            var boundInspector = new Inspector();
            var places = limit == "pages" ? 100 : limit == "decoded" ? 4 : limit == "bytes" ? 2 : 1;
            var documentPdf = boundInspector.Pdf(Enumerable.Range(1, places).Select(n => "PLACE " + n).ToArray());
            var largePng = limit == "bytes" ? new byte[41 * 1024 * 1024] : [1];
            var boundRenderer = new Renderer { Override = document => Renderer.Pages(document).Select(page => page with
                { WidthMm = limit == "layout" ? 210 : 100, Png = largePng }).ToArray() };
            var boundHandler = new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/label") ?
                Json(new { statusCode = 0, data = new { label = Convert.ToBase64String(documentPdf) } }) :
                Json(new { statusCode = 0, data = new { id = request.RequestUri.AbsolutePath.Split('/')[^1], places } })));
            using var boundClient = new HttpClient(boundHandler);
            var selection = Enumerable.Range(1, limit == "pages" ? 3 : 1).Select(n => Request(Rd("bound-" + n, places))).ToArray();
            try
            {
                await Service(new(Config), boundClient, boundInspector, boundRenderer).PrepareAsync(selection, Settings,
                    Geometry with { DpiX = limit == "decoded" ? 1200 : 96, DpiY = limit == "decoded" ? 1200 : 96 });
                throw new Exception("Raster/layout limit bypassed.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or ShippingLabelException)
            {
                Check(limit == "layout" ? ex is ShippingLabelException { Kind: LabelFailureKind.UnsupportedFormat } :
                    ex.Message.Contains(limit == "decoded" ? "256" : limit == "bytes" ? "80" : "250"), "Expected packet bound was not reached.");
            }
        }
    }
    private static async Task Cancellation()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var handler = new Handler((_, _) => throw new Exception("Cancelled preparation reached HTTP.")); using var client = new HttpClient(handler);
        try { await Service(new(Config), client, new(), new()).PrepareAsync([Request(Np())], Settings, Geometry, stop.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        using var during = new CancellationTokenSource();
        var active = new Handler((_, ct) => { during.Cancel(); return Task.FromCanceled<HttpResponseMessage>(ct); }); using var activeClient = new HttpClient(active); var renderer = new Renderer();
        try { await Service(new(Config), activeClient, new(), renderer).PrepareAsync([Request(Np())], Settings, Geometry, during.Token); throw new Exception("Active cancellation ignored."); }
        catch (OperationCanceledException) { }
        Check(handler.Calls == 0 && active.Calls == 1 && renderer.Documents.Count == 0, "Cancellation started rendering or repeated requests.");
    }
    private static async Task RendererErrors()
    {
        var inspector = new Inspector(); var pdf = inspector.Pdf(First); var handler = new Handler((_, _) => Task.FromResult(Pdf(pdf))); using var client = new HttpClient(handler);
        foreach (var error in new[] { "shipment", "account", "document", "hash", "zero-page" })
        {
            var renderer = new Renderer { Override = document => Renderer.Pages(document).Select(page => error switch
            {
                "shipment" => page with { Shipment = page.Shipment with { TrackingNumber = Second } },
                "account" => page with { ConnectionId = "other" },
                "document" => page with { DocumentId = "other" },
                "hash" => page with { Fingerprint = "other" },
                _ => page with { PageNumber = 0 }
            }).ToArray() };
            await Failure(Service(new(Config), client, inspector, renderer).PrepareAsync([Request(Np())], Settings, Geometry), LabelFailureKind.InvalidDocument);
        }
    }
    private static async Task SourceProof()
    {
        var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "api.novaposhta.ua" ?
            Json(new { success = true, errors = Array.Empty<string>(), info = new { totalCount = 1 }, data = new[] { new { IntDocNumber = First,
                Ref = "10000000-0000-0000-0000-000000000001", DateTime = "09.10.2026", SeatsAmount = 1, DeletionMark = "0" } } }) :
            Json(new { statusCode = 0, data = new[] { new { id = "rd-001", created_at = "2026-10-09", places = 1 } },
                pagination = new { total_count = 1, page_count = 1, page = 1, limit = 100 } })));
        using var client = new HttpClient(handler);
        var result = await new WaybillService(NovaPoshtaRequestPolicyTests.FastTransport(client), new Store(Config)).FetchAsync(new(2026, 10, 9), new(2026, 10, 9));
        Check(result.Documents.Count == 2 && result.Documents.All(row => row.CredentialFingerprint == WaybillAccountProof.Create(row.Carrier, row.ConnectionId,
            row.Carrier == ShippingCarrier.NovaPoshta ? Key : Token) && !row.CredentialFingerprint.Contains(Key) && !row.CredentialFingerprint.Contains(Token)), "Live source proof missing or raw credentials exposed.");
        Check(WaybillAccountProof.Create(ShippingCarrier.NovaPoshta, "np", Key) != WaybillAccountProof.Create(ShippingCarrier.NovaPoshta, "other", Key), "Same secret allowed another account identity.");
    }
}
