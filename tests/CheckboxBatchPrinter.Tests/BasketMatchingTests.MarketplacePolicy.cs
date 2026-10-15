using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static partial class BasketMatchingTests
{
    private static MarketplaceOrder RozetkaOrder() => Order() with
        { Key = new(MarketplaceKind.Rozetka, "synthetic-rozetka-store", "908000001") };

    private static Task MarketplacePolicyAsync()
    {
        var receipt = Receipt(); var rozetka = RozetkaOrder(); var prom = Order();
        var details = new Dictionary<string, ReceiptDetails> { [receipt.Id] = Details() };
        var matcher = new ReceiptOrderMatchingService();
        foreach (var complete in new[] { false, true })
        foreach (var products in new[] { false, true })
        {
            var match = matcher.MatchAll([receipt], "account", [rozetka], [], complete,
                products ? details : null)[receipt.Id];
            if (!complete) { True(match.Order is null); True(match.State != ReceiptLinkState.Suggested); }
            else
            {
                Equal(ReceiptLinkState.Suggested, match.State); Equal(rozetka.Key, match.Order!.Key);
                Equal(products ? AutomaticLinkBasis.AmountAndProducts : AutomaticLinkBasis.UniqueAmount, match.Basis);
            }
        }
        // Without fiscal assertions Rozetka uses the same amount/product rules as Prom.
        var paid = new ReceiptRecord
        {
            Id = receipt.Id, Type = receipt.Type, Status = receipt.Status, FiscalCode = receipt.FiscalCode,
            FiscalDate = receipt.FiscalDate, TotalSumMinor = receipt.TotalSumMinor,
            Payments = [new("CASHLESS", "Платіж RozetkaPay", 25000)]
        };
        var paidOrder = rozetka with { PaymentMethod = "ROZETKAPAY", Currency = "UAH" };
        Equal(rozetka.Key, matcher.MatchAll([paid], "account", [paidOrder], [], true, details)[receipt.Id].Order!.Key);
        Equal(rozetka.Key, matcher.Match(receipt, "account", [rozetka], [], true).Candidates.Single().Key);
        foreach (var orders in new[] { new[] { rozetka, prom }, new[] { prom, rozetka } })
        {
            var match = matcher.MatchAll([receipt], "account", orders, [], true, details)[receipt.Id];
            True(match.Order is null && match.Ambiguous);
            Equal(2, match.CompetingOrderCount);
            True(match.Candidates.Select(o => o.Key).ToHashSet().SetEquals([rozetka.Key, prom.Key]));
        }
        // A previously accepted keyless Rozetka pair keeps its allocation during refresh.
        var old = new ReceiptOrderMatch(ReceiptLinkState.Suggested, rozetka, "Legacy amount link", [rozetka]);
        var resumed = matcher.MatchAll([receipt], "account", [rozetka, prom], [], true, details,
            existingLinks: new Dictionary<string, ReceiptOrderMatch> { [receipt.Id] = old })[receipt.Id];
        Equal(rozetka.Key, resumed.Order!.Key);

        var blank = rozetka with
        {
            ReceiptIds = ["", " "], FiscalReceiptNumbers = ["\t"], FiscalReceiptUrls = [""],
            FiscalReferences = [new(FiscalDocumentKeyKind.FiscalCode, " ", "synthetic", rozetka.Key, "Checkbox", "sale")],
            FiscalDataStatus = "Synthetic unavailable PRRO: 15005"
        };
        True(AutomaticReceiptLinkPolicy.CanUseAmountFallback(blank));
        Equal(rozetka.Key, matcher.MatchAll([receipt], "account", [blank], [], true, details)[receipt.Id].Order!.Key);
        Equal(ReceiptLinkState.Suggested, matcher.MatchAll([receipt], "account", [prom], [], true, details)[receipt.Id].State);
        return Task.CompletedTask;
    }

    private static Task MarketplacePolicyUnresolvedAsync()
    {
        var receipt = Receipt(); var order = RozetkaOrder();
        var details = new Dictionary<string, ReceiptDetails> { [receipt.Id] = Details() };
        var matcher = new ReceiptOrderMatchingService();
        var old = new ReceiptOrderMatch(ReceiptLinkState.Suggested, order, "Earlier keyless fallback", [order]);
        var code = new FiscalDocumentReference(FiscalDocumentKeyKind.FiscalCode, "fiscal-code", "prro.prro_receipt_fiscal_code",
            order.Key, "Checkbox", "rozetka.prro");
        foreach (var asserted in new[]
        {
            order with { ReceiptIds = [OtherId] },
            order with { ReceiptIds = ["4961"] },
            order with { FiscalReceiptNumbers = ["fiscal-code"] },
            order with { FiscalReceiptUrls = ["https://unknown.example/receipt"] },
            order with { FiscalReferences = [code] },
            order with { FiscalReferences = [code with { Provider = "unknown" }] },
            order with { FiscalReferences = [code with { Value = "", VerifiedReceiptId = OtherId }] },
            order with { FiscalReferences = [code with { VerifiedReceiptId = Id, VerifiedAccountContext = "other-account" }] }
        })
        {
            True(AutomaticReceiptLinkPolicy.HasFiscalKey(asserted));
            True(!AutomaticReceiptLinkPolicy.CanUseAmountFallback(asserted));
            foreach (var restoreOld in new[] { false, true })
            {
                var match = matcher.MatchAll([receipt], "account", [asserted], [], true, details,
                    existingLinks: restoreOld ? new Dictionary<string, ReceiptOrderMatch> { [Id] = old } : null)[Id];
                True(match.Order is null && match.State != ReceiptLinkState.Suggested);
            }
        }
        var conflicting = order with
        {
            FiscalReferences = [code with { VerifiedReceiptId = Id, VerifiedAccountContext = "account" },
                code with { Value = "contradictory-code" }]
        };
        Equal(ReceiptLinkState.Conflict, Match([conflicting]).State);
        return Task.CompletedTask;
    }

    private static Task MarketplacePolicyAuthoritativeAsync()
    {
        var receipt = Receipt(); var rozetka = RozetkaOrder(); var prom = Order();
        var exact = rozetka with { ReceiptIds = [receipt.Id] };
        var matched = Match([prom, exact]);
        Equal(ReceiptLinkState.Exact, matched.State); Equal(rozetka.Key, matched.Order!.Key);
        var manual = new ReceiptOrderDecision
            { AccountContext = "account", ReceiptId = receipt.Id, ConfirmedOrder = rozetka.Key, UpdatedAtUtc = Time };
        matched = Match([prom, rozetka], decisions: [manual]);
        Equal(ReceiptLinkState.Manual, matched.State); Equal(rozetka.Key, matched.Order!.Key);
        matched = Match([prom, exact], decisions:
            [new() { AccountContext = "account", ReceiptId = receipt.Id, SuppressAutomatic = true, UpdatedAtUtc = Time }]);
        True(matched.Order is null);
        var rejected = new ReceiptOrderDecision
            { AccountContext = "account", ReceiptId = receipt.Id, RejectedOrders = [rozetka.Key], UpdatedAtUtc = Time };
        True(Match([rozetka], decisions: [rejected]).Order is null);
        True(Match([rozetka], decisions:
            [new() { AccountContext = "account", ReceiptId = receipt.Id, SuppressAutomatic = true, UpdatedAtUtc = Time }]).Order is null);
        return Task.CompletedTask;
    }
}
