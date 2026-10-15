using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static partial class EstablishedReceiptLinkSessionTests
{
    private const string Account = "session-cashier";
    private const string Shop = "77000000000000000000000000000001";
    private static readonly string[] Connections = [$"Prom:{Shop}", $"Rozetka:{Shop}"];
    private static readonly DateTimeOffset Time = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        Test("established session: unchanged fiscal links survive fresh order data and reserve allocations", StableRefresh),
        Test("established session: account and connection changes clear runtime proof and an unloaded session has none", AccountAndRestart),
        Test("established session: missing or changed pair evidence invalidates only that pair", ChangedEvidence),
        Test("established session: manual decisions and canceled orders override prior exact links", DecisionsAndCancellation),
        Test("established session: new exact and unresolved fiscal claims block reuse without leaking proof", CompetingClaims),
        Test("established session: repeated captures keep out-of-range pairs but never promote suggestions or inherited returns", CaptureScope),
        Test("established session: separate sale and return documents retain both accepted proofs", MultipleDocuments),
        Test("established session: runtime storage remains bounded and capture rejects fabricated exact results", BoundAndCaptureGuard),
        .. PersistenceScenarios
    ];
    private static (string, Func<Task>) Test(string name, Action test) => (name, () => { test(); return Task.CompletedTask; });
    private static void Yes(bool condition) { if (!condition) throw new InvalidOperationException("Established session assertion failed."); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; got {actual}."); }
    private static ReceiptRecord Receipt(int n = 1, long amount = 10000, string type = ReceiptTypes.Sell, string? code = null,
        string branch = "", string paymentLabel = "", DateTimeOffset? fiscalDate = null) => new()
    {
        Id = $"77000000-0000-0000-0000-{n:D12}", FiscalCode = code ?? $"CODE-{n:D6}",
        Status = "DONE", Type = type, Serial = n, TotalSumMinor = amount, FiscalDate = fiscalDate ?? Time,
        OrganizationId = "78000000-0000-0000-0000-000000000001", CashRegisterFiscalNumber = "12345", BranchName = branch,
        Payments = paymentLabel.Length == 0 ? [] : [new("CASH", paymentLabel, amount)]
    };
    private static MarketplaceOrder Order(ReceiptRecord receipt, MarketplaceKind kind = MarketplaceKind.Rozetka, int n = 1)
    {
        var key = new OrderKey(kind, Shop, n.ToString());
        return new()
        {
            Key = key, Number = n.ToString(), Total = 100, Currency = "UAH", CreatedAt = Time.AddHours(-1),
            Items = [new("Synthetic cable", "", 1, 100, 100)], ItemsComplete = true, ItemListComplete = true,
            ReceiptIds = kind == MarketplaceKind.Prom ? [receipt.Id] : [],
            FiscalReferences = kind == MarketplaceKind.Prom ? [] : [new(FiscalDocumentKeyKind.FiscalCode,
                receipt.FiscalCode, "prro.prro_receipt_fiscal_code", key, "Checkbox", "rozetka.prro")
                { VerifiedReceiptId = receipt.Id, VerifiedAccountContext = Account }]
        };
    }
    private static MarketplaceOrder Raw(MarketplaceOrder order) => JsonSerializer.Deserialize<MarketplaceOrder>(JsonSerializer.Serialize(order))!;
    private static EstablishedReceiptLinkSession Accepted(ReceiptRecord receipt, MarketplaceOrder order)
    {
        var session = new EstablishedReceiptLinkSession();
        var matches = new ReceiptOrderMatchingService().MatchAll([receipt], Account, [order], [], true);
        Equal(ReceiptLinkState.Exact, matches[receipt.Id].State);
        session.Capture(Account, [receipt], matches, Connections);
        return session;
    }
    private static void NoProof(EstablishedLinkReuse result)
    {
        Equal(0, result.Matches.Count);
        Yes(result.Orders.SelectMany(order => order.FiscalReferences).All(reference => reference.VerifiedReceiptId.Length == 0));
    }

    private static void StableRefresh()
    {
        foreach (var kind in new[] { MarketplaceKind.Prom, MarketplaceKind.Rozetka })
        {
            var receipt = Receipt(); var order = Order(receipt, kind); var session = Accepted(receipt, order);
            var updated = Raw(order) with { Status = "completed", PaymentStatus = "paid", RetrievedAtUtc = Time.AddHours(1),
                Items = [new("Updated product display", "", 2, 50, 100)], Shipments = [new("nova_poshta", "synthetic", "in transit")],
                Buyer = new("Updated buyer"), Total = 101 };
            var otherReceipt = Receipt(2);
            var refreshedReceipt = Receipt(branch: "Updated branch display", paymentLabel: "Updated payment label");
            var reuse = session.Restore(Account, [refreshedReceipt, otherReceipt], [updated], [], Connections);
            Equal(1, reuse.Matches.Count); Equal(ReceiptLinkState.Exact, reuse.Matches[receipt.Id].State);
            Equal("completed", reuse.Matches[receipt.Id].Order!.Status); Equal(101m, reuse.Matches[receipt.Id].Order!.Total);
            Equal(order.ReceiptIds.Count, reuse.Orders[0].ReceiptIds.Count);
            if (kind == MarketplaceKind.Rozetka) Equal(receipt.Id, reuse.Orders[0].FiscalReferences.Single().VerifiedReceiptId);
            var matcher = new ReceiptOrderMatchingService();
            var partial = matcher.MatchAll([receipt, otherReceipt], Account, reuse.Orders, [], false, existingLinks: reuse.Matches);
            Equal(ReceiptLinkState.Exact, partial[receipt.Id].State); Yes(partial[otherReceipt.Id].Order is null);
            var subset = matcher.MatchAll([otherReceipt], Account, [reuse.Orders[0] with { Total = 100 }], [], true, existingLinks: reuse.Matches);
            Equal(1, subset.Count); Yes(subset[otherReceipt.Id].Order is null);
        }
    }

    private static void AccountAndRestart()
    {
        var receipt = Receipt(); var order = Order(receipt); var raw = Raw(order);
        NoProof(new EstablishedReceiptLinkSession().Restore(Account, [receipt], [raw], [], Connections));
        var session = Accepted(receipt, order);
        NoProof(session.Restore("different-cashier", [receipt], [raw], [], Connections));
        NoProof(session.Restore(Account, [receipt], [raw], [], Connections));
        session = Accepted(receipt, order);
        NoProof(session.Restore(Account, [receipt], [raw], [], Connections.Append("Rozetka:other-shop").ToArray()));
        NoProof(session.Restore(Account, [receipt], [raw], [], Connections));
        session = Accepted(receipt, order);
        Equal(1, session.Restore(Account, [receipt], [raw], [], Connections.Reverse().ToArray()).Matches.Count);
    }

    private static void ChangedEvidence()
    {
        var receipt = Receipt(); var order = Order(receipt); var raw = Raw(order);
        var changed = new[]
        {
            raw with { FiscalReferences = [] },
            raw with { FiscalReferences = [raw.FiscalReferences.Single() with { Value = "changed-code" }] },
            raw with { FiscalReferences = [raw.FiscalReferences.Single() with { OrganizationId = "other-organization" }] },
            raw with { ReceiptIds = [Receipt(2).Id] },
            raw with { Key = raw.Key with { ConnectionId = "other-shop" } }
        };
        foreach (var value in changed)
        {
            var session = Accepted(receipt, order);
            NoProof(session.Restore(Account, [receipt], [value], [], Connections));
            NoProof(session.Restore(Account, [receipt], [raw], [], Connections));
        }
        var changedReceipt = Accepted(receipt, order);
        NoProof(changedReceipt.Restore(Account, [Receipt(amount: 10100)], [raw], [], Connections));
        NoProof(changedReceipt.Restore(Account, [receipt], [raw], [], Connections));
        var missing = Accepted(receipt, order);
        NoProof(missing.Restore(Account, [receipt], [], [], Connections));
        NoProof(missing.Restore(Account, [receipt], [raw], [], Connections));
        var runtime = Accepted(receipt, order);
        Equal(0, runtime.Restore(Account, [receipt], [raw with { FiscalReferences =
            [raw.FiscalReferences.Single() with { VerifiedReceiptId = Receipt(2).Id, VerifiedAccountContext = Account }] }], [], Connections).Matches.Count);
        var duplicate = Accepted(receipt, order);
        NoProof(duplicate.Restore(Account, [receipt, Receipt(amount: 10100)], [raw], [], Connections));
    }

    private static void DecisionsAndCancellation()
    {
        var receipt = Receipt(); var order = Order(receipt); var raw = Raw(order);
        foreach (var decision in new[]
        {
            new ReceiptOrderDecision { AccountContext = Account, ReceiptId = receipt.Id, SuppressAutomatic = true },
            new ReceiptOrderDecision { AccountContext = Account, ReceiptId = receipt.Id, RejectedOrders = [order.Key] },
            new ReceiptOrderDecision { AccountContext = Account, ReceiptId = receipt.Id, ConfirmedOrder = order.Key with { OrderId = "other" } },
            new ReceiptOrderDecision { AccountContext = Account, ReceiptId = Receipt(2).Id, ConfirmedOrder = order.Key }
        }) NoProof(Accepted(receipt, order).Restore(Account, [receipt], [raw], [decision], Connections));
        NoProof(Accepted(receipt, order).Restore(Account, [receipt], [raw with { Status = "canceled" }], [], Connections));
        var old = new ReceiptOrderDecision { AccountContext = Account, ReceiptId = receipt.Id, SuppressAutomatic = true, UpdatedAtUtc = Time };
        var latest = new ReceiptOrderDecision { AccountContext = Account, ReceiptId = receipt.Id, UpdatedAtUtc = Time.AddMinutes(1) };
        Equal(1, Accepted(receipt, order).Restore(Account, [receipt], [raw], [old, latest], Connections).Matches.Count);
        Equal(1, Accepted(receipt, order).Restore(Account, [receipt], [raw],
            [new() { AccountContext = "other-cashier", ReceiptId = receipt.Id, SuppressAutomatic = true }], Connections).Matches.Count);
    }

    private static void CompetingClaims()
    {
        var receipt = Receipt(); var order = Order(receipt); var raw = Raw(order);
        var competing = Order(receipt, n: 2);
        foreach (var other in new[]
        {
            Raw(competing),
            Raw(competing) with { FiscalReferences = [Raw(competing).FiscalReferences.Single() with { Provider = "unknown", Source = "" }] },
            Order(receipt, MarketplaceKind.Prom, 2),
            Raw(competing) with { FiscalReferences = [], FiscalReceiptNumbers = [receipt.FiscalCode] },
            Raw(competing) with { FiscalReferences = [], FiscalReceiptUrls = [$"https://api.checkbox.ua/api/v1/receipts/{receipt.Id}/html"] }
        }) NoProof(Accepted(receipt, order).Restore(Account, [receipt], [raw, other], [], Connections));
        NoProof(Accepted(receipt, order).Restore(Account, [receipt, Receipt(2, code: receipt.FiscalCode)], [raw], [], Connections));
        Equal(1, Accepted(receipt, order).Restore(Account, [receipt, Receipt(2)], [raw, Order(Receipt(2), n: 2)], [], Connections).Matches.Count);
    }

    private static void CaptureScope()
    {
        var receipt = Receipt(); var order = Order(receipt); var raw = Raw(order); var session = Accepted(receipt, order);
        session.Capture(Account, [Receipt(2)], new Dictionary<string, ReceiptOrderMatch>(), Connections);
        NoProof(session.Restore(Account, [Receipt(2)], [raw], [], Connections));
        Equal(1, session.Restore(Account, [receipt], [raw], [], Connections).Matches.Count);
        var blank = new EstablishedReceiptLinkSession();
        blank.Capture(Account, [receipt], new Dictionary<string, ReceiptOrderMatch>
            { [receipt.Id] = new(ReceiptLinkState.Suggested, order, "suggestion", []) }, Connections);
        NoProof(blank.Restore(Account, [receipt], [raw], [], Connections));
        var returned = Receipt(2, type: ReceiptTypes.Return);
        blank.Capture(Account, [returned], new Dictionary<string, ReceiptOrderMatch>
            { [returned.Id] = new(ReceiptLinkState.Exact, order, "inherited return", []) }, Connections);
        NoProof(blank.Restore(Account, [returned], [raw], [], Connections));
    }

    private static void MultipleDocuments()
    {
        var sale = Receipt(); var returned = Receipt(2, type: ReceiptTypes.Return); var order = Order(sale);
        order = order with { FiscalReferences = [order.FiscalReferences[0], order.FiscalReferences[0] with
            { Value = returned.FiscalCode, DocumentId = "rozetka.return", VerifiedReceiptId = returned.Id }] };
        var matches = new ReceiptOrderMatchingService().MatchAll([sale, returned], Account, [order], [], true);
        Equal(ReceiptLinkState.Exact, matches[sale.Id].State); Equal(ReceiptLinkState.Exact, matches[returned.Id].State);
        var session = new EstablishedReceiptLinkSession(); session.Capture(Account, [sale, returned], matches, Connections);
        var reuse = session.Restore(Account, [sale, returned], [Raw(order)], [], Connections);
        Equal(2, reuse.Matches.Count);
        Yes(reuse.Matches.Values.All(match => match.Order!.FiscalReferences.All(reference => reference.VerifiedReceiptId.Length > 0)));
        var partial = new ReceiptOrderMatchingService().MatchAll([sale, returned], Account, reuse.Orders, [], false, existingLinks: reuse.Matches);
        Yes(partial.Values.All(match => match.State == ReceiptLinkState.Exact));
    }

    private static void BoundAndCaptureGuard()
    {
        var session = new EstablishedReceiptLinkSession(); var first = Receipt(); var last = Receipt(5001);
        for (var n = 1; n <= 5001; n++)
        {
            var receipt = Receipt(n); var order = Order(receipt, MarketplaceKind.Prom, n);
            session.Capture(Account, [receipt], new Dictionary<string, ReceiptOrderMatch>
                { [receipt.Id] = new(ReceiptLinkState.Exact, order, "accepted UUID", []) }, Connections);
        }
        var result = session.Restore(Account, [first, last],
            [Order(first, MarketplaceKind.Prom), Order(last, MarketplaceKind.Prom, 5001)], [], Connections);
        Equal(1, result.Matches.Count); Yes(result.Matches.ContainsKey(last.Id));
        var invalid = new EstablishedReceiptLinkSession(); var bare = Raw(Order(first));
        invalid.Capture(Account, [first], new Dictionary<string, ReceiptOrderMatch>
            { [first.Id] = new(ReceiptLinkState.Exact, bare, "unconfirmed bare fiscal code", []) }, Connections);
        NoProof(invalid.Restore(Account, [first], [bare], [], Connections));
    }
}
