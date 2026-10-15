using System.IO;
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
    private static IReadOnlyList<(string Name, Func<Task> Test)> ExactPersistenceScenarios =>
    [
        ("STA DPAPI restart restores verified Rozetka exact offline and both marketplace fallbacks without repeat verification", () => StaAsync(ExactPersistenceRestartAsync)),
        ("STA DPAPI restart rejects stale exact proof after changed keys account conflicts or manual decisions", () => StaAsync(ExactPersistenceInvalidationAsync)),
        ("STA changed Rozetka fiscal proof after restart is verified again and contradictory server keys remain conflicting", () => StaAsync(ExactPersistenceChangedCodeAsync)),
        ("STA verified exact is saved before unrelated basket work so cancelling refresh survives restart", () => StaAsync(ExactPersistenceCancelledBasketAsync))
    ];

    private sealed class ExactPersistenceFixture : IDisposable
    {
        public Fixture State { get; } = new();
        public MarketplaceOrder Prom { get; } = BasketOrder() with { ItemListComplete = true };
        public MarketplaceOrder Exact { get; } = EstablishedRozetkaOrder("persist-exact", 200, EstablishedOldCode);
        public MarketplaceOrder Fallback { get; } = EstablishedRozetkaOrder("persist-fallback", 300, "") with
            { FiscalReferences = [], Currency = "" };
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "checkbox-exact-ui-" + Guid.NewGuid().ToString("N"));
        public string CachePath => Path.Combine(DirectoryPath, "automatic.dpapi");
        public string Account => PrintAccountContext.Create(State.Settings.App);

        public ExactPersistenceFixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            State.Settings.Market.Connections.Add(new()
                { Id = Exact.Key.ConnectionId, Marketplace = MarketplaceKind.Rozetka, Enabled = true });
            State.Source.Orders = [Prom]; State.Rozetka.Orders = [Exact, Fallback];
            State.ReceiptSource.Rows = [EstablishedReceipt(ReceiptOne, 1, 10000),
                EstablishedReceipt(ReceiptTwo, 2, 20000, EstablishedOldCode), EstablishedReceipt(ReceiptThree, 3, 30000)];
            State.Details.Values[ReceiptOne] = new(ReceiptOne, Prom.Items) { ItemListComplete = true };
            State.Details.Values[ReceiptThree] = new(ReceiptThree, Fallback.Items) { ItemListComplete = true };
            State.Details.FailIds.Add(ReceiptTwo);
        }

        public async Task SeedAsync()
        {
            using var host = new ExactPersistenceHost(this);
            await host.Main.RefreshAllAsync();
            AssertLinks(host);
            Equal(1, host.HttpCalls); Equal(1, host.Verifier.Calls.Count);
            True(File.Exists(CachePath), "Accepted exact links were never saved to the encrypted cache.");
            True(State.Cache.Snapshot.Orders.SelectMany(order => order.FiscalReferences)
                .All(reference => reference.VerifiedReceiptId.Length == 0), "Marketplace cache leaked runtime verification flags.");
        }

        public void AssertLinks(ExactPersistenceHost host)
        {
            var expected = new[] { (ReceiptOne, Prom, ReceiptLinkState.Suggested),
                (ReceiptTwo, Exact, ReceiptLinkState.Exact), (ReceiptThree, Fallback, ReceiptLinkState.Suggested) };
            foreach (var (id, order, state) in expected)
            {
                var receipt = host.Main.Receipts.Single(row => row.Id == id);
                Equal(state, receipt.OrderMatch!.State); Equal(order.Key, receipt.OrderMatch.Order!.Key);
                True(host.Workspace.AllOrderRows.Single(row => row.Key == order.Key).LinkedReceipts.Any(row => row.Id == id));
            }
            Equal(0, State.Printer.Calls); Equal(0, State.History.Saves);
        }

        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }

    // Each host has genuinely new VMs, DPAPI store, HTTP client and verifier. Reusing
    // a verifier here could accidentally hide a missing persistent proof behind its memo.
    private sealed class ExactPersistenceHost : IDisposable
    {
        private readonly HttpClient _http;
        public MainViewModel Main { get; }
        public MarketplaceWorkspaceViewModel Workspace { get; }
        public EstablishedCountingVerifier Verifier { get; }
        public int HttpCalls { get; private set; }
        public ExactPersistenceHost(ExactPersistenceFixture fixture, bool offline = false, bool withoutVerifier = false,
            string code = EstablishedOldCode, string receiptId = ReceiptTwo, bool resolveOldAndNewCodes = false)
        {
            var state = fixture.State;
            _http = new(new FiscalVerificationHandler(request =>
            {
                HttpCalls++;
                if (offline) return new(HttpStatusCode.ServiceUnavailable)
                    { Content = new StringContent("Synthetic unavailable fiscal verification") };
                Equal("/api/v1/receipts/search", request.RequestUri!.AbsolutePath);
                var lookupCode = resolveOldAndNewCodes && request.RequestUri.Query.Contains("fiscal_code=" + EstablishedOldCode, StringComparison.Ordinal)
                    ? EstablishedOldCode : code;
                var lookupReceipt = lookupCode == EstablishedOldCode && resolveOldAndNewCodes ? ReceiptTwo : receiptId;
                Equal($"?fiscal_code={lookupCode}&self_receipts=true&limit=2&offset=0", request.RequestUri.Query);
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                    { results = new[] { new { id = lookupReceipt, fiscal_code = lookupCode, type = "SELL" } } }), Encoding.UTF8, "application/json") };
            }));
            Verifier = new(new FiscalReferenceVerifier(new CheckboxApiClient(_http, state.Authentication, new Logger()), state.Settings));
            var sync = new MarketplaceSyncService([state.Source, state.Rozetka], state.Settings, state.Cache);
            Workspace = new(state.Settings, sync, state.Links, state.Details, state.Dialogs, autoLinkEnabled: true,
                automaticCache: new DpapiAutomaticMatchCacheStore(fixture.CachePath), fiscalVerifier: withoutVerifier ? null : Verifier)
                { CoordinatorManaged = true };
            Main = new(state.ReceiptSource, state.Images, state.Settings, state.Authentication, state.History,
                state.Printer, state.Dialogs, new Logger(), Workspace)
                { DateFrom = new(2026, 9, 23), DateTo = new(2026, 9, 23), CoordinatorManaged = true };
        }
        public async Task AttachAsync() { await Main.RefreshAsync(); await Workspace.EnsureAttachedAsync(); }
        public void Dispose() { Main.CancelPendingOperations(); _http.Dispose(); }
    }

    private static async Task ExactPersistenceRestartAsync()
    {
        using var fixture = new ExactPersistenceFixture();
        await fixture.SeedAsync();
        var detailCalls = fixture.State.Details.Calls;
        var marketplaceCalls = fixture.State.Source.FetchCalls + fixture.State.Rozetka.FetchCalls;
        fixture.State.Details.FailIds.UnionWith([ReceiptOne, ReceiptThree]);
        using (var local = new ExactPersistenceHost(fixture, withoutVerifier: true))
        {
            await local.AttachAsync(); fixture.AssertLinks(local);
            Equal(marketplaceCalls, fixture.State.Source.FetchCalls + fixture.State.Rozetka.FetchCalls);
            Equal(0, local.HttpCalls); Equal(0, local.Verifier.Calls.Count);
            Equal(detailCalls, fixture.State.Details.Calls);
        }
        using var restarted = new ExactPersistenceHost(fixture, offline: true);
        await restarted.Main.RefreshAllAsync(); fixture.AssertLinks(restarted);
        await restarted.Main.RefreshAllAsync(); fixture.AssertLinks(restarted);
        Equal(0, restarted.HttpCalls); Equal(0, restarted.Verifier.Calls.Count);
        Equal(detailCalls, fixture.State.Details.Calls);
        foreach (var row in restarted.Main.Receipts) { row.IsSelected = true; row.IsSelectedForOrders = true; }
        foreach (var row in restarted.Workspace.AllOrderRows) row.IsSelectedForLabels = true;
        fixture.State.Source.Fail = fixture.State.Rozetka.Fail = true;
        await restarted.Main.RefreshAllAsync(); fixture.AssertLinks(restarted);
        True(restarted.Main.Receipts.All(row => row.IsSelected && row.IsSelectedForOrders));
        True(restarted.Workspace.AllOrderRows.All(row => row.IsSelectedForLabels));
        Equal(0, restarted.HttpCalls); Equal(0, restarted.Verifier.Calls.Count);
        Equal(detailCalls, fixture.State.Details.Calls); Equal(0, fixture.State.Links.Saves);
    }

    private static async Task ExactPersistenceInvalidationAsync()
    {
        foreach (var scenario in new[] { "new-code", "new-uuid", "account", "conflict", "manual", "rejected" })
        {
            using var fixture = new ExactPersistenceFixture(); await fixture.SeedAsync();
            var snapshot = fixture.State.Cache.Snapshot;
            var raw = snapshot.Orders.Single(order => order.Key == fixture.Exact.Key);
            var changed = scenario switch
            {
                "new-code" => raw with { FiscalReferences = [raw.FiscalReferences.Single() with { Value = EstablishedNewCode }] },
                "new-uuid" => raw with { ReceiptIds = [EstablishedNextRozetkaReceipt] },
                _ => raw
            };
            var orders = snapshot.Orders.Select(order => order.Key == raw.Key ? changed : order).ToList();
            if (scenario == "conflict") orders.Add(EstablishedRozetkaOrder("competing-exact", 999, EstablishedOldCode));
            await fixture.State.Cache.SaveAsync(snapshot with { Orders = orders });
            if (scenario is "manual" or "rejected") await fixture.State.Links.SaveDecisionAsync(new()
            {
                AccountContext = fixture.Account, ReceiptId = ReceiptTwo, UpdatedAtUtc = DateTimeOffset.UtcNow,
                ConfirmedOrder = scenario == "manual" ? fixture.Exact.Key : null,
                RejectedOrders = scenario == "rejected" ? [fixture.Exact.Key] : []
            });
            if (scenario == "account") fixture.State.Settings.App.Login = "different-synthetic-cashier";
            using var restarted = new ExactPersistenceHost(fixture, withoutVerifier: true);
            await restarted.AttachAsync();
            var receipt = restarted.Main.Receipts.Single(row => row.Id == ReceiptTwo);
            if (scenario == "manual")
            { Equal(ReceiptLinkState.Manual, receipt.OrderMatch!.State); Equal(fixture.Exact.Key, receipt.OrderMatch.Order!.Key); }
            else True(receipt.OrderMatch?.Order is null, "Persisted exact bypassed invalidation: " + scenario);
            True(restarted.Workspace.AllOrderRows.SelectMany(row => row.Model.FiscalReferences)
                .All(reference => reference.VerifiedReceiptId.Length == 0), "Rejected persisted proof leaked into current order: " + scenario);
            Equal(0, restarted.HttpCalls); Equal(0, restarted.Verifier.Calls.Count);
        }
    }

    private static async Task ExactPersistenceChangedCodeAsync()
    {
        foreach (var conflictingServerKeys in new[] { false, true })
        {
            using var fixture = new ExactPersistenceFixture(); await fixture.SeedAsync();
            var changed = fixture.Exact with { FiscalReferences = [fixture.Exact.FiscalReferences.Single() with { Value = EstablishedNewCode }] };
            fixture.State.Rozetka.Orders = [changed, fixture.Fallback];
            fixture.State.ReceiptSource.Rows = [.. fixture.State.ReceiptSource.Rows,
                EstablishedReceipt(EstablishedNextRozetkaReceipt, 4, 20000, EstablishedNewCode)];
            if (!conflictingServerKeys)
            {
                // An authoritative current cache contains only the replacement key.
                // Its old persisted attestation must fail validation, then be reverified.
                var snapshot = fixture.State.Cache.Snapshot;
                await fixture.State.Cache.SaveAsync(snapshot with { Orders = snapshot.Orders.Select(order =>
                    order.Key == changed.Key ? changed : order).ToArray() });
            }
            // In the normal sync case the adapter deliberately keeps contradictory
            // old/new assertions from one document instead of silently replacing them.
            using var restarted = new ExactPersistenceHost(fixture, code: EstablishedNewCode,
                receiptId: EstablishedNextRozetkaReceipt, resolveOldAndNewCodes: conflictingServerKeys);
            await restarted.Main.RefreshAllAsync();
            Equal(conflictingServerKeys ? 2 : 1, restarted.HttpCalls); Equal(1, restarted.Verifier.Calls.Count);
            True(restarted.Main.Receipts.Single(row => row.Id == ReceiptTwo).OrderMatch?.Order is null);
            var linked = restarted.Main.Receipts.Single(row => row.Id == EstablishedNextRozetkaReceipt);
            if (!conflictingServerKeys)
            {
                Equal(ReceiptLinkState.Exact, linked.OrderMatch!.State); Equal(changed.Key, linked.OrderMatch.Order!.Key);
                Equal(linked, restarted.Workspace.AllOrderRows.Single(row => row.Key == changed.Key).LinkedReceipts.Single());
                await restarted.Main.RefreshAllAsync(); Equal(1, restarted.HttpCalls); Equal(1, restarted.Verifier.Calls.Count);
            }
            else
            {
                Equal(ReceiptLinkState.Conflict, linked.OrderMatch!.State);
                Equal(ReceiptLinkState.Conflict, restarted.Main.Receipts.Single(row => row.Id == ReceiptTwo).OrderMatch!.State);
                True(linked.OrderMatch.Order is null);
                var saved = await new DpapiAutomaticMatchCacheStore(fixture.CachePath).LoadAsync(fixture.Account, 30);
                True(!saved.ExactLinks.ContainsKey(ReceiptTwo) && !saved.ExactLinks.ContainsKey(EstablishedNextRozetkaReceipt),
                    "Contradictory server fiscal keys left an accepted exact pair on disk.");
                using var offline = new ExactPersistenceHost(fixture, withoutVerifier: true);
                await offline.AttachAsync();
                True(offline.Main.Receipts.Where(row => row.Id is ReceiptTwo or EstablishedNextRozetkaReceipt)
                    .All(row => row.OrderMatch?.Order is null), "Restart resurrected a contradicted exact link.");
                Equal(0, offline.HttpCalls); Equal(0, offline.Verifier.Calls.Count);
            }
            Equal(0, fixture.State.Printer.Calls); Equal(0, fixture.State.Links.Saves);
        }
    }

    private static async Task ExactPersistenceCancelledBasketAsync()
    {
        using var fixture = new ExactPersistenceFixture();
        fixture.State.Details.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var first = new ExactPersistenceHost(fixture))
        {
            var refresh = first.Main.RefreshAllAsync();
            try
            {
                await fixture.State.Details.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Equal(ReceiptLinkState.Exact, first.Main.Receipts.Single(row => row.Id == ReceiptTwo).OrderMatch!.State);
                True(File.Exists(fixture.CachePath), "Accepted exact was not persisted before waiting for unrelated baskets.");
                first.Main.CancelRefreshAllCommand.Execute(null);
                await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                fixture.State.Details.Gate.TrySetResult(true);
                await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        fixture.State.Details.Gate = null;
        using var restarted = new ExactPersistenceHost(fixture, withoutVerifier: true);
        await restarted.AttachAsync();
        var exact = restarted.Main.Receipts.Single(row => row.Id == ReceiptTwo);
        Equal(ReceiptLinkState.Exact, exact.OrderMatch!.State); Equal(fixture.Exact.Key, exact.OrderMatch.Order!.Key);
        True(restarted.Main.Receipts.Where(row => row.Id != ReceiptTwo).All(row => row.OrderMatch?.Order is null),
            "Cancelled basket preparation persisted an unverified amount guess.");
        Equal(0, restarted.HttpCalls); Equal(0, restarted.Verifier.Calls.Count);
        Equal(0, fixture.State.Printer.Calls); Equal(0, fixture.State.Links.Saves);
    }
}
