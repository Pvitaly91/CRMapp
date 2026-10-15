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
            True(match.Order is null); True(match.State != ReceiptLinkState.Suggested);
            Equal(AutomaticLinkBasis.None, match.Basis); Equal(0, match.Candidates.Count);
        }
        // Matching product names, quantities and payment labels never enable Rozetka heuristics.
        var paid = new ReceiptRecord
        {
            Id = receipt.Id, Type = receipt.Type, Status = receipt.Status, FiscalCode = receipt.FiscalCode,
            FiscalDate = receipt.FiscalDate, TotalSumMinor = receipt.TotalSumMinor,
            Payments = [new("CASHLESS", "Платіж RozetkaPay", 25000)]
        };
        var paidOrder = rozetka with { PaymentMethod = "ROZETKAPAY", Currency = "UAH" };
        True(matcher.MatchAll([paid], "account", [paidOrder], [], true, details)[receipt.Id].Order is null);
        Equal(0, matcher.Match(receipt, "account", [rozetka], [], true).Candidates.Count);
        foreach (var orders in new[] { new[] { rozetka, prom }, new[] { prom, rozetka } })
        {
            var match = matcher.MatchAll([receipt], "account", orders, [], true, details)[receipt.Id];
            Equal(ReceiptLinkState.Suggested, match.State); Equal(prom.Key, match.Order!.Key);
            Equal(AutomaticLinkBasis.AmountAndProducts, match.Basis); Equal(1, match.CompetingOrderCount);
            True(match.Candidates.All(o => o.Key.Marketplace == MarketplaceKind.Prom));
        }
        // A caller cannot revive a previously accepted Rozetka amount guess directly.
        var old = new ReceiptOrderMatch(ReceiptLinkState.Suggested, rozetka, "Legacy amount link", [rozetka]);
        var resumed = matcher.MatchAll([receipt], "account", [rozetka, prom], [], true, details,
            existingLinks: new Dictionary<string, ReceiptOrderMatch> { [receipt.Id] = old })[receipt.Id];
        Equal(prom.Key, resumed.Order!.Key);
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
        return Task.CompletedTask;
    }
}
