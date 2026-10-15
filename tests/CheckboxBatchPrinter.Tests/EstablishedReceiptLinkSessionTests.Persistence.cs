using System.IO;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static partial class EstablishedReceiptLinkSessionTests
{
    private static IReadOnlyList<(string Name, Func<Task> Test)> PersistenceScenarios =>
    [
        ("established persistence: DPAPI restart restores accepted code UUID and direct ID alongside both marketplace fallbacks", PersistenceRoundTrip),
        ("established persistence: changed evidence scope conflicts and manual decisions remove saved proof durably", PersistenceInvalidation),
        ("established persistence: genuine legacy Exact graph migrates offline only with identical receipt and fiscal provenance", PersistenceLegacyGraph),
        ("established persistence: legacy pairs migrate across separate date ranges and invalidated pairs never resurrect", PersistenceLegacyPartialRange)
    ];

    private static string PersistenceDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "checkbox-established-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }

    private static async Task PersistenceRoundTrip()
    {
        var directory = PersistenceDirectory();
        try
        {
            var path = Path.Combine(directory, "automatic.dpapi");
            var receipts = new[] { Receipt(), Receipt(2, 20000), Receipt(3, 30000), Receipt(4, 40000) };
            var orders = new[]
            {
                Order(receipts[0]), Order(receipts[1], MarketplaceKind.Prom, 2) with { Total = 200 },
                Order(receipts[2], MarketplaceKind.Prom, 3) with { Total = 300, ReceiptIds = [],
                    Items = [new("Synthetic Prom fallback", "", 1, 300, 300)] },
                Order(receipts[3], MarketplaceKind.Rozetka, 4) with { Total = 400, FiscalReferences = [],
                    Items = [new("Synthetic Rozetka fallback", "", 1, 400, 400)] }
            };
            var details = new Dictionary<string, ReceiptDetails>
            {
                [receipts[2].Id] = new(receipts[2].Id, orders[2].Items) { ItemListComplete = true },
                [receipts[3].Id] = new(receipts[3].Id, orders[3].Items) { ItemListComplete = true }
            };
            var cache = new AutomaticMatchCache { AccountContext = Account };
            var matcher = new CachedReceiptOrderMatchingService();
            var first = matcher.MatchAll(cache, receipts, Account, orders, [], true, details,
                enabledConnectionIds: Connections).Matches;
            Equal(ReceiptLinkState.Exact, first[receipts[0].Id].State);
            Equal(ReceiptLinkState.Exact, first[receipts[1].Id].State);
            Equal(ReceiptLinkState.Suggested, first[receipts[2].Id].State);
            Equal(ReceiptLinkState.Suggested, first[receipts[3].Id].State);
            new EstablishedReceiptLinkSession().Capture(cache, Account, receipts, first, Connections);
            await new DpapiAutomaticMatchCacheStore(path).SaveAsync(Account, cache, 30);
            var rawOrders = orders.Select(Raw).ToArray();
            Yes(rawOrders.SelectMany(order => order.FiscalReferences).All(reference => reference.VerifiedReceiptId.Length == 0));
            // A fresh store/session, with no HTTP verifier or serialized runtime flags.
            var loaded = await new DpapiAutomaticMatchCacheStore(path).LoadAsync(Account, 30);
            var restored = new EstablishedReceiptLinkSession().Restore(loaded, Account, receipts, rawOrders, [], Connections);
            Equal(2, restored.Matches.Count);
            Equal(ReceiptLinkState.Exact, restored.Matches[receipts[0].Id].State);
            Equal(orders[0].Key, restored.Matches[receipts[0].Id].Order!.Key);
            Equal(receipts[0].Id, restored.Orders[0].FiscalReferences.Single().VerifiedReceiptId);
            Equal(Account, restored.Orders[0].FiscalReferences.Single().VerifiedAccountContext);
            var replay = new CachedReceiptOrderMatchingService().MatchAll(loaded, receipts, Account, restored.Orders, [], false,
                establishedExactLinks: restored.Matches).Matches;
            for (var i = 0; i < receipts.Length; i++)
            {
                Equal(i < 2 ? ReceiptLinkState.Exact : ReceiptLinkState.Suggested, replay[receipts[i].Id].State);
                Equal(orders[i].Key, replay[receipts[i].Id].Order!.Key);
            }
            Equal(2, loaded.Links.Count);
            // A cache containing only exact pairs must survive the store's empty-cache
            // pruning without depending on suggestions or complete-graph snapshots.
            loaded.Snapshots.Clear(); loaded.Links.Clear();
            await new DpapiAutomaticMatchCacheStore(path).SaveAsync(Account, loaded, 30);
            var again = await new DpapiAutomaticMatchCacheStore(path).LoadAsync(Account, 30);
            Equal(2, new EstablishedReceiptLinkSession().Restore(again, Account, receipts, rawOrders, [], Connections).Matches.Count);
            var otherAccount = await new DpapiAutomaticMatchCacheStore(path).LoadAsync("other-account", 30);
            NoProof(new EstablishedReceiptLinkSession().Restore(otherAccount, "other-account", receipts, rawOrders, [], Connections));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task PersistenceInvalidation()
    {
        var directory = PersistenceDirectory();
        try
        {
            var receipt = Receipt(); var verified = Order(receipt); var raw = Raw(verified);
            foreach (var scenario in new[] { "new-code", "new-uuid", "receipt", "account", "scope", "cancelled",
                "competing-code", "competing-uuid", "manual", "rejected", "suppressed" })
            {
                var path = Path.Combine(directory, scenario + ".dpapi");
                var cache = new AutomaticMatchCache { AccountContext = Account };
                var matches = new CachedReceiptOrderMatchingService().MatchAll(cache, [receipt], Account, [verified], [], true,
                    enabledConnectionIds: Connections).Matches;
                new EstablishedReceiptLinkSession().Capture(cache, Account, [receipt], matches, Connections);
                await new DpapiAutomaticMatchCacheStore(path).SaveAsync(Account, cache, 30);
                var loaded = await new DpapiAutomaticMatchCacheStore(path).LoadAsync(Account, 30);
                var changedOrder = scenario switch
                {
                    "new-code" => raw with { FiscalReferences = [raw.FiscalReferences.Single() with { Value = "NEW-CODE" }] },
                    "new-uuid" => raw with { ReceiptIds = [Receipt(2).Id] },
                    "cancelled" => raw with { Status = "canceled" },
                    _ => raw
                };
                var orders = new List<MarketplaceOrder> { changedOrder };
                if (scenario.StartsWith("competing-", StringComparison.Ordinal))
                    orders.Add(scenario == "competing-code" ? Raw(Order(receipt, n: 2))
                        : Order(receipt, MarketplaceKind.Prom, 2));
                ReceiptOrderDecision[] decisions = scenario is "manual" or "rejected" or "suppressed"
                    ? [new() { AccountContext = Account, ReceiptId = receipt.Id, UpdatedAtUtc = Time,
                        ConfirmedOrder = scenario == "manual" ? raw.Key : null,
                        RejectedOrders = scenario == "rejected" ? [raw.Key] : [], SuppressAutomatic = scenario == "suppressed" }] : [];
                var currentReceipt = scenario == "receipt" ? Receipt(amount: 10100) : receipt;
                var account = scenario == "account" ? "other-account" : Account;
                var scope = scenario == "scope" ? Connections.Append("Rozetka:other-shop").ToArray() : Connections;
                NoProof(new EstablishedReceiptLinkSession().Restore(loaded, account, [currentReceipt], orders, decisions, scope));
                // Account mismatch must never import proof; the account's original cache
                // remains owned by its original store and is not written under a new identity.
                if (scenario == "account") continue;
                await new DpapiAutomaticMatchCacheStore(path).SaveAsync(Account, loaded, 30);
                var restarted = await new DpapiAutomaticMatchCacheStore(path).LoadAsync(Account, 30);
                NoProof(new EstablishedReceiptLinkSession().Restore(restarted, Account, [receipt], [raw], [], Connections));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task PersistenceLegacyGraph()
    {
        var directory = PersistenceDirectory();
        try
        {
            var receipt = Receipt(); var verified = Order(receipt); var raw = Raw(verified);
            foreach (var scenario in new[] { "identical", "changed-reference", "changed-receipt", "wrong-scope", "not-exact" })
            {
                var path = Path.Combine(directory, scenario + ".dpapi");
                var cache = new AutomaticMatchCache { AccountContext = Account };
                var result = new CachedReceiptOrderMatchingService().MatchAll(cache, [receipt], Account, [verified], [], true,
                    enabledConnectionIds: Connections);
                Equal(ReceiptLinkState.Exact, result.Matches[receipt.Id].State);
                if (scenario == "not-exact")
                    cache.Snapshots = cache.Snapshots.Select(snapshot => snapshot with { Matches = snapshot.Matches.ToDictionary(
                        pair => pair.Key, pair => pair.Value with { State = ReceiptLinkState.Candidates }) }).ToList();
                // No Capture call: this is the actual pre-fix v17 cache shape.
                await new DpapiAutomaticMatchCacheStore(path).SaveAsync(Account, cache, 30);
                var loaded = await new DpapiAutomaticMatchCacheStore(path).LoadAsync(Account, 30);
                var order = scenario == "changed-reference" ? raw with
                    { FiscalReferences = [raw.FiscalReferences.Single() with { Source = "untrusted.renamed_field" }] } : raw;
                var currentReceipt = scenario == "changed-receipt" ? Receipt(amount: 10100) : receipt;
                var scope = scenario == "wrong-scope" ? [Connections[1]] : Connections;
                var migrated = new EstablishedReceiptLinkSession().Restore(loaded, Account, [currentReceipt], [order], [], scope);
                if (scenario == "identical")
                {
                    Equal(1, migrated.Matches.Count); Equal(ReceiptLinkState.Exact, migrated.Matches[receipt.Id].State);
                    Equal(receipt.Id, migrated.Orders.Single().FiscalReferences.Single().VerifiedReceiptId);
                    loaded.Snapshots.Clear();
                    await new DpapiAutomaticMatchCacheStore(path).SaveAsync(Account, loaded, 30);
                    var second = await new DpapiAutomaticMatchCacheStore(path).LoadAsync(Account, 30);
                    Equal(1, new EstablishedReceiptLinkSession().Restore(second, Account, [receipt], [raw], [], Connections).Matches.Count);
                }
                else NoProof(migrated);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task PersistenceLegacyPartialRange()
    {
        var directory = PersistenceDirectory();
        try
        {
            var path = Path.Combine(directory, "legacy-ranges.dpapi");
            var firstReceipt = Receipt(); var secondReceipt = Receipt(2, 20000, fiscalDate: Time.AddDays(1));
            var first = Order(firstReceipt); var second = Order(secondReceipt, n: 2) with { Total = 200, CreatedAt = Time.AddDays(1).AddHours(-1) };
            var cache = new AutomaticMatchCache { AccountContext = Account };
            var seeded = new CachedReceiptOrderMatchingService().MatchAll(cache, [firstReceipt, secondReceipt], Account,
                [first, second], [], true, enabledConnectionIds: Connections).Matches;
            Equal(ReceiptLinkState.Exact, seeded[firstReceipt.Id].State);
            Equal(ReceiptLinkState.Exact, seeded[secondReceipt.Id].State);
            await new DpapiAutomaticMatchCacheStore(path).SaveAsync(Account, cache, 30);
            var firstLoad = await new DpapiAutomaticMatchCacheStore(path).LoadAsync(Account, 30);
            var firstReuse = new EstablishedReceiptLinkSession().Restore(firstLoad, Account, [firstReceipt], [Raw(first), Raw(second)], [], Connections);
            Equal(1, firstReuse.Matches.Count); Equal(first.Key, firstReuse.Matches[firstReceipt.Id].Order!.Key);
            await new DpapiAutomaticMatchCacheStore(path).SaveAsync(Account, firstLoad, 30);
            var secondLoad = await new DpapiAutomaticMatchCacheStore(path).LoadAsync(Account, 30);
            var secondReuse = new EstablishedReceiptLinkSession().Restore(secondLoad, Account, [secondReceipt], [Raw(first), Raw(second)], [], Connections);
            Equal(1, secondReuse.Matches.Count); Equal(second.Key, secondReuse.Matches[secondReceipt.Id].Order!.Key);
            await new DpapiAutomaticMatchCacheStore(path).SaveAsync(Account, secondLoad, 30);

            var changed = Raw(first) with { FiscalReferences = [Raw(first).FiscalReferences.Single() with { Value = "CHANGED-LEGACY-CODE" }] };
            var changedLoad = await new DpapiAutomaticMatchCacheStore(path).LoadAsync(Account, 30);
            var changedReuse = new EstablishedReceiptLinkSession().Restore(changedLoad, Account, [firstReceipt, secondReceipt],
                [changed, Raw(second)], [], Connections);
            Yes(!changedReuse.Matches.ContainsKey(firstReceipt.Id)); Yes(changedReuse.Matches.ContainsKey(secondReceipt.Id));
            await new DpapiAutomaticMatchCacheStore(path).SaveAsync(Account, changedLoad, 30);
            var finalLoad = await new DpapiAutomaticMatchCacheStore(path).LoadAsync(Account, 30);
            var final = new EstablishedReceiptLinkSession().Restore(finalLoad, Account, [firstReceipt, secondReceipt],
                [Raw(first), Raw(second)], [], Connections);
            Yes(!final.Matches.ContainsKey(firstReceipt.Id)); Equal(ReceiptLinkState.Exact, final.Matches[secondReceipt.Id].State);
        }
        finally { Directory.Delete(directory, true); }
    }
}
