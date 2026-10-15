using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;
using CheckboxBatchPrinter.Views;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> RozetkaDuplicateScenarios =>
    [
        ("STA identical Rozetka accounts collapse cached rows without changing manual alias links after F5 and restart", () => StaAsync(RozetkaDuplicateCachedManualAsync)),
        ("STA duplicate Rozetka baskets remain unlinked without receipt ID and an alias rejection cannot be bypassed", () => StaAsync(RozetkaDuplicateBasketAsync)),
        ("STA changed Rozetka API token separates rows and restores the original saved manual connection key", () => StaAsync(RozetkaDuplicateTokenChangeAsync)),
        ("Rozetka deduplication requires complete readable exact credentials and an enabled account", RozetkaDuplicateCredentialProofAsync),
        ("Rozetka duplicate primary is deterministic while other accounts and Prom identities stay separate", RozetkaDuplicateIdentityBoundariesAsync),
        ("Rozetka duplicate projection preserves contradictory fiscal evidence and the newest order fields", RozetkaDuplicateEvidenceAsync),
        ("Rozetka decision normalization preserves scope suppression and original persisted keys", RozetkaDuplicateDecisionsAsync),
        ("explicit historical marketplace aliases retain old keys while following token-equivalent canonical accounts", HistoricalConnectionAliasesAsync),
        ("marketplace settings reject invalid historical alias graphs without replacing the stored configuration", HistoricalConnectionAliasValidationAsync),
        ("STA one Rozetka print candidate respects submitted history across duplicate projection and restart", () => StaAsync(RozetkaDuplicatePrintCandidatesAsync))
    ];

    private const string DuplicateRozetkaId = "rz-z-duplicate";
    private static Fixture DuplicateRozetkaFixture()
    {
        var fixture = new Fixture();
        fixture.Settings.Market.Connections.Clear();
        // Reverse insertion proves that the canonical ID does not depend on settings order.
        fixture.Settings.Market.Connections.Add(new() { Id = DuplicateRozetkaId, Marketplace = MarketplaceKind.Rozetka, Enabled = true, Name = "Same displayed name" });
        fixture.Settings.Market.Connections.Add(new() { Id = "rz-test", Marketplace = MarketplaceKind.Rozetka, Enabled = true, Name = "Same displayed name" });
        fixture.Settings.Secrets["rz-test"] = new(Token: "synthetic-api-token", Login: "legacy-seller", Password: "legacy-password");
        fixture.Settings.Secrets[DuplicateRozetkaId] = new(Token: " synthetic-api-token ", Login: "different-legacy-seller", Password: "different-legacy-password");
        var order = BasketOrder() with
        {
            Key = new(MarketplaceKind.Rozetka, "rz-test", "908483548"), Number = "908483548",
            Shipments = [new("rozetka_delivery", "RMP-synthetic-908483548")]
        };
        fixture.Rozetka.Orders = [order, order with { Key = order.Key with { ConnectionId = DuplicateRozetkaId } }];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 10000)];
        fixture.Details.Values[ReceiptOne] = BasketDetails(ReceiptOne);
        return fixture;
    }

    private static async Task RozetkaDuplicateCachedManualAsync()
    {
        var fixture = DuplicateRozetkaFixture();
        await fixture.Cache.SaveAsync(new(fixture.Rozetka.Orders, []));
        var account = PrintAccountContext.Create(fixture.Settings.App);
        var originalKey = fixture.Rozetka.Orders[1].Key;
        await fixture.Links.SaveDecisionAsync(new() { AccountContext = account, ReceiptId = ReceiptOne,
            ConfirmedOrder = originalKey, UpdatedAtUtc = DateTimeOffset.UtcNow });
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync(); main.SelectedTabIndex = 1; await main.PrepareOrdersAsync();
        Equal(1, workspace.Orders.Cast<object>().Count());
        Equal("rz-test", workspace.AllOrderRows.Single().Key.ConnectionId);
        Equal(ReceiptLinkState.Manual, main.Receipts.Single().OrderMatch!.State);
        True(workspace.AllOrderRows.Single().HasConfirmedLink);
        main.Receipts.Single().IsSelectedForOrders = true;
        await main.RefreshAsync(); await main.PrepareOrdersAsync();
        Equal(1, workspace.Orders.Cast<object>().Count());
        True(main.Receipts.Single().IsSelectedForOrders);
        var (restarted, restored) = fixture.Create();
        await restarted.RefreshAsync(); restarted.SelectedTabIndex = 1; await restarted.PrepareOrdersAsync();
        Equal(1, restored.Orders.Cast<object>().Count());
        Equal(ReceiptLinkState.Manual, restarted.Receipts.Single().OrderMatch!.State);
        Equal("rz-test", restarted.Receipts.Single().OrderMatch!.Order!.Key.ConnectionId);
        Equal(originalKey, (await fixture.Links.LoadAsync(account)).Single().ConfirmedOrder);
        Equal(2, fixture.Cache.Snapshot.Orders.Count); Equal(1, fixture.Links.Saves);
        Equal(0, fixture.Rozetka.FetchCalls); Equal(0, fixture.Source.FetchCalls);
        Equal(0, fixture.Settings.SecretSaves); Equal(0, fixture.Settings.MarketplaceSaves);
    }

    private static async Task RozetkaDuplicateBasketAsync()
    {
        foreach (var rejected in new[] { false, true })
        {
            var fixture = DuplicateRozetkaFixture();
            if (rejected) await fixture.Links.SaveDecisionAsync(new()
            {
                AccountContext = PrintAccountContext.Create(fixture.Settings.App), ReceiptId = ReceiptOne,
                RejectedOrders = [fixture.Rozetka.Orders[1].Key], UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            var (main, workspace) = fixture.Create(autoLinkEnabled: true);
            await main.RefreshAsync(); main.SelectedTabIndex = 1; await main.PrepareOrdersAsync();
            Equal(1, workspace.Orders.Cast<object>().Count()); Equal(2, fixture.Cache.Snapshot.Orders.Count);
            var receipt = main.Receipts.Single();
            True(receipt.OrderMatch?.Order is null && receipt.OrderMatch?.State != ReceiptLinkState.Suggested,
                rejected ? "Rejecting an alias must reject the canonical order rather than expose its other copy."
                    : "Identical Rozetka products cannot create a link without a receipt ID.");
            Equal(0, fixture.Details.Calls);
            Equal(1, fixture.Rozetka.FetchCalls); Equal(0, fixture.Source.FetchCalls);
            Equal(rejected ? 1 : 0, fixture.Links.Saves);
            Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
        }
    }

    private static async Task RozetkaDuplicateTokenChangeAsync()
    {
        var fixture = DuplicateRozetkaFixture();
        var account = PrintAccountContext.Create(fixture.Settings.App);
        var originalKey = fixture.Rozetka.Orders[1].Key;
        await fixture.Links.SaveDecisionAsync(new() { AccountContext = account, ReceiptId = ReceiptOne, ConfirmedOrder = originalKey });
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync(); main.SelectedTabIndex = 1; await main.PrepareOrdersAsync(); await workspace.SyncAsync();
        Equal(1, workspace.Orders.Cast<object>().Count());
        fixture.Settings.Secrets[DuplicateRozetkaId] = new(Token: "different-api-token", Login: "legacy-seller", Password: "legacy-password");
        await workspace.SyncAsync();
        Equal(2, workspace.Orders.Cast<object>().Count());
        Equal(originalKey, main.Receipts.Single().OrderMatch!.Order!.Key);
        var (restarted, restored) = fixture.Create();
        await restarted.RefreshAsync(); restarted.SelectedTabIndex = 1; await restarted.PrepareOrdersAsync();
        Equal(2, restored.Orders.Cast<object>().Count());
        Equal(originalKey, restarted.Receipts.Single().OrderMatch!.Order!.Key);
        restored.AllOrderRows.Single(r => r.Key == originalKey).IsSelectedForLabels = true;
        True(!restored.AllOrderRows.Single(r => r.Key.ConnectionId == "rz-test").IsSelectedForLabels);
        fixture.Settings.Secrets[DuplicateRozetkaId] = fixture.Settings.Secrets["rz-test"];
        await restored.SyncAsync();
        Equal(1, restored.Orders.Cast<object>().Count());
        Equal("rz-test", restored.AllOrderRows.Single().Key.ConnectionId);
        True(restored.AllOrderRows.Single().IsSelectedForLabels,
            "A marked alias must transfer its print selection when an unchanged source order becomes canonical.");
        Equal("rz-test", restarted.Receipts.Single().OrderMatch!.Order!.Key.ConnectionId);
        Equal(originalKey, (await fixture.Links.LoadAsync(account)).Single().ConfirmedOrder);
        Equal(1, fixture.Links.Saves); Equal(4, fixture.Rozetka.FetchCalls);
        Equal(0, fixture.Printer.Calls);
    }

    private sealed class DuplicateRozetkaSecrets : IMarketplaceSecretStore
    {
        public Dictionary<string, MarketplaceCredentials?> Values { get; } = [];
        public HashSet<string> Unreadable { get; } = [];
        public int Reads;
        public Task<MarketplaceCredentials?> LoadAsync(string connectionId, CancellationToken ct = default)
        {
            Reads++;
            return Unreadable.Contains(connectionId)
                ? Task.FromException<MarketplaceCredentials?>(new InvalidOperationException("Synthetic unreadable credential"))
                : Task.FromResult(Values.GetValueOrDefault(connectionId));
        }
        public Task SaveAsync(string id, MarketplaceCredentials value, CancellationToken ct = default) =>
            throw new InvalidOperationException("Deduplication must never rewrite credentials.");
    }

    private static async Task RozetkaDuplicateCredentialProofAsync()
    {
        var fixture = DuplicateRozetkaFixture();
        var secrets = new DuplicateRozetkaSecrets();
        secrets.Values["rz-test"] = new(Token: "synthetic-api-token");
        foreach (var value in new MarketplaceCredentials?[]
        {
            null, new(Token: ""), new(Login: "legacy-seller", Password: "legacy-password"),
            new(Token: "different-token"), new(Token: "Synthetic-api-token"),
            new(Token: "synthetic-api-token\n"), new(Token: "synthetic-api-token extra"), new(Token: "токен")
        })
        {
            secrets.Values[DuplicateRozetkaId] = value;
            Equal(2, (await MarketplaceOrderDeduplication.ResolveAsync(fixture.Settings.Market, secrets)).Merge(fixture.Rozetka.Orders).Count);
        }
        secrets.Values[DuplicateRozetkaId] = secrets.Values["rz-test"];
        secrets.Unreadable.Add(DuplicateRozetkaId);
        Equal(2, (await MarketplaceOrderDeduplication.ResolveAsync(fixture.Settings.Market, secrets)).Merge(fixture.Rozetka.Orders).Count);
        secrets.Unreadable.Clear(); fixture.Settings.Market.Connections[0].Enabled = false;
        Equal(2, (await MarketplaceOrderDeduplication.ResolveAsync(fixture.Settings.Market, secrets)).Merge(fixture.Rozetka.Orders).Count);
        fixture.Settings.Market.Connections.ForEach(c => c.Enabled = false);
        var reads = secrets.Reads;
        await MarketplaceOrderDeduplication.ResolveAsync(fixture.Settings.Market, secrets);
        Equal(reads, secrets.Reads);
    }

    private static async Task RozetkaDuplicateIdentityBoundariesAsync()
    {
        var fixture = DuplicateRozetkaFixture();
        fixture.Settings.Market.Connections.Add(new() { Id = "other-seller", Marketplace = MarketplaceKind.Rozetka, Enabled = true, Name = "Same displayed name" });
        fixture.Settings.Market.Connections.Add(new() { Id = "prom-test", Marketplace = MarketplaceKind.Prom, Enabled = true, Name = "Same displayed name" });
        fixture.Settings.Secrets["other-seller"] = new(Token: "different-api-token", Login: "legacy-seller", Password: "legacy-password");
        fixture.Settings.Secrets["prom-test"] = new(Token: "synthetic-api-token", Login: "legacy-seller", Password: "legacy-password");
        var first = fixture.Rozetka.Orders[0];
        var source = fixture.Rozetka.Orders.Concat([
            first with { Key = first.Key with { ConnectionId = "other-seller" } },
            first with { Key = new(MarketplaceKind.Prom, "prom-test", first.Key.OrderId) }
        ]).ToArray();
        foreach (var reverse in new[] { false, true })
        {
            fixture.Settings.Market.Connections.Reverse();
            var identity = await MarketplaceOrderDeduplication.ResolveAsync(fixture.Settings.Market, fixture.Settings);
            var projected = identity.Merge(reverse ? source.Reverse() : source);
            Equal(3, projected.Count);
            Equal("rz-test", identity.NormalizeKey(fixture.Rozetka.Orders[1].Key).ConnectionId);
            True(projected.Any(o => o.Key.ConnectionId == "other-seller"));
            True(projected.Any(o => o.Key.Marketplace == MarketplaceKind.Prom));
        }
    }

    private static async Task RozetkaDuplicateEvidenceAsync()
    {
        var fixture = DuplicateRozetkaFixture();
        var identity = await MarketplaceOrderDeduplication.ResolveAsync(fixture.Settings.Market, fixture.Settings);
        var first = fixture.Rozetka.Orders[0]; var second = fixture.Rozetka.Orders[1];
        var reference = new FiscalDocumentReference(FiscalDocumentKeyKind.CheckboxReceiptUuid, ReceiptOne,
            "synthetic", first.Key, "checkbox", "same-document");
        var old = first with { FiscalReferences = [reference], ReceiptIds = [ReceiptOne],
            FiscalReceiptNumbers = ["old-number"], FiscalReceiptUrls = ["https://example.invalid/old"], RetrievedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var latest = second with { Total = 600m, Status = "Скасовано", Shipments = [new("nova_poshta", "new-ttn")],
            FiscalReferences = [reference with { Order = second.Key, Value = ReceiptTwo }], ReceiptIds = [ReceiptTwo],
            FiscalReceiptNumbers = ["new-number"], FiscalReceiptUrls = ["https://example.invalid/new"], RetrievedAtUtc = DateTimeOffset.UtcNow };
        var projected = identity.Merge([old, latest]);
        Equal(1, projected.Count); Equal(600m, projected[0].Total); True(projected[0].IsCanceled);
        Equal("new-ttn", projected[0].Shipments.Single().TrackingNumber);
        Equal(2, projected[0].ReceiptIds.Count); Equal(2, projected[0].FiscalReceiptNumbers.Count);
        Equal(2, projected[0].FiscalReceiptUrls.Count); Equal(2, projected[0].FiscalReferences.Count);
        True(projected[0].FiscalReferences.All(r => r.Order == projected[0].Key));
        Equal(ReceiptLinkState.Conflict, new ReceiptOrderMatchingService().Match(Receipt(ReceiptOne, 1, 10000),
            "account", projected, [], true, receiptScope: [Receipt(ReceiptOne, 1, 10000)]).State);
        Equal(first.Key, old.FiscalReferences.Single().Order); Equal(second.Key, latest.FiscalReferences.Single().Order);
        Equal(latest.Status, identity.Merge([latest, old]).Single().Status);
    }

    private static async Task RozetkaDuplicateDecisionsAsync()
    {
        var fixture = DuplicateRozetkaFixture();
        var identity = await MarketplaceOrderDeduplication.ResolveAsync(fixture.Settings.Market, fixture.Settings);
        var alias = fixture.Rozetka.Orders[1].Key; var primary = fixture.Rozetka.Orders[0].Key;
        var time = DateTimeOffset.UtcNow;
        var original = new ReceiptOrderDecision { AccountContext = "cashier-a", ReceiptId = ReceiptOne,
            ConfirmedOrder = alias, RejectedOrders = [alias, primary], SuppressAutomatic = true, UpdatedAtUtc = time };
        var otherScope = new ReceiptOrderDecision { AccountContext = "cashier-b", ReceiptId = ReceiptOne,
            RejectedOrders = [alias], UpdatedAtUtc = time.AddMinutes(1) };
        var normalized = identity.NormalizeDecisions([original, otherScope]);
        Equal(2, normalized.Count); Equal(primary, normalized[0].ConfirmedOrder);
        Equal(primary, normalized[0].RejectedOrders.Single()); True(normalized[0].SuppressAutomatic);
        Equal("cashier-a", normalized[0].AccountContext); Equal("cashier-b", normalized[1].AccountContext);
        Equal(time, normalized[0].UpdatedAtUtc); Equal(time.AddMinutes(1), normalized[1].UpdatedAtUtc);
        Equal(alias, original.ConfirmedOrder); Equal(2, original.RejectedOrders.Count);
        Equal(alias, otherScope.RejectedOrders.Single());
    }

    private static async Task RozetkaDuplicatePrintCandidatesAsync()
    {
        var fixture = DuplicateRozetkaFixture();
        await fixture.Cache.SaveAsync(new(fixture.Rozetka.Orders, []));
        await fixture.Links.SaveDecisionAsync(new() { AccountContext = PrintAccountContext.Create(fixture.Settings.App),
            ReceiptId = ReceiptOne, ConfirmedOrder = fixture.Rozetka.Orders[1].Key });
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync(); main.SelectedTabIndex = 1; await main.PrepareOrdersAsync();
        var history = new ShippingLabelsTests.History(); var printer = new LabelPrinter();
        using var labels = Labels(workspace, history, new(), printer, new());
        await ExecuteAsync(labels.SelectPrintCandidatesCommand);
        var row = workspace.AllOrderRows.Single(); True(row.IsSelectedForLabels);
        await history.SaveAsync(CandidateAttempt(row, LabelSubmissionState.Submitted, "synthetic-previously-submitted"));
        await ExecuteAsync(labels.SelectPrintCandidatesCommand);
        True(!row.IsSelectedForLabels && row.HasPrintedLabel);
        var (restarted, restored) = fixture.Create();
        await restarted.RefreshAsync(); restarted.SelectedTabIndex = 1; await restarted.PrepareOrdersAsync();
        using var restoredLabels = Labels(restored, history, new(), printer, new());
        await ExecuteAsync(restoredLabels.SelectPrintCandidatesCommand);
        Equal(1, restored.AllOrderRows.Count); True(!restored.AllOrderRows.Single().IsSelectedForLabels);
        Equal(0, printer.Calls); Equal(0, fixture.Printer.Calls); Equal(0, fixture.Rozetka.FetchCalls);
    }

    private static async Task RozetkaDuplicateDashboardAsync()
    {
        _xamlCheckpoint = "deduplicated Rozetka dashboard";
        var fixture = DuplicateRozetkaFixture();
        await fixture.Cache.SaveAsync(new(fixture.Rozetka.Orders, []));
        await fixture.Links.SaveDecisionAsync(new() { AccountContext = PrintAccountContext.Create(fixture.Settings.App),
            ReceiptId = ReceiptOne, ConfirmedOrder = fixture.Rozetka.Orders[1].Key });
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync(); main.SelectedTabIndex = 1; await main.PrepareOrdersAsync();
        var printer = new LabelPrinter();
        using var labels = Labels(workspace, new(), new(), printer, new()); workspace.Labels = labels;
        var window = new MainWindow { DataContext = main };
        var failures = new BindingErrorListener();
        var oldLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Listeners.Add(failures);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            LayoutContent(window); await DrainDispatcherAsync();
            var grid = (DataGrid)window.FindName("MarketplaceOrdersGrid");
            Equal(1, grid.Items.Count); Equal(1, workspace.AllOrderRows.Single().DisplayIndex);
            var candidates = (Button)window.FindName("OrdersPrintCandidatesButton");
            ClickCandidateButton(candidates); await DrainDispatcherAsync();
            True(workspace.AllOrderRows.Single().IsSelectedForLabels);
            Equal(1, workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Count(r => r.IsSelectedForLabels));
            RenderSyntheticContent(window, "rozetka-orders-deduplicated.png");
            Equal(0, printer.Calls); Equal(0, fixture.Printer.Calls); Equal(0, fixture.Rozetka.FetchCalls);
            True(failures.Messages.Count == 0, "Duplicate Rozetka binding errors: " + string.Join("\n", failures.Messages));
        }
        finally
        {
            main.CancelPendingOperations(); window.Close(); await DrainDispatcherAsync();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(failures);
            PresentationTraceSources.DataBindingSource.Switch.Level = oldLevel;
        }
    }
}
