using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;
using CheckboxBatchPrinter.Services;
using CheckboxBatchPrinter.Views;
using static CheckboxBatchPrinter.Tests.ShippingLabelsTests;

namespace CheckboxBatchPrinter.Tests;

internal static class NovaPoshtaDirectTests
{
    private const string Ttn = "20400000000001", OtherTtn = "20400000000002", Ref = "10000000-0000-0000-0000-000000000001";
    private static readonly NovaPoshtaConnection Connection = new("10000000000000000000000000000002", "Synthetic NP", "synthetic_secret_key");
    private static readonly NovaPoshtaSearchPeriod Period = new(new(2026,9,25), new(2026,9,27));
    private static ShipmentReference Shipment(string number = Ttn) => ShippingCarrierNames.ForOrder(Order(number, ShippingCarrier.NovaPoshta, number)).Single();
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("direct NP SDK request uses bounded explicit period and separate exact TTN Ref", Lookup),
        ("direct NP absent and missing Ref mean account sample result, not invalid key or invented identity", Missing),
        ("direct NP repeated and exhausted pages remain incomplete and never request marking", Incomplete),
        ("direct NP malformed envelope and account denial stop without exposing response or key", Malformed),
        ("direct NP cancels bounded lookup without a marking request", Cancellation),
        ("direct NP transport rejects write methods, extra parameters and duplicate JSON keys", MutationGuards),
        ("direct NP rejects HTML JSON wrong PDF header redirect and secret-bearing network errors", TransportErrors),
        ("STA PDFium direct NP checks text identity dimensions and text outside page", () => MarketplaceViewModelTests.StaAsync(Identity)),
        ("STA direct NP can inspect a found public TTN without fabricating a missing Ref", () => MarketplaceViewModelTests.StaAsync(OptionalRef)),
        ("STA NP multi-place PDFs retain every page but count alone cannot confirm completeness", () => MarketplaceViewModelTests.StaAsync(Places)),
        ("STA NP unconfirmed multi-page diagnostic preview has no print or batch acceptance button", () => MarketplaceViewModelTests.StaAsync(DiagnosticPreview)),
        ("STA Prom direct NP without Seller reaches existing mixed NP RD NP backend as one job", () => MarketplaceViewModelTests.StaAsync(Mixed)),
        ("STA direct NP failure in one mixed document never launches a partial job", () => MarketplaceViewModelTests.StaAsync(Broken)),
        ("direct NP session evidence rejects restart rotation foreign account and unverified document", Session),
        ("direct NP missing explicit store binding never falls through to a random Seller account", MissingBinding),
        ("DPAPI direct NP settings survive rotation without touching label or receipt history", Persistence),
        ("STA real NP settings window saves masked key and explicit store binding with no printer or Seller", () => MarketplaceViewModelTests.StaAsync(SettingsUi)),
        ("STA manual NP check blocks reentry and cancellation stops the selected account request", () => MarketplaceViewModelTests.StaAsync(ManualUiCancellation))
    ];
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return response(request, ct); }
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Pdf(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("application/pdf") } } };
    private static object Row(string number = Ttn, string? reference = Ref, int? places = 1) => new { IntDocNumber = number, Ref = reference, SeatsAmount = places, DeletionMark = false };
    private static HttpResponseMessage List(params object[] rows) => Json(new { success = true, data = rows, errors = Array.Empty<string>() });
    private static async Task<int> Page(HttpRequestMessage request)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == NovaPoshtaReadOnlyContract.EntryPoint, "Wrong SDK entry point.");
        Check(body.RootElement.GetProperty("apiKey").GetString() == Connection.ApiKey, "Key from wrong account.");
        Check(body.RootElement.GetProperty("modelName").GetString() == "InternetDocument" && body.RootElement.GetProperty("calledMethod").GetString() == "getDocumentList", "Wrong SDK model/method.");
        var props = body.RootElement.GetProperty("methodProperties");
        Check(props.EnumerateObject().Count() == 4 && !props.GetProperty("GetFullList").GetBoolean() && props.GetProperty("DateTimeFrom").GetString() == "25.09.2026" && props.GetProperty("DateTimeTo").GetString() == "27.09.2026", "Guessed exact-search parameter or unbounded dates.");
        return int.Parse(props.GetProperty("Page").GetString()!);
    }
    private static async Task Lookup()
    {
        var handler = new Handler(async (request, _) => await Page(request) switch { 1 => List(Row(OtherTtn)), 2 => List(Row()), _ => List() });
        using var http = new HttpClient(handler);
        var result = await new NovaPoshtaReadOnlyClient(new(http)).LookupAsync(Connection, Shipment(), Period);
        Check(result.Complete && result.PagesRead == 3 && result.DocumentRef == Ref && result.DocumentRef != Ttn && result.Places == 1 && handler.Calls == 3, "TTN/Ref or empty-page termination failed.");
    }
    private static async Task Missing()
    {
        foreach (var missingRef in new[] { false, true })
        {
            var handler = new Handler(async (r, _) => await Page(r) == 1 ? List(Row(missingRef ? Ttn : OtherTtn, missingRef ? null : Ref)) : List());
            using var http = new HttpClient(handler);
            var lookup = await new NovaPoshtaReadOnlyClient(new(http)).LookupAsync(Connection, Shipment(), Period);
            Check(lookup.Complete && lookup.DocumentRef == "" && lookup.Found == missingRef && (missingRef ? lookup.Message.Contains("Ref") : lookup.Message.Contains("Не знайдено у перевіреній вибірці")), "Absent sample became invalid key or invented Ref.");
        }
    }
    private sealed class NoInspector : ILabelPdfInspector
    { public Task<LabelPdfInspection> InspectAsync(byte[] bytes, CancellationToken ct = default) => throw new Exception("Unverified document reached PDF inspection."); }
    private static async Task Incomplete()
    {
        foreach (var repeated in new[] { true, false })
        {
            var handler = new Handler(async (r, _) => { var page = await Page(r); return List(Row(repeated || page == 1 ? Ttn : "204" + page.ToString("00000000000"))); });
            using var http = new HttpClient(handler);
            var result = await new NovaPoshtaDiagnostics(new(new(http)), new NoInspector()).CheckAsync(Connection, Shipment(), Period);
            Check(!result.Lookup.Complete && result.Document is null && handler.Calls == (repeated ? 2 : 10), "Incomplete sample claimed success or fetched PDF.");
        }
    }
    private static async Task Malformed()
    {
        foreach (var value in new object[] { new { data = new[] { Row() }, errors = Array.Empty<string>() }, new { success = true, data = new { }, errors = Array.Empty<string>() },
            new { success = true, data = new[] { new { IntDocNumber = Ttn, Ref = "not-ref", SeatsAmount = 1 } }, errors = Array.Empty<string>() },
            new { success = false, data = Array.Empty<object>(), errors = new[] { Connection.ApiKey + " buyer data" } } })
        {
            var handler = new Handler((_, _) => Task.FromResult(Json(value))); using var http = new HttpClient(handler);
            try { await new NovaPoshtaReadOnlyClient(new(http)).LookupAsync(Connection, Shipment(), Period); throw new Exception("Malformed response accepted."); }
            catch (ShippingLabelException ex) { Check(!ex.ToString().Contains(Connection.ApiKey) && !ex.ToString().Contains("buyer data") && handler.Calls == 1, "Secret/error body leaked or retried."); }
        }
    }
    private static async Task Cancellation()
    {
        using var cancel = new CancellationTokenSource();
        var handler = new Handler((_, ct) => { cancel.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(List()); }); using var http = new HttpClient(handler);
        try { await new NovaPoshtaReadOnlyClient(new(http)).LookupAsync(Connection, Shipment(), Period, cancel.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { Check(handler.Calls == 1, "Cancelled lookup continued."); }
    }
    private static string Body(string method = "getDocumentList", string extra = "") =>
        "{\"apiKey\":\"synthetic_secret_key\",\"modelName\":\"InternetDocument\",\"calledMethod\":\"" + method + "\",\"methodProperties\":{\"DateTimeFrom\":\"25.09.2026\",\"DateTimeTo\":\"27.09.2026\",\"GetFullList\":false,\"Page\":\"1\"" + extra + "}}";
    private static Task MutationGuards()
    {
        foreach (var body in new[] { Body("save"), Body("update"), Body("delete"), Body("saveScanSheet"), Body("getDocument"), Body(extra: ",\"IntDocNumber\":\""+Ttn+"\""),
            Body(extra: ",\"Page\":\"2\""), Body().Replace("\"Page\":\"1\"", "\"Page\":\"11\""), Body().Replace("25.09.2026", "01.09.2026"), Body().Replace("false", "true"), Body()+Connection.ApiKey })
        {
            using var r = new HttpRequestMessage(HttpMethod.Post, NovaPoshtaReadOnlyContract.EntryPoint) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            try { ShippingLabelHttp.Validate(r); throw new Exception("Unallowed NP payload accepted."); }
            catch (InvalidOperationException ex) { Check(!ex.ToString().Contains(Connection.ApiKey), "Body leaked."); }
        }
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Delete })
        { using var r = new HttpRequestMessage(method, NovaPoshtaReadOnlyContract.EntryPoint) { Content = new StringContent(Body(),Encoding.UTF8,"application/json") }; try { ShippingLabelHttp.Validate(r); throw new Exception("Wrong HTTP method accepted."); } catch (InvalidOperationException) { } }
        return Task.CompletedTask;
    }
    private static async Task TransportErrors()
    {
        foreach (var kind in new[] { "html", "json", "fake-pdf", "redirect", "network" })
        {
            var handler = new Handler((r, _) =>
            {
                if (kind == "network") throw new HttpRequestException(r.RequestUri!.AbsoluteUri);
                return Task.FromResult(kind switch
                {
                    "html" => new(HttpStatusCode.OK) { Content = new StringContent("<html>login " + Connection.ApiKey + "</html>", Encoding.UTF8, "text/html") },
                    "json" => Json(new { error = Connection.ApiKey }),
                    "redirect" => new(HttpStatusCode.Redirect) { Headers = { Location = new("https://evil.test/"+Connection.ApiKey) } },
                    _ => Pdf(Encoding.UTF8.GetBytes("not-pdf " + Connection.ApiKey))
                });
            });
            using var http = new HttpClient(handler);
            try { await new NovaPoshtaReadOnlyClient(new(http)).ReadMarkingAsync(Connection, Shipment(), new(Ttn,Ref,1,true,2,"")); throw new Exception("Unsafe PDF accepted."); }
            catch (ShippingLabelException ex) { Check(!ex.ToString().Contains(Connection.ApiKey) && handler.Calls == 1, "Secret URL/body exposed or redirect followed."); }
        }
    }
    private static async Task<NovaPoshtaDiagnosticResult> Evaluated(string number, byte[] pdf, int? seats = 1)
    {
        var shipment = Shipment(number); var lookup = new NovaPoshtaLookup(number, Ref, seats, true, 2, "");
        var document = new ShippingLabelDocument(shipment, Connection.Id, Ref, pdf, "synthetic", DateTimeOffset.UtcNow, seats ?? 0);
        return NovaPoshtaDiagnostics.Evaluate(lookup, document, await new WindowsLabelPdfInspector().InspectAsync(pdf));
    }
    private static async Task Identity()
    {
        foreach (var kind in new[] { "right", "wrong", "empty", "outside", "formatted", "a4", "longer" })
        {
            var text = kind == "wrong" ? OtherTtn : kind == "empty" ? "" : kind == "formatted" ? "2040 0000 0000 01" : kind == "longer" ? Ttn + " 5" : Ttn;
            var result = await Evaluated(Ttn, SyntheticShippingPdf.Create(kind == "a4" ? 210 : 100, kind == "a4" ? 297 : 100, pageTexts: [text], textOutsidePage: kind == "outside"));
            Check(result.Identity == (kind == "wrong" ? LabelIdentityCheck.Contradiction : kind is "empty" or "outside" or "longer" ? LabelIdentityCheck.Unconfirmed : LabelIdentityCheck.Verified), "Text identity classification wrong.");
            Check(result.CanUseInBatch == (kind is "right" or "formatted"), "Unconfirmed/wrong geometry admitted.");
            Check(Math.Abs(result.Inspection!.Pages[0].WidthMm - (kind == "a4" ? 210 : 100)) < .01, "Physical size unknown.");
        }
    }
    private static async Task Places()
    {
        foreach (var count in new[] { 1, 2, 3 })
        {
            var result = await Evaluated(Ttn, SyntheticShippingPdf.Create(pages: count, pageTexts: Enumerable.Repeat(Ttn,count).ToArray()), 2);
            Check(result.Inspection!.Pages.Count == count && !result.AllPlacesVerified && !result.CanUseInBatch, "First page/count became proof of all seats.");
            var pages = await new WindowsShippingLabelRenderer().RenderAsync(result.Document! with { ExpectedPages = 0 }, 203,203);
            Check(pages.Count == count, "Diagnostic preview dropped places.");
        }
        var missing = await Evaluated(Ttn, SyntheticShippingPdf.Create(pageTexts: [Ttn]), null);
        Check(!missing.AllPlacesVerified, "Invented one seat.");
    }
    private static async Task OptionalRef()
    {
        var handler = new Handler(async (r,_) => r.Method == HttpMethod.Post ? await Page(r) == 1 ? List(Row(Ttn,null)) : List()
            : Pdf(SyntheticShippingPdf.Create(pageTexts:[Ttn])));
        using var http = new HttpClient(handler);
        var result = await new NovaPoshtaDiagnostics(new(new(http)),new WindowsLabelPdfInspector()).CheckAsync(Connection,Shipment(),Period);
        Check(result.Lookup.Found && result.Lookup.DocumentRef == "" && result.Document!.DocumentId == "ttn:"+Ttn && result.CanUseInBatch,
            "Public TTN substituted as Ref, or unnecessary Ref requirement blocked the NUMBER route.");
    }
    private static async Task Session()
    {
        var result = new NovaPoshtaDiagnosticResult(new(Ttn,Ref,1,true,2,""), new(Shipment(),Connection.Id,Ref,[] ,"",DateTimeOffset.UtcNow),
            new([new(1,100,100,Ttn)]), LabelIdentityCheck.Verified, true, "");
        var session = new NovaPoshtaVerifiedSession(); session.Accept(Connection,result);
        Check(session.Require(Connection,Ttn).DocumentRef == Ref, "Valid proof lost.");
        foreach (var pair in new[] { (new NovaPoshtaVerifiedSession(),Connection), (session,Connection with { ApiKey = "rotated_key" }), (session,Connection with { Id = Guid.NewGuid().ToString("N") }) })
        { try { pair.Item1.Require(pair.Item2,Ttn); throw new Exception("Stale/foreign proof accepted."); } catch (ShippingLabelException) { } }
        try { session.Accept(Connection,result with { Identity = LabelIdentityCheck.Unconfirmed }); throw new Exception("Unconfirmed identity accepted."); } catch (InvalidOperationException) { }
        await Task.CompletedTask;
    }
    private static async Task DiagnosticPreview()
    {
        var result = await Evaluated(Ttn,SyntheticShippingPdf.Create(pages:2,pageTexts:[Ttn,Ttn]),2);
        var pages = await new WindowsShippingLabelRenderer().RenderAsync(result.Document! with { ExpectedPages = 0 },203,203);
        var window = new NovaPoshtaDiagnosticPreviewWindow(result,pages);
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(880,660)); content.Arrange(new Rect(0,0,880,660)); content.UpdateLayout();
        IEnumerable<DependencyObject> Nodes(DependencyObject parent)
        { yield return parent; foreach(var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()) foreach(var node in Nodes(child)) yield return node; }
        Check(Nodes(content).OfType<Button>().All(b=>b.Visibility == Visibility.Collapsed || Equals(b.Content,"Закрити без дозволу пакета")),"Unverified diagnostic can print/accept.");
        Check(Nodes(content).OfType<TextBlock>().Count(b=>b.Text.StartsWith("Сторінка ")) == 2,"A place disappeared in diagnostic view.");
        if(Environment.GetEnvironmentVariable("CHECKBOX_TEST_RENDER_DIR") is {Length:>0} output)
        {
            var bitmap = new RenderTargetBitmap(880,660,96,96,PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            await using var file = File.Create(Path.Combine(output,"np-diagnostic-preview.png")); encoder.Save(file);
        }
        window.Close();
    }
    private sealed class Device : ILabelPageDevice
    {
        public int Begins, Pages, Ends; public LabelPrinterGeometry Geometry => ShippingLabelsTests.Geometry;
        public int Begin(string name) { Begins++; return 100; }
        public bool BeginPage() { Pages++; return true; }
        public bool Draw(byte[] data,int pw,int ph,int x,int y,int w,int h) => data.Length == pw*ph*4;
        public bool FinishPage() => true; public bool Finish() { Ends++; return true; } public void Abort() { } public void Dispose() { }
    }
    private static async Task Mixed() => await MixedCore(false);
    private static async Task Broken() => await MixedCore(true);
    private static async Task MixedCore(bool broken)
    {
        var stores = new Stores(); stores.Config = stores.Config with { NovaPoshtaConnections = [Connection], NovaPoshtaStoreBindings = [new(Shipment().Order.ConnectionId,Connection.Id)] };
        var handler = new Handler(async (request, _) =>
        {
            if (request.RequestUri!.Host == "api.novaposhta.ua") return await Page(request) == 1 ? List(Row(),Row(OtherTtn,"10000000-0000-0000-0000-000000000002")) : List();
            if (request.RequestUri.Host == "my.novaposhta.ua")
            {
                var number = request.RequestUri.AbsolutePath.Contains(OtherTtn) ? OtherTtn : Ttn;
                return Pdf(SyntheticShippingPdf.Create(pageTexts: [number]));
            }
            Check(request.Headers.Authorization?.Parameter == "synthetic-rd-token", "NP key sent to RD.");
            return request.RequestUri.AbsolutePath.EndsWith("/label") ? Json(new { statusCode = 0, data = new { label = Convert.ToBase64String(SyntheticShippingPdf.Create()) } })
                : Json(new { statusCode = 0, data = new { id = "00012345", places = 1 } });
        });
        using var http = new HttpClient(handler); var transport = new ShippingLabelHttp(http); var client = new NovaPoshtaReadOnlyClient(transport);
        var inspector = new WindowsLabelPdfInspector(); var session = new NovaPoshtaVerifiedSession(); var diagnostics = new NovaPoshtaDiagnostics(client,inspector);
        foreach (var number in new[] { Ttn,OtherTtn }) session.Accept(Connection, await diagnostics.CheckAsync(Connection,Shipment(number),Period));
        var source = new ConfiguredShippingLabelSource(stores,new NovaPoshtaDirectLabelSource(stores,client,inspector,session),new OfficialShippingLabelSource(transport,stores,stores,stores));
        var orders = new[] { Order("NP1",ShippingCarrier.NovaPoshta,Ttn), Order("RD",ShippingCarrier.RozetkaDelivery,broken ? "000badid" : "00012345"), Order("NP2",ShippingCarrier.NovaPoshta,OtherTtn) };
        var device = new Device(); var history = new History();
        try
        {
            var batch = await new LabelBatchPreparation(source,new WindowsShippingLabelRenderer()).PrepareAsync(orders,Settings,ShippingLabelsTests.Geometry);
            if (broken) throw new Exception("Partial packet allowed.");
            Check(batch.Pages.Select(p=>p.Shipment.OrderNumber).SequenceEqual(new[]{"NP1","RD","NP2"}) && batch.Pages[0].DocumentId == Ref && batch.Pages[2].DocumentId != Ref,"Mixed adapter sequence/Ref wrong.");
            await new LabelPrintCoordinator(history,new WindowsLabelPrinter(_=>device)).SubmitAsync(batch);
            Check(device.Begins == 1 && device.Pages == 3 && device.Ends == 1,"Not one native backend job.");
        }
        catch (ShippingLabelException) when (broken) { Check(device.Begins == 0 && history.Attempts.Count == 0,"Partial print/history created."); }
        Check(stores.Market.Connections.Count == 0,"Test accidentally required Seller.");
    }
    private static async Task Persistence()
    {
        var root = Path.Combine(Path.GetTempPath(),"cbp-np-test-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root,"settings.dpapi"); var historyPath = Path.Combine(root,"attempts.dpapi"); var store = new DpapiShippingSettingsStore(path);
            var history = new DpapiLabelHistoryStore(historyPath);
            await history.SaveAsync(new("attempt","job","printer",DateTimeOffset.UtcNow,LabelSubmissionState.SubmissionUnknown,[new(ShippingCarrier.NovaPoshta,Connection.Id,Ttn,Ref,1,"fingerprint")]));
            var before = await File.ReadAllBytesAsync(historyPath);
            // No label/receipt printer, Seller or Checkbox required for saving diagnostic access.
            await store.SaveAsync(new() { NovaPoshtaConnections = [Connection], NovaPoshtaStoreBindings = [new(Shipment().Order.ConnectionId,Connection.Id)] });
            var first = await new DpapiShippingSettingsStore(path).LoadAsync();
            await store.SaveAsync(first with { NovaPoshtaConnections = [Connection with { ApiKey = "rotated_key" }] });
            var restored = await new DpapiShippingSettingsStore(path).LoadAsync();
            Check(restored.NovaPoshtaConnections.Single().Id == Connection.Id && restored.NovaPoshtaConnections.Single().ApiKey == "rotated_key" &&
                restored.NovaPoshtaStoreBindings.Single().NovaPoshtaConnectionId == Connection.Id && (await history.LoadAsync()).Single().State == LabelSubmissionState.SubmissionUnknown,"Rotation lost account/binding/history.");
            Check((await File.ReadAllBytesAsync(historyPath)).SequenceEqual(before) && !Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)).Contains("rotated_key"),"Plaintext key or history overwritten.");
            Check(!Connection.ToString().Contains(Connection.ApiKey),"Account display includes key.");
        }
        finally { Directory.Delete(root,true); } // exact disposable test directory
    }
    private sealed class NeverSource : IShippingLabelSource
    { public Task<ShippingLabelDocument> GetAsync(ShipmentReference s,CancellationToken ct=default) => throw new Exception("An unselected account was contacted."); }
    private static async Task MissingBinding()
    {
        var stores = new Stores(); stores.Config = new() { NovaPoshtaConnections = [Connection] };
        var source = new ConfiguredShippingLabelSource(stores,new NeverSource(),new NeverSource());
        try { await source.GetAsync(Shipment()); throw new Exception("Absent binding accepted."); }
        catch(ShippingLabelException ex) { Check(ex.Kind == LabelFailureKind.ConnectionRequired && ex.Message.Contains("Rozetka Seller не потрібний"),"Misreported missing explicit NP selection."); }
    }
    private static async Task SettingsUi()
    {
        var stores = new Stores(); stores.Config = new();
        var promId = Shipment().Order.ConnectionId;
        stores.Market.Connections.Add(new() { Id = promId,Name = "Synthetic Prom",Enabled = true,Marketplace = MarketplaceKind.Prom });
        var handler = new Handler((_,_)=>throw new Exception("Settings automatically called API.")); using var http = new HttpClient(handler);
        var window = new NovaPoshtaConnectionWindow(stores,stores.Config,stores.Market,new(new(new(http)),new NoInspector()),new(),new WindowsShippingLabelRenderer(),Order("selected",ShippingCarrier.NovaPoshta,Ttn));
        IEnumerable<DependencyObject> Nodes(DependencyObject parent)
        { yield return parent; foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()) foreach(var node in Nodes(child)) yield return node; }
        var add = Nodes(window).OfType<Button>().Single(b=>Equals(b.Content,"Додати NP-підключення")); add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        T Field<T>(string name) => (T)typeof(NovaPoshtaConnectionWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
        Field<TextBox>("_name").Text = "Saved synthetic NP"; Field<PasswordBox>("_key").Password = Connection.ApiKey;
        Field<ComboBox>("_boundAccount").SelectedIndex = 1;
        var save = Nodes(window).OfType<Button>().Single(b=>Equals(b.Content,"Зберегти NP-підключення")); save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Yield();
        Check(stores.Config.NovaPoshtaConnections.Single().ApiKey == Connection.ApiKey && stores.Config.NovaPoshtaStoreBindings.Single().MarketplaceConnectionId == promId &&
            Field<PasswordBox>("_key").Password == "" && handler.Calls == 0,"Actual UI failed masked key/store binding or auto-scanned.");
        if (Environment.GetEnvironmentVariable("CHECKBOX_TEST_RENDER_DIR") is { Length: > 0 } output)
        {
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(760,710)); content.Arrange(new Rect(0,0,760,710)); content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(760,710,96,96,PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); Directory.CreateDirectory(output);
            await using var file = File.Create(Path.Combine(output,"np-connection-settings.png")); encoder.Save(file);
        }
        window.Close();
    }
    private static async Task ManualUiCancellation()
    {
        var stores = new Stores(); var promId = Shipment().Order.ConnectionId;
        stores.Config = stores.Config with { NovaPoshtaConnections = [Connection], NovaPoshtaStoreBindings = [new(promId,Connection.Id)] };
        stores.Market.Connections.Add(new() {Id=promId,Name="Synthetic Prom",Enabled=true,Marketplace=MarketplaceKind.Prom});
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (r,ct)=>{await Page(r); entered.TrySetResult(); await Task.Delay(Timeout.Infinite,ct); return List();});
        using var http = new HttpClient(handler);
        var window = new NovaPoshtaConnectionWindow(stores,stores.Config,stores.Market,new(new(new(http)),new NoInspector()),new(),new WindowsShippingLabelRenderer(),Order("selected",ShippingCarrier.NovaPoshta,Ttn));
        T Field<T>(string name) => (T)typeof(NovaPoshtaConnectionWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
        Field<DatePicker>("_from").SelectedDate = Period.From.ToDateTime(TimeOnly.MinValue); Field<DatePicker>("_to").SelectedDate = Period.To.ToDateTime(TimeOnly.MinValue);
        var buttons = Field<StackPanel>("_form").Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
        var check = buttons.Single(b=>Equals(b.Content,"Перевірити наявну накладну"));
        Check(handler.Calls == 0,"Opening settings automatically scanned history.");
        check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await entered.Task;
        check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(handler.Calls == 1 && !Field<StackPanel>("_form").IsEnabled,"Manual check reentered while saving/loading.");
        Field<Button>("_cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for(var i=0;i<200 && !Field<StackPanel>("_form").IsEnabled;i++) await Task.Delay(1);
        Check(Field<StackPanel>("_form").IsEnabled && Field<TextBlock>("_status").Text.Contains("скасовано") && handler.Calls == 1,"Cancellation did not stop bounded API check.");
        window.Close();
    }
}
