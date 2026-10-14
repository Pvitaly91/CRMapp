using System.ComponentModel;
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
    private static IReadOnlyList<(string Name, Func<Task> Test)> EstablishedRefreshScenarios =>
    [
        ("STA global refresh never removes established Prom and Rozetka links while early Checkbox rows arrive", () => StaAsync(EstablishedRefreshNoFlickerAsync)),
        ("STA unchanged linked receipts skip fiscal verification and basket reads on repeated refresh", () => StaAsync(EstablishedRefreshSkipsOldReadsAsync)),
        ("STA failed or cancelled global refresh retains established links and marks without assigning new receipts", () => StaAsync(EstablishedRefreshFailureAsync)),
        ("STA failed Checkbox refresh retains established links but cannot assign a previously unlinked receipt", () => StaAsync(EstablishedRefreshCheckboxFailureAsync))
    ];

    private const string EstablishedNextRozetkaReceipt = "00000000-0000-0000-0000-000000000004";
    private const string EstablishedOldCode = "SYNTHOLD001", EstablishedNewCode = "SYNTHNEW001";

    private static ReceiptRecord EstablishedReceipt(string id, int serial, long amount, string code = "") => new()
    {
        Id = id, Serial = serial, Status = "DONE", Type = ReceiptTypes.Sell, TotalSumMinor = amount,
        FiscalCode = code, FiscalDate = new(2026, 9, 23, 10 + serial, 0, 0, TimeSpan.FromHours(3))
    };

    private static MarketplaceOrder EstablishedRozetkaOrder(string id, decimal amount, string code)
    {
        var order = BasketOrder() with
        {
            Key = new(MarketplaceKind.Rozetka, "rz-test", id), Number = "ROZETKA-" + id, Total = amount,
            Items = [new("Synthetic Rozetka goods " + id, "", 1, amount, amount)], ItemListComplete = true
        };
        return order with { FiscalReferences = [new(FiscalDocumentKeyKind.FiscalCode, code,
            "prro.prro_receipt_fiscal_code", order.Key, "Checkbox", "rozetka.prro")] };
    }

    private sealed class EstablishedRefreshFixture : IDisposable
    {
        public Fixture State { get; } = new();
        public AutomaticCache Automatic { get; } = new();
        public MainViewModel Main { get; }
        public MarketplaceWorkspaceViewModel Workspace { get; }
        public EstablishedCountingVerifier Verifier { get; }
        public List<string> FiscalHttpCodes { get; } = [];
        public MarketplaceOrder OldProm { get; } = BasketOrder() with { Number = "PROM-OLD", ItemListComplete = true };
        public MarketplaceOrder OldRozetka { get; } = EstablishedRozetkaOrder("42", 200, EstablishedOldCode);
        public MarketplaceOrder NewProm { get; } = BasketOrder() with
        {
            Key = new(MarketplaceKind.Prom, "prom-test", "43"), Number = "PROM-NEW", Total = 300,
            Items = [new("Synthetic new Prom goods", "", 1, 300, 300)], ItemListComplete = true
        };
        public MarketplaceOrder NewRozetka { get; } = EstablishedRozetkaOrder("44", 400, EstablishedNewCode);
        private readonly HttpClient _http;

        public EstablishedRefreshFixture(bool includeUnlinkedReceipt = false)
        {
            State.Settings.Market.Connections.Add(new()
                { Id = "rz-test", Marketplace = MarketplaceKind.Rozetka, Enabled = true, Name = "Synthetic Rozetka" });
            State.Source.Orders = [OldProm]; State.Rozetka.Orders = [OldRozetka];
            State.ReceiptSource.Rows = [EstablishedReceipt(ReceiptOne, 1, 10000),
                EstablishedReceipt(ReceiptTwo, 2, 20000, EstablishedOldCode),
                .. (includeUnlinkedReceipt ? new[] { EstablishedReceipt(ReceiptThree, 3, 30000) } : Array.Empty<ReceiptRecord>())];
            State.Details.Values[ReceiptOne] = new(ReceiptOne, OldProm.Items) { ItemListComplete = true };
            State.Details.Values[ReceiptTwo] = new(ReceiptTwo, OldRozetka.Items) { ItemListComplete = true };
            State.Details.Values[ReceiptThree] = new(ReceiptThree, NewProm.Items) { ItemListComplete = true };
            State.Details.Values[EstablishedNextRozetkaReceipt] = new(EstablishedNextRozetkaReceipt, NewRozetka.Items)
                { ItemListComplete = true };
            _http = new HttpClient(new FiscalVerificationHandler(request =>
            {
                Equal("/api/v1/receipts/search", request.RequestUri!.AbsolutePath);
                var query = request.RequestUri.Query;
                var code = query.Contains("fiscal_code=" + EstablishedOldCode, StringComparison.Ordinal)
                    ? EstablishedOldCode : query.Contains("fiscal_code=" + EstablishedNewCode, StringComparison.Ordinal)
                        ? EstablishedNewCode : throw new InvalidOperationException("Unexpected fiscal search.");
                FiscalHttpCodes.Add(code);
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                {
                    results = new[] { new { id = code == EstablishedOldCode ? ReceiptTwo : EstablishedNextRozetkaReceipt,
                        fiscal_code = code, type = "SELL" } }
                }), Encoding.UTF8, "application/json") };
            }));
            Verifier = new(new FiscalReferenceVerifier(new CheckboxApiClient(_http, State.Authentication, new Logger()), State.Settings));
            var sync = new MarketplaceSyncService([State.Source, State.Rozetka], State.Settings, State.Cache);
            Workspace = new(State.Settings, sync, State.Links, State.Details, State.Dialogs,
                autoLinkEnabled: true, automaticCache: Automatic, fiscalVerifier: Verifier) { CoordinatorManaged = true };
            Main = new(State.ReceiptSource, State.Images, State.Settings, State.Authentication, State.History,
                State.Printer, State.Dialogs, new Logger(), Workspace)
                { DateFrom = new(2026, 9, 23), DateTo = new(2026, 9, 23), CoordinatorManaged = true };
        }

        public async Task SeedAsync()
        {
            await Main.RefreshAllAsync();
            AssertOldLinks();
            Equal(1, FiscalHttpCodes.Count); Equal(EstablishedOldCode, FiscalHttpCodes[0]);
            foreach (var row in Main.Receipts.Where(row => row.Id is ReceiptOne or ReceiptTwo))
            { row.IsSelected = true; row.IsSelectedForOrders = true; }
            foreach (var row in Workspace.AllOrderRows) row.IsSelectedForLabels = true;
        }

        public void AddNewPairs()
        {
            State.Source.Orders = [OldProm, NewProm]; State.Rozetka.Orders = [OldRozetka, NewRozetka];
            State.ReceiptSource.Rows = [EstablishedReceipt(ReceiptOne, 1, 10000),
                EstablishedReceipt(ReceiptTwo, 2, 20000, EstablishedOldCode), EstablishedReceipt(ReceiptThree, 3, 30000),
                EstablishedReceipt(EstablishedNextRozetkaReceipt, 4, 40000, EstablishedNewCode)];
        }

        public void AssertOldLinks()
        {
            var prom = Main.Receipts.Single(row => row.Id == ReceiptOne);
            var rozetka = Main.Receipts.Single(row => row.Id == ReceiptTwo);
            Equal(ReceiptLinkState.Suggested, prom.OrderMatch!.State); Equal(OldProm.Key, prom.OrderMatch.Order!.Key);
            Equal(ReceiptLinkState.Exact, rozetka.OrderMatch!.State); Equal(OldRozetka.Key, rozetka.OrderMatch.Order!.Key);
            Equal("Prom", prom.LinkedMarketplace); Equal("Rozetka", rozetka.LinkedMarketplace);
            True(Workspace.AllOrderRows.Single(row => row.Key == OldProm.Key).LinkedReceipts.Any(row => row.Id == ReceiptOne));
            True(Workspace.AllOrderRows.Single(row => row.Key == OldRozetka.Key).LinkedReceipts.Any(row => row.Id == ReceiptTwo));
        }

        public void AssertOldMarks()
        {
            True(Main.Receipts.Where(row => row.Id is ReceiptOne or ReceiptTwo)
                .All(row => row.IsSelected && row.IsSelectedForOrders), "Receipt selection was lost during refresh.");
            True(Workspace.AllOrderRows.Where(row => row.Key == OldProm.Key || row.Key == OldRozetka.Key)
                .All(row => row.IsSelectedForLabels), "Order label marks were lost during refresh.");
        }

        public void AssertNoPrintOrManualWrites()
        { Equal(0, State.Printer.Calls); Equal(0, State.Links.Saves); Equal(0, State.History.Saves); }

        public void Dispose()
        {
            Main.CancelPendingOperations();
            State.Source.Gate?.TrySetResult(true); State.Rozetka.Gate?.TrySetResult(true);
            _http.Dispose();
        }
    }

    private sealed class EstablishedCountingVerifier(IFiscalReferenceVerifier inner) : IFiscalReferenceVerifier
    {
        public List<OrderKey[]> Calls { get; } = [];
        public Task<IReadOnlyList<MarketplaceOrder>> VerifyAsync(IReadOnlyList<MarketplaceOrder> orders, string accountContext,
            CancellationToken cancellationToken = default)
        { Calls.Add(orders.Select(order => order.Key).ToArray()); return inner.VerifyAsync(orders, accountContext, cancellationToken); }
    }

    private sealed class EstablishedLinkObserver : IDisposable
    {
        private readonly List<(INotifyPropertyChanged Source, PropertyChangedEventHandler Handler)> _handlers = [];
        public List<string> Lost { get; } = [];
        public EstablishedLinkObserver(EstablishedRefreshFixture fixture)
        {
            foreach (var receipt in fixture.Main.Receipts.Where(row => row.Id is ReceiptOne or ReceiptTwo))
            {
                PropertyChangedEventHandler handler = (_, e) =>
                {
                    if (e.PropertyName is nameof(ReceiptRowViewModel.OrderMatch) or nameof(ReceiptRowViewModel.LinkedMarketplace))
                        if (receipt.OrderMatch?.Order is null || receipt.LinkedMarketplace.Length == 0)
                            Lost.Add("Receipt link/color disappeared: " + receipt.Id);
                };
                receipt.PropertyChanged += handler; _handlers.Add((receipt, handler));
            }
            foreach (var order in fixture.Workspace.AllOrderRows)
            {
                PropertyChangedEventHandler handler = (_, e) =>
                {
                    if (e.PropertyName == nameof(MarketplaceOrderRowViewModel.LinkedReceipts) && order.LinkedReceipts.Count == 0)
                        Lost.Add("Order receipt button disappeared: " + order.Number);
                };
                order.PropertyChanged += handler; _handlers.Add((order, handler));
            }
        }
        public void Dispose() { foreach (var (source, handler) in _handlers) source.PropertyChanged -= handler; }
    }

    private static async Task EstablishedRefreshNoFlickerAsync()
    {
        using var fixture = new EstablishedRefreshFixture();
        await fixture.SeedAsync();
        var oldReceipts = fixture.Main.Receipts.ToDictionary(row => row.Id);
        var oldOrders = fixture.Workspace.AllOrderRows.ToDictionary(row => row.Key);
        var oldDetails = fixture.State.Details.RequestedIds.ToArray();
        fixture.State.Details.FailIds.UnionWith([ReceiptOne, ReceiptTwo]);
        fixture.AddNewPairs();
        fixture.State.Source.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.State.Rozetka.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observer = new EstablishedLinkObserver(fixture);
        var refresh = fixture.Main.RefreshAllAsync();
        try
        {
            await RefreshWaitUntilAsync(() => fixture.State.Source.FetchCalls == 2 && fixture.State.Rozetka.FetchCalls == 2 &&
                fixture.Main.Receipts.Count == 4);
            True(!refresh.IsCompleted && fixture.Main.IsRefreshingAll);
            fixture.AssertOldLinks(); fixture.AssertOldMarks();
            Equal(1, fixture.Verifier.Calls.Count); Equal(1, fixture.FiscalHttpCodes.Count);
            True(fixture.Main.Receipts.Where(row => row.Id is ReceiptThree or EstablishedNextRozetkaReceipt)
                .All(row => row.OrderMatch?.Order is null), "New receipts were paired before marketplace coverage completed.");
            True(observer.Lost.Count == 0, string.Join("; ", observer.Lost));
            fixture.State.Source.Gate.TrySetResult(true); fixture.State.Rozetka.Gate.TrySetResult(true);
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.AssertOldLinks(); fixture.AssertOldMarks();
            Equal(ReceiptLinkState.Suggested, fixture.Main.Receipts.Single(row => row.Id == ReceiptThree).OrderMatch!.State);
            Equal(fixture.NewProm.Key, fixture.Main.Receipts.Single(row => row.Id == ReceiptThree).OrderMatch!.Order!.Key);
            Equal(ReceiptLinkState.Exact, fixture.Main.Receipts.Single(row => row.Id == EstablishedNextRozetkaReceipt).OrderMatch!.State);
            Equal(fixture.NewRozetka.Key, fixture.Main.Receipts.Single(row => row.Id == EstablishedNextRozetkaReceipt).OrderMatch!.Order!.Key);
            Equal(2, fixture.Verifier.Calls.Count);
            True(fixture.Verifier.Calls[1].SequenceEqual([fixture.NewRozetka.Key]), "Established fiscal links were submitted for verification again.");
            True(fixture.FiscalHttpCodes.SequenceEqual([EstablishedOldCode, EstablishedNewCode]));
            foreach (var id in new[] { ReceiptOne, ReceiptTwo })
                Equal(oldDetails.Count(item => item == id), fixture.State.Details.RequestedIds.Count(item => item == id));
            foreach (var (id, row) in oldReceipts) True(ReferenceEquals(row, fixture.Main.Receipts.Single(current => current.Id == id)));
            foreach (var (key, row) in oldOrders) True(ReferenceEquals(row, fixture.Workspace.AllOrderRows.Single(current => current.Key == key)));
            True(observer.Lost.Count == 0, string.Join("; ", observer.Lost));
            fixture.AssertNoPrintOrManualWrites();
        }
        finally
        {
            fixture.State.Source.Gate.TrySetResult(true); fixture.State.Rozetka.Gate.TrySetResult(true);
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task EstablishedRefreshSkipsOldReadsAsync()
    {
        using var fixture = new EstablishedRefreshFixture();
        await fixture.SeedAsync();
        var details = fixture.State.Details.Calls;
        fixture.State.Details.FailIds.UnionWith([ReceiptOne, ReceiptTwo]);
        using var observer = new EstablishedLinkObserver(fixture);
        for (var i = 0; i < 2; i++)
        {
            await fixture.Main.RefreshAllAsync();
            fixture.AssertOldLinks(); fixture.AssertOldMarks();
            Equal(1, fixture.Verifier.Calls.Count); Equal(1, fixture.FiscalHttpCodes.Count);
            Equal(details, fixture.State.Details.Calls);
        }
        Equal(3, fixture.State.Source.FetchCalls); Equal(3, fixture.State.Rozetka.FetchCalls);
        Equal(0, fixture.State.Source.DetailCalls); Equal(0, fixture.State.Rozetka.DetailCalls);
        True(observer.Lost.Count == 0, string.Join("; ", observer.Lost));
        fixture.AssertNoPrintOrManualWrites();
    }

    private static async Task EstablishedRefreshFailureAsync()
    {
        foreach (var cancel in new[] { false, true })
        {
            using var fixture = new EstablishedRefreshFixture();
            await fixture.SeedAsync();
            fixture.AddNewPairs();
            fixture.State.Source.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.State.Rozetka.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.State.Source.Fail = !cancel; fixture.State.Rozetka.Fail = !cancel;
            using var observer = new EstablishedLinkObserver(fixture);
            var refresh = fixture.Main.RefreshAllAsync();
            try
            {
                await RefreshWaitUntilAsync(() => fixture.State.Source.FetchCalls == 2 && fixture.State.Rozetka.FetchCalls == 2 &&
                    fixture.Main.Receipts.Count == 4);
                fixture.AssertOldLinks(); fixture.AssertOldMarks();
                if (cancel) fixture.Main.CancelRefreshAllCommand.Execute(null);
                else { fixture.State.Source.Gate.TrySetResult(true); fixture.State.Rozetka.Gate.TrySetResult(true); }
                await refresh.WaitAsync(TimeSpan.FromSeconds(5));
                fixture.AssertOldLinks(); fixture.AssertOldMarks();
                True(fixture.Main.Receipts.Where(row => row.Id is ReceiptThree or EstablishedNextRozetkaReceipt)
                    .All(row => row.OrderMatch is { State: ReceiptLinkState.Incomplete, Order: null }),
                    "Failed/canceled refresh certified or assigned a previously unlinked receipt.");
                True(observer.Lost.Count == 0, string.Join("; ", observer.Lost));
                Equal(1, fixture.Verifier.Calls.Count); Equal(1, fixture.FiscalHttpCodes.Count);
                fixture.AssertNoPrintOrManualWrites();
            }
            finally
            {
                fixture.State.Source.Gate.TrySetResult(true); fixture.State.Rozetka.Gate.TrySetResult(true);
                await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    private static async Task EstablishedRefreshCheckboxFailureAsync()
    {
        using var fixture = new EstablishedRefreshFixture(includeUnlinkedReceipt: true);
        await fixture.SeedAsync();
        True(fixture.Main.Receipts.Single(row => row.Id == ReceiptThree).OrderMatch?.Order is null);
        fixture.State.Source.Orders = [fixture.OldProm, fixture.NewProm];
        fixture.State.ReceiptSource.Fail = true;
        using var observer = new EstablishedLinkObserver(fixture);
        await fixture.Main.RefreshAllAsync();
        fixture.AssertOldLinks(); fixture.AssertOldMarks();
        True(fixture.Main.Receipts.Single(row => row.Id == ReceiptThree).OrderMatch is
            { State: ReceiptLinkState.Incomplete, Order: null }, "Stale Checkbox coverage authorized a new automatic pair.");
        Equal(1, fixture.Verifier.Calls.Count); Equal(1, fixture.FiscalHttpCodes.Count);
        True(observer.Lost.Count == 0, string.Join("; ", observer.Lost));
        fixture.AssertNoPrintOrManualWrites();
    }
}
