using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static IReadOnlyList<(string Name, Func<Task> Test)> RozetkaIdOnlyScenarios =>
    [
        ("STA Rozetka equal amount and products stay unlinked without basket reads while Prom still suggests", () => StaAsync(RozetkaIdOnlyMixedMarketplaceAsync)),
        ("STA old saved Rozetka suggestions are discarded on cache load F5 and restart", () => StaAsync(RozetkaIdOnlyDropsSavedSuggestionsAsync)),
        ("STA real Seller and verified Checkbox receipt UUIDs link Rozetka despite different amounts and products", () => StaAsync(RozetkaIdOnlyExactMismatchAsync))
    ];

    private static MarketplaceOrder RozetkaIdOnlyOrder() => BasketOrder() with
    {
        Key = new(MarketplaceKind.Rozetka, "rz-id-only", "100"), Number = "100", Total = 200,
        Items = [new("Synthetic Rozetka goods", "RZ-ID-ONLY", 1, 200, 200)], ItemListComplete = true
    };

    private static (MainViewModel Main, MarketplaceWorkspaceViewModel Workspace) CreateIdOnlyWorkspace(
        Fixture fixture, AutomaticCache? automatic = null, IFiscalReferenceVerifier? verifier = null, bool automaticMode = true)
    {
        var sync = new MarketplaceSyncService([fixture.Source, fixture.Rozetka], fixture.Settings, fixture.Cache);
        var workspace = new MarketplaceWorkspaceViewModel(fixture.Settings, sync, fixture.Links, fixture.Details,
            fixture.Dialogs, autoLinkEnabled: automaticMode, automaticCache: automatic ?? new AutomaticCache(),
            fiscalVerifier: verifier) { CoordinatorManaged = true };
        var main = new MainViewModel(fixture.ReceiptSource, fixture.Images, fixture.Settings, fixture.Authentication,
            fixture.History, fixture.Printer, fixture.Dialogs, new Logger(), workspace)
            { DateFrom = new(2026, 9, 23), DateTo = new(2026, 9, 23), CoordinatorManaged = true };
        return (main, workspace);
    }

    private static async Task RozetkaIdOnlyMixedMarketplaceAsync()
    {
        var fixture = new Fixture();
        var prom = BasketOrder() with { ItemListComplete = true };
        var rozetka = RozetkaIdOnlyOrder();
        fixture.Settings.Market.Connections.Add(new()
            { Id = rozetka.Key.ConnectionId, Marketplace = MarketplaceKind.Rozetka, Enabled = true });
        fixture.Source.Orders = [prom]; fixture.Rozetka.Orders = [rozetka];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 10000), Receipt(ReceiptTwo, 2, 20000)];
        fixture.Details.Values[ReceiptOne] = new(ReceiptOne, prom.Items) { ItemListComplete = true };
        fixture.Details.Values[ReceiptTwo] = new(ReceiptTwo, rozetka.Items) { ItemListComplete = true };
        var (main, workspace) = CreateIdOnlyWorkspace(fixture);
        try
        {
            await main.RefreshAllAsync();
            var promReceipt = main.Receipts.Single(row => row.Id == ReceiptOne);
            Equal(ReceiptLinkState.Suggested, promReceipt.OrderMatch!.State);
            Equal(AutomaticLinkBasis.AmountAndProducts, promReceipt.OrderMatch.Basis);
            Equal(prom.Key, promReceipt.OrderMatch.Order!.Key);
            var rozetkaReceipt = main.Receipts.Single(row => row.Id == ReceiptTwo);
            True(rozetkaReceipt.OrderMatch?.Order is null, "Rozetka was linked by matching amount/products without a receipt UUID.");
            True(fixture.Details.RequestedIds.SequenceEqual([ReceiptOne]), "Rozetka triggered an unnecessary receipt basket request.");
            var rozetkaRow = workspace.AllOrderRows.Single(row => row.Key == rozetka.Key);
            True(!rozetkaRow.HasSuggestedLink && !rozetkaRow.HasConfirmedLink && rozetkaRow.LinkedReceipts.Count == 0);
            Equal("Prom", promReceipt.LinkedMarketplace); Equal("", rozetkaReceipt.LinkedMarketplace);
            Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
        }
        finally { main.CancelPendingOperations(); }
    }

    private static async Task RozetkaIdOnlyDropsSavedSuggestionsAsync()
    {
        var fixture = new Fixture(); var automatic = new AutomaticCache();
        var order = RozetkaIdOnlyOrder(); var receipt = Receipt(ReceiptTwo, 2, 20000);
        var account = PrintAccountContext.Create(fixture.Settings.App);
        fixture.Settings.Market.Connections.Clear();
        fixture.Settings.Market.Connections.Add(new()
            { Id = order.Key.ConnectionId, Marketplace = MarketplaceKind.Rozetka, Enabled = true });
        fixture.Rozetka.Orders = [order]; fixture.ReceiptSource.Rows = [receipt];
        fixture.Details.FailIds.Add(receipt.Id);
        await fixture.Cache.SaveAsync(new([order], []));
        // Serialize a previously accepted suggestion without asking the current policy
        // to manufacture one. Pack supplies the actual schema's evidence fingerprints.
        var legacy = new ReceiptOrderMatch(ReceiptLinkState.Suggested, order, "Legacy amount and products", [order])
        {
            Basis = AutomaticLinkBasis.AmountAndProducts, Products = ProductComparison.Match,
            CompetingOrderCount = 1, CompetingReceiptIds = [receipt.Id], GroupOrders = [order]
        };
        var pack = typeof(CachedReceiptOrderMatchingService).GetMethod("Pack", BindingFlags.NonPublic | BindingFlags.Static)!;
        var savedMatch = (CachedReceiptMatch)pack.Invoke(null, [legacy, receipt])!;
        var saved = new AutomaticMatchCache { AccountContext = account, LinkSchema = 1 };
        saved.Links[receipt.Id] = new(savedMatch, DateTimeOffset.UtcNow.AddMinutes(-1));
        saved.Snapshots.Add(new("same-kyiv-day-active-unlinked-unit-symbols-v15", "legacy-graph",
            DateTimeOffset.UtcNow.AddMinutes(-1), new Dictionary<string, CachedReceiptMatch> { [receipt.Id] = savedMatch }));
        await automatic.SaveAsync(account, saved, 30);

        var (main, workspace) = CreateIdOnlyWorkspace(fixture, automatic, automaticMode: false);
        try
        {
            await main.RefreshAsync(); await workspace.EnsureAttachedAsync();
            Equal(0, fixture.Rozetka.FetchCalls);
            True(main.Receipts.Single().OrderMatch?.Order is null, "A serialized Rozetka amount link survived cache loading.");
            True(workspace.AllOrderRows.Single().LinkedReceipts.Count == 0);
            await main.RefreshAllAsync();
            True(main.Receipts.Single().OrderMatch?.Order is null, "F5 recreated an obsolete Rozetka amount link.");
            Equal(0, fixture.Details.Calls);
            var updated = await automatic.LoadAsync(account, 30);
            True(!updated.Links.ContainsKey(receipt.Id), "The rejected Rozetka suggestion remained persisted.");
            var (restarted, restored) = CreateIdOnlyWorkspace(fixture, automatic, automaticMode: false);
            try
            {
                await restarted.RefreshAsync(); await restored.EnsureAttachedAsync();
                True(restarted.Receipts.Single().OrderMatch?.Order is null);
                True(restored.AllOrderRows.Single().LinkedReceipts.Count == 0);
                Equal(0, fixture.Details.Calls);
            }
            finally { restarted.CancelPendingOperations(); }
            Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
        }
        finally { main.CancelPendingOperations(); }
    }

    private static async Task RozetkaIdOnlyExactMismatchAsync()
    {
        const string code = "SYNT-IDONLY";
        foreach (var lookupUuid in new[] { false, true })
        {
            var parsed = lookupUuid ? await FiscalLinkTests.FetchUnavailableRozetkaPrroAsync(code)
                : await FiscalLinkTests.FetchSellerHtml($"https://api.checkbox.in.ua/api/v1/receipts/{ReceiptOne}/html", code);
            var order = parsed with
            {
                Total = 700, CreatedAt = Order().CreatedAt, DeliveryCost = 49,
                Items = [new("Renamed synthetic goods after checkout", "OTHER-SKU", 1, 700, 700)],
                ItemsComplete = true, ItemListComplete = true
            };
            var fixture = new Fixture();
            fixture.Settings.Market.Connections.Clear();
            fixture.Settings.Market.Connections.Add(new()
                { Id = order.Key.ConnectionId, Marketplace = MarketplaceKind.Rozetka, Enabled = true });
            fixture.Rozetka.Orders = [order];
            fixture.ReceiptSource.Rows =
            [
                new() { Id = ReceiptOne, FiscalCode = code, Serial = 1, Status = "DONE", Type = ReceiptTypes.Sell,
                    TotalSumMinor = 10000, FiscalDate = Order().CreatedAt!.Value.AddHours(1) },
                Receipt(ReceiptTwo, 2, 70000)
            ];
            fixture.Details.FailIds.UnionWith([ReceiptOne, ReceiptTwo]);
            var lookupCalls = 0;
            using var http = new HttpClient(new FiscalVerificationHandler(request =>
            {
                Equal("/api/v1/receipts/search", request.RequestUri!.AbsolutePath);
                Equal($"?fiscal_code={code}&self_receipts=true&limit=2&offset=0", request.RequestUri.Query);
                lookupCalls++;
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                {
                    results = new[] { new { id = ReceiptOne, fiscal_code = code, type = "SELL" } }
                }), Encoding.UTF8, "application/json") };
            }));
            var verifier = new FiscalReferenceVerifier(new CheckboxApiClient(http, fixture.Authentication, new Logger()), fixture.Settings);
            var (main, workspace) = CreateIdOnlyWorkspace(fixture, verifier: verifier);
            try
            {
                await main.RefreshAllAsync();
                var linked = main.Receipts.Single(row => row.Id == ReceiptOne);
                Equal(ReceiptLinkState.Exact, linked.OrderMatch!.State); Equal(order.Key, linked.OrderMatch.Order!.Key);
                Equal(100m, linked.Total); Equal(700m, linked.OrderMatch.Order.Total);
                Equal(linked, workspace.AllOrderRows.Single().LinkedReceipts.Single());
                True(main.Receipts.Single(row => row.Id == ReceiptTwo).OrderMatch?.Order is null,
                    "Equal amount overrode the authoritative receipt UUID.");
                Equal(lookupUuid ? 1 : 0, lookupCalls); Equal(0, fixture.Details.Calls);
                if (lookupUuid)
                {
                    var reference = workspace.AllOrderRows.Single().Model.FiscalReferences.Single();
                    Equal(ReceiptOne, reference.VerifiedReceiptId);
                    Equal(PrintAccountContext.Create(fixture.Settings.App), reference.VerifiedAccountContext);
                }
                Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
            }
            finally { main.CancelPendingOperations(); }
        }
    }
}
