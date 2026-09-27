using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;
using CheckboxBatchPrinter.Services;
using CheckboxBatchPrinter.Views;

namespace CheckboxBatchPrinter.Tests;

internal static class ShippingLabelsTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("shipping carriers are independent of marketplace; old shipment cache round trips", Carrier),
        ("STA legacy label driver empty capabilities use only its actual sized default", () => Sta(DriverDefault)),
        ("RD verifies shipment and retrieves single-ID base64 PDF with separate token", DeliveryApi),
        ("RD rejects wrong identity, missing connection, forbidden and HTML documents", DeliveryErrors),
        ("NP reads document Ref and official Zebra through verified Seller API", SellerApi),
        ("shipping HTTP rejects every create/edit/registry route and redirects", ReadOnlyRoutes),
        ("mixed label preparation preserves order, seats and deduplicates identical pages", MixedPreparation),
        ("invalid or unavailable label blocks complete batch; no partial print", BrokenBatch),
        ("STA real label backend produces one job and separate pages", () => Sta(Backend)),
        ("STA backend registration-then-exception persists whole batch Unknown", () => Sta(Unknown)),
        ("STA preparation failure remains NotSubmitted and cancellation never starts job", () => Sta(NotSubmitted)),
        ("DPAPI label attempts survive restart independently from receipt history and secrets", Persistence),
        ("STA bundled PDFium renders100/101.5mm203/300dpi rotation full edges and barcode ink", () => Sta(Render))
    ];
    internal static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static Task Sta(Func<Task> body) => MarketplaceViewModelTests.StaAsync(body);
    internal static MarketplaceOrder Order(string number, ShippingCarrier carrier, string tracking) => new()
    { Key = new(MarketplaceKind.Prom, "10000000000000000000000000000001", number), Number = number,
        Shipments = tracking.Length == 0 ? [] : [new(carrier == ShippingCarrier.NovaPoshta ? "nova_poshta" : "rozetka_delivery", tracking)] };
    internal static LabelPrintSettings Settings => new() { PrinterName = "synthetic-label-printer", WidthMm = 101.5, HeightMm = 101.5 };
    internal static LabelPrinterGeometry Geometry => new(101.5, 101.5, 0, 0, 101.5, 101.5, 203, 203);
    private static Task DriverDefault()
    {
        var media = new System.Printing.PageMediaSize(101.5*96/25.4,101.5*96/25.4);
        Check(WindowsLabelPrinter.MediaSizes([],media).Single() == media,"Actual custom default omitted.");
        Check(WindowsLabelPrinter.MediaSizes([media],media).Count == 1,"Default duplicated.");
        Check(WindowsLabelPrinter.MediaSizes([],null).Count == 0,"Invented label format.");
        Check(WindowsLabelPrinter.MatchesMedia(new(105.6,101.6,2,0,101.6,101.6,203,203),101.5,101.5),"Sized ticket / symmetric external GDI frame rejected.");
        Check(!WindowsLabelPrinter.MatchesMedia(new(210,297,2,2,206,293,203,203),101.5,101.5),"A4 substitution accepted.");
        Check(!WindowsLabelPrinter.MatchesMedia(new(210,101.6,2,0,101.6,101.6,203,203),101.5,101.5),"Unexplained virtual frame accepted.");
        return Task.CompletedTask;
    }
    private static Task Carrier()
    {
        var prom = Order("1", ShippingCarrier.NovaPoshta, "000000000001");
        var rozetka = prom with { Key = new(MarketplaceKind.Rozetka, prom.Key.ConnectionId, "2") };
        Check(ShippingCarrierNames.ForOrder(prom).Single().Carrier == ShippingCarrier.NovaPoshta && ShippingCarrierNames.ForOrder(rozetka).Single().Carrier == ShippingCarrier.NovaPoshta, "Marketplace replaced carrier.");
        Check(ShippingCarrierNames.ForOrder(Order("3", ShippingCarrier.RozetkaDelivery, "00101")).Single().TrackingNumber == "00101", "Leading zero lost.");
        var old = JsonSerializer.Deserialize<OrderShipment>("{\"Carrier\":\"unknown\",\"TrackingNumber\":\"00123\",\"Destination\":\"synthetic\"}")!;
        Check(old.ShipmentId == "" && ShippingCarrierNames.FromDocumentedDeliveryField(old.Carrier) == ShippingCarrier.Unknown, "Old cache became a guessed carrier.");
        return Task.CompletedTask;
    }
    internal sealed class Stores : IShippingSettingsStore, IMarketplaceSettingsStore, IMarketplaceSecretStore
    {
        public ShippingLabelSettings Config = new() { Print = Settings, RozetkaDeliveryToken = "synthetic-rd-token" };
        public MarketplaceSettings Market = new();
        Task<ShippingLabelSettings> IShippingSettingsStore.LoadAsync(CancellationToken ct) => Task.FromResult(Config);
        public Task SaveAsync(ShippingLabelSettings value, CancellationToken ct) { Config = value; return Task.CompletedTask; }
        Task<MarketplaceSettings> IMarketplaceSettingsStore.LoadAsync(CancellationToken ct) => Task.FromResult(Market);
        public Task SaveAsync(MarketplaceSettings value, CancellationToken ct) { Market = value; return Task.CompletedTask; }
        public Task<MarketplaceCredentials?> LoadAsync(string id, CancellationToken ct) => Task.FromResult<MarketplaceCredentials?>(new(Login: "synthetic", Password: "synthetic-secret"));
        public Task SaveAsync(string id, MarketplaceCredentials value, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public readonly List<string> Paths = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Paths.Add(request.RequestUri!.PathAndQuery); return Task.FromResult(response(request)); }
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static ShipmentReference Shipment(ShippingCarrier carrier = ShippingCarrier.RozetkaDelivery) => ShippingCarrierNames.ForOrder(Order("1", carrier, "00123456")).Single();
    private static async Task DeliveryApi()
    {
        var stores = new Stores();
        var handler = new Handler(request =>
        {
            Check(request.Headers.Authorization?.Parameter == "synthetic-rd-token", "Wrong service token.");
            return request.RequestUri!.AbsolutePath.EndsWith("/label")
                ? Json(new { statusCode = 0, data = new { label = Convert.ToBase64String(SyntheticShippingPdf.Create()) } })
                : Json(new { statusCode = 0, data = new { id = "00123456", places = 1 } });
        });
        using var client = new HttpClient(handler);
        var result = await new OfficialShippingLabelSource(new(client), stores, stores, stores).GetAsync(Shipment());
        Check(result.DocumentId == "00123456" && result.ExpectedPages == 1 && handler.Paths.SequenceEqual(new[] { "/api/track/00123456", "/api/track/label?id=00123456" }), "RD mapping/order differs.");
        Check(result.Fingerprint == Convert.ToHexString(SHA256.HashData(result.Pdf)), "Fingerprint missing.");
    }
    private static async Task DeliveryErrors()
    {
        foreach (var kind in new[] { "wrong", "denied", "html", "no-token" })
        {
            var stores = new Stores(); if (kind == "no-token") stores.Config = stores.Config with { RozetkaDeliveryToken = "" };
            var handler = new Handler(_ => kind == "denied" ? new(HttpStatusCode.Forbidden) : kind == "html"
                ? new(HttpStatusCode.OK) { Content = new StringContent("<html>error</html>") }
                : Json(new { statusCode = 0, data = new { id = "different", places = 1 } }));
            using var client = new HttpClient(handler);
            try { await new OfficialShippingLabelSource(new(client), stores, stores, stores).GetAsync(Shipment()); throw new Exception("Invalid source allowed."); }
            catch (ShippingLabelException ex) { Check(ex.Kind == (kind == "wrong" || kind == "html" ? LabelFailureKind.InvalidDocument : kind == "no-token" ? LabelFailureKind.ConnectionRequired : LabelFailureKind.AccessDenied), "Error not distinguished."); }
            Check(handler.Paths.Count <= 1, "Label fetched after failed identity/rights.");
        }
    }
    private static async Task SellerApi()
    {
        var stores = new Stores(); var id = Guid.NewGuid().ToString("N");
        stores.Market.Connections.Add(new() { Id = id, Marketplace = MarketplaceKind.Rozetka, Enabled = true });
        stores.Config = stores.Config with { NovaPoshtaSellerConnectionId = id };
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/sites" => Json(new { success = true, content = new { access_token = "synthetic-seller-token" } }),
            "/ttns/ttn-list" => Json(new { success = true, content = new { ttn_list = new[] { new { Ref = "10000000-0000-0000-0000-000000000001", IntDocNumber = "00123456", SeatsAmount = "2" } } } }),
            "/ttns/ttn-print/zebra" => new(HttpStatusCode.OK) { Content = new ByteArrayContent(SyntheticShippingPdf.Create(pages: 2)) { Headers = { ContentType = new("application/pdf") } } },
            _ => throw new Exception("Unexpected write route.")
        });
        using var client = new HttpClient(handler);
        var source = new OfficialShippingLabelSource(new(client), stores, stores, stores);
        foreach (var marketplace in new[] { MarketplaceKind.Prom, MarketplaceKind.Rozetka })
        {
            var shipment = Shipment(ShippingCarrier.NovaPoshta) with { Order = new(marketplace, id, "1") };
            var document = await source.GetAsync(shipment);
            Check(document.DocumentId != shipment.TrackingNumber && Guid.TryParse(document.DocumentId, out _) && document.ExpectedPages == 2, "Public TTN treated as Ref.");
        }
        Check(handler.Paths.Count == 6 && handler.Paths.Last().Contains("ttnNumbers%5B%5D=00123456"), "Zebra request differs.");
    }
    private static async Task ReadOnlyRoutes()
    {
        foreach (var route in new[] { "https://rz-delivery.rozetka.ua/api/track", "https://rz-delivery.rozetka.ua/api/registry", "https://api-seller.rozetka.com.ua/ttns/create", "https://evil.test/api/track/00123" })
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Patch, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, route);
            try { ShippingLabelHttp.Validate(request); throw new Exception("Mutation route accepted."); } catch (InvalidOperationException) { }
        }
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.Redirect) { Headers = { Location = new("https://evil.test/") } }));
        try { await new ShippingLabelHttp(client).SendAsync(() => new(HttpMethod.Get, "https://rz-delivery.rozetka.ua/api/track/00123"), "application/json", default); throw new Exception("Redirect allowed."); }
        catch (ShippingLabelException ex) { Check(ex.Kind == LabelFailureKind.AccessDenied, "Redirect not guarded."); }
    }
    internal sealed class Source : IShippingLabelSource
    {
        public string? FailTracking; public Action? OnLoad;
        public Task<ShippingLabelDocument> GetAsync(ShipmentReference shipment, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); OnLoad?.Invoke();
            if (shipment.TrackingNumber == FailTracking) throw new ShippingLabelException(LabelFailureKind.AccessDenied, "Synthetic denied");
            var bytes = SyntheticShippingPdf.Create(pages: shipment.TrackingNumber == "two" ? 2 : 1);
            return Task.FromResult(new ShippingLabelDocument(shipment, "synthetic", "doc-" + shipment.TrackingNumber, bytes, Convert.ToHexString(SHA256.HashData(bytes)), DateTimeOffset.UtcNow, shipment.TrackingNumber == "two" ? 2 : 1));
        }
    }
    internal sealed class Renderer : IShippingLabelRenderer
    {
        public Task<IReadOnlyList<ShippingLabelPage>> RenderAsync(ShippingLabelDocument d, double x, double y, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ShippingLabelPage>>(Enumerable.Range(1, d.ExpectedPages).Select(i => new ShippingLabelPage(d.Shipment, d.DocumentId, d.Fingerprint, i, 100, 100, Png()) { ConnectionId = d.ConnectionId, Source = d.Source }).ToArray());
    }
    private static byte[] Png()
    {
        var bitmap = BitmapSource.Create(100, 100, 96, 96, PixelFormats.Gray8, null, Enumerable.Repeat((byte)255, 10000).ToArray(), 100);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }
    internal static Task<LabelPrintBatch> Batch() => new LabelBatchPreparation(new Source(), new Renderer()).PrepareAsync(new[]
    { Order("1", ShippingCarrier.NovaPoshta, "first"), Order("2", ShippingCarrier.RozetkaDelivery, "two"), Order("3", ShippingCarrier.NovaPoshta, "last") }, Settings, Geometry);
    private static async Task MixedPreparation()
    {
        var batch = await Batch();
        Check(batch.Pages.Select(p => p.Shipment.OrderNumber + ":" + p.PageNumber).SequenceEqual(new[] { "1:1", "2:1", "2:2", "3:1" }), "Mixed order/seats changed.");
        var order = Order("1", ShippingCarrier.NovaPoshta, "same");
        var duplicate = order with { Key = new(MarketplaceKind.Prom, "20000000000000000000000000000001", "1") };
        var deduplicated = await new LabelBatchPreparation(new Source(), new Renderer()).PrepareAsync([order, duplicate], Settings, Geometry);
        Check(deduplicated.Pages.Count == 1, "Duplicate connection duplicated sticker.");
        Check(batch.Pages.All(p => p.Png.Length > 0), "No raster payload.");
    }
    private static async Task BrokenBatch()
    {
        foreach (var noTtn in new[] { false, true })
        {
            var source = new Source { FailTracking = "broken" };
            try { await new LabelBatchPreparation(source, new Renderer()).PrepareAsync([Order("1", ShippingCarrier.NovaPoshta, "first"), Order("2", ShippingCarrier.RozetkaDelivery, noTtn ? "" : "broken")], Settings, Geometry); throw new Exception("Partial batch allowed."); }
            catch (ShippingLabelException) { }
        }
    }
    internal sealed class History : ILabelHistoryStore
    {
        public readonly List<LabelPrintAttempt> Attempts = [];
        public Task<IReadOnlyList<LabelPrintAttempt>> LoadAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LabelPrintAttempt>>(Attempts.ToArray());
        public Task SaveAsync(LabelPrintAttempt a, CancellationToken ct = default) { Attempts.RemoveAll(x => x.AttemptId == a.AttemptId); Attempts.Add(a); return Task.CompletedTask; }
    }
    private sealed class Device : ILabelPageDevice
    {
        public LabelPrinterGeometry Geometry => ShippingLabelsTests.Geometry;
        public int Begins, Pages, Ends, Aborts; public bool ThrowAfterRegister; public History? History;
        public int Begin(string name) { Begins++; Check(History!.Attempts.Last().State == LabelSubmissionState.SubmissionUnknown && name == History.Attempts.Last().JobName, "Boundary not journaled before registration."); if (ThrowAfterRegister) throw new Exception("Registered, no ID returned."); return 101; }
        public bool BeginPage() { Pages++; return true; }
        public bool Draw(byte[] pixels, int pw, int ph, int x, int y, int w, int h) => pixels.Length == pw * ph * 4;
        public bool FinishPage() => true;
        public bool Finish() { Ends++; return true; }
        public void Abort() { Aborts++; }
        public void Dispose() { }
    }
    private static async Task Backend()
    {
        var history = new History(); var device = new Device { History = history }; var batch = await Batch();
        var result = await new LabelPrintCoordinator(history, new WindowsLabelPrinter(_ => device)).SubmitAsync(batch);
        Check(device.Begins == 1 && device.Pages == 4 && device.Ends == 1 && result.WindowsJobId == 101 && result.State == LabelSubmissionState.Submitted, "Not one job with four pages.");
    }
    private static async Task Unknown()
    {
        var history = new History(); var device = new Device { History = history, ThrowAfterRegister = true };
        try { await new LabelPrintCoordinator(history, new WindowsLabelPrinter(_ => device)).SubmitAsync(await Batch()); throw new Exception("Registration failure hidden."); }
        catch (LabelTransmissionException ex) { Check(ex.Attempt.State == LabelSubmissionState.SubmissionUnknown && ex.Attempt.WindowsJobId is null && ex.Attempt.Pages.Count == 4, "Unknown lost whole packet."); }
        Check(device.Begins == 1 && history.Attempts.Single().State == LabelSubmissionState.SubmissionUnknown, "Automatic retry happened.");
    }
    private static async Task NotSubmitted()
    {
        var history = new History(); var device = new Device { History = history }; var batch = await Batch();
        var bad = batch with { Pages = [batch.Pages[0] with { WidthMm = 210, HeightMm = 297 }] };
        try { await new LabelPrintCoordinator(history, new WindowsLabelPrinter(_ => device)).SubmitAsync(bad); throw new Exception("A4 silently cropped."); }
        catch (LabelTransmissionException ex) { Check(ex.Attempt.State == LabelSubmissionState.NotSubmitted && device.Begins == 0, "Preparation called spooler."); }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await new LabelPrintCoordinator(history, new WindowsLabelPrinter(_ => device)).SubmitAsync(batch, cancel.Token); }
        catch (LabelTransmissionException ex) { Check(ex.Attempt.State == LabelSubmissionState.NotSubmitted && device.Begins == 0, "Cancel called printer."); }
    }
    private static async Task Persistence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cbp-label-history-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var history = new DpapiLabelHistoryStore(Path.Combine(directory, "labels.dpapi"));
        var a = new LabelPrintAttempt("synthetic-attempt", "synthetic-job", "synthetic-printer", DateTimeOffset.UtcNow, LabelSubmissionState.SubmissionUnknown,
            [new(ShippingCarrier.NovaPoshta, "synthetic-connection", "00123", "synthetic-document", 1, "synthetic-hash")]);
        await history.SaveAsync(a);
        Check((await new DpapiLabelHistoryStore(Path.Combine(directory, "labels.dpapi")).LoadAsync()).Single().State == LabelSubmissionState.SubmissionUnknown, "Restart erased Unknown.");
        var store = new DpapiShippingSettingsStore(Path.Combine(directory, "settings.dpapi")); await store.SaveAsync(new() { Print = Settings, RozetkaDeliveryToken = "synthetic-token-do-not-leak" });
        Check(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(directory, "settings.dpapi"))).Contains("synthetic-token"), "Token plaintext.");
        Check(!File.Exists(Path.Combine(directory, "printed-receipts.json")), "Receipt history touched.");
    }
    internal static async Task Render()
    {
        foreach (var size in new[] { 100d, 101.5 })
        foreach (var dpi in new[] { 203d, 300 })
        foreach (var rotation in new[] { 0, 90, 180, 270 })
        {
            var shipment = Shipment(); var pdf = SyntheticShippingPdf.Create(size, size, rotation: rotation);
            var doc = new ShippingLabelDocument(shipment, "synthetic", "synthetic-document", pdf, "synthetic-hash", DateTimeOffset.UtcNow, 1);
            var pages = await new WindowsShippingLabelRenderer().RenderAsync(doc, dpi, dpi);
            var page = pages.Single();
            Check(Math.Abs(page.WidthMm - size) < 0.05 && Math.Abs(page.HeightMm - size) < 0.05, "Points/DIP conversion differs.");
            using var stream = new MemoryStream(page.Png); var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Check(Math.Abs(frame.PixelWidth - Math.Ceiling(size * dpi / 25.4)) <= 1, "Raster not actual DPI.");
            var gray = new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0); var pixels = new byte[gray.PixelWidth * gray.PixelHeight]; gray.CopyPixels(pixels, gray.PixelWidth, 0);
            var w = gray.PixelWidth; var h = gray.PixelHeight; var edge = (int)Math.Ceiling(2 * dpi / 25.4);
            int Ink(int x0, int y0, int x1, int y1) { var count = 0; for (var y = y0; y < y1; y++) for (var x = x0; x < x1; x++) if (pixels[y*w+x] < 100) count++; return count; }
            Check(Ink(0,0,w,edge) > w/2 && Ink(0,h-edge,w,h) > w/2 && Ink(0,0,edge,h) > h/2 && Ink(w-edge,0,w,h) > h/2, "An edge marker is cropped.");
            Check(Ink(edge,edge,w-edge,h-edge) > 1000, "Barcode/content ink missing.");
            if (size == 100 && dpi == 203 && rotation == 0 && Environment.GetEnvironmentVariable("CHECKBOX_TEST_RENDER_DIR") is { Length: > 0 } output)
            { Directory.CreateDirectory(output); await File.WriteAllBytesAsync(Path.Combine(output, "synthetic-shipping-label.png"), page.Png); }
        }
        var a4 = new ShippingLabelDocument(Shipment(), "synthetic", "a4", SyntheticShippingPdf.Create(210,297), "hash", DateTimeOffset.UtcNow);
        var a4Page = (await new WindowsShippingLabelRenderer().RenderAsync(a4,203,203)).Single();
        try { LabelPageLayout.Place(a4Page, Settings, Geometry); throw new Exception("A4 cropped."); } catch (ShippingLabelException) { }
        var marker = new ShippingLabelPage(Shipment(), "doc", "hash", 1,100,100,[]);
        try { LabelPageLayout.Place(marker, Settings, Geometry with { LeftMm = 2, PrintableWidthMm = 97.5 }); throw new Exception("Hard margins ignored."); } catch (ShippingLabelException) { }
        _ = LabelPageLayout.Place(marker, Settings with { ScalePercent = 95 }, Geometry with { LeftMm = 2, PrintableWidthMm = 97.5 });
        foreach (var rotation in new[] { 90, 270 })
        {
            var rectangular = a4 with { Pdf = SyntheticShippingPdf.Create(100,80,rotation:rotation) };
            var rotated = (await new WindowsShippingLabelRenderer().RenderAsync(rectangular,203,300)).Single();
            Check(Math.Abs(rotated.WidthMm - 80) < 0.05 && Math.Abs(rotated.HeightMm - 100) < 0.05, "Non-square rotation ignored.");
        }
        var partner = a4 with { Pdf = SyntheticShippingPdf.Create(pages:3), ExpectedPages = 2 };
        Check((await new WindowsShippingLabelRenderer().RenderAsync(partner,203,203)).Count == 3, "Partner label discarded by place count.");
        foreach (var invalid in new[] { a4 with { Pdf = "%PDF-broken"u8.ToArray() }, a4 with { Pdf = SyntheticShippingPdf.Create(), ExpectedPages = 2 } })
        {
            try { await new WindowsShippingLabelRenderer().RenderAsync(invalid,203,203); throw new Exception("Invalid PDF accepted."); }
            catch (ShippingLabelException ex) { Check(ex.Kind == LabelFailureKind.InvalidDocument, "Corruption/places not categorized."); }
        }
    }
}
