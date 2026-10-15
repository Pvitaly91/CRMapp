using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static IReadOnlyList<(string Name, Func<Task> Test)> RozetkaGoodsTotalScenarios =>
    [
        ("STA real Rozetka goods390 and delivery49 display separately without amount links or receipt basket reads", () => StaAsync(RozetkaGoodsAmountDisplayAsync)),
        ("STA Rozetka checkout including delivery cannot substitute the goods receipt amount", () => StaAsync(RozetkaCheckoutDoesNotLinkAsync)),
        ("STA missing Rozetka goods fields cannot infer an automatic link from checkout minus delivery", () => StaAsync(RozetkaMissingGoodsAmountAsync))
    ];

    private const string RozetkaGoodsName = "Synthetic adapter goods model G390";
    private const string RozetkaGoodsOrderId = "908891418";

    private static ReceiptRecord RozetkaGoodsReceipt(string id, int serial, long amount) => new()
    {
        Id = id, Serial = serial, Status = "DONE", Type = ReceiptTypes.Sell, TotalSumMinor = amount,
        FiscalDate = new(2026, 9, 23, 14, 0, 0, TimeSpan.FromHours(3))
    };

    private sealed class RozetkaGoodsFixture : IDisposable
    {
        public Fixture State { get; } = new();
        public MainViewModel Main { get; }
        public MarketplaceWorkspaceViewModel Workspace { get; }
        public int ListCalls, DetailCalls, FiscalCalls;
        private readonly HttpClient _http;

        public RozetkaGoodsFixture(decimal goods, decimal checkout, bool hydrate, bool goodsFields = true)
        {
            State.Settings.Market.Connections.Clear();
            State.Settings.Market.Connections.Add(new()
                { Id = "rz-goods", Marketplace = MarketplaceKind.Rozetka, Enabled = true, Name = "Synthetic Rozetka" });
            var full = new Dictionary<string, object?>
            {
                ["id"] = long.Parse(RozetkaGoodsOrderId), ["created"] = "2026-09-23 10:00:00",
                ["amount"] = goods, ["amount_with_discount"] = goods,
                ["cost"] = checkout, ["cost_with_discount"] = checkout,
                ["status"] = 1, ["status_data"] = new { id = 1, name_uk = "Нове" },
                ["user"] = new { contact_fio = "Synthetic buyer" },
                ["delivery"] = new { cost = 49, delivery_service_name = "Нова пошта" },
                ["purchases"] = new[] { new { item_name = RozetkaGoodsName, quantity = 1,
                    price = goods, price_with_discount = goods, cost = goods, cost_with_discount = goods,
                    item = new { article = "SYNTH-G390" } } },
                ["prro"] = new { prro_receipt_status = 1 }
            };
            if (!goodsFields) { full.Remove("amount"); full.Remove("amount_with_discount"); }
            var list = new Dictionary<string, object?>(full);
            if (hydrate) list.Remove("purchases");
            _http = new HttpClient(new FiscalVerificationHandler(request =>
            {
                Equal(HttpMethod.Get, request.Method);
                Equal("Bearer", request.Headers.Authorization!.Scheme);
                Equal("synthetic-rz-goods", request.Headers.Authorization.Parameter);
                object response;
                switch (request.RequestUri!.AbsolutePath)
                {
                    case "/orders/search":
                        ListCalls++;
                        response = new { success = true, content = new { orders = new[] { list },
                            _meta = new { currentPage = 1, pageCount = 1, perPage = 20 } } };
                        break;
                    case "/orders/" + RozetkaGoodsOrderId:
                        DetailCalls++;
                        response = new { success = true, content = full };
                        break;
                    case "/prro/receipt/" + RozetkaGoodsOrderId:
                        FiscalCalls++;
                        Equal("?type=link", request.RequestUri.Query);
                        response = new { success = false, errors = new { code = 15005 } };
                        break;
                    default: throw new InvalidOperationException("Unexpected synthetic Rozetka request.");
                }
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response),
                    Encoding.UTF8, "application/json") };
            }));
            var adapter = new RozetkaOrdersClient(new MarketplaceHttpTransport(_http));
            var sync = new MarketplaceSyncService([adapter], State.Settings, State.Cache);
            Workspace = new(State.Settings, sync, State.Links, State.Details, State.Dialogs,
                autoLinkEnabled: true, automaticCache: new AutomaticCache()) { CoordinatorManaged = true };
            Main = new(State.ReceiptSource, State.Images, State.Settings, State.Authentication, State.History,
                State.Printer, State.Dialogs, new Logger(), Workspace)
                { DateFrom = new(2026, 9, 23), DateTo = new(2026, 9, 23), CoordinatorManaged = true };
        }

        public void Receipts(params (string Id, int Serial, decimal Total)[] receipts)
        {
            State.ReceiptSource.Rows = receipts.Select(receipt => RozetkaGoodsReceipt(receipt.Id, receipt.Serial,
                decimal.ToInt64(receipt.Total * 100))).ToArray();
            foreach (var receipt in receipts)
                State.Details.Values[receipt.Id] = new(receipt.Id,
                    [new(RozetkaGoodsName, "SYNTH-G390", 1, receipt.Total, receipt.Total)]) { ItemListComplete = true };
        }

        public void AssertReadOnly()
        {
            Equal(1, ListCalls); Equal(1, FiscalCalls);
            Equal(0, State.Links.Saves); Equal(0, State.Printer.Calls); Equal(0, State.History.Saves);
            True(State.Dialogs.Errors.Count == 0, string.Join("; ", State.Dialogs.Errors));
        }

        public void Dispose() { Main.CancelPendingOperations(); _http.Dispose(); }
    }

    private static async Task RozetkaGoodsAmountDisplayAsync()
    {
        foreach (var hydrate in new[] { false, true })
        {
            using var fixture = new RozetkaGoodsFixture(390, 439, hydrate);
            fixture.Receipts((ReceiptOne, 4961, 390), (ReceiptTwo, 4962, 439));
            await fixture.Main.RefreshAllAsync();
            var order = fixture.Workspace.AllOrderRows.Single();
            Equal(390m, order.Model.Total); Equal(49m, order.Model.DeliveryCost); Equal("", order.Model.Currency);
            True(order.TotalDisplay.StartsWith(390m.ToString("N2")) && !order.TotalDisplay.Contains("439"));
            True(order.Model.FiscalReferences.Count == 0 && order.Model.ReceiptIds.Count == 0 &&
                order.Model.FiscalReceiptNumbers.Count == 0, "Synthetic fiscal failure unexpectedly supplied exact evidence.");
            True(order.Model.FiscalDataStatus.Contains("15005"));
            True(fixture.Main.Receipts.All(row => row.OrderMatch?.Order is null),
                "Rozetka was linked by amount despite missing an exact receipt ID.");
            Equal(0, fixture.State.Details.Calls);
            True(!order.HasSuggestedLink && !order.HasConfirmedLink && order.LinkedReceipts.Count == 0);
            True(!hydrate || fixture.DetailCalls >= 1, "Unexpanded list row was not hydrated.");
            fixture.AssertReadOnly();
        }
    }

    private static async Task RozetkaCheckoutDoesNotLinkAsync()
    {
        foreach (var goods in new[] { 390m, 341m })
        {
            var checkout = goods + 49;
            using var fixture = new RozetkaGoodsFixture(goods, checkout, hydrate: true);
            fixture.Receipts((ReceiptOne, 4961, checkout));
            await fixture.Main.RefreshAllAsync();
            var order = fixture.Workspace.AllOrderRows.Single();
            Equal(goods, order.Model.Total); Equal(49m, order.Model.DeliveryCost);
            True(fixture.Main.Receipts.Single().OrderMatch?.Order is null,
                "A receipt matching only the delivery-inclusive checkout total was automatically linked.");
            True(!order.HasSuggestedLink && order.LinkedReceipts.Count == 0);
            fixture.AssertReadOnly();
        }
    }

    private static async Task RozetkaMissingGoodsAmountAsync()
    {
        using var fixture = new RozetkaGoodsFixture(390, 439, hydrate: true, goodsFields: false);
        fixture.Receipts((ReceiptOne, 4961, 390), (ReceiptTwo, 4962, 439));
        await fixture.Main.RefreshAllAsync();
        var order = fixture.Workspace.AllOrderRows.Single();
        Equal(49m, order.Model.DeliveryCost);
        True(order.Model.AmountComparisonIssue.Length > 0);
        True(fixture.Main.Receipts.All(row => row.OrderMatch?.Order is null),
            "Missing goods fields allowed checkout or checkout-minus-delivery to establish an automatic link.");
        True(!order.HasSuggestedLink && order.LinkedReceipts.Count == 0);
        fixture.AssertReadOnly();
    }
}
