using System.IO;
using System.Text;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class MarketplaceMatchingTests
{
    private const string PromId = "938c0f5520cf43119fe1f49945a24253";
    private const string RozetkaId = "4ced01c77d9d43f6979c3bde6e7a8bcd";
    private const string ReceiptId = "fc8bc019-6e28-4bb4-a089-5c4ca905b208";
    private const string ReturnId = "f9fa1b61-e156-42f9-9dc2-d2d55b2ebc73";
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<(string Name, Func<Task> Test)> All { get; } =
    [
        ("matching: UUID proves link, serial and payment label do not", ExactUuidAsync),
        ("matching: equal amount/date candidates retain separate platform/account keys", SimilarityAsync),
        ("matching: conflicting UUIDs cannot be resolved by discarding a candidate", ConflictAsync),
        ("matching: partial coverage is not proof of absence or uniqueness", PartialCoverageAsync),
        ("matching: multiple sale/return receipts may share an order", MultipleReceiptsAsync),
        ("matching: return follows proven original link but never fuzzy amount", ReturnProvenanceAsync),
        ("matching: manual links survive restart and override automatic results", ManualRestartAsync),
        ("matching: rejected/unlinked decisions persist and stay account isolated", RejectionRestartAsync),
        ("storage: encrypted cache expires without erasing durable decisions", CacheRetentionAsync),
        ("storage: secrets stay separate, encrypted and stable on token rotation", SecretIsolationAsync),
        ("storage: concurrent link updates preserve all decisions", ConcurrentDecisionsAsync),
        ("storage: malformed local files fail without silently overwriting data", CorruptStorageAsync)
    ];

    private static Task ExactUuidAsync()
    {
        var matcher = new ReceiptOrderMatchingService();
        var exact = Order("42", ids: [ReceiptId.ToUpperInvariant()]);
        var result = matcher.Match(Receipt(), "cashier-a", [exact], [], true);
        Equal(ReceiptLinkState.Exact, result.State);
        Equal(exact.Key, result.Order?.Key);

        // Same serial and payment label cannot manufacture a marketplace source.
        var receipt = new ReceiptRecord
        {
            Id = ReceiptId, Serial = 42, Type = ReceiptTypes.Sell, TotalSumMinor = 10000,
            FiscalDate = Now, Payments = [new("CASHLESS", "RozetkaPay", 10000)]
        };
        var unrelated = Order("42", total: 200m, ids: ["42"]);
        Equal(ReceiptLinkState.NotFound, matcher.Match(receipt, "cashier-a", [unrelated], [], true).State);
        return Task.CompletedTask;
    }

    private static Task SimilarityAsync()
    {
        var orders = new[]
        {
            Order("42", created: Now.AddDays(-12)),
            Order("42", MarketplaceKind.Rozetka, RozetkaId, created: Now.AddDays(-12)),
            Order("42", connectionId: "48234d9d36ce4ac8a95d4b9a2c3f425c", created: Now.AddDays(-12))
        };
        var matcher = new ReceiptOrderMatchingService();
        var result = matcher.Match(Receipt(), "cashier-a", orders, [], true);
        Equal(ReceiptLinkState.Candidates, result.State);
        Equal(3, result.Candidates.Count);
        True(result.Order is null);
        var single = matcher.Match(Receipt(), "cashier-a", [orders[0]], [], true);
        Equal(ReceiptLinkState.Candidates, single.State);
        True(single.Order is null);
        var unknownCurrency = matcher.Match(Receipt(), "cashier-a", [Order("unknown", currency: "")], [], true);
        Equal(ReceiptLinkState.Candidates, unknownCurrency.State);
        True(unknownCurrency.Order is null && unknownCurrency.Explanation.Contains("валюта API не підтверджена", StringComparison.Ordinal));

        Equal(ReceiptLinkState.NotFound, matcher.Match(Receipt(), "cashier-a",
            [Order("old", created: Now.AddDays(-31)), Order("future", created: Now.AddHours(1)),
             Order("currency", currency: "USD")], [], true).State);
        // Compare instants, not the PC timezone: +03:00 13:00 is before 12:00Z.
        Equal(ReceiptLinkState.Candidates, matcher.Match(Receipt(), "cashier-a",
            [Order("offset", created: new DateTimeOffset(2026, 9, 23, 13, 0, 0, TimeSpan.FromHours(3)))], [], true).State);
        return Task.CompletedTask;
    }

    private static Task ConflictAsync()
    {
        var first = Order("42", ids: [ReceiptId]);
        var second = Order("42", MarketplaceKind.Rozetka, RozetkaId, ids: [ReceiptId]);
        var matcher = new ReceiptOrderMatchingService();
        Equal(ReceiptLinkState.Conflict, matcher.Match(Receipt(), "cashier-a", [first, second], [], true).State);
        var rejected = Decision(rejected: [first.Key]);
        var afterReject = matcher.Match(Receipt(), "cashier-a", [first, second], [rejected], true);
        Equal(ReceiptLinkState.Conflict, afterReject.State);
        True(afterReject.Order is null);
        var manual = Decision(confirmed: second.Key);
        Equal(second.Key, matcher.Match(Receipt(), "cashier-a", [first, second], [manual], true).Order?.Key);
        // Repeated pages for one compound key are not a conflict.
        Equal(ReceiptLinkState.Exact, matcher.Match(Receipt(), "cashier-a", [first, first], [], true).State);
        return Task.CompletedTask;
    }

    private static Task PartialCoverageAsync()
    {
        var matcher = new ReceiptOrderMatchingService();
        Equal(ReceiptLinkState.Incomplete, matcher.Match(Receipt(), "cashier-a", [], [], false).State);
        var match = matcher.Match(Receipt(), "cashier-a", [Order("42", ids: [ReceiptId])], [], false);
        Equal(ReceiptLinkState.Incomplete, match.State);
        True(match.Order is null);
        Equal(1, match.Candidates.Count);
        Equal(ReceiptLinkState.NotFound, matcher.Match(Receipt(), "cashier-a", [], [], true).State);
        return Task.CompletedTask;
    }

    private static Task MultipleReceiptsAsync()
    {
        var matcher = new ReceiptOrderMatchingService();
        var order = Order("42", ids: [ReceiptId, ReturnId]);
        var sale = matcher.Match(Receipt(), "cashier-a", [order], [], true);
        var returned = matcher.Match(Receipt(ReturnId, ReceiptTypes.Return), "cashier-a", [order], [], true);
        Equal(ReceiptLinkState.Exact, sale.State);
        Equal(ReceiptLinkState.Exact, returned.State);
        Equal(sale.Order?.Key, returned.Order?.Key);
        return Task.CompletedTask;
    }

    private static Task ReturnProvenanceAsync()
    {
        var matcher = new ReceiptOrderMatchingService();
        var returned = Receipt(ReturnId, ReceiptTypes.Return);
        var details = new ReceiptDetails(ReturnId, [], ReceiptId);
        var fuzzy = Order("42");
        Equal(ReceiptLinkState.NotFound, matcher.Match(returned, "cashier-a", [fuzzy], [], true, details).State);
        var exactOriginal = Order("42", ids: [ReceiptId]);
        Equal(ReceiptLinkState.Exact, matcher.Match(returned, "cashier-a", [exactOriginal], [], true, details).State);
        var manual = Decision(confirmed: fuzzy.Key);
        Equal(ReceiptLinkState.Exact, matcher.Match(returned, "cashier-a", [fuzzy], [manual], true, details).State);
        Equal(ReceiptLinkState.NotFound, matcher.Match(returned, "other-cashier", [fuzzy], [manual], true, details).State);
        var directOther = Order("43", ids: [ReturnId]);
        Equal(ReceiptLinkState.Conflict, matcher.Match(returned, "cashier-a", [exactOriginal, directOther], [], true, details).State);
        // A detail DTO from another receipt cannot establish this return's chain.
        Equal(ReceiptLinkState.NotFound, matcher.Match(returned, "cashier-a", [exactOriginal], [], true,
            new ReceiptDetails(ReceiptId, [], ReceiptId)).State);
        return Task.CompletedTask;
    }

    private static async Task ManualRestartAsync()
    {
        using var files = new TestDirectory();
        var path = files.PathFor("links.dpapi");
        var chosen = Order("42");
        var competing = Order("43", ids: [ReceiptId]);
        await new DpapiReceiptOrderLinkStore(path).SaveDecisionAsync(Decision(confirmed: chosen.Key));

        var reloaded = await new DpapiReceiptOrderLinkStore(path).LoadAsync("cashier-a");
        var matcher = new ReceiptOrderMatchingService();
        // A refreshed receipt object and a new store simulate F5/date roundtrip + restart.
        var match = matcher.Match(Receipt(), "cashier-a", [chosen, competing], reloaded, true);
        Equal(ReceiptLinkState.Manual, match.State);
        Equal(chosen.Key, match.Order?.Key);
        var outOfRange = matcher.Match(Receipt(), "cashier-a", [], reloaded, false);
        Equal(ReceiptLinkState.Manual, outOfRange.State);
        Equal(chosen.Key, reloaded.Single().ConfirmedOrder);
        var otherAccount = await new DpapiReceiptOrderLinkStore(path).LoadAsync("cashier-b");
        Equal(0, otherAccount.Count);
        AssertEncrypted(path, ReceiptId, "cashier-a");
    }

    private static async Task RejectionRestartAsync()
    {
        using var files = new TestDirectory();
        var path = files.PathFor("links.dpapi");
        var wrong = Order("42", ids: [ReceiptId]);
        await new DpapiReceiptOrderLinkStore(path).SaveDecisionAsync(Decision(rejected: [wrong.Key]));
        var rejected = await new DpapiReceiptOrderLinkStore(path).LoadAsync("cashier-a");
        var matcher = new ReceiptOrderMatchingService();
        Equal(ReceiptLinkState.NotFound, matcher.Match(Receipt(), "cashier-a", [wrong], rejected, true).State);
        Equal(ReceiptLinkState.Exact, matcher.Match(Receipt(), "cashier-b", [wrong], rejected, true).State);
        await new DpapiReceiptOrderLinkStore(path).SaveDecisionAsync(Decision(suppress: true));
        var unlinked = await new DpapiReceiptOrderLinkStore(path).LoadAsync("cashier-a");
        var result = matcher.Match(Receipt(), "cashier-a", [wrong], unlinked, true);
        Equal(ReceiptLinkState.Candidates, result.State);
        True(result.Order is null);
        Equal(1, unlinked.Count);
    }

    private static async Task CacheRetentionAsync()
    {
        using var files = new TestDirectory();
        var clock = new TestClock(Now);
        var cachePath = files.PathFor("orders.dpapi");
        var linkPath = files.PathFor("links.dpapi");
        var old = Order("old", retrieved: Now.AddDays(-31));
        var fresh = Order("42", retrieved: Now);
        var state = new ConnectionSyncState(PromId, new(Now.AddDays(-30), Now), true, Now, "", Now);
        await new DpapiMarketplaceCacheStore(cachePath, clock).SaveAsync(new([old, fresh, fresh], [state]));
        await new DpapiReceiptOrderLinkStore(linkPath).SaveDecisionAsync(Decision(confirmed: old.Key));
        var cache = await new DpapiMarketplaceCacheStore(cachePath, clock).LoadAsync(30);
        Equal(1, cache.Orders.Count);
        Equal("42", cache.Orders[0].Key.OrderId);
        True(!cache.States.Single().Complete);
        AssertEncrypted(cachePath, "Buyer test only", "+380000000000", "sku-test-only");
        clock.UtcNow = Now.AddDays(100);
        Equal(0, (await new DpapiMarketplaceCacheStore(cachePath, clock).LoadAsync(90)).Orders.Count);
        Equal(old.Key, (await new DpapiReceiptOrderLinkStore(linkPath).LoadAsync("cashier-a")).Single().ConfirmedOrder);
        await ThrowsAsync<ArgumentOutOfRangeException>(() => new DpapiMarketplaceCacheStore(cachePath).LoadAsync(0));
        await ThrowsAsync<ArgumentOutOfRangeException>(() => new DpapiMarketplaceCacheStore(cachePath).LoadAsync(91));
    }

    private static async Task SecretIsolationAsync()
    {
        using var files = new TestDirectory();
        var secretStore = new DpapiMarketplaceSecretStore(files.DirectoryPath);
        var checkboxPath = files.PathFor("checkbox-credential.dpapi");
        await new DpapiCredentialStore(checkboxPath).SavePasswordAsync("fake-checkbox-password");
        await secretStore.SaveAsync(PromId, new(Token: "fake-prom-token-one"));
        await secretStore.SaveAsync(RozetkaId, new(Login: "seller-test", Password: "fake-rozetka-password"));
        await secretStore.SaveAsync(PromId, new(Token: "fake-prom-token-rotated"));
        var restarted = new DpapiMarketplaceSecretStore(files.DirectoryPath);
        Equal("fake-prom-token-rotated", (await restarted.LoadAsync(PromId))?.Token);
        Equal("fake-rozetka-password", (await restarted.LoadAsync(RozetkaId))?.Password);
        Equal("fake-checkbox-password", await new DpapiCredentialStore(checkboxPath).LoadPasswordAsync());
        AssertEncrypted(files.PathFor($"marketplace-{PromId}.dpapi"), "fake-prom-token-rotated");
        AssertEncrypted(files.PathFor($"marketplace-{RozetkaId}.dpapi"), "fake-rozetka-password", "seller-test");
        await ThrowsAsync<ArgumentException>(() => secretStore.SaveAsync("../checkbox-credential", new(Token: "fake")));
        var settingsPath = files.PathFor("marketplaces.json");
        var settings = new MarketplaceSettings { HistoryDays = 0, Connections = [new() { Id = PromId, Name = "Test store", Marketplace = MarketplaceKind.Prom }] };
        await new JsonMarketplaceSettingsStore(settingsPath).SaveAsync(settings);
        Equal(PromId, (await new JsonMarketplaceSettingsStore(settingsPath).LoadAsync()).Connections.Single().Id);
        var raw = await File.ReadAllTextAsync(settingsPath);
        True(!raw.Contains("token", StringComparison.OrdinalIgnoreCase) && !raw.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task ConcurrentDecisionsAsync()
    {
        using var files = new TestDirectory();
        var path = files.PathFor("links.dpapi");
        await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
            new DpapiReceiptOrderLinkStore(path).SaveDecisionAsync(new ReceiptOrderDecision
            {
                AccountContext = "cashier-a", ReceiptId = $"receipt-{index}",
                ConfirmedOrder = Order($"order-{index}").Key, UpdatedAtUtc = Now
            })));
        Equal(20, (await new DpapiReceiptOrderLinkStore(path).LoadAsync("cashier-a")).Count);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => new DpapiReceiptOrderLinkStore(path)
            .SaveDecisionAsync(Decision(), cancellation.Token));
        Equal(20, (await new DpapiReceiptOrderLinkStore(path).LoadAsync("cashier-a")).Count);
    }

    private static async Task CorruptStorageAsync()
    {
        using var files = new TestDirectory();
        var path = files.PathFor("links.dpapi");
        await new DpapiCredentialStore(path).SavePasswordAsync("{invalid json buyer-secret-marker");
        var before = await File.ReadAllBytesAsync(path);
        var store = new DpapiReceiptOrderLinkStore(path);
        await ThrowsAsync<InvalidDataException>(() => store.SaveDecisionAsync(Decision()));
        True(before.SequenceEqual(await File.ReadAllBytesAsync(path)));
    }

    private static ReceiptRecord Receipt(string id = ReceiptId, string type = ReceiptTypes.Sell) =>
        new() { Id = id, Type = type, Status = "DONE", TotalSumMinor = 10000, FiscalDate = Now };

    private static MarketplaceOrder Order(string id, MarketplaceKind marketplace = MarketplaceKind.Prom,
        string connectionId = PromId, decimal total = 100m, string currency = "UAH",
        DateTimeOffset? created = null, string[]? ids = null, DateTimeOffset? retrieved = null) => new()
    {
        Key = new(marketplace, connectionId, id), Number = id, Total = total, Currency = currency,
        CreatedAt = created ?? Now.AddDays(-1), ReceiptIds = ids ?? [], RetrievedAtUtc = retrieved ?? Now,
        Buyer = new("Buyer test only", "+380000000000"), Items = [new("Test item", "sku-test-only", 1m, total)]
    };

    private static ReceiptOrderDecision Decision(OrderKey? confirmed = null, List<OrderKey>? rejected = null, bool suppress = false) => new()
    {
        AccountContext = "cashier-a", ReceiptId = ReceiptId, ConfirmedOrder = confirmed,
        RejectedOrders = rejected ?? [], SuppressAutomatic = suppress, UpdatedAtUtc = Now
    };

    private static void AssertEncrypted(string path, params string[] values)
    {
        var bytes = File.ReadAllBytes(path);
        var utf8 = Encoding.UTF8.GetString(bytes);
        var utf16 = Encoding.Unicode.GetString(bytes);
        foreach (var value in values) True(!utf8.Contains(value, StringComparison.Ordinal) && !utf16.Contains(value, StringComparison.Ordinal));
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
    }
    private static void True(bool condition) { if (!condition) throw new Exception("Condition is false."); }
    private static async Task ThrowsAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
    private sealed class TestDirectory : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"checkbox-marketplace-tests-{Guid.NewGuid():N}");
        public string PathFor(string name) => Path.Combine(DirectoryPath, name);
        public void Dispose()
        {
            var absolute = Path.GetFullPath(DirectoryPath);
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!string.Equals(Path.GetDirectoryName(absolute), parent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(absolute).StartsWith("checkbox-marketplace-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid test directory cleanup target.");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
        }
    }
}
