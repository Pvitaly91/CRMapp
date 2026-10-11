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
    public static IReadOnlyList<(string Name, Func<Task> Test)> FiscalVerificationScenarios =>
    [
        ("STA fiscal verification uses the requested Kyiv period and current receipt evidence without reading unrelated cached history", () => StaAsync(FiscalVerificationPeriodAsync)),
        ("STA an unrelated cached fiscal archive does not invoke the Checkbox fiscal verifier", () => StaAsync(FiscalVerificationUnrelatedHistoryAsync)),
        ("STA first current return detail read selects its historical original for code verification without probing unrelated history", () => StaAsync(FiscalVerificationReturnOriginalAsync)),
        ("STA pending fiscal verification never publishes an unverified candidate snapshot before the exact relation", () => StaAsync(FiscalVerificationPendingPublicationAsync)),
        ("STA failed or cancelled fiscal verification cannot publish its candidate snapshot from cleanup", () => StaAsync(FiscalVerificationFailedPublicationAsync))
    ];

    private static MarketplaceOrder VerificationOrder(string id, DateTimeOffset? created, string code)
    {
        var order = Order() with { Key = new(MarketplaceKind.Prom, "prom-test", id), Number = id, CreatedAt = created };
        return order with { FiscalReferences = [new(FiscalDocumentKeyKind.FiscalCode, code,
            "synthetic documented fiscal code", order.Key, "Checkbox", "sale", CashRegisterFiscalNumber: "123")] };
    }

    private static (MainViewModel Main, MarketplaceWorkspaceViewModel Workspace) CreateFiscalVerificationWorkspace(
        Fixture fixture, IFiscalReferenceVerifier verifier)
    {
        var sync = new MarketplaceSyncService([fixture.Source, fixture.Rozetka], fixture.Settings, fixture.Cache);
        var workspace = new MarketplaceWorkspaceViewModel(fixture.Settings, sync, fixture.Links, fixture.Details,
            fixture.Dialogs, fiscalVerifier: verifier, autoLinkEnabled: false);
        var main = new MainViewModel(fixture.ReceiptSource, fixture.Images, fixture.Settings, fixture.Authentication,
            fixture.History, fixture.Printer, fixture.Dialogs, new Logger(), workspace)
            { DateFrom = new(2026, 9, 23), DateTo = new(2026, 9, 23) };
        return (main, workspace);
    }

    private static async Task FiscalVerificationPeriodAsync()
    {
        var fixture = new Fixture();
        var archived = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var current = VerificationOrder("kyiv-start", new(2026, 9, 22, 21, 0, 0, TimeSpan.Zero), "CURRENT-DAY");
        var byUuid = VerificationOrder("old-current-uuid", archived, "OLD-UUID");
        byUuid = byUuid with { FiscalReferences = byUuid.FiscalReferences.Append(new FiscalDocumentReference(
            FiscalDocumentKeyKind.CheckboxReceiptUuid, ReceiptOne, "synthetic documented UUID", byUuid.Key, "Checkbox", "sale")).ToArray() };
        var byCode = VerificationOrder("old-current-code", archived, "CURRENT-CODE");
        var manual = VerificationOrder("old-manual", archived, "OLD-MANUAL");
        var anotherAccount = VerificationOrder("old-other-account", archived, "OTHER-ACCOUNT");
        var orders = new[]
        {
            current, byUuid, byCode, manual, anotherAccount,
            VerificationOrder("before-kyiv-start", new(2026, 9, 22, 20, 59, 59, TimeSpan.Zero), "BEFORE"),
            VerificationOrder("kyiv-end", new(2026, 9, 23, 21, 0, 0, TimeSpan.Zero), "AFTER"),
            VerificationOrder("old-other-receipt", archived, "HISTORICAL-CODE"),
            VerificationOrder("old-wrong-code-case", archived, "current-code"),
            VerificationOrder("unknown-date", null, "UNKNOWN")
        };
        fixture.Source.Orders = orders;
        fixture.ReceiptSource.Rows =
        [
            Receipt(ReceiptOne, 1, 10000),
            new() { Id = ReceiptTwo, Serial = 2, Status = "DONE", Type = ReceiptTypes.Sell, TotalSumMinor = 10000,
                FiscalCode = "CURRENT-CODE", FiscalDate = new(2026, 9, 23, 12, 0, 0, TimeSpan.FromHours(3)) },
            new() { Id = ReceiptThree, Serial = 3, Status = "DONE", Type = ReceiptTypes.Sell, TotalSumMinor = 99900,
                FiscalCode = "HISTORICAL-CODE", FiscalDate = new(2026, 9, 20, 13, 0, 0, TimeSpan.FromHours(3)) }
        ];
        var account = PrintAccountContext.Create(fixture.Settings.App);
        await fixture.Links.SaveDecisionAsync(new() { AccountContext = account, ReceiptId = ReceiptOne, ConfirmedOrder = manual.Key });
        await fixture.Links.SaveDecisionAsync(new() { AccountContext = "another-account", ReceiptId = ReceiptTwo, ConfirmedOrder = anotherAccount.Key });
        var verifier = new ScopedFiscalVerifier();
        var (main, workspace) = CreateFiscalVerificationWorkspace(fixture, verifier);
        try
        {
            await main.RefreshAllAsync();
            Equal(1, verifier.Calls.Count);
            True(verifier.Calls.Single().Select(order => order.Number).ToHashSet().SetEquals(
                ["kyiv-start", "old-current-uuid", "old-current-code", "old-manual"]));
            Equal(orders.Length, workspace.AllOrderRows.Count);
            foreach (var order in orders)
            {
                var retained = workspace.AllOrderRows.Single(row => row.Key == order.Key).Model;
                True(retained.FiscalReferences.Select(reference => (reference.Kind, reference.Value, reference.Source, reference.Order,
                        reference.Provider, reference.DocumentId, reference.CashRegisterFiscalNumber, reference.OrganizationId))
                    .SequenceEqual(order.FiscalReferences.Select(reference => (reference.Kind, reference.Value, reference.Source, reference.Order,
                        reference.Provider, reference.DocumentId, reference.CashRegisterFiscalNumber, reference.OrganizationId))));
            }
            Equal(ReceiptLinkState.Manual, main.Receipts.Single(row => row.Id == ReceiptOne).OrderMatch!.State);
            Equal(0, fixture.Printer.Calls);
        }
        finally { main.CancelPendingOperations(); }
    }

    private static async Task FiscalVerificationUnrelatedHistoryAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [VerificationOrder("archive", new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero), "ARCHIVE-CODE")];
        var verifier = new ScopedFiscalVerifier();
        var (main, workspace) = CreateFiscalVerificationWorkspace(fixture, verifier);
        try
        {
            await main.RefreshAllAsync();
            Equal(0, verifier.Calls.Count);
            Equal("archive", workspace.AllOrderRows.Single().Number);
            Equal("ARCHIVE-CODE", workspace.AllOrderRows.Single().Model.FiscalReferences.Single().Value);
        }
        finally { main.CancelPendingOperations(); }
    }

    private static async Task FiscalVerificationReturnOriginalAsync()
    {
        const string code = "ORIG-CODE01";
        const string organization = "f2000000-0000-0000-0000-000000000001";
        foreach (var parallel in new[] { true, false })
        {
            var fixture = new Fixture();
            var originalOrder = VerificationOrder("original-sale", new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero), code);
            originalOrder = originalOrder with { FiscalReferences = originalOrder.FiscalReferences.Select(reference => reference with
                { CashRegisterFiscalNumber = "", OrganizationId = organization }).ToArray() };
            fixture.Source.Orders = [originalOrder, VerificationOrder("unrelated-archive",
                new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero), "UNRELATED01")];
            fixture.ReceiptSource.Rows =
            [
                new() { Id = ReceiptOne, Type = ReceiptTypes.Sell, Status = "DONE", Serial = 1, TotalSumMinor = 10000,
                    FiscalCode = code, OrganizationId = organization, FiscalDate = new(2026, 9, 20, 14, 0, 0, TimeSpan.FromHours(3)) },
                new() { Id = ReceiptTwo, Type = ReceiptTypes.Return, Status = "DONE", Serial = 2, TotalSumMinor = 10000,
                    FiscalDate = new(2026, 9, 23, 14, 0, 0, TimeSpan.FromHours(3)) }
            ];
            fixture.Details.Values[ReceiptTwo] = new(ReceiptTwo, [], ReceiptOne);
            var calls = 0;
            using var http = new HttpClient(new FiscalVerificationHandler(request =>
            {
                True(fixture.Details.RequestedIds.Contains(ReceiptTwo), "Fiscal scoping ran before the first return details were available.");
                Equal("/api/v1/receipts/search", request.RequestUri!.AbsolutePath);
                True(request.RequestUri.Query.Contains("fiscal_code=" + code));
                calls++;
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                {
                    results = new[] { new { id = ReceiptOne, fiscal_code = code, organization_id = organization, type = "SELL" } }
                }), Encoding.UTF8, "application/json") };
            }));
            var verifier = new FiscalReferenceVerifier(new CheckboxApiClient(http, fixture.Authentication, new Logger()), fixture.Settings);
            var (main, workspace) = CreateFiscalVerificationWorkspace(fixture, verifier);
            try
            {
                if (parallel) await main.RefreshAllAsync();
                else
                {
                    await ExecuteAsync(main.RefreshCommand);
                    await ExecuteAsync(workspace.SyncCommand);
                }
                Equal(1, calls); Equal(1, fixture.Details.RequestedIds.Count(id => id == ReceiptTwo));
                Equal(2, workspace.AllOrderRows.Count);
                var retained = workspace.AllOrderRows.Single(row => row.Key == originalOrder.Key).Model;
                var proof = retained.FiscalReferences.Single();
                Equal(ReceiptOne, proof.VerifiedReceiptId);
                Equal(PrintAccountContext.Create(fixture.Settings.App), proof.VerifiedAccountContext);
                True(workspace.AllOrderRows.Single(row => row.Number == "unrelated-archive").Model.FiscalReferences
                    .All(reference => reference.VerifiedReceiptId.Length == 0));
            }
            finally { main.CancelPendingOperations(); }
        }
    }

    private sealed class FiscalVerificationHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(reply(request));
        }
    }

    private static Task FiscalVerificationPendingPublicationAsync() => FiscalVerificationPublicationAsync(false);
    private static Task FiscalVerificationFailedPublicationAsync() => FiscalVerificationPublicationAsync(true);

    private static async Task FiscalVerificationPublicationAsync(bool fail)
    {
        foreach (var parallel in new[] { true, false })
        foreach (var cancel in fail ? new[] { true, false } : new[] { false })
        {
            const string code = "PENDINGCODE";
            const string organization = "f2000000-0000-0000-0000-000000000001";
            var fixture = new Fixture();
            var correct = VerificationOrder("exact-old-order", new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero), code);
            correct = correct with { FiscalReferences = correct.FiscalReferences.Select(reference => reference with
                { CashRegisterFiscalNumber = "", OrganizationId = organization }).ToArray() };
            fixture.Source.Orders = [Order() with { Number = "same-amount-candidate" }, correct];
            fixture.ReceiptSource.Rows = [new() { Id = ReceiptOne, Type = ReceiptTypes.Sell, Status = "DONE", Serial = 1,
                FiscalCode = code, OrganizationId = organization, TotalSumMinor = 10000,
                FiscalDate = new(2026, 9, 23, 14, 0, 0, TimeSpan.FromHours(3)) }];
            var verifier = new PendingFiscalVerifier { Fail = fail && !cancel };
            var (main, workspace) = CreateFiscalVerificationWorkspace(fixture, verifier);
            using var cancellation = new CancellationTokenSource();
            Task? refreshing = null;
            try
            {
                await main.RefreshAsync();
                True(workspace.AllOrderRows.Count == 0, $"{parallel}/{fail}/{cancel}: initial receipt-only refresh unexpectedly published orders.");
                workspace.SetBackgroundRange(new(2026, 9, 23), new(2026, 9, 23));
                var account = PrintAccountContext.Create(fixture.Settings.App);
                refreshing = parallel ? workspace.SyncBackgroundAsync(Task.CompletedTask, account, cancellation.Token)
                    : workspace.RefreshAllAsync(new(2026, 9, 23), new(2026, 9, 23), cancellation.Token);
                await verifier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                True(!refreshing.IsCompleted);
                True(workspace.AllOrderRows.Count == 0, $"{parallel}/{fail}/{cancel}: pending verifier already published {workspace.AllOrderRows.Count} orders.");
                True(main.Receipts.Single().OrderMatch?.Order is null, $"{parallel}/{fail}/{cancel}: pending verifier published a receipt link.");
                // Exercise another matching entry point while verification is pending.
                typeof(MarketplaceWorkspaceViewModel).GetMethod("ApplyMatches", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!.Invoke(workspace, [null]);
                True(workspace.AllOrderRows.Count == 0, $"{parallel}/{fail}/{cancel}: a secondary ApplyMatches published pending orders.");
                True(main.Receipts.Single().OrderMatch?.Order is null, $"{parallel}/{fail}/{cancel}: a secondary ApplyMatches published a pending link.");
                if (cancel) cancellation.Cancel();
                verifier.Release.TrySetResult();
                try { await refreshing.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (cancel) { }
                catch (InvalidOperationException) when (fail) { }
                if (fail)
                {
                    True(workspace.AllOrderRows.Count == 0, $"{parallel}/{fail}/{cancel}: failure cleanup published the rejected candidate snapshot.");
                    True(main.Receipts.Single().OrderMatch?.Order is null, $"{parallel}/{fail}/{cancel}: failure cleanup published an unverified link.");
                }
                else
                {
                    Equal(2, workspace.AllOrderRows.Count);
                    Equal(ReceiptLinkState.Exact, main.Receipts.Single().OrderMatch!.State);
                    Equal(correct.Key, main.Receipts.Single().OrderMatch!.Order!.Key);
                }
                Equal(0, fixture.Printer.Calls);
            }
            finally
            {
                verifier.Release.TrySetResult();
                main.CancelPendingOperations();
                if (refreshing is not null) try { await refreshing; } catch (Exception) when (fail) { }
            }
        }
    }

    private sealed class PendingFiscalVerifier : IFiscalReferenceVerifier
    {
        public bool Fail;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<MarketplaceOrder>> VerifyAsync(IReadOnlyList<MarketplaceOrder> orders, string accountContext,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            if (Fail) throw new InvalidOperationException("Synthetic fiscal verification failure.");
            return orders.Select(order => order with { FiscalReferences = order.FiscalReferences.Select(reference => reference with
                { VerifiedReceiptId = ReceiptOne, VerifiedAccountContext = accountContext }).ToArray() }).ToArray();
        }
    }

    private sealed class ScopedFiscalVerifier : IFiscalReferenceVerifier
    {
        public List<IReadOnlyList<MarketplaceOrder>> Calls { get; } = [];
        public Task<IReadOnlyList<MarketplaceOrder>> VerifyAsync(IReadOnlyList<MarketplaceOrder> orders, string accountContext,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(orders.ToArray());
            return Task.FromResult(orders);
        }
    }
}
