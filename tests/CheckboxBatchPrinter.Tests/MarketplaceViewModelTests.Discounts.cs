using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task DiscountCacheRefreshAsync()
    {
        var fixture = new Fixture(); var store = new AutomaticCache();
        var data = DiscountMatchingTests.Parse(DiscountMatchingTests.FixtureJson(ReceiptOne));
        fixture.Source.Orders = [data.Order]; fixture.ReceiptSource.Rows = [data.Receipt];
        fixture.Details.Values[ReceiptOne] = data.Details;
        var account = PrintAccountContext.Create(fixture.Settings.App);
        var oldCache = new AutomaticMatchCache { AccountContext = account };
        new CachedReceiptOrderMatchingService().RememberDetails(oldCache, account, data.Receipt,
            data.Details with { AmountValidationVersion = 0, DiscountReconciliation = null, ItemsComplete = false,
                AmountComparisonIssue = "Сума позицій чека відрізняється від загальної: потрібна перевірка структури оплати." });
        await store.SaveAsync(account, oldCache, 30);
        var (main, workspace) = fixture.Create(automaticCache: store);
        main.DateFrom = main.DateTo = new(2026, 10, 13);
        await main.RefreshAsync(); await OpenOrdersAsync(main, workspace);
        Equal(1, fixture.Details.Calls);
        Equal(ReceiptLinkState.Suggested, main.Receipts.Single().OrderMatch!.State);
        True(main.Receipts.Single().OrderMatch!.Explanation.Contains("320 − 16 = 304"));
        fixture.Details.FailIds.Add(ReceiptOne);
        var (restarted, restored) = fixture.Create(automaticCache: store);
        restarted.DateFrom = restarted.DateTo = new(2026, 10, 13);
        await restarted.RefreshAsync(); await OpenOrdersAsync(restarted, restored);
        Equal(1, fixture.Details.Calls);
        Equal(ReceiptLinkState.Suggested, restarted.Receipts.Single().OrderMatch!.State);
        Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
    }
}
