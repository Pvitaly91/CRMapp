using System.Text.Json;
using System.Text.Json.Nodes;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class DiscountMatchingTests
{
    internal const string Id = "b5500000-0000-0000-0000-000000000001";
    internal static readonly DateTimeOffset Time = new(2026, 10, 13, 13, 44, 0, TimeSpan.FromHours(3));
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("discounts: calculated item VALUE reconciles 320 minus16 to304 without changing source goods", Run(ValueItem)),
        ("discounts: item and receipt VALUE PERCENT and sequential reductions reconcile exactly", Run(Formats)),
        ("discounts: malformed surcharge return rounding and partial payment documents remain blocked", Run(InvalidDocuments)),
        ("discounts: complete matching products prices quantities and trustworthy totals remain mandatory", Run(ProductGuards)),
        ("discounts: ambiguity manual suppression canceled orders and fiscal keys keep priority", Run(DecisionGuards)),
        ("discounts: old affected details refresh while ordinary details and assigned pairs survive cache upgrade", Run(CacheUpgrade))
    ];
    private static Func<Task> Run(Action action) => () => { action(); return Task.CompletedTask; };
    private static void Check(bool condition, string message = "Discount assertion failed.") { if (!condition) throw new Exception(message); }

    internal static JsonObject FixtureJson(string id = Id) => JsonNode.Parse(JsonSerializer.Serialize(new
    {
        id, serial = 4891, type = "SELL", status = "DONE", fiscal_date = Time.ToString("O"),
        total_sum = 30400, round_sum = 0, discounts = Array.Empty<object>(),
        goods = new object[]
        {
            new { good = new { name = "Синтетичний модуль A", code = "a", price = 5000 }, quantity = 5000, sum = 25000, is_return = false,
                discounts = new[] { new { type = "DISCOUNT", mode = "VALUE", value = 1600.0m, sum = -1600 } } },
            new { good = new { name = "Синтетичний модуль B", code = "b", price = 7000 }, quantity = 1000, sum = 7000, is_return = false,
                discounts = Array.Empty<object>() }
        }
    }))!.AsObject();
    internal static (ReceiptRecord Receipt, ReceiptDetails Details, MarketplaceOrder Order) Parse(JsonObject json)
    {
        var receipt = ReceiptParser.ParsePage("{\"results\":[" + json.ToJsonString() + "]}").Single();
        var details = ReceiptDetailsService.Parse(json.ToJsonString(), receipt.Id);
        var order = new MarketplaceOrder { Key = new(MarketplaceKind.Prom, "prom-test", "discount-order"),
            Number = "DISCOUNT-ORDER", Currency = "UAH", Total = receipt.TotalSum, CreatedAt = Time.AddHours(-2),
            Items = [new("Синтетичний модуль A", "shop-a", 5, 50, 250), new("Синтетичний модуль B", "shop-b", 1, 70, 70)],
            ItemsComplete = true, ItemListComplete = true, DeliveryCost = 0 };
        return (receipt, details, order);
    }
    private static ReceiptOrderMatch Match(ReceiptRecord receipt, ReceiptDetails? details, params MarketplaceOrder[] orders) =>
        new ReceiptOrderMatchingService().MatchAll([receipt], "account", orders, [], true,
            details is null ? null : new Dictionary<string, ReceiptDetails> { [receipt.Id] = details })[receipt.Id];
    private static void Suggested(JsonObject json)
    {
        var (receipt, details, order) = Parse(json);
        Check(details.ItemsComplete && details.AmountComparisonIssue == "" && details.DiscountReconciliation is not null);
        var match = Match(receipt, details, order);
        Check(match.State == ReceiptLinkState.Suggested && match.Basis == AutomaticLinkBasis.AmountAndProducts, match.Explanation);
        Check(match.Explanation.Contains("Знижку враховано"));
    }
    private static void ValueItem()
    {
        var json = FixtureJson(); var before = json.ToJsonString(); Suggested(json);
        var (receipt, details, order) = Parse(json);
        Check(details.DiscountReconciliation is { GrossTotal: 320m, DiscountTotal: 16m, NetTotal: 304m });
        Check(details.Items[0].Total == 250 && details.Items[0].Quantity == 5 && details.Items[0].UnitPrice == 50);
        Check(receipt.AmountComparisonIssue.Length > 0 && receipt.TotalSum == 304 && order.Total == 304);
        Check(before == json.ToJsonString());
        Check(Match(receipt, details, order).Explanation.Contains("320 − 16 = 304"));
    }
    private static void Formats()
    {
        foreach (var mode in new[] { "VALUE", "PERCENT" })
        {
            var root = FixtureJson(); root["goods"]![0]!["discounts"] = new JsonArray();
            root["discounts"] = new JsonArray(new JsonObject { ["type"] = "DISCOUNT", ["mode"] = mode,
                ["value"] = mode == "VALUE" ? 1600 : 5, ["sum"] = -1600 });
            Suggested(root);
        }
        var percentItem = FixtureJson(); percentItem["goods"]![0]!["discounts"]![0]!["mode"] = "PERCENT";
        percentItem["goods"]![0]!["discounts"]![0]!["value"] = 6.4m; Suggested(percentItem);
        var sequential = FixtureJson(); sequential["total_sum"] = 30910;
        sequential["goods"]![0]!["discounts"] = new JsonArray(
            new JsonObject { ["type"] = "DISCOUNT", ["mode"] = "VALUE", ["value"] = 500, ["sum"] = -500 },
            new JsonObject { ["type"] = "DISCOUNT", ["mode"] = "PERCENT", ["value"] = 2, ["sum"] = -490 });
        sequential["discounts"] = new JsonArray(new JsonObject { ["type"] = "DISCOUNT", ["mode"] = "VALUE", ["value"] = 100, ["sum"] = -100 });
        Suggested(sequential);
    }
    private static void InvalidDocuments()
    {
        Action<JsonObject>[] invalid =
        [
            j => j["goods"]![0]!["discounts"]![0]!["type"] = "EXTRA_CHARGE",
            j => j["goods"]![0]!["discounts"]![0]!["mode"] = "UNKNOWN",
            j => j["goods"]![0]!["discounts"]![0]!["value"] = 1601,
            j => j["goods"]![0]!["discounts"]![0]!["sum"] = 1600,
            j => j["goods"]![0]!["discounts"]![0]!.AsObject().Remove("sum"),
            j => { j["goods"]![0]!["discounts"]![0]!["mode"] = "PERCENT"; j["goods"]![0]!["discounts"]![0]!["value"] = 5; },
            j => j["goods"]![0]!["is_return"] = true,
            j => j["pre_payment_relation_id"] = Id,
            j => j["round_sum"] = 1,
            j => j["round_sum"] = "unknown",
            j => j["total_sum"] = 30401,
            j => j.Remove("total_sum"),
            j => j["goods"]![0]!["sum"] = 24999,
            j => j["goods"]![0]!["discounts"] = new JsonObject { ["value"] = 1600 },
            j => j["goods"]![0]!["discounts"]![0]!["value"] = decimal.MaxValue
        ];
        foreach (var change in invalid)
        {
            var json = FixtureJson(); change(json); var (receipt, details, order) = Parse(json);
            Check(details.DiscountReconciliation is null, json.ToJsonString());
            Check(Match(receipt, details, order).State != ReceiptLinkState.Suggested);
        }
    }
    private static void ProductGuards()
    {
        var (receipt, details, order) = Parse(FixtureJson());
        foreach (var changed in new[]
        {
            order with { Items = [order.Items[0] with { Name = "Інший модуль" }, order.Items[1]] },
            order with { Items = [order.Items[0] with { Quantity = 6, Total = 300 }, order.Items[1]] },
            order with { Items = [order.Items[0] with { UnitPrice = 51, Total = 255 }, order.Items[1]] },
            order with { Items = [], ItemListComplete = false }, order with { Discount = 15 },
            order with { AmountComparisonIssue = "Часткова оплата" }
        }) Check(Match(receipt, details, changed).State != ReceiptLinkState.Suggested);
        Check(Match(receipt, null, order).State != ReceiptLinkState.Suggested);
        // List responses may omit goods/discount flags. Parsed detail evidence must
        // still require matching gross prices instead of falling back to unique sum.
        var noListFlag = new ReceiptRecord { Id = receipt.Id, Type = "SELL", Status = "DONE", FiscalDate = Time, TotalSumMinor = 30400 };
        var netPricedOrder = order with { Items = [order.Items[0] with { UnitPrice = 46.8m, Total = 234 }, order.Items[1]] };
        Check(Match(noListFlag, details, netPricedOrder).State != ReceiptLinkState.Suggested);
        Check(Match(noListFlag, details, order).State == ReceiptLinkState.Suggested);
        Check(Match(receipt, details with { DiscountReconciliation = details.DiscountReconciliation! with { Schema = 99 } }, order).State != ReceiptLinkState.Suggested);
        Check(Match(receipt, details with { DiscountReconciliation = details.DiscountReconciliation! with { DiscountTotal = 15 } }, order).State != ReceiptLinkState.Suggested);
        var loading = new ReceiptOrderMatchingService().MatchAll([receipt], "account", [order], [], true,
            new Dictionary<string, ReceiptDetails> { [receipt.Id] = details }, loading: new HashSet<string> { receipt.Id });
        Check(loading[receipt.Id].State != ReceiptLinkState.Suggested);
    }
    private static void DecisionGuards()
    {
        var (receipt, details, order) = Parse(FixtureJson()); var other = order with { Key = order.Key with { OrderId = "other" } };
        var ambiguous = Match(receipt, details, order, other);
        Check(ambiguous.State == ReceiptLinkState.Candidates && ambiguous.Ambiguous);
        Check(Match(receipt, details, order with { Status = "canceled" }).State != ReceiptLinkState.Suggested);
        Check(Match(receipt, details, order with { CreatedAt = Time.AddDays(-1) }).State != ReceiptLinkState.Suggested);
        var decisions = new[] { new ReceiptOrderDecision { AccountContext = "account", ReceiptId = receipt.Id, SuppressAutomatic = true } };
        var mapper = new ReceiptOrderMatchingService(); var data = new Dictionary<string, ReceiptDetails> { [receipt.Id] = details };
        Check(mapper.MatchAll([receipt], "account", [order], decisions, true, data)[receipt.Id].State != ReceiptLinkState.Suggested);
        decisions[0].SuppressAutomatic = false; decisions[0].ConfirmedOrder = order.Key;
        Check(mapper.MatchAll([receipt], "account", [order], decisions, true, data)[receipt.Id].State == ReceiptLinkState.Manual);
        Check(Match(receipt, details, order with { ReceiptIds = [receipt.Id] }).State == ReceiptLinkState.Exact);
    }
    private static void CacheUpgrade()
    {
        var (receipt, details, order) = Parse(FixtureJson());
        var mapper = new CachedReceiptOrderMatchingService(); var cache = new AutomaticMatchCache { AccountContext = "account" };
        var legacy = details with { AmountValidationVersion = 0, DiscountReconciliation = null, ItemsComplete = false,
            AmountComparisonIssue = "Сума позицій чека відрізняється від загальної: потрібна перевірка структури оплати." };
        mapper.RememberDetails(cache, "account", receipt, legacy);
        Check(!mapper.TryGetDetails(cache, "account", receipt, out _));
        mapper.RememberDetails(cache, "account", receipt, details);
        Check(mapper.TryGetDetails(cache, "account", receipt, out var restored) && restored.DiscountReconciliation!.DiscountTotal == 16);
        cache = JsonSerializer.Deserialize<AutomaticMatchCache>(JsonSerializer.Serialize(cache))!;
        Check(mapper.TryGetDetails(cache, "account", receipt, out restored));
        var matches = mapper.MatchAll(cache, [receipt], "account", [order], [], true,
            new Dictionary<string, ReceiptDetails> { [receipt.Id] = restored });
        Check(matches.Matches[receipt.Id].State == ReceiptLinkState.Suggested);
        var linkedAt = cache.Links[receipt.Id].LinkedAtUtc;
        cache.Snapshots[0] = cache.Snapshots[0] with { AlgorithmVersion = "same-kyiv-day-active-unlinked-rozetka-canceled-v13" };
        var saved = mapper.RestoreKnownSuggestions(cache, [receipt], "account", [order], []);
        Check(saved[receipt.Id].State == ReceiptLinkState.Suggested && cache.Links[receipt.Id].LinkedAtUtc == linkedAt);
        var ordinaryJson = FixtureJson("b5500000-0000-0000-0000-000000000002"); ordinaryJson["goods"]![0]!["discounts"] = new JsonArray(); ordinaryJson["total_sum"] = 32000;
        var ordinary = Parse(ordinaryJson);
        mapper.RememberDetails(cache, "account", ordinary.Receipt, ordinary.Details with { AmountValidationVersion = 0 });
        Check(mapper.TryGetDetails(cache, "account", ordinary.Receipt, out _));
    }
}
