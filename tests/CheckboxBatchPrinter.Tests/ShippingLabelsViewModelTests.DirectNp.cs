using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;
using CheckboxBatchPrinter.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> DirectNpShippingScenarios =>
    [
        ("STA two selected NP TTNs use one bulk GET before one confirmation and separate label jobs", () => StaAsync(DirectNpSelectionAsync)),
        ("STA foreign page in NP bulk PDF blocks both labels with a whole-packet identity error", () => StaAsync(DirectNpInvalidSecondAsync))
    ];

    private const string BatchTtnOne = "20400000000001", BatchTtnTwo = "20400000000002";
    private static readonly NovaPoshtaConnection BatchConnection = new("10000000000000000000000000000007", "Synthetic batch NP", "synthetic_batch_np_key");

    private sealed class BatchNpHandler(bool invalidSecond = false) : HttpMessageHandler
    {
        public int JsonCalls, PdfCalls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (request.RequestUri!.Host == "api.novaposhta.ua")
            {
                JsonCalls++;
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Equal(BatchConnection.ApiKey, json.RootElement.GetProperty("apiKey").GetString());
                var first = json.RootElement.GetProperty("methodProperties").GetProperty("Page").GetString() == "1";
                object[] rows = first ?
                [
                    new { IntDocNumber = BatchTtnOne, Ref = "10000000-0000-0000-0000-000000000001", SeatsAmount = "1", DeletionMark = "0" },
                    new { IntDocNumber = BatchTtnTwo, Ref = "10000000-0000-0000-0000-000000000002", SeatsAmount = "1", DeletionMark = "0" }
                ] : [];
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { success = true, data = rows, errors = Array.Empty<string>() }), Encoding.UTF8, "application/json") };
            }
            Equal("my.novaposhta.ua", request.RequestUri.Host);
            Equal(HttpMethod.Get, request.Method);
            Equal(BatchConnection.ApiKey, request.RequestUri.AbsolutePath.Split('/')[^1]);
            PdfCalls++;
            var segments = Uri.UnescapeDataString(request.RequestUri.AbsolutePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
            var numbers = segments[3].Split(',');
            True(numbers.All(n => n is BatchTtnOne or BatchTtnTwo), "Unexpected public TTN in marking URL.");
            Equal(numbers.Length > 1 ? "orders" : "orders[]", segments[2]);
            var texts = numbers.Select(n => invalidSecond && n == BatchTtnTwo ? "20400000000003" : n).Reverse().ToArray();
            var bytes = SyntheticShippingPdf.Create(pages: texts.Length, pageTexts: texts);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("application/pdf") } } };
        }
    }

    private static async Task DirectNpSelectionAsync()
    {
        foreach (var approvedFirst in new[] { true, false })
        foreach (var confirm in new[] { true, false })
        {
            var (workspace, fixture) = await LabelWorkspace();
            var rows = workspace.AllOrderRows.ToArray();
            rows[0].Update(rows[0].Model with { Shipments = [new("nova_poshta", BatchTtnOne)] }, [], []);
            rows[1].Update(rows[1].Model with { Shipments = [new("nova_poshta", BatchTtnTwo)] }, [], []);
            var store = new ShippingLabelsTests.Stores();
            store.Config = store.Config with { NovaPoshtaConnections = [BatchConnection], NovaPoshtaStoreBindings = [new(rows[0].Key.ConnectionId, BatchConnection.Id)] };
            var handler = new BatchNpHandler();
            using var http = new HttpClient(handler);
            var client = new NovaPoshtaReadOnlyClient(NovaPoshtaRequestPolicyTests.FastTransport(http));
            var inspector = new WindowsLabelPdfInspector();
            var session = new NovaPoshtaVerifiedSession();
            if (approvedFirst)
            {
                var today = DateOnly.FromDateTime(DateRangeBuilder.TodayKyiv);
                session.Accept(BatchConnection, await new NovaPoshtaDiagnostics(client, inspector).CheckAsync(BatchConnection,
                    ShippingCarrierNames.ForOrder(rows[0].Model).Single(), new(today.AddDays(-6), today)));
            }
            handler.JsonCalls = handler.PdfCalls = 0;
            var source = new ConfiguredShippingLabelSource(store, new NovaPoshtaDirectLabelSource(store, client, inspector),
                new ShippingLabelsTests.Source());
            var history = new ShippingLabelsTests.History();
            var printer = new LabelPrinter();
            var dialogs = new LabelDialogs { Confirm = confirm };
            dialogs.OnPreview = () =>
            {
                Equal(0, printer.Calls); Equal(0, history.Attempts.Count);
                Equal(0, handler.JsonCalls); Equal(1, handler.PdfCalls);
            };
            var labels = new ShippingLabelsViewModel(workspace, store, history, source, new WindowsShippingLabelRenderer(), printer, dialogs);
            labels.SelectVisibleCommand.Execute(null);
            await labels.RunAsync(null, true);
            True(dialogs.Errors.Count == 0, string.Join(" | ", dialogs.Errors));
            Equal(1, dialogs.Previews);
            Equal(0, handler.JsonCalls);
            Equal(1, handler.PdfCalls);
            Equal(confirm ? 2 : 0, printer.Calls);
            Equal(confirm ? 2 : 0, history.Attempts.Count);
            if (confirm)
            {
                True(printer.Batches.SelectMany(b => b.Pages).Select(p => p.Shipment.TrackingNumber).SequenceEqual(new[] { BatchTtnOne, BatchTtnTwo }));
                Equal(2, dialogs.PreviewedBatch!.SelectedOrderCount);
                True(history.Attempts.All(a => a.Pages.Count == 1));
                True(rows.All(r => !r.IsSelectedForLabels));
            }
            else True(rows.All(r => r.IsSelectedForLabels));
            // A manual diagnostic decision must not affect the bulk print request or be written by it.
            if (approvedFirst) Equal("10000000-0000-0000-0000-000000000001", session.Require(BatchConnection, BatchTtnOne).DocumentRef);
            else
            {
                try { session.Require(BatchConnection, BatchTtnOne); throw new Exception("Bulk download wrote a manual session approval."); }
                catch (ShippingLabelException) { }
            }
            try { session.Require(BatchConnection, BatchTtnTwo); throw new Exception("Automatic batch verification wrote a manual session approval."); }
            catch (ShippingLabelException) { }
            Equal(0, fixture.Printer.Calls);
            Equal(0, fixture.ReceiptSource.Calls);
        }
    }

    private static async Task DirectNpInvalidSecondAsync()
    {
        var (workspace, fixture) = await LabelWorkspace();
        var rows = workspace.AllOrderRows.ToArray();
        rows[0].Update(rows[0].Model with { Shipments = [new("nova_poshta", BatchTtnOne)] }, [], []);
        rows[1].Update(rows[1].Model with { Shipments = [new("nova_poshta", BatchTtnTwo)] }, [], []);
        var store = new ShippingLabelsTests.Stores();
        store.Config = store.Config with { NovaPoshtaConnections = [BatchConnection], NovaPoshtaStoreBindings = [new(rows[0].Key.ConnectionId, BatchConnection.Id)] };
        var handler = new BatchNpHandler(invalidSecond: true);
        using var http = new HttpClient(handler);
        var source = new ConfiguredShippingLabelSource(store,
            new NovaPoshtaDirectLabelSource(store, new(NovaPoshtaRequestPolicyTests.FastTransport(http)), new WindowsLabelPdfInspector()),
            new ShippingLabelsTests.Source());
        var history = new ShippingLabelsTests.History();
        var printer = new LabelPrinter();
        var dialogs = new LabelDialogs();
        var labels = new ShippingLabelsViewModel(workspace, store, history, source, new WindowsShippingLabelRenderer(), printer, dialogs);
        labels.SelectVisibleCommand.Execute(null);
        await labels.RunAsync(null, true);
        Equal(0, handler.JsonCalls);
        Equal(1, handler.PdfCalls);
        Equal(0, printer.Calls);
        Equal(0, dialogs.Previews);
        Equal(0, history.Attempts.Count);
        True(dialogs.Errors.Single().Contains("Пакет наклейок Нової пошти", StringComparison.Ordinal) &&
            dialogs.Errors[0].Contains("іншим номером", StringComparison.Ordinal) &&
            !dialogs.Errors[0].Contains("Замовлення №" + rows[0].Model.Number, StringComparison.Ordinal),
            "A foreign bulk page must fail the whole packet without blaming the first selected order.");
        True(rows.All(r => r.IsSelectedForLabels));
        Equal(0, fixture.Printer.Calls);
    }
}
