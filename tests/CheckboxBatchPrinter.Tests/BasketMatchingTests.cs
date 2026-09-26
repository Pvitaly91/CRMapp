using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class BasketMatchingTests
{
    private const string Id = "a7000000-0000-0000-0000-000000000001";
    private const string OtherId = "a7000000-0000-0000-0000-000000000002";
    private static readonly DateTimeOffset Time = new(2026, 9, 24, 12, 0, 0, TimeSpan.FromHours(3));
    private static readonly OrderItem[] Products = [new("Датчик руху", "sku-1", 2m, 100m, 200m), new("Кабель", "sku-2", 1m, 50m, 50m)];
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("basket API-to-matcher: full Prom products and Checkbox goods suggest but never prove", () => AdapterToMatcherAsync(false)),
        ("basket API-to-matcher: localized Prom hryvnia totals and product prices permit probable links", () => AdapterToMatcherAsync(true)),
        ("basket API-to-matcher: identical name and price lines aggregate without bypassing ambiguity", SplitBasketAdapterAsync),
        ("basket API-to-matcher: typography normalizes but product codes and punctuation stay distinct", TypographyAdapterAsync),
        ("basket matching: complete details of every competing receipt are required", CompetingReceiptsAsync),
        ("basket matching: two orders stay ambiguous even after one is rejected", CompetingOrdersAsync),
        ("basket matching: unique sum tolerates missing basket fields but not product contradictions", FullBasketAsync),
        ("basket matching: time bounds currency type status and coverage are fail closed", BoundariesAsync),
        ("basket matching: saved manual rejected and suppression decisions take priority", DecisionsAsync),
        ("basket matching: fiscal keys and fiscal contradictions are never bypassed", FiscalPriorityAsync),
        ("basket matching: discounts invalid totals extra quantities and differing prices do not link", AdjustmentsAsync),
        ("basket parser safety: incomplete items allow amount basis, known adjustments remain blocked", ParserSafetyAsync)
    ];

    private static ReceiptRecord Receipt(string id = Id, string type = ReceiptTypes.Sell, string status = "DONE") => new()
        { Id = id, Type = type, Status = status, FiscalCode = "fiscal-code", FiscalDate = Time, TotalSumMinor = 25000 };
    private static MarketplaceOrder Order(string id = "429000001") => new()
    {
        Key = new(MarketplaceKind.Prom, "synthetic-store", id), Number = id, CreatedAt = Time.AddHours(-1),
        Items = Products, ItemsComplete = true, Total = 250m, Currency = "", SourceStatus = "accepted"
    };
    private static ReceiptDetails Details(string id = Id, IReadOnlyList<OrderItem>? products = null)
    {
        products ??= Products;
        return ReceiptDetailsService.Parse(JsonSerializer.Serialize(new
        {
            id, goods = products.Select(p => new
            {
                good = new { name = p.Name, code = p.Sku, price = p.UnitPrice * 100 },
                quantity = p.Quantity * 1000, sum = p.Total * 100
            }).ToArray(), total_sum = 25000, type = "SELL", status = "DONE"
        }), id);
    }
    private static ReceiptOrderMatch Match(IReadOnlyList<MarketplaceOrder>? orders = null,
        ReceiptRecord? receipt = null, ReceiptDetails? details = null,
        IReadOnlyList<ReceiptRecord>? scope = null, IReadOnlyDictionary<string, ReceiptDetails>? detailsScope = null,
        IReadOnlyList<ReceiptOrderDecision>? decisions = null, bool complete = true)
    {
        receipt ??= Receipt(); details ??= Details(receipt.Id);
        return new ReceiptOrderMatchingService().Match(receipt, "account", orders ?? [Order()], decisions ?? [],
            complete, details, scope ?? [receipt], detailsScope ?? new Dictionary<string, ReceiptDetails> { [receipt.Id] = details });
    }

    private static async Task AdapterToMatcherAsync(bool localized)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            Equal(HttpMethod.Get, request.Method);
            Equal("https://my.prom.ua/api/v1/orders/429000001", request.RequestUri!.AbsoluteUri);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    order = new
                    {
                        id = 429000001, price = localized ? "250 грн" : "250.00", date_created = Time.AddHours(-1).ToString("O"), status = "accepted",
                        products = new[]
                        {
                            new { name = " кабель ", sku = "other-namespace", quantity = 1, price = localized ? "50,00 грн" : "50.00", total_price = localized ? "50 грн" : "50.00" },
                            new { name = "ДАТЧИК   руху", sku = "other-code", quantity = 2, price = localized ? "100 грн" : "100.00", total_price = localized ? "200 грн" : "200.00" }
                        }
                    }
                }), Encoding.UTF8, "application/json")
            };
        }));
        var client = new PromOrdersClient(new MarketplaceHttpTransport(http));
        var order = await client.GetOrderAsync(new() { Id = "synthetic-store", Marketplace = MarketplaceKind.Prom },
            new(Token: "synthetic"), "429000001");
        var result = Match([order!]);
        Equal(1, calls); Equal(ReceiptLinkState.Suggested, result.State); Equal(order!.Key, result.Order!.Key);
        Equal(localized ? "UAH" : "", order.Currency);
        Equal(!localized, result.Explanation.Contains("Валюта API не підтверджена", StringComparison.Ordinal));
        True(result.Explanation.Contains("не фіскальне підтвердження", StringComparison.Ordinal));
        Equal(0, order.ReceiptIds.Count);
    }

    private static async Task<MarketplaceOrder> ParsePromProductsAsync(IReadOnlyList<OrderItem> products)
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Equal(HttpMethod.Get, request.Method);
            Equal("https://my.prom.ua/api/v1/orders/429000001", request.RequestUri!.AbsoluteUri);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    order = new
                    {
                        id = 429000001, price = "250.00", date_created = Time.AddHours(-1).ToString("O"), status = "accepted",
                        products = products.Select(product => new
                        {
                            name = product.Name, sku = product.Sku, quantity = product.Quantity,
                            price = product.UnitPrice, total_price = product.Total
                        }).ToArray()
                    }
                }), Encoding.UTF8, "application/json")
            };
        }));
        return (await new PromOrdersClient(new MarketplaceHttpTransport(http)).GetOrderAsync(
            new() { Id = "synthetic-store", Marketplace = MarketplaceKind.Prom },
            new(Token: "synthetic"), "429000001"))!;
    }

    private static async Task SplitBasketAdapterAsync()
    {
        OrderItem[] split =
        [
            new("Датчик руху", "unrelated-sku", 1m, 100m, 100m), Products[1],
            new("  ДАТЧИК\u00a0руху ", "another-sku", 1m, 100m, 100m)
        ];
        // Parse real adapter-shaped Prom products and raw Checkbox goods in both directions.
        var splitOrder = await ParsePromProductsAsync(split);
        var combinedOrder = await ParsePromProductsAsync(Products);
        var splitDetails = Details(products: split);
        True(splitOrder.ItemsComplete); True(splitDetails.ItemsComplete);
        Equal(ReceiptLinkState.Suggested, Match([splitOrder], details: Details()).State);
        Equal(ReceiptLinkState.Suggested, Match([combinedOrder], details: splitDetails).State);
        Equal(ReceiptLinkState.Suggested, Match([splitOrder], details: splitDetails).State);
        var duplicate = combinedOrder with { Key = new(MarketplaceKind.Prom, "synthetic-store", "429000002") };
        NotSuggested(Match([splitOrder, duplicate]));
        NotSuggested(Match([splitOrder, duplicate], decisions:
            [new() { AccountContext = "account", ReceiptId = Id, RejectedOrders = [duplicate.Key] }]));
        // A second receipt with another line representation is still a real competitor.
        NotSuggested(Match([combinedOrder], scope: [Receipt(), Receipt(OtherId)], detailsScope:
            new Dictionary<string, ReceiptDetails> { [Id] = Details(), [OtherId] = Details(OtherId, split) }));

        var unequalPrices = await ParsePromProductsAsync(
            [new("Датчик руху", "sku-1", 1m, 75m, 75m), new("Датчик руху", "sku-1", 1m, 125m, 125m), Products[1]]);
        // New rule: individual line prices need not match when totals and quantities agree.
        Equal(ReceiptLinkState.Suggested, Match([unequalPrices]).State);
        Equal(ReceiptLinkState.Suggested, Match([combinedOrder], details: Details(products: unequalPrices.Items)).State);
    }

    private static async Task TypographyAdapterAsync()
    {
        const string name = "PIR DC-9/60 \"12/24V\" O'Ring";
        var details = Details(products: [Products[0] with { Name = name }, Products[1]]);
        var normalized = await ParsePromProductsAsync(
            [Products[0] with { Name = "  pir\u00a0DC\u20139/60\u2003\u00ab12/24V\u00bb\tO\u2019Ring  " }, Products[1]]);
        Equal(ReceiptLinkState.Suggested, Match([normalized], details: details).State);
        foreach (var changed in new[]
                 {
                     "PIR DC-9/60 \"12/48V\" O'Ring", // Different model digits.
                     "PIR DC9/60 \"12/24V\" O'Ring",  // Punctuation is not removed.
                     "PIR DC-9-60 \"12/24V\" O'Ring", // Slash is not a dash variant.
                     "PIR DC-9/60 12/24V O'Ring",      // Quotes are normalized, not removed.
                     "PIR DC-9/60 \"12/24V\" ORing",
                     "DC-9/60 \"12/24V\" O'Ring"      // Partial names cannot match.
                 })
        {
            var different = await ParsePromProductsAsync([Products[0] with { Name = changed }, Products[1]]);
            var result = Match([different], details: details);
            if (changed.Contains("12/48V") || changed.Contains("DC-9-60")) NotSuggested(result);
            else
            {
                Equal(ReceiptLinkState.Suggested, result.State);
                // Partial/punctuation-only differences do not prove either a match or a contradiction.
                Equal(ProductComparison.Insufficient, result.Products);
                Equal(AutomaticLinkBasis.UniqueAmount, result.Basis);
            }
        }
    }

    private static Task CompetingReceiptsAsync()
    {
        var first = Receipt(); var second = Receipt(OtherId);
        var details = Details();
        var scope = new[] { first, second };
        var available = new Dictionary<string, ReceiptDetails> { [Id] = details };
        NotSuggested(Match(scope: scope, detailsScope: available));
        available[OtherId] = Details(OtherId);
        NotSuggested(Match(scope: scope, detailsScope: available));
        // Even a rejected/suppressed competitor is not evidence the remaining receipt owns an order.
        NotSuggested(Match(scope: scope, detailsScope: available, decisions:
            [new() { AccountContext = "account", ReceiptId = OtherId, SuppressAutomatic = true, RejectedOrders = [Order().Key] }]));
        available[OtherId] = Details(OtherId, [new("Інший товар", "sku-1", 1m, 250m, 250m)]);
        Equal(ReceiptLinkState.Suggested, Match(scope: scope, detailsScope: available).State);
        available[OtherId] = available[OtherId] with { ItemsComplete = false, ItemListComplete = false };
        NotSuggested(Match(scope: scope, detailsScope: available));
        var matcher = new ReceiptOrderMatchingService();
        NotSuggested(matcher.Match(first, "account", [Order()], [], true, details));
        Equal(ReceiptLinkState.Suggested, matcher.Match(first, "account", [Order()], [], true, details, [first]).State);
        NotSuggested(matcher.Match(first, "account", [Order()], [], true, details, [second], available));
        NotSuggested(Match(scope: [first, new() { Id = OtherId, Type = ReceiptTypes.Sell, Status = "DONE", TotalSumMinor = 25000 }]));
        return Task.CompletedTask;
    }

    private static Task CompetingOrdersAsync()
    {
        var orders = new[] { Order(), Order("429000002") };
        NotSuggested(Match(orders));
        NotSuggested(Match(orders, decisions: [new() { AccountContext = "account", ReceiptId = Id, RejectedOrders = [orders[1].Key] }]));
        // Equal baskets in another connected shop are not assumed to be the same order.
        NotSuggested(Match([orders[0], orders[0] with { Key = new(MarketplaceKind.Prom, "second-store", orders[0].Key.OrderId) }]));
        NotSuggested(Match([orders[0], orders[1] with { ItemsComplete = false }]));
        NotSuggested(Match([orders[0], orders[1] with { Items = [] }]));
        NotSuggested(Match([orders[0], orders[1] with { CreatedAt = null }]));
        NotSuggested(Match([orders[0], orders[1] with { Total = null }]));
        NotSuggested(Match([orders[0], orders[1] with { Total = null, CreatedAt = null }]));
        Equal(ReceiptLinkState.Suggested, Match([orders[0], orders[1] with { Total = null, CreatedAt = Time.AddDays(-31) }]).State);
        Equal(ReceiptLinkState.Suggested, Match([orders[0], orders[1] with { Total = null, Currency = "USD" }]).State);
        return Task.CompletedTask;
    }

    private static Task FullBasketAsync()
    {
        var variants = new[]
        {
            new OrderItem[] { new("Інший товар", "sku-1", 1m, 250m, 250m) },
            [new("Датчик руху", "sku-1", 1m, 200m, 200m), Products[1]],
            [new("Датчик", "sku-1", 2m, 100m, 200m), Products[1]],
            [new("Датчик-руху", "sku-1", 2m, 100m, 200m), Products[1]],
            [Products[0]],
            [Products[0], Products[1], new("extra", "sku-x", 1m, 0m, 0m)],
            [Products[0] with { Quantity = null }, Products[1]],
            [Products[0] with { UnitPrice = null }, Products[1]]
        };
        for (var i = 0; i < variants.Length; i++)
        {
            var result = Match([Order() with { Items = variants[i] }]);
            if (i is 0 or 1 or 4 or 5) NotSuggested(result);
            else Equal(ReceiptLinkState.Suggested, result.State);
        }
        Equal(ReceiptLinkState.Suggested, Match(details: Details() with { ItemsComplete = false, ItemListComplete = false }).State);
        Equal(AutomaticLinkBasis.UniqueAmount, Match(details: Details(OtherId)).Basis);
        Equal(ReceiptLinkState.Suggested, Match(details: new(Id, [Products[0] with { Total = null }, Products[1]])).State);
        return Task.CompletedTask;
    }

    private static Task BoundariesAsync()
    {
        NotSuggested(Match([Order() with { Currency = "USD" }]));
        Equal(ReceiptLinkState.Suggested, Match([Order() with { Currency = "UAH" }]).State);
        NotSuggested(Match([Order() with { CreatedAt = Time.AddTicks(1) }]));
        NotSuggested(Match([Order() with { CreatedAt = Time.AddDays(-30).AddTicks(-1) }]));
        Equal(ReceiptLinkState.Suggested, Match([Order() with { CreatedAt = Time.AddDays(-30) }]).State);
        NotSuggested(Match([Order() with { CreatedAt = null }]));
        NotSuggested(Match(receipt: Receipt(type: ReceiptTypes.Return)));
        NotSuggested(Match(receipt: Receipt(status: "CREATED")));
        NotSuggested(Match(receipt: Receipt(status: "CANCELLED")));
        NotSuggested(Match(complete: false));
        return Task.CompletedTask;
    }

    private static Task DecisionsAsync()
    {
        var order = Order();
        ReceiptOrderDecision Decision() => new() { AccountContext = "account", ReceiptId = Id, UpdatedAtUtc = Time };
        var suppressed = Decision(); suppressed.SuppressAutomatic = true;
        NotSuggested(Match(decisions: [suppressed]));
        var rejected = Decision(); rejected.RejectedOrders = [order.Key];
        NotSuggested(Match(decisions: [rejected]));
        var manual = Decision(); manual.ConfirmedOrder = order.Key;
        Equal(ReceiptLinkState.Manual, Match(decisions: [manual]).State);
        manual.ReceiptId = OtherId;
        NotSuggested(Match(decisions: [manual]));
        // Account scopes remain distinct; a decision for another profile does not reserve this order.
        manual.AccountContext = "another-account";
        Equal(ReceiptLinkState.Suggested, Match(decisions: [manual]).State);
        return Task.CompletedTask;
    }

    private static Task FiscalPriorityAsync()
    {
        var order = Order();
        Equal(ReceiptLinkState.Exact, Match([order with { ReceiptIds = [Id] }]).State);
        NotSuggested(Match([order with { ReceiptIds = [OtherId] }]));
        NotSuggested(Match([order with { FiscalReceiptUrls = ["https://unverified.example/receipt"] }]));
        var code = new FiscalDocumentReference(FiscalDocumentKeyKind.FiscalCode, "fiscal-code", "document", order.Key, "Checkbox", "doc");
        Equal(ReceiptLinkState.Incomplete, Match([order with { FiscalReferences = [code] }]).State);
        var uuid = new FiscalDocumentReference(FiscalDocumentKeyKind.CheckboxReceiptUuid, Id, "document", order.Key, "Checkbox", "doc");
        Equal(ReceiptLinkState.Conflict, Match([order with { FiscalReferences = [uuid, code with { Value = "different" }] }]).State);
        return Task.CompletedTask;
    }

    private static Task AdjustmentsAsync()
    {
        NotSuggested(Match([Order() with { Discount = 1m }]));
        NotSuggested(Match([Order() with { Items = [Products[0] with { Total = 199m }, Products[1]] }]));
        NotSuggested(Match([Order() with { Total = 249m }]));
        Equal(ReceiptLinkState.Suggested, Match([Order() with { Items = Products.Select(p => p with { Total = null }).ToArray() }]).State);
        Equal(ReceiptLinkState.Suggested, Match([Order() with { Items = [new("Датчик руху", "sku-1", 1m, 100m, 100m), new("Датчик руху", "sku-1", 1m, 100m, 100m), Products[1]] }]).State);
        NotSuggested(Match([Order() with { Items = [Products[0], new("Датчик руху", "sku-1", 1m, 100m, 100m), Products[1]] }]));
        NotSuggested(Match([Order() with { Items = [Products[0], Products[1], new("extra", "sku-extra", 1m, 0m, 0m)] }]));
        NotSuggested(Match([Order() with { Items = [new("Датчик руху", "sku-1", decimal.MaxValue, 100m), Products[1]] }]));
        return Task.CompletedTask;
    }

    private static async Task ParserSafetyAsync()
    {
        static JsonObject ReceiptJson() => JsonNode.Parse(JsonSerializer.Serialize(new
        {
            id = Id, type = "SELL", status = "DONE", total_sum = 25000, round_sum = 0,
            discounts = Array.Empty<object>(),
            goods = Products.Select(p => new
            {
                good = new { name = p.Name, code = p.Sku, price = p.UnitPrice * 100 },
                quantity = p.Quantity * 1000, sum = p.Total * 100, is_return = false,
                discounts = Array.Empty<object>()
            }).ToArray()
        }))!.AsObject();

        True(ReceiptDetailsService.Parse(ReceiptJson().ToJsonString(), Id).ItemsComplete);
        Action<JsonObject>[] receiptMutations =
        [
            root => root["discounts"] = new JsonArray(new JsonObject { ["value"] = 1 }),
            root => root["goods"]![0]!["discounts"] = new JsonArray(new JsonObject { ["value"] = 1 }),
            root => root["goods"]![0]!["is_return"] = true,
            root => root["pre_payment_relation_id"] = OtherId,
            root => root["round_sum"] = 1,
            root => root["total_sum"] = 24999,
            root => root.Remove("total_sum"),
            root => root["goods"]!.AsArray().Add("malformed-item"),
            root => root["goods"]!.AsArray().Add(new JsonObject { ["quantity"] = 1000 })
        ];
        foreach (var mutate in receiptMutations)
        {
            var root = ReceiptJson(); mutate(root);
            var parsed = ReceiptDetailsService.Parse(root.ToJsonString(), Id);
            True(!parsed.ItemsComplete);
            Equal(Products.Length, parsed.Items.Count);
            Equal(Products[0], parsed.Items[0]);
            if (parsed.AmountComparisonIssue.Length > 0) NotSuggested(Match(details: parsed));
            else Equal(ReceiptLinkState.Suggested, Match(details: parsed).State);
        }

        static JsonObject PromJson() => JsonNode.Parse(JsonSerializer.Serialize(new
        {
            id = 429000001, price = "250.00", date_created = Time.AddHours(-1).ToString("O"), status = "accepted",
            products = Products.Select(p => new
            {
                name = p.Name, sku = p.Sku, quantity = p.Quantity, price = p.UnitPrice, total_price = p.Total,
                discount_types = Array.Empty<object>()
            }).ToArray()
        }))!.AsObject();
        (Action<JsonObject> Mutate, int Items)[] promMutations =
        [
            (root => root["products"] = null, 0),
            (root => root["products"] = new JsonObject(), 0),
            (root => root["products"]!.AsArray().Add("malformed-item"), 2),
            (root => root["products"]![0]!["discount_types"] = new JsonArray(JsonValue.Create("fixed")), 2),
            (root => root["products"]![0] = null, 1)
        ];
        foreach (var (mutate, count) in promMutations)
        {
            var root = PromJson(); mutate(root);
            var requests = 0;
            using var http = new HttpClient(new Handler(request =>
            {
                requests++; Equal(HttpMethod.Get, request.Method);
                Equal("https://my.prom.ua/api/v1/orders/429000001", request.RequestUri!.AbsoluteUri);
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(new JsonObject { ["order"] = root.DeepClone() }.ToJsonString(),
                        Encoding.UTF8, "application/json")
                };
            }));
            var client = new PromOrdersClient(new MarketplaceHttpTransport(http));
            var parsed = await client.GetOrderAsync(new() { Id = "synthetic-store", Marketplace = MarketplaceKind.Prom },
                new(Token: "synthetic"), "429000001");
            Equal(1, requests); True(!parsed!.ItemsComplete); Equal(count, parsed.Items.Count);
            if (count == 2) Equal(Products[0], parsed.Items[0]);
            if (count == 1) Equal(Products[1], parsed.Items[0]);
            if (parsed.AmountComparisonIssue.Length > 0) NotSuggested(Match([parsed]));
            else Equal(ReceiptLinkState.Suggested, Match([parsed]).State);
        }

        var oldCache = JsonNode.Parse(JsonSerializer.Serialize(Order()))!.AsObject();
        True(oldCache.Remove(nameof(MarketplaceOrder.ItemsComplete)));
        var restored = JsonSerializer.Deserialize<MarketplaceOrder>(oldCache.ToJsonString())!;
        True(!restored.ItemsComplete); Equal(Products.Length, restored.Items.Count);
        Equal(AutomaticLinkBasis.UniqueAmount, Match([restored]).Basis);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(action(request));
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
    }
    private static void True(bool condition) { if (!condition) throw new Exception("Condition is false."); }
    private static void NotSuggested(ReceiptOrderMatch result)
    {
        if (result.State is ReceiptLinkState.Suggested or ReceiptLinkState.Exact || result.Order is not null)
            throw new Exception("Ambiguous or incomplete basket unexpectedly linked.");
    }
}
