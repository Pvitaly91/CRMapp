using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task BackgroundRecentLinksAsync()
    {
        var fixture = new Fixture();
        fixture.ReceiptSource.FilterDates = true;
        fixture.Source.Orders = [Order() with
        {
            CreatedAt = new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.FromHours(3))
        }];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 10000, 30)];
        var cache = new AutomaticCache();
        var (main, workspace) = fixture.Create(autoLinkEnabled: true, automaticCache: cache);
        main.DateFrom = new DateTime(2026, 9, 1);
        main.DateTo = new DateTime(2026, 9, 30);
        main.CoordinatorManaged = true;
        workspace.CoordinatorManaged = true;
        await main.RefreshAsync();
        await workspace.EnsureAttachedAsync();
        Equal(0, fixture.Source.FetchCalls);
        Equal(0, main.SelectedTabIndex); // Hidden marketplace tab; no manual matching command.
        var row = main.Receipts.Single();
        row.IsSelectedForOrders = true;
        main.SearchText = "no visible receipts";
        workspace.OrderSearch = "no visible orders";
        await using var coordinator = new BackgroundSyncCoordinator(
            token => main.BackgroundRefreshAsync(new(2026, 9, 24), new(2026, 9, 30), token),
            token =>
            {
                workspace.SetBackgroundRange(new(2026, 9, 24), new(2026, 9, 30));
                return workspace.SyncBackgroundAsync(token);
            }, token => Task.FromResult(fixture.Settings.App));
        await coordinator.RefreshNowAsync();
        Equal(ReceiptLinkState.Suggested, row.OrderMatch!.State);
        Equal(fixture.Source.Orders.Single().Key, row.OrderMatch.Order!.Key);
        True(workspace.AllOrderRows.Single().HasSuggestedLink);
        True(workspace.AllOrderRows.Single().LinkedReceipts.Contains(row));
        Equal(new DateTime(2026, 9, 1), main.DateFrom);
        Equal(new DateTime(2026, 9, 30), main.DateTo);
        Equal("no visible receipts", main.SearchText);
        Equal("no visible orders", workspace.OrderSearch);
        True(row.IsSelectedForOrders);
        Equal(1, fixture.Source.FetchCalls);
        True(cache.Saves > 0);
        Equal(0, fixture.Links.Saves);
        Equal(0, fixture.Printer.Calls);
    }

    private static async Task BackgroundPartialLinksAsync()
    {
        var fixture = AutomaticCacheFixture();
        var cache = new AutomaticCache();
        var (main, workspace) = fixture.Create(autoLinkEnabled: true, automaticCache: cache);
        main.CoordinatorManaged = true;
        workspace.CoordinatorManaged = true;
        await main.RefreshAsync();
        workspace.SetBackgroundRange(new(2026, 9, 23), new(2026, 9, 23));
        await main.BackgroundRefreshAsync(new(2026, 9, 23), new(2026, 9, 23));
        await workspace.SyncBackgroundAsync();
        Equal(ReceiptLinkState.Suggested, main.Receipts.Single().OrderMatch!.State);

        // Restore into new VMs and receive a partial API list with an unrelated new pair.
        fixture.Source.Orders = [fixture.Source.Orders.Single(), Order() with
        {
            Key = new(MarketplaceKind.Prom, "prom-test", "new-order"), Number = "NEW-355", Total = 355
        }];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 10000), Receipt(ReceiptTwo, 2, 35500)];
        fixture.Source.Complete = false;
        var (restarted, restored) = fixture.Create(autoLinkEnabled: true, automaticCache: cache);
        restarted.CoordinatorManaged = true;
        restored.CoordinatorManaged = true;
        await restarted.RefreshAsync();
        await restored.EnsureAttachedAsync();
        restored.SetBackgroundRange(new(2026, 9, 23), new(2026, 9, 23));
        await restarted.BackgroundRefreshAsync(new(2026, 9, 23), new(2026, 9, 23));
        try { await restored.SyncBackgroundAsync(); }
        catch (InvalidOperationException) { }
        var old = restarted.Receipts.Single(r => r.Id == ReceiptOne);
        var added = restarted.Receipts.Single(r => r.Id == ReceiptTwo);
        Equal(ReceiptLinkState.Suggested, old.OrderMatch!.State);
        True(old.OrderMatch.Explanation.Contains("Збережений"));
        True(added.OrderMatch?.State != ReceiptLinkState.Suggested);
        True(!restored.AllOrderRows.Single(o => o.Number == "NEW-355").HasSuggestedLink);
        fixture.Source.Complete = true;
        await restored.SyncBackgroundAsync();
        Equal(ReceiptLinkState.Suggested, added.OrderMatch!.State);
        True(restored.AllOrderRows.Single(o => o.Number == "NEW-355").HasSuggestedLink);
        Equal(0, fixture.Links.Saves);
        Equal(0, fixture.Printer.Calls);
    }

    private static async Task BackgroundCompetingLinksAsync()
    {
        var fixture = AutomaticCacheFixture();
        var cache = new AutomaticCache();
        var (main, workspace) = fixture.Create(autoLinkEnabled: true, automaticCache: cache);
        main.CoordinatorManaged = true;
        workspace.CoordinatorManaged = true;
        await main.RefreshAsync();
        workspace.SetBackgroundRange(new(2026, 9, 23), new(2026, 9, 23));
        await main.BackgroundRefreshAsync(new(2026, 9, 23), new(2026, 9, 23));
        await workspace.SyncBackgroundAsync();
        var first = main.Receipts.Single();
        Equal(ReceiptLinkState.Suggested, first.OrderMatch!.State);
        main.SearchText = first.Serial.ToString();
        first.IsSelectedForOrders = true;
        fixture.ReceiptSource.Rows = [fixture.ReceiptSource.Rows.Single(), Receipt(ReceiptTwo, 2, 10000)];
        fixture.Details.Values[ReceiptTwo] = BasketDetails(ReceiptTwo);
        await main.BackgroundRefreshAsync(new(2026, 9, 23), new(2026, 9, 23));
        await workspace.SyncBackgroundAsync();
        True(main.Receipts.All(r => r.OrderMatch!.Ambiguous && r.OrderMatch.Order is null));
        True(!workspace.AllOrderRows.Single().HasSuggestedLink);
        True(first.IsSelectedForOrders);
        Equal(0, fixture.Links.Saves);
        Equal(0, fixture.Printer.Calls);
    }
}
