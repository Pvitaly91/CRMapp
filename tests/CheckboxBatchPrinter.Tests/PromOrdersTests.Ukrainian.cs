using System.Net.Http;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static partial class PromOrdersTests
{
    private static async Task UkrainianNamesAsync()
    {
        var names = new[] { (Ru: "Запасная крышка контейнера", Uk: "Запасна кришка контейнера"),
            (Ru: "Кухонное полотенце", Uk: "Кухонний рушник"), (Ru: "", Uk: "Подарункова листівка") };
        var orderJson = JsonSerializer.Serialize(new
        {
            id = 501, date_created = "2026-09-23T10:00:00Z", price = "60 грн",
            products = names.Select((n, i) => new { id = i + 1, name = n.Ru, name_multilang = new { uk = "  " + n.Uk + "  ", ru = n.Ru },
                sku = "item-" + i, quantity = i + 1, price = "10 грн", total_price = (10 * (i + 1)) + " грн" }).ToArray()
        });
        var listCalls = 0; var detailCalls = 0;
        using var http = Http((request, _) =>
        {
            Assert(request.Method == HttpMethod.Get && request.RequestUri!.Host == "my.prom.ua", "Only Prom read requests allowed.");
            Assert(request.Headers.GetValues("X-LANGUAGE").Single() == "uk", "Ukrainian response locale missing.");
            if (request.RequestUri!.AbsolutePath == "/api/v1/orders/list")
                return Task.FromResult(Json(++listCalls == 1 ? "{\"orders\":[" + orderJson + "]}" : Page()));
            Assert(request.RequestUri.AbsolutePath == "/api/v1/orders/501", "Unexpected product/catalog request.");
            detailCalls++; return Task.FromResult(Json("{\"order\":" + orderJson + "}"));
        });
        var client = Client(http);
        var page = await client.FetchAsync(Connection, Credentials, Range);
        Assert(page.Complete, "Translation selection changed pagination completeness.");
        var detail = await client.GetOrderAsync(Connection, Credentials, "501");
        foreach (var order in new[] { page.Orders.Single(), detail! })
        {
            Assert(order.Items.Select(i => i.Name).SequenceEqual(names.Select(n => n.Uk)), "Ukrainian name_multilang.uk was ignored.");
            Assert(order.Items.Select(i => i.Quantity).SequenceEqual(new decimal?[] { 1, 2, 3 }), "Language selection changed quantities.");
            Assert(order.Items.All(i => i.UnitPrice == 10) && order.Total == 60 && order.Currency == "UAH", "Language selection changed money.");
            Assert(order.ItemListComplete == true && order.ItemsComplete, "Language selection changed basket completeness.");
        }
        Assert(listCalls == 2 && detailCalls == 1, "Translation incurred extra API requests.");
    }

    private static async Task UkrainianFallbackAsync()
    {
        foreach (var translation in new[] { "null", "[]", "\"not-an-object\"", "{}", "{\"uk\":null}", "{\"uk\":1}",
            "{\"uk\":true}", "{\"uk\":[]}", "{\"uk\":{}}", "{\"uk\":\"  \",\"ru\":\"Перевод\"}", "{\"ru\":\"Перевод\"}", "{\"ua\":\"Не документований ключ\"}" })
        {
            using var http = Http((_, _) => Task.FromResult(Json("{\"order\":{\"id\":501,\"products\":[{\"name\":\"  Вихідна назва  \",\"name_multilang\":" + translation + "}]}}")));
            var order = await Client(http).GetOrderAsync(Connection, Credentials, "501");
            Assert(order!.Items.Single().Name == "Вихідна назва", "Missing or malformed Ukrainian value erased the original or invented a translation.");
        }
        using var unnamed = Http((_, _) => Task.FromResult(Json("""{"order":{"id":501,"products":[{"name":null,"name_multilang":{"uk":null}}]}}""")));
        Assert((await Client(unnamed).GetOrderAsync(Connection, Credentials, "501"))!.Items.Single().Name == "", "Unknown name was invented.");
    }

    // Deliberately outside the type dictionary; Ukrainian selection is not special-cased
    // to the socket/lighting examples. Distinct aligned sizes disambiguate equal totals.
    internal static string UkrainianTowelName(int size) => $"Кухонний рушник розмір {size} см";
    internal static async Task<MarketplaceOrder[]> ReadLanguageOrdersAsync(bool withUkrainianNames)
    {
        using var http = Http((request, _) =>
        {
            Assert(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath is "/api/v1/orders/501" or "/api/v1/orders/502",
                "Language integration test attempted a different operation.");
            var id = request.RequestUri!.AbsolutePath.EndsWith("501", StringComparison.Ordinal) ? 501 : 502;
            var size = id == 501 ? 30 : 40;
            return Task.FromResult(Json(JsonSerializer.Serialize(new { order = new
            {
                id, date_created = "2026-09-23T07:00:00Z", price = "201 грн",
                products = new[] { new { name = $"Кухонное полотенце размер {size} см",
                    name_multilang = withUkrainianNames ? new { uk = UkrainianTowelName(size) } : null,
                    quantity = 1, price = "201 грн", total_price = "201 грн" } }
            } })));
        });
        var client = Client(http);
        return [(await client.GetOrderAsync(Connection, Credentials, "501"))!, (await client.GetOrderAsync(Connection, Credentials, "502"))!];
    }

    private static async Task UkrainianMatchingCacheAsync()
    {
        var oldOrders = await ReadLanguageOrdersAsync(false);
        var ukrainianOrders = await ReadLanguageOrdersAsync(true);
        var receipts = Enumerable.Range(1, 2).Select(i => new ReceiptRecord
        {
            Id = $"cf400000-0000-0000-0000-{i:D12}", Type = "SELL", Status = "DONE", TotalSumMinor = 20100,
            FiscalDate = new DateTimeOffset(2026, 9, 23, 15, 0, 0, TimeSpan.FromHours(3))
        }).ToArray();
        // Use the actual Checkbox detail parser, including its minor money/quantity units.
        var details = receipts.Select((r, i) => ReceiptDetailsService.Parse(JsonSerializer.Serialize(new
        {
            id = r.Id, type = "SELL", status = "DONE", total_sum = 20100,
            goods = new[] { new { good = new { name = UkrainianTowelName(i == 0 ? 30 : 40), price = 20100 }, quantity = 1000, sum = 20100 } }
        }), r.Id)).ToDictionary(d => d.Id);
        var cache = new AutomaticMatchCache(); var matcher = new CachedReceiptOrderMatchingService();
        foreach (var r in receipts) matcher.RememberDetails(cache, "test-account", r, details[r.Id]);
        var old = matcher.MatchAll(cache, receipts, "test-account", oldOrders, [], true, details);
        Assert(old.Matches.Values.All(m => m.Ambiguous && m.Order is null), "Russian/unknown names were incorrectly resolved.");
        var result = matcher.MatchAll(cache, receipts, "test-account", ukrainianOrders, [], true, details);
        Assert(!result.CacheHit, "Old Russian-name graph survived updated Ukrainian API names.");
        for (var i = 0; i < receipts.Length; i++)
        {
            var match = result.Matches[receipts[i].Id];
            Assert(match.State == ReceiptLinkState.Suggested && match.Basis == AutomaticLinkBasis.AmountAndProducts &&
                match.Products == ProductComparison.Match && match.Order!.Key == ukrainianOrders[i].Key, "Ukrainian API name did not match the Ukrainian Checkbox item.");
        }
        var restartedCache = JsonSerializer.Deserialize<AutomaticMatchCache>(JsonSerializer.Serialize(cache))!;
        var restoredOrders = JsonSerializer.Deserialize<MarketplaceOrder[]>(JsonSerializer.Serialize(ukrainianOrders))!;
        var restartedMatcher = new CachedReceiptOrderMatchingService();
        Assert(restartedMatcher.MatchAll(restartedCache, receipts, "test-account", restoredOrders, [], true, details).CacheHit,
            "Ukrainian names/results did not survive cache serialization.");
        Assert(receipts.All(r => restartedMatcher.TryGetDetails(restartedCache, "test-account", r, out _)), "Changing order names erased reusable receipt details.");
        var duplicate = ukrainianOrders[0] with { Key = ukrainianOrders[0].Key with { OrderId = "503" } };
        Assert(matcher.MatchAll(cache, receipts, "test-account", [.. ukrainianOrders, duplicate], [], true, details)
            .Matches[receipts[0].Id].Order is null, "Ukrainian-name equality bypassed duplicate-order ambiguity.");
    }
}
