using System.IO;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static Fixture AutomaticCacheFixture()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [BasketOrder()];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 10000)];
        fixture.Details.Values[ReceiptOne] = BasketDetails(ReceiptOne);
        return fixture;
    }

    private static async Task CachedAutomaticRestartAsync()
    {
        var fixture = AutomaticCacheFixture();
        var cache = new AutomaticCache();
        var (main, workspace) = fixture.Create(automaticCache: cache);
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        Equal(ReceiptLinkState.Suggested, main.Receipts.Single().OrderMatch!.State);
        Equal(AutomaticLinkBasis.AmountAndProducts, main.Receipts.Single().OrderMatch!.Basis);
        Equal(1, fixture.Details.Calls);
        True(cache.Saves > 0);
        var fetches = fixture.Source.FetchCalls;

        // No network success is available after restart. A newly constructed VM has
        // only serialized cache state, not the first VM's receipt-detail dictionary.
        fixture.Source.Fail = true;
        fixture.Details.FailIds.Add(ReceiptOne);
        var (restarted, restored) = fixture.Create(automaticCache: cache);
        await restarted.RefreshAsync();
        restarted.SelectedTabIndex = 1;
        await restarted.PrepareOrdersAsync();
        var row = restarted.Receipts.Single();
        Equal(ReceiptLinkState.Suggested, row.OrderMatch!.State);
        Equal(AutomaticLinkBasis.AmountAndProducts, row.OrderMatch.Basis);
        Equal(fixture.Source.Orders.Single().Key, row.OrderMatch.Order!.Key);
        True(restored.Orders.Cast<MarketplaceOrderRowViewModel>().Single().HasSuggestedLink);
        Equal(fetches, fixture.Source.FetchCalls);
        Equal(1, fixture.Details.Calls);
        await restored.SyncAsync(); // Failed API check must not erase the last-known cached pair.
        Equal(ReceiptLinkState.Suggested, row.OrderMatch!.State);
        var localOrders = fixture.Cache.Snapshot;
        await fixture.Cache.SaveAsync(new(localOrders.Orders.Select(o => o with { Status = "Доставлено" }).ToArray(),
            localOrders.States)); // Unrelated marketplace status must not invalidate the pair.
        var (thirdLaunch, thirdWorkspace) = fixture.Create(automaticCache: cache);
        await thirdLaunch.RefreshAsync();
        await thirdWorkspace.EnsureAttachedAsync();
        Equal(ReceiptLinkState.Suggested, thirdLaunch.Receipts.Single().OrderMatch!.State);
        Equal(fixture.Source.Orders.Single().Key, thirdLaunch.Receipts.Single().OrderMatch!.Order!.Key);
        fixture.Source.Fail = false;
        True(restored.AutoMatchCommand.CanExecute(null));
        await ExecuteAsync(restored.AutoMatchCommand);
        Equal(fetches + 2, fixture.Source.FetchCalls);
        Equal(1, fixture.Details.Calls);
        Equal(ReceiptLinkState.Suggested, row.OrderMatch!.State);
        Equal(0, fixture.Links.Saves);
        Equal(0, fixture.Printer.Calls);
        Equal(0, fixture.History.Saves);
    }

    private static async Task CachedAutomaticNewReceiptAsync()
    {
        var fixture = AutomaticCacheFixture();
        var cache = new AutomaticCache();
        var (main, workspace) = fixture.Create(automaticCache: cache);
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        Equal(1, fixture.Details.Calls);

        var next = BasketOrder() with
        {
            Key = new(MarketplaceKind.Prom, "prom-test", "42"), Number = "ORDER-42", Total = 200m,
            Items = [new("Товар", "SKU", 1, 200, 200)]
        };
        fixture.Source.Orders = [fixture.Source.Orders.Single(), next];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 10000), Receipt(ReceiptTwo, 2, 20000)];
        fixture.Details.Values[ReceiptTwo] = new(ReceiptTwo, [new("Товар", "SKU", 1, 200, 200)]);
        fixture.Details.FailIds.Add(ReceiptOne); // Any unnecessary old-detail read fails this assertion below.
        var (restarted, restored) = fixture.Create(automaticCache: cache);
        await restarted.RefreshAsync();
        restarted.SelectedTabIndex = 1;
        await restarted.PrepareOrdersAsync();
        True(restored.AutoMatchCommand.CanExecute(null), "Refresh automatic links must work before fresh API coverage is available.");
        await ExecuteAsync(restored.AutoMatchCommand);
        True(restarted.Receipts.All(r => r.OrderMatch?.State == ReceiptLinkState.Suggested));
        True(restarted.Receipts.All(r => r.OrderMatch?.Basis == AutomaticLinkBasis.AmountAndProducts));
        Equal(2, fixture.Details.Calls);
        True(fixture.Details.RequestedIds.SequenceEqual([ReceiptOne, ReceiptTwo]));
        Equal(next.Key, restarted.Receipts.Single(r => r.Id == ReceiptTwo).OrderMatch!.Order!.Key);
        Equal(0, fixture.Links.Saves);
        Equal(0, fixture.Printer.Calls);
    }

    private static async Task CachedAutomaticPartialRestartAsync()
    {
        var fixture = AutomaticCacheFixture();
        var cache = new AutomaticCache();
        var (main, workspace) = fixture.Create(automaticCache: cache);
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        Equal(ReceiptLinkState.Suggested, main.Receipts.Single().OrderMatch!.State);

        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 10000), Receipt(ReceiptTwo, 2, 20000)];
        fixture.Details.Values[ReceiptTwo] = new(ReceiptTwo, [new("Товар", "SKU", 1, 200, 200)]);
        var (restarted, restored) = fixture.Create(automaticCache: cache);
        await restarted.RefreshAsync();
        await restored.EnsureAttachedAsync();
        Equal(ReceiptLinkState.Suggested, restarted.Receipts.Single(r => r.Id == ReceiptOne).OrderMatch!.State);
        True(restarted.Receipts.Single(r => r.Id == ReceiptTwo).OrderMatch?.Order is null);

        fixture.Source.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = restored.SyncAsync();
        for (var n = 0; fixture.Source.FetchCalls < 2 && n < 100; n++) await Task.Delay(1);
        True(fixture.Source.FetchCalls >= 2);
        Equal(ReceiptLinkState.Suggested, restarted.Receipts.Single(r => r.Id == ReceiptOne).OrderMatch!.State);
        fixture.Source.Orders = [fixture.Source.Orders.Single(), BasketOrder() with
        {
            Key = new(MarketplaceKind.Prom, "prom-test", "42"), Number = "ORDER-42", Total = 200m,
            Items = [new("Товар", "SKU", 1, 200, 200)]
        }];
        fixture.Source.Gate.SetResult(true);
        await pending;
        True(restarted.Receipts.All(r => r.OrderMatch?.State == ReceiptLinkState.Suggested));
        Equal(0, fixture.Links.Saves);
    }

    private static async Task CachedAutomaticCompetitorAsync()
    {
        foreach (var competingReceipt in new[] { false, true })
        {
            var fixture = AutomaticCacheFixture();
            var cache = new AutomaticCache();
            var original = fixture.Source.Orders.Single();
            var (main, workspace) = fixture.Create(automaticCache: cache);
            await main.RefreshAsync();
            await OpenOrdersAsync(main, workspace);
            Equal(ReceiptLinkState.Suggested, main.Receipts.Single().OrderMatch!.State);
            if (competingReceipt)
            {
                fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 10000), Receipt(ReceiptTwo, 2, 10000)];
                fixture.Details.Values[ReceiptTwo] = BasketDetails(ReceiptTwo);
            }
            else
            {
                fixture.Source.Orders = [original, original with
                {
                    Key = new(MarketplaceKind.Prom, "prom-test", "42"), Number = "OTHER-42"
                }];
                // Another local refresh may have discovered an order before this VM starts.
                await fixture.Cache.SaveAsync(new(fixture.Source.Orders, fixture.Cache.Snapshot.States));
            }
            var (restarted, restored) = fixture.Create(automaticCache: cache);
            await restarted.RefreshAsync();
            await restored.EnsureAttachedAsync();
            True(restarted.Receipts.Single(r => r.Id == ReceiptOne).OrderMatch?.State != ReceiptLinkState.Suggested,
                "A locally visible competitor must block cached restore before API refresh.");
            // Hide the new order/receipt from its table without removing it from the graph.
            restored.OrderSearch = original.Number;
            restarted.OrdersReceiptsTab.SearchText = "1";
            await OpenOrdersAsync(restarted, restored);
            var row = restarted.Receipts.Single(r => r.Id == ReceiptOne);
            Equal(ReceiptLinkState.Candidates, row.OrderMatch!.State);
            True(row.OrderMatch.Order is null && row.OrderMatch.Ambiguous);
            Equal(1, restored.Orders.Cast<object>().Count());
            if (competingReceipt) Equal(1, restarted.OrdersReceiptsTab.View.Cast<object>().Count());
            Equal(0, fixture.Links.Saves);
            Equal(0, fixture.Printer.Calls);
        }
    }

    private static async Task CachedAutomaticDecisionOverridesAsync()
    {
        foreach (var suppress in new[] { false, true })
        {
            var fixture = AutomaticCacheFixture();
            var cache = new AutomaticCache();
            var order = fixture.Source.Orders.Single();
            var (main, workspace) = fixture.Create(automaticCache: cache);
            await main.RefreshAsync();
            await OpenOrdersAsync(main, workspace);
            Equal(ReceiptLinkState.Suggested, main.Receipts.Single().OrderMatch!.State);
            await fixture.Links.SaveDecisionAsync(new()
            {
                AccountContext = PrintAccountContext.Create(fixture.Settings.App), ReceiptId = ReceiptOne,
                ConfirmedOrder = suppress ? null : order.Key, SuppressAutomatic = suppress,
                RejectedOrders = suppress ? [order.Key] : [], UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            var (restarted, restored) = fixture.Create(automaticCache: cache);
            await restarted.RefreshAsync();
            await OpenOrdersAsync(restarted, restored);
            var row = restarted.Receipts.Single();
            if (suppress) True(row.OrderMatch!.Order is null && row.OrderMatch.State != ReceiptLinkState.Suggested);
            else Equal(ReceiptLinkState.Manual, row.OrderMatch!.State);
            Equal(1, fixture.Links.Saves); // Memoization never writes a manual decision.
            Equal(0, fixture.Printer.Calls);
        }
    }

    private static async Task CachedAutomaticFailureIsolationAsync()
    {
        foreach (var failRead in new[] { true, false })
        {
            var fixture = AutomaticCacheFixture();
            var cache = new AutomaticCache { FailLoad = failRead, FailSave = !failRead };
            var (main, workspace) = fixture.Create(automaticCache: cache);
            await main.RefreshAsync();
            await OpenOrdersAsync(main, workspace);
            Equal(1, main.Receipts.Count);
            Equal(ReceiptLinkState.Suggested, main.Receipts.Single().OrderMatch!.State);
            Equal(AutomaticLinkBasis.AmountAndProducts, main.Receipts.Single().OrderMatch!.Basis);
            True(!main.IsBusy && !workspace.IsBusy);
            Equal(0, fixture.Links.Saves);
            Equal(0, fixture.Printer.Calls);
            Equal(0, fixture.History.Saves);
            Equal(0, fixture.Dialogs.Errors.Count);
        }
    }

    private static async Task CachedAutomaticCoverageAsync()
    {
        var fixture = AutomaticCacheFixture();
        var cache = new AutomaticCache();
        var (main, workspace) = fixture.Create(automaticCache: cache);
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        Equal(ReceiptLinkState.Suggested, main.Receipts.Single().OrderMatch!.State);
        fixture.Settings.Market.Connections.Add(new()
        {
            Id = "rz-test", Marketplace = MarketplaceKind.Rozetka, Enabled = true, Name = "New unverified shop"
        });
        fixture.Rozetka.Complete = false;
        var (restarted, restored) = fixture.Create(automaticCache: cache);
        await restarted.RefreshAsync();
        restarted.SelectedTabIndex = 1;
        await restarted.PrepareOrdersAsync();
        True(restarted.Receipts.Single().OrderMatch?.State != ReceiptLinkState.Suggested,
            "An empty newly enabled shop cannot inherit the old complete coverage merely because order arrays match.");
        await restored.SyncAsync();
        Equal(ReceiptLinkState.Incomplete, restarted.Receipts.Single().OrderMatch!.State);
        fixture.Rozetka.Complete = true;
        await restored.SyncAsync();
        Equal(ReceiptLinkState.Suggested, restarted.Receipts.Single().OrderMatch!.State);
        Equal(1, fixture.Details.Calls);
        Equal(0, fixture.Links.Saves);
    }

    private static async Task CachedAutomaticChangedReceiptAsync()
    {
        var fixture = AutomaticCacheFixture();
        var cache = new AutomaticCache();
        var (main, workspace) = fixture.Create(automaticCache: cache);
        await main.RefreshAsync();
        await OpenOrdersAsync(main, workspace);
        Equal(ReceiptLinkState.Suggested, main.Receipts.Single().OrderMatch!.State);
        Equal(1, fixture.Details.Calls);

        // Same account and ID, but different matching input. A stale in-memory detail
        // dictionary must not bypass the persisted entry's receipt fingerprint check.
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 20000)];
        fixture.Source.Orders = [BasketOrder() with
        {
            Total = 200m, Items = [new("Товар", "SKU", 1, 200, 200)]
        }];
        fixture.Details.Values[ReceiptOne] = new(ReceiptOne, [new("Товар", "SKU", 2, 100, 200)]);
        await main.RefreshAsync();
        await workspace.EnsureAttachedAsync();
        True(main.Receipts.Single().OrderMatch?.State != ReceiptLinkState.Suggested,
            "A changed same-ID receipt must not inherit its previous cached link before API refresh.");
        await workspace.SyncAsync();
        var row = main.Receipts.Single();
        Equal(2, fixture.Details.Calls);
        Equal(ProductComparison.Contradiction, row.OrderMatch!.Products);
        True(row.OrderMatch.Order is null && row.OrderMatch.State != ReceiptLinkState.Suggested);
        Equal(0, fixture.Links.Saves);
        Equal(0, fixture.Printer.Calls);
    }

    private sealed class AutomaticCache : IAutomaticMatchCacheStore
    {
        private readonly Dictionary<string, string> _accounts = new(StringComparer.Ordinal);
        public bool FailLoad, FailSave;
        public int Loads, Saves;

        public Task<AutomaticMatchCache> LoadAsync(string accountContext, int retentionDays,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Loads++;
            if (FailLoad) return Task.FromException<AutomaticMatchCache>(new IOException("Synthetic automatic cache read failure."));
            // JSON round trip is intentional: restart cannot share mutable object references.
            var result = _accounts.TryGetValue(accountContext, out var json)
                ? JsonSerializer.Deserialize<AutomaticMatchCache>(json)!
                : new() { AccountContext = accountContext, RetentionDays = retentionDays };
            return Task.FromResult(result);
        }

        public Task SaveAsync(string accountContext, AutomaticMatchCache cache, int retentionDays,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Saves++;
            if (FailSave) return Task.FromException(new IOException("Synthetic automatic cache write failure."));
            Equal(accountContext, cache.AccountContext);
            _accounts[accountContext] = JsonSerializer.Serialize(cache);
            return Task.CompletedTask;
        }
    }
}
