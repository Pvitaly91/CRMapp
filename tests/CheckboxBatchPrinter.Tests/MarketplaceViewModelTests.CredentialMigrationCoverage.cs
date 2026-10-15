using System.Net;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task CredentialMigrationCoverageAsync()
    {
        var fixture = new Fixture();
        var automatic = new AutomaticCache();
        var account = PrintAccountContext.Create(fixture.Settings.App);
        string[] aliases = ["10000000000000000000000000000001", "10000000000000000000000000000002",
            "10000000000000000000000000000003", "10000000000000000000000000000004"];
        foreach (var id in aliases)
        {
            fixture.Settings.Market.Connections.Add(new()
                { Id = id, Marketplace = MarketplaceKind.Rozetka, Enabled = true, Name = "Synthetic same seller" });
            fixture.Settings.Secrets[id] = new(Login: "synthetic-legacy-seller", Password: "synthetic-legacy-password");
        }
        fixture.Settings.Secrets[aliases[^1]] = new(Token: "synthetic-current-api-token");

        string[] receiptIds = Enumerable.Range(1, 6).Select(index => $"10000000-0000-0000-0000-{index:000000000000}").ToArray();
        decimal[] promAmounts = [141, 160, 400, 300];
        string[] promNumbers = ["433727692", "433724234", "433723988", "433721651"];
        var prom = promAmounts.Select((amount, index) => BasketOrder() with
        {
            Key = new(MarketplaceKind.Prom, "prom-test", promNumbers[index]), Number = promNumbers[index],
            Total = amount, Items = [new("Synthetic Prom goods " + index, "SKU-" + index, 1, amount, amount)]
        }).ToArray();
        fixture.Source.Orders = prom;

        var parsed = await FiscalLinkTests.FetchSellerHtml(
            $"https://api.checkbox.in.ua/api/v1/receipts/{receiptIds[4]}/html", "SyntheticRzCode");
        var originalKey = new OrderKey(MarketplaceKind.Rozetka, aliases[0], "908836284");
        var exact = parsed with
        {
            Key = originalKey, Number = originalKey.OrderId, Total = 380, Currency = "",
            CreatedAt = Order().CreatedAt, RetrievedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            FiscalReferences = parsed.FiscalReferences.Select(reference => reference with { Order = originalKey }).ToArray()
        };
        var manual = exact with
        {
            Key = originalKey with { OrderId = "908826485" }, Number = "908826485", Total = 649,
            ReceiptIds = [], FiscalReceiptUrls = [], FiscalReceiptNumbers = [], FiscalReferences = [],
            Shipments = [new("rozetka_delivery", "RMP-synthetic-migration-manual")]
        };
        var legacyManualKey = manual.Key with { ConnectionId = aliases[1] };
        await fixture.Links.SaveDecisionAsync(new()
        {
            AccountContext = account, ReceiptId = receiptIds[5], ConfirmedOrder = legacyManualKey,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        var cachedRozetka = aliases.SelectMany(id => new[] { exact, manual }.Select(order => ProjectMigrationOrder(order, id))).ToArray();
        await fixture.Cache.SaveAsync(new(prom.Concat(cachedRozetka).ToArray(), []));
        fixture.ReceiptSource.Rows = promAmounts.Concat([380m, 649m]).Select((amount, index) => new ReceiptRecord
        {
            Id = receiptIds[index], Serial = index + 1, Status = "DONE", Type = ReceiptTypes.Sell,
            TotalSumMinor = (long)(amount * 100), FiscalCode = index == 4 ? "SyntheticRzCode" : "",
            FiscalDate = Order().CreatedAt!.Value.AddHours(index + 1)
        }).ToArray();
        foreach (var (order, index) in prom.Select((order, index) => (order, index)))
            fixture.Details.Values[receiptIds[index]] = new(receiptIds[index], order.Items) { ItemListComplete = true };

        // A previously completed pair remains visible while NEW pairs are correctly
        // withheld when enabled legacy connections cannot prove source completeness.
        var saved = new AutomaticMatchCache { AccountContext = account };
        Equal(ReceiptLinkState.Suggested, new CachedReceiptOrderMatchingService().MatchAll(saved,
            [fixture.ReceiptSource.Rows[3]], account, [prom[3]], [], true).Matches[receiptIds[3]].State);
        await automatic.SaveAsync(account, saved, 30);
        var source = new MigrationCoverageSource([exact, manual]);
        (MainViewModel Main, MarketplaceWorkspaceViewModel Workspace) Create()
        {
            var sync = new MarketplaceSyncService([fixture.Source, source], fixture.Settings, fixture.Cache);
            var workspace = new MarketplaceWorkspaceViewModel(fixture.Settings, sync, fixture.Links, fixture.Details,
                fixture.Dialogs, autoLinkEnabled: false, automaticCache: automatic);
            var main = new MainViewModel(fixture.ReceiptSource, fixture.Images, fixture.Settings, fixture.Authentication,
                fixture.History, fixture.Printer, fixture.Dialogs, new Logger(), workspace)
                { DateFrom = new(2026, 9, 23), DateTo = new(2026, 9, 23) };
            return (main, workspace);
        }

        var (main, workspace) = Create();
        await main.RefreshAsync();
        main.SelectedTabIndex = 1; await main.PrepareOrdersAsync();
        await ExecuteAsync(main.RefreshAllCommand);
        Equal(8, workspace.AllOrderRows.Count(row => row.Key.Marketplace == MarketplaceKind.Rozetka));
        Equal(3, fixture.Cache.Snapshot.States.Count(state => !state.Complete));
        Equal(ReceiptLinkState.Suggested, main.Receipts.Single(row => row.Id == receiptIds[3]).OrderMatch!.State);
        foreach (var id in receiptIds.Take(3))
            True(main.Receipts.Single(row => row.Id == id).OrderMatch?.Order is null,
                "A failed separate source must not be ignored to manufacture new Prom links.");
        Equal(ReceiptLinkState.Manual, main.Receipts.Single(row => row.Id == receiptIds[5]).OrderMatch!.State);

        // The user has confirmed these accounts as aliases and retained only the
        // current API-token connection. Durable mappings keep cache and manual
        // decisions under their original keys; coverage still comes from sync.
        foreach (var id in aliases.SkipLast(1))
        {
            fixture.Settings.Market.Connections.RemoveAll(connection => connection.Id == id);
            fixture.Settings.Market.ConnectionAliases.Add(new(MarketplaceKind.Rozetka, id, aliases[^1]));
        }
        var fetches = source.SuccessfulFetches;
        await ExecuteAsync(main.RefreshAllCommand);
        Equal(fetches + 1, source.SuccessfulFetches);
        Equal(2, workspace.AllOrderRows.Count(row => row.Key.Marketplace == MarketplaceKind.Rozetka));
        True(fixture.Settings.Market.Connections.All(connection => fixture.Cache.Snapshot.States.Any(state =>
            state.ConnectionId == connection.Id && state.Complete)));
        foreach (var (order, index) in prom.Select((order, index) => (order, index)))
        {
            var match = main.Receipts.Single(row => row.Id == receiptIds[index]).OrderMatch!;
            Equal(ReceiptLinkState.Suggested, match.State); Equal(order.Key, match.Order!.Key);
        }
        var exactMatch = main.Receipts.Single(row => row.Id == receiptIds[4]).OrderMatch!;
        Equal(ReceiptLinkState.Exact, exactMatch.State); Equal(originalKey with { ConnectionId = aliases[^1] }, exactMatch.Order!.Key);
        Equal(manual.Key with { ConnectionId = aliases[^1] }, main.Receipts.Single(row => row.Id == receiptIds[5]).OrderMatch!.Order!.Key);
        Equal(legacyManualKey, (await fixture.Links.LoadAsync(account)).Single().ConfirmedOrder);
        Equal(8, fixture.Cache.Snapshot.Orders.Count(order => order.Key.Marketplace == MarketplaceKind.Rozetka));
        True(fixture.Cache.Snapshot.Orders.Where(order => order.Number == exact.Number).All(order =>
            order.FiscalReceiptUrls.SequenceEqual(exact.FiscalReceiptUrls) && order.FiscalReferences.Any(reference =>
                reference.Source.Contains("type=link") && reference.Order == order.Key)));

        var labelHistory = new ShippingLabelsTests.History(); var labelPrinter = new LabelPrinter();
        var legacyAttempt = new LabelPrintAttempt("synthetic-migration-attempt", "synthetic-job", "synthetic-printer",
            DateTimeOffset.UtcNow, LabelSubmissionState.Submitted,
            [new(ShippingCarrier.RozetkaDelivery, legacyManualKey.ConnectionId, manual.Shipments.Single().TrackingNumber,
                "synthetic-document", 1, "synthetic-hash") { Order = legacyManualKey }], 42);
        await labelHistory.SaveAsync(legacyAttempt);
        using var labels = Labels(workspace, labelHistory, new(), labelPrinter, new());
        await ExecuteAsync(labels.SelectPrintCandidatesCommand);
        var migratedManualRow = workspace.AllOrderRows.Single(row => row.Number == manual.Number);
        True(migratedManualRow.HasPrintedLabel && !migratedManualRow.IsSelectedForLabels,
            "Order-scoped history on a removed connection must prevent duplicate printing on its retained alias.");

        var (restarted, restored) = Create();
        await restarted.RefreshAsync(); restarted.SelectedTabIndex = 1; await restarted.PrepareOrdersAsync();
        Equal(2, restored.AllOrderRows.Count(row => row.Key.Marketplace == MarketplaceKind.Rozetka));
        Equal(ReceiptLinkState.Exact, restarted.Receipts.Single(row => row.Id == receiptIds[4]).OrderMatch!.State);
        foreach (var id in receiptIds.Take(4))
            Equal(ReceiptLinkState.Suggested, restarted.Receipts.Single(row => row.Id == id).OrderMatch!.State);
        using var restoredLabels = Labels(restored, labelHistory, new(), labelPrinter, new());
        await ExecuteAsync(restoredLabels.SelectPrintCandidatesCommand);
        True(restored.AllOrderRows.Single(row => row.Number == manual.Number).HasPrintedLabel);
        True(!restored.AllOrderRows.Single(row => row.Number == manual.Number).IsSelectedForLabels);
        Equal(legacyManualKey, labelHistory.Attempts.Single().Pages.Single().Order);
        Equal(0, labelPrinter.Calls);
        Equal(legacyManualKey, (await fixture.Links.LoadAsync(account)).Single().ConfirmedOrder);
        Equal(1, fixture.Links.Saves); Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
        main.CancelPendingOperations(); restarted.CancelPendingOperations();
    }

    private static MarketplaceOrder ProjectMigrationOrder(MarketplaceOrder order, string connectionId)
    {
        var key = order.Key with { ConnectionId = connectionId };
        return order with { Key = key, FiscalReferences = order.FiscalReferences.Select(reference => reference with { Order = key }).ToArray() };
    }

    private static void SeedLegacyAmountLink(AutomaticMatchCache cache, ReceiptRecord receipt, MarketplaceOrder order)
    {
        var legacy = new ReceiptOrderMatch(ReceiptLinkState.Suggested, order, "Legacy amount-based link", [order])
            { Basis = AutomaticLinkBasis.UniqueAmount };
        var packed = (CachedReceiptMatch)typeof(CachedReceiptOrderMatchingService)
            .GetMethod("Pack", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [legacy, receipt])!;
        cache.Links[receipt.Id] = new(packed, DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    private static async Task CredentialAliasSavedSuggestionAsync()
    {
        const string oldId = "20000000000000000000000000000001", currentId = "20000000000000000000000000000002";
        var fixture = new Fixture(); var automatic = new AutomaticCache();
        var account = PrintAccountContext.Create(fixture.Settings.App);
        fixture.Settings.Market.Connections.Add(new() { Id = currentId, Marketplace = MarketplaceKind.Rozetka,
            Name = "Synthetic retained seller", Enabled = true });
        fixture.Settings.Market.ConnectionAliases.Add(new(MarketplaceKind.Rozetka, oldId, currentId));
        var original = BasketOrder() with { Key = new(MarketplaceKind.Rozetka, oldId, "100"), Number = "100" };
        var canonical = ProjectMigrationOrder(original, currentId) with { RetrievedAtUtc = original.RetrievedAtUtc.AddMinutes(1) };
        var competitor = canonical with { Key = canonical.Key with { OrderId = "101" }, Number = "101" };
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 10000)];
        var saved = new AutomaticMatchCache { AccountContext = account };
        SeedLegacyAmountLink(saved, fixture.ReceiptSource.Rows.Single(), original);
        await automatic.SaveAsync(account, saved, 30);
        await fixture.Cache.SaveAsync(new([original, canonical, competitor], []));
        fixture.Rozetka.Orders = [canonical, competitor]; fixture.Rozetka.Complete = false;
        var (main, workspace) = fixture.Create(automaticCache: automatic);
        await main.RefreshAsync(); main.SelectedTabIndex = 1; await main.PrepareOrdersAsync();
        True(main.Receipts.Single().OrderMatch?.Order is null);
        await ExecuteAsync(main.RefreshAllCommand);
        True(main.Receipts.Single().OrderMatch?.Order is null);
        True(!fixture.Cache.Snapshot.States.Single(state => state.ConnectionId == currentId).Complete);
        Equal(2, workspace.AllOrderRows.Count);
        Equal(0, fixture.Details.Calls);
        var stored = await automatic.LoadAsync(account, 30);
        True(!stored.Links.ContainsKey(ReceiptOne));

        var (restarted, restored) = fixture.Create(automaticCache: automatic);
        await restarted.RefreshAsync(); restarted.SelectedTabIndex = 1; await restarted.PrepareOrdersAsync();
        True(restarted.Receipts.Single().OrderMatch?.Order is null);
        Equal(2, restored.AllOrderRows.Count); Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls);
        main.CancelPendingOperations(); restarted.CancelPendingOperations();
    }

    private static Task CredentialAliasFingerprintGuardsAsync()
    {
        const string account = "synthetic-alias-account", oldId = "old", targetId = "target";
        OrderKey Normalize(OrderKey key) => key.ConnectionId == oldId ? key with { ConnectionId = targetId } : key;
        var original = BasketOrder() with { Key = new(MarketplaceKind.Prom, oldId, "100") };
        var canonical = ProjectMigrationOrder(original, targetId);
        var receipt = Receipt(ReceiptOne, 1, 10000);
        foreach (var mode in new[] { "source-changed", "canonical-changed", "fiscal-added", "canceled", "unrelated-account" })
        {
            var cache = new AutomaticMatchCache { AccountContext = account };
            var service = new CachedReceiptOrderMatchingService();
            Equal(ReceiptLinkState.Suggested, service.MatchAll(cache, [receipt], account, [original], [], true).Matches[ReceiptOne].State);
            var source = mode == "source-changed" ? original with { Total = 101 } : original;
            var current = mode switch
            {
                "canonical-changed" => canonical with { Items = [new("Different goods", "SKU", 1, 100, 100)] },
                "fiscal-added" => canonical with { ReceiptIds = [ReceiptTwo] },
                "canceled" => canonical with { Status = "Скасовано" },
                _ => canonical
            };
            service.NormalizeConnectionAliases(cache, mode == "unrelated-account" ? "other-account" : account,
                [source], [current], Normalize);
            Equal(original.Key, cache.Links[ReceiptOne].Match.Order);
        }

        var conflicting = new AutomaticMatchCache { AccountContext = account };
        var matcher = new CachedReceiptOrderMatchingService();
        var otherReceipt = Receipt(ReceiptTwo, 2, 10000);
        matcher.MatchAll(conflicting, [receipt], account, [original], [], true);
        var secondCache = new AutomaticMatchCache { AccountContext = account };
        matcher.MatchAll(secondCache, [otherReceipt], account, [canonical], [], true);
        conflicting.Links[ReceiptTwo] = secondCache.Links[ReceiptTwo];
        matcher.NormalizeConnectionAliases(conflicting, account, [original, canonical], [canonical], Normalize);
        var result = matcher.MatchAll(conflicting, [receipt, otherReceipt], account, [canonical], [], false).Matches;
        True(result.Values.All(match => match.Order is null), "Alias migration must not choose between conflicting saved receipt pairs.");
        return Task.CompletedTask;
    }

    private static async Task CredentialAliasColdStartFiscalAsync()
    {
        const string oldId = "30000000000000000000000000000001", currentId = "30000000000000000000000000000002";
        var fixture = new Fixture(); var automatic = new AutomaticCache();
        var account = PrintAccountContext.Create(fixture.Settings.App);
        fixture.Settings.Market.Connections.Add(new() { Id = currentId, Marketplace = MarketplaceKind.Rozetka,
            Name = "Synthetic retained seller", Enabled = true });
        fixture.Settings.Market.ConnectionAliases.Add(new(MarketplaceKind.Rozetka, oldId, currentId));
        var original = BasketOrder() with { Key = new(MarketplaceKind.Rozetka, oldId, "100"), Number = "100" };
        var canonical = ProjectMigrationOrder(original, currentId) with { RetrievedAtUtc = original.RetrievedAtUtc.AddMinutes(1) };
        var competitor = canonical with { Key = canonical.Key with { OrderId = "101" }, Number = "101" };
        var unrelatedFiscal = VerificationOrder("unrelated-current", Order().CreatedAt, "SYNTHETIC-UNRELATED") with { Total = 200 };
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 10000)];
        fixture.Source.Orders = [unrelatedFiscal];
        fixture.Rozetka.Orders = [canonical, competitor]; fixture.Rozetka.Complete = false;
        var saved = new AutomaticMatchCache { AccountContext = account };
        SeedLegacyAmountLink(saved, fixture.ReceiptSource.Rows.Single(), original);
        await automatic.SaveAsync(account, saved, 30);
        await fixture.Cache.SaveAsync(new([original, canonical, competitor], []));
        var verifier = new ScopedFiscalVerifier();
        var sync = new MarketplaceSyncService([fixture.Source, fixture.Rozetka], fixture.Settings, fixture.Cache);
        var workspace = new MarketplaceWorkspaceViewModel(fixture.Settings, sync, fixture.Links, fixture.Details,
            fixture.Dialogs, fiscalVerifier: verifier, autoLinkEnabled: false, automaticCache: automatic);
        var main = new MainViewModel(fixture.ReceiptSource, fixture.Images, fixture.Settings, fixture.Authentication,
            fixture.History, fixture.Printer, fixture.Dialogs, new Logger(), workspace)
            { DateFrom = new(2026, 9, 23), DateTo = new(2026, 9, 23) };
        try
        {
            Equal(0, workspace.AllOrderRows.Count);
            // No earlier tab activation/local attachment may normalize the saved
            // pair first: reproduce the production cold-start F5 publication path.
            await main.RefreshAllAsync();
            Equal(1, verifier.Calls.Count);
            Equal(unrelatedFiscal.Key, verifier.Calls.Single().Single().Key);
            True(main.Receipts.Single().OrderMatch?.Order is null);
            Equal(3, workspace.AllOrderRows.Count);
            True(!(await automatic.LoadAsync(account, 30)).Links.ContainsKey(ReceiptOne));
            True(!fixture.Cache.Snapshot.States.Single(state => state.ConnectionId == currentId).Complete);
            Equal(0, fixture.Details.Calls); Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls);
        }
        finally { main.CancelPendingOperations(); }
    }

    private sealed class MigrationCoverageSource(IReadOnlyList<MarketplaceOrder> orders) : IMarketplaceOrdersClient
    {
        public MarketplaceKind Marketplace => MarketplaceKind.Rozetka;
        public int SuccessfulFetches;
        public Task TestConnectionAsync(MarketplaceConnection connection, MarketplaceCredentials credentials, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<OrdersFetchResult> FetchAsync(MarketplaceConnection connection, MarketplaceCredentials credentials,
            MarketplaceRange range, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!RozetkaApiCredentials.ValidToken(credentials.Token))
                throw new MarketplaceApiException("Synthetic legacy credentials require API-token migration.", HttpStatusCode.Unauthorized);
            SuccessfulFetches++;
            return Task.FromResult(new OrdersFetchResult(orders.Select(order => Current(order, connection.Id)).ToArray(), true));
        }
        public Task<MarketplaceOrder?> GetOrderAsync(MarketplaceConnection connection, MarketplaceCredentials credentials,
            string orderId, CancellationToken ct = default) => Task.FromResult<MarketplaceOrder?>(
                orders.FirstOrDefault(order => order.Key.OrderId == orderId) is { } order ? Current(order, connection.Id) : null);
        private static MarketplaceOrder Current(MarketplaceOrder order, string id) => ProjectMigrationOrder(order, id) with
        {
            // Optional PRRO is temporarily unavailable: known immutable evidence
            // must survive the list refresh and credential alias normalization.
            ReceiptIds = [], FiscalReceiptUrls = [], FiscalReceiptNumbers = [], FiscalReferences = [],
            FiscalDataStatus = "Synthetic PRRO read paused", RetrievedAtUtc = DateTimeOffset.UtcNow
        };
    }
}
