using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> RozetkaExactIdScenarios =>
    [
        ("STA Seller HTML receipt UUID survives duplicate accounts, global F5 and serialized restart without guessing from amount", () => StaAsync(RozetkaExactIdRestartAsync)),
        ("STA Seller HTML exact evidence respects alias rejections, suppressions, manual decisions and canceled print exclusion", () => StaAsync(RozetkaExactIdDecisionsAsync))
    ];

    private const string SellerExactCode = "00Ab_Cd-123";
    private static async Task<Fixture> SellerExactFixtureAsync()
    {
        var fixture = DuplicateRozetkaFixture();
        var normalized = await FiscalLinkTests.FetchSellerHtml(
            $"https://api.checkbox.in.ua/api/v1/receipts/{ReceiptOne}/html", SellerExactCode);
        var key = new OrderKey(MarketplaceKind.Rozetka, "rz-test", "100");
        var order = normalized with
        {
            Key = key, Number = "100", Total = 100m, Currency = "", CreatedAt = Order().CreatedAt,
            Items = [new("Name changed after checkout", "new-sku", 1, 100, 100)], ItemsComplete = true,
            FiscalReferences = normalized.FiscalReferences.Select(r => r with { Order = key }).ToArray(),
            Shipments = [new("rozetka_delivery", "RMP-synthetic-100")]
        };
        var alias = order.Key with { ConnectionId = DuplicateRozetkaId };
        var competitor = order with { Key = key with { OrderId = "101" }, Number = "101", FiscalReferences = [],
            FiscalReceiptNumbers = [], FiscalReceiptUrls = [], Shipments = [new("rozetka_delivery", "RMP-synthetic-101")] };
        fixture.Rozetka.Orders = [order, order with
            { Key = alias, FiscalReferences = order.FiscalReferences.Select(r => r with { Order = alias }).ToArray() }, competitor];
        fixture.ReceiptSource.Rows =
        [
            new ReceiptRecord { Id = ReceiptOne, FiscalCode = SellerExactCode, Serial = 1, Status = "DONE", Type = ReceiptTypes.Sell,
                TotalSumMinor = 10000, FiscalDate = Order().CreatedAt!.Value.AddHours(1) },
            Receipt(ReceiptTwo, 2, 10000)
        ];
        fixture.Details.Values[ReceiptOne] = BasketDetails(ReceiptOne); // Original name/SKU contradicts the current seller title.
        fixture.Details.Values[ReceiptTwo] = BasketDetails(ReceiptTwo);
        return fixture;
    }

    private static async Task RozetkaExactIdRestartAsync()
    {
        var fixture = await SellerExactFixtureAsync(); var automatic = new AutomaticCache();
        var (main, workspace) = fixture.Create(autoLinkEnabled: true, automaticCache: automatic);
        await main.RefreshAsync(); await OpenOrdersAsync(main, workspace);
        var receipt = main.Receipts.Single(r => r.Id == ReceiptOne);
        Equal(ReceiptLinkState.Exact, receipt.OrderMatch!.State); Equal("100", receipt.OrderNumber);
        Equal("Точний фіскальний зв’язок", receipt.LinkStatus); True(receipt.LinkExplanation.Contains("За посиланням на чек"));
        Equal(2, workspace.AllOrderRows.Count); Equal(3, fixture.Cache.Snapshot.Orders.Count);
        True(workspace.AllOrderRows.Single(r => r.Number == "100").HasConfirmedLink);
        True(main.Receipts.Single(r => r.Id == ReceiptTwo).OrderMatch?.Order?.Key.OrderId != "100");
        receipt.IsSelectedForOrders = true;
        await ExecuteAsync(main.RefreshAllCommand);
        Equal(ReceiptLinkState.Exact, main.Receipts.Single(r => r.Id == ReceiptOne).OrderMatch!.State);
        True(main.Receipts.Single(r => r.Id == ReceiptOne).IsSelectedForOrders);
        var fetches = fixture.Rozetka.FetchCalls;
        fixture.Rozetka.Fail = true;
        // Manual refresh mode proves the serialized evidence can be restored locally;
        // production's normal automatic activation remains exercised on the first VM.
        var (restarted, restored) = fixture.Create(autoLinkEnabled: false, automaticCache: automatic);
        await restarted.RefreshAsync(); restarted.SelectedTabIndex = 1; await restarted.PrepareOrdersAsync();
        Equal(fetches, fixture.Rozetka.FetchCalls);
        Equal(ReceiptLinkState.Exact, restarted.Receipts.Single(r => r.Id == ReceiptOne).OrderMatch!.State);
        Equal("100", restarted.Receipts.Single(r => r.Id == ReceiptOne).OrderNumber);
        Equal(2, restored.AllOrderRows.Count); Equal(3, fixture.Cache.Snapshot.Orders.Count);
        True(fixture.Cache.Snapshot.Orders.Where(o => o.Number == "100").All(o =>
            o.FiscalReferences.Any(r => r.Kind == FiscalDocumentKeyKind.CheckboxReceiptUrl && r.Source.Contains("type=html"))));
        Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
        Equal(0, fixture.Settings.SecretSaves); Equal(0, fixture.Settings.MarketplaceSaves);
        // Version 1.0.43's optimization graph cannot replay the old parser result.
        // Assigned automatic pairs live separately and survive graph invalidation.
        var account = PrintAccountContext.Create(fixture.Settings.App);
        var previous = new AutomaticMatchCache { AccountContext = account };
        var service = new CachedReceiptOrderMatchingService();
        var previousReceipt = Receipt(ReceiptOne, 1, 10000); var previousOrder = BasketOrder();
        Equal(ReceiptLinkState.Suggested, service.MatchAll(previous, [previousReceipt], account, [previousOrder], [], true).Matches[ReceiptOne].State);
        var savedPair = previous.Links.Values.Single();
        previous.Snapshots = previous.Snapshots.Select(s => s with { AlgorithmVersion = "same-kyiv-day-active-unlinked-allocation-v11" }).ToList();
        True(!service.TryRestore(previous, [previousReceipt], account, [previousOrder], [], out _));
        Equal(0, previous.Snapshots.Count); Equal(savedPair, previous.Links.Values.Single());
        main.CancelPendingOperations(); restarted.CancelPendingOperations();
    }

    private static async Task RozetkaExactIdDecisionsAsync()
    {
        foreach (var mode in new[] { "rejected", "suppressed", "manual", "canceled" })
        {
            var fixture = await SellerExactFixtureAsync();
            var account = PrintAccountContext.Create(fixture.Settings.App);
            var alias = fixture.Rozetka.Orders.Single(o => o.Key.ConnectionId == DuplicateRozetkaId).Key;
            if (mode == "canceled") fixture.Rozetka.Orders = fixture.Rozetka.Orders.Select(o => o.Number == "100"
                ? o with { Status = "Скасовано" } : o).ToArray();
            else await fixture.Links.SaveDecisionAsync(new()
            {
                AccountContext = account, ReceiptId = ReceiptOne, UpdatedAtUtc = DateTimeOffset.UtcNow,
                RejectedOrders = mode == "rejected" ? [alias] : [], SuppressAutomatic = mode == "suppressed",
                ConfirmedOrder = mode == "manual" ? fixture.Rozetka.Orders.Single(o => o.Number == "101").Key : null
            });
            var (main, workspace) = fixture.Create(autoLinkEnabled: true);
            await main.RefreshAsync(); await OpenOrdersAsync(main, workspace);
            var match = main.Receipts.Single(r => r.Id == ReceiptOne).OrderMatch!;
            if (mode == "manual") { Equal(ReceiptLinkState.Manual, match.State); Equal("101", match.Order!.Number); }
            else if (mode == "canceled")
            {
                Equal(ReceiptLinkState.Exact, match.State); True(match.Order!.IsCanceled);
                True(workspace.Orders.Cast<MarketplaceOrderRowViewModel>().All(o => o.Number != "100"));
                workspace.ShowCanceledOrders = true;
                var printer = new LabelPrinter();
                using var labels = Labels(workspace, new(), new(), printer, new());
                await ExecuteAsync(labels.SelectPrintCandidatesCommand);
                True(!workspace.AllOrderRows.Single(o => o.Number == "100").IsSelectedForLabels);
                Equal(0, printer.Calls);
            }
            else True(match.State != ReceiptLinkState.Exact && match.Order?.Number != "100");
            Equal(mode == "canceled" ? 0 : 1, fixture.Links.Saves);
            if (mode == "rejected") Equal(alias, (await fixture.Links.LoadAsync(account)).Single().RejectedOrders.Single());
            Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
            main.CancelPendingOperations();
        }
    }
}
