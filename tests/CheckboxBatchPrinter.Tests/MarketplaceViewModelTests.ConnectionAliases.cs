using System.IO;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task HistoricalConnectionAliasesAsync()
    {
        const string targetId = SettingsRozetkaBId, canonicalId = SettingsRozetkaAId;
        const string oldA = "90000000000000000000000000000010", oldB = "90000000000000000000000000000011";
        var settings = new MarketplaceSettings
        {
            Connections =
            [
                new() { Id = targetId, Marketplace = MarketplaceKind.Rozetka, Enabled = true },
                new() { Id = canonicalId, Marketplace = MarketplaceKind.Rozetka, Enabled = true }
            ],
            ConnectionAliases =
            [
                new(MarketplaceKind.Rozetka, oldA, oldB),
                new(MarketplaceKind.Rozetka, oldB, targetId)
            ]
        };
        var secrets = new DuplicateRozetkaSecrets();
        secrets.Values[targetId] = new(Token: "synthetic_same_token");
        secrets.Values[canonicalId] = new(Token: "synthetic_same_token");
        var identity = await MarketplaceOrderDeduplication.ResolveAsync(settings, secrets);
        Equal(2, secrets.Reads); // Removed aliases must not try legacy authentication or secret reads.
        var old = new OrderKey(MarketplaceKind.Rozetka, oldA, "same-order");
        var current = new OrderKey(MarketplaceKind.Rozetka, targetId, "same-order");
        var canonical = new OrderKey(MarketplaceKind.Rozetka, canonicalId, "same-order");
        Equal(canonical, identity.NormalizeKey(old)); Equal(canonical, identity.NormalizeKey(current));
        Equal(canonical, identity.NormalizeKey(old with { ConnectionId = Guid.Parse(old.ConnectionId).ToString("D").ToUpperInvariant() }));
        var saved = new ReceiptOrderDecision { AccountContext = "synthetic-cashier", ReceiptId = ReceiptOne,
            ConfirmedOrder = old, RejectedOrders = [old, current], SuppressAutomatic = true };
        var projected = identity.NormalizeDecisions([saved]).Single();
        Equal(canonical, projected.ConfirmedOrder); Equal(canonical, projected.RejectedOrders.Single());
        True(projected.SuppressAutomatic); Equal(old, saved.ConfirmedOrder); Equal(2, saved.RejectedOrders.Count);
        var reference = new FiscalDocumentReference(FiscalDocumentKeyKind.CheckboxReceiptUuid, ReceiptOne,
            "synthetic", old, "checkbox", "same-document");
        var older = Order([ReceiptOne]) with { Key = old, FiscalReferences = [reference],
            RetrievedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var newer = older with { Key = current, ReceiptIds = [], FiscalReferences = [], RetrievedAtUtc = DateTimeOffset.UtcNow };
        var merged = identity.Merge([older, newer]);
        Equal(1, merged.Count); Equal(canonical, merged.Single().Key);
        Equal(ReceiptOne, merged.Single().ReceiptIds.Single());
        Equal(canonical, merged.Single().FiscalReferences.Single().Order);
        Equal(old, older.FiscalReferences.Single().Order);
        Equal(new(MarketplaceKind.Prom, old.ConnectionId, old.OrderId),
            identity.NormalizeKey(new(MarketplaceKind.Prom, old.ConnectionId, old.OrderId)));

        settings.Connections.Single(c => c.Id == targetId).Enabled = false;
        identity = await MarketplaceOrderDeduplication.ResolveAsync(settings, secrets);
        Equal(old, identity.NormalizeKey(old)); // Disabling the explicit destination must also hide its history.
    }

    private static async Task HistoricalConnectionAliasValidationAsync()
    {
        using var files = new RozetkaSettingsFiles();
        var target = SettingsConnection(SettingsRozetkaAId);
        var prom = SettingsConnection(SettingsPromId, MarketplaceKind.Prom);
        await files.SeedAsync(target, prom);
        var original = await File.ReadAllBytesAsync(files.SettingsPath);
        const string oldA = "90000000000000000000000000000010", oldB = "90000000000000000000000000000011";
        var invalid = new List<MarketplaceConnectionAlias>[]
        {
            [new(MarketplaceKind.Rozetka, oldA, oldA)],
            [new(MarketplaceKind.Rozetka, oldA, oldB), new(MarketplaceKind.Rozetka, oldB, oldA)],
            [new(MarketplaceKind.Rozetka, oldA, target.Id), new(MarketplaceKind.Rozetka, oldA, target.Id)],
            [new(MarketplaceKind.Rozetka, oldA, target.Id), new(MarketplaceKind.Rozetka, Guid.Parse(oldA).ToString("D"), target.Id)],
            [new(MarketplaceKind.Rozetka, target.Id, oldA)],
            [new(MarketplaceKind.Rozetka, Guid.Parse(target.Id).ToString("D"), oldA)],
            [new(MarketplaceKind.Rozetka, oldA, oldB)],
            [new(MarketplaceKind.Rozetka, oldA, prom.Id)],
            [new(MarketplaceKind.Rozetka, oldA, oldB), new(MarketplaceKind.Prom, oldB, prom.Id)],
            [new((MarketplaceKind)999, oldA, target.Id)],
            [new(MarketplaceKind.Rozetka, "../invalid", target.Id)],
            [null!], null!
        };
        foreach (var aliases in invalid)
        {
            var rejected = false;
            try { await files.Settings.Inner.SaveAsync(new() { Connections = [target, prom], ConnectionAliases = aliases }); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { rejected = true; }
            True(rejected, "An invalid explicit account alias graph was saved.");
            True(original.SequenceEqual(await File.ReadAllBytesAsync(files.SettingsPath)), "Invalid aliases replaced saved settings.");
        }
        var valid = new MarketplaceSettings { Connections = [target, prom], ConnectionAliases =
            [new(MarketplaceKind.Rozetka, oldA, oldB), new(MarketplaceKind.Rozetka, oldB, target.Id)] };
        await files.Settings.Inner.SaveAsync(valid);
        var restored = await files.Settings.Inner.LoadAsync();
        Equal(2, restored.ConnectionAliases.Count); Equal(valid.ConnectionAliases[0], restored.ConnectionAliases[0]);
    }
}
