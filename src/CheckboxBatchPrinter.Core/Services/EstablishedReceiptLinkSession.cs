using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public sealed record EstablishedLinkReuse(IReadOnlyList<MarketplaceOrder> Orders,
    IReadOnlyDictionary<string, ReceiptOrderMatch> Matches);

/// <summary>Revalidates accepted exact pairs; cache-aware overloads also retain their narrowly bound attestations.</summary>
public sealed partial class EstablishedReceiptLinkSession(TimeProvider? timeProvider = null)
{
    private const int MaximumPairs = 5000;
    private readonly Dictionary<string, Entry> _pairs = new(StringComparer.Ordinal);
    private string _account = "";
    private string[] _connections = [];
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private sealed record Entry(OrderKey Order, string ReceiptFingerprint, string FiscalFingerprint,
        IReadOnlyList<FiscalDocumentReference> Verified, ReceiptOrderMatch Match, DateTimeOffset AcceptedAtUtc);

    public void Capture(string account, IReadOnlyList<ReceiptRecord> receipts,
        IReadOnlyDictionary<string, ReceiptOrderMatch> matches, IReadOnlyList<string> enabledConnectionIds)
    {
        if (!Bind(account, enabledConnectionIds)) return;
        var byReceipt = receipts.GroupBy(receipt => Normalize(receipt.Id)).ToDictionary(group => group.Key, group => group.First());
        foreach (var (id, match) in matches)
        {
            var key = Normalize(id);
            if (match.State is ReceiptLinkState.Manual or ReceiptLinkState.Conflict) { _pairs.Remove(key); continue; }
            if (match.State != ReceiptLinkState.Exact || match.Ambiguous || match.Order is not { } order || order.IsCanceled ||
                !Enabled(order.Key) || !byReceipt.TryGetValue(key, out var receipt) ||
                FiscalReferenceMatching.Evaluate(order, receipt, account, receipts) is not { Exact: true, Conflict: false }) continue;
            // This also excludes inherited return links: their original-sale relationship
            // cannot be revalidated from the fiscal assertions of this receipt alone.
            var verified = order.FiscalReferences.Where(reference => reference.Order == order.Key &&
                reference.VerifiedAccountContext == account && CheckboxReceiptReference.SameId(reference.VerifiedReceiptId, receipt.Id)).ToArray();
            if (!_pairs.ContainsKey(key) && _pairs.Count >= MaximumPairs) _pairs.Remove(_pairs.Keys.First());
            var receiptFingerprint = ReceiptFingerprint(receipt);
            var fiscalFingerprint = FiscalFingerprint(order);
            var acceptedAt = _pairs.TryGetValue(key, out var previous) && previous.Order == order.Key &&
                previous.ReceiptFingerprint == receiptFingerprint && previous.FiscalFingerprint == fiscalFingerprint
                ? previous.AcceptedAtUtc : _clock.GetUtcNow();
            _pairs[key] = new(order.Key, receiptFingerprint, fiscalFingerprint, verified, match, acceptedAt);
        }
    }

    public EstablishedLinkReuse Restore(string account, IReadOnlyList<ReceiptRecord> receipts,
        IReadOnlyList<MarketplaceOrder> orders, IReadOnlyList<ReceiptOrderDecision> decisions,
        IReadOnlyList<string> enabledConnectionIds)
    {
        if (!Bind(account, enabledConnectionIds) || _pairs.Count == 0) return new(orders, new Dictionary<string, ReceiptOrderMatch>());
        var byReceipt = receipts.GroupBy(receipt => Normalize(receipt.Id)).ToDictionary(group => group.Key, group => group.ToArray());
        var byOrder = orders.GroupBy(order => order.Key).ToDictionary(group => group.Key, group => group.ToArray());
        var latestDecisions = decisions.Where(decision => decision.AccountContext == account)
            .GroupBy(decision => Normalize(decision.ReceiptId)).ToDictionary(group => group.Key, group => group.MaxBy(d => d.UpdatedAtUtc)!);
        var manualOrders = latestDecisions.Values.Where(decision => decision.ConfirmedOrder is not null)
            .Select(decision => decision.ConfirmedOrder!).ToHashSet();
        var candidates = new Dictionary<string, (Entry Entry, ReceiptRecord Receipt, MarketplaceOrder Order)>(StringComparer.Ordinal);
        foreach (var (id, entry) in _pairs.ToArray())
        {
            latestDecisions.TryGetValue(id, out var decision);
            if (!Enabled(entry.Order) || decision?.ConfirmedOrder is not null || decision?.SuppressAutomatic == true ||
                decision?.RejectedOrders.Contains(entry.Order) == true || manualOrders.Contains(entry.Order) ||
                !byOrder.TryGetValue(entry.Order, out var versions) || versions.Any(order => order.IsCanceled ||
                    FiscalFingerprint(order) != entry.FiscalFingerprint))
            { _pairs.Remove(id); continue; }
            // Changing the date range does not forget an established pair. It cannot
            // publish an absent receipt or enrich its order without a current receipt.
            if (!byReceipt.TryGetValue(id, out var receiptVersions)) continue;
            if (receiptVersions.Any(receipt => ReceiptFingerprint(receipt) != entry.ReceiptFingerprint))
            { _pairs.Remove(id); continue; }
            var receipt = receiptVersions[0];
            var order = versions.MaxBy(value => value.RetrievedAtUtc)!;
            var enriched = Enrich(order, entry.Verified, account);
            if (enriched is null) { _pairs.Remove(id); continue; }
            candidates[id] = (entry, receipt, enriched);
        }
        var provisionalByOrder = candidates.Values.GroupBy(candidate => candidate.Order.Key).ToDictionary(group => group.Key,
            group => Enrich(byOrder[group.Key].MaxBy(order => order.RetrievedAtUtc)!, group.SelectMany(candidate => candidate.Entry.Verified).ToArray(), account)!);
        var provisional = orders.Select(order => provisionalByOrder.GetValueOrDefault(order.Key) ?? order).ToArray();
        var restored = new Dictionary<string, ReceiptOrderMatch>(StringComparer.Ordinal);
        var accepted = new List<(Entry Entry, ReceiptRecord Receipt)>();
        foreach (var (id, candidate) in candidates)
        {
            var evidence = provisional.Select(order => (Order: order,
                Evidence: FiscalReferenceMatching.Evaluate(order, candidate.Receipt, account, receipts))).ToArray();
            var exact = evidence.Where(value => value.Evidence.Exact).Select(value => value.Order.Key).Distinct().ToArray();
            if (evidence.Any(value => value.Evidence.Conflict) || exact.Length != 1 || exact[0] != candidate.Order.Key ||
                provisional.Any(order => order.Key != candidate.Order.Key && ClaimsReceipt(order, candidate.Receipt)))
            { _pairs.Remove(id); continue; }
            accepted.Add((candidate.Entry, candidate.Receipt));
        }
        var acceptedOrders = accepted.GroupBy(candidate => candidate.Entry.Order).ToDictionary(group => group.Key,
            group => Enrich(byOrder[group.Key].MaxBy(order => order.RetrievedAtUtc)!, group.SelectMany(candidate => candidate.Entry.Verified).ToArray(), account)!);
        foreach (var candidate in accepted)
            restored[candidate.Receipt.Id] = candidate.Entry.Match with
            {
                Order = acceptedOrders[candidate.Entry.Order], Candidates = [], GroupOrders = [],
                CompetingReceiptIds = [], CompetingOrderCount = 0,
                Explanation = "Збережений точний фіскальний зв’язок. " +
                    FiscalReferenceMatching.Evaluate(acceptedOrders[candidate.Entry.Order], candidate.Receipt, account, receipts).Basis
            };
        // Failed candidates must not leak their provisional authentication evidence.
        return new(orders.Select(order => acceptedOrders.GetValueOrDefault(order.Key) ?? order).ToArray(), restored);
    }

    private static MarketplaceOrder? Enrich(MarketplaceOrder order, IReadOnlyList<FiscalDocumentReference> verified, string account)
    {
        var result = new List<FiscalDocumentReference>();
        foreach (var reference in order.FiscalReferences)
        {
            var previous = verified.FirstOrDefault(value => Raw(value) == Raw(reference));
            if (previous is not null && (reference.VerifiedReceiptId.Length > 0 &&
                    !CheckboxReceiptReference.SameId(reference.VerifiedReceiptId, previous.VerifiedReceiptId) ||
                reference.VerifiedAccountContext.Length > 0 && reference.VerifiedAccountContext != account)) return null;
            result.Add(previous is null ? reference : reference with
                { VerifiedReceiptId = previous.VerifiedReceiptId, VerifiedAccountContext = account });
        }
        return order with { FiscalReferences = result };
    }

    private static bool ClaimsReceipt(MarketplaceOrder order, ReceiptRecord receipt) =>
        order.ReceiptIds.Any(id => CheckboxReceiptReference.SameId(id, receipt.Id)) ||
        receipt.FiscalCode.Length > 0 && order.FiscalReceiptNumbers.Contains(receipt.FiscalCode, StringComparer.Ordinal) ||
        order.FiscalReceiptUrls.Any(url => CheckboxReceiptReference.TryParseUrl(url, out var parsed) &&
            (CheckboxReceiptReference.SameId(parsed.ReceiptId, receipt.Id) || receipt.FiscalCode.Length > 0 && parsed.FiscalCode == receipt.FiscalCode)) ||
        order.FiscalReferences.Where(reference => reference.Order == order.Key).Any(reference => reference.Kind switch
        {
            FiscalDocumentKeyKind.CheckboxReceiptUuid => CheckboxReceiptReference.SameId(reference.Value, receipt.Id),
            FiscalDocumentKeyKind.FiscalCode => receipt.FiscalCode.Length > 0 && reference.Value == receipt.FiscalCode,
            FiscalDocumentKeyKind.CheckboxReceiptUrl => CheckboxReceiptReference.TryParseUrl(reference.Value, out var parsed) &&
                (CheckboxReceiptReference.SameId(parsed.ReceiptId, receipt.Id) || receipt.FiscalCode.Length > 0 && parsed.FiscalCode == receipt.FiscalCode),
            _ => false
        });

    private bool Bind(string account, IReadOnlyList<string> connections)
    {
        var current = connections.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (_account != account || !_connections.SequenceEqual(current, StringComparer.Ordinal))
        { _pairs.Clear(); _account = account; _connections = current; }
        return !string.IsNullOrWhiteSpace(account);
    }
    private bool Enabled(OrderKey key) => _connections.Contains($"{key.Marketplace}:{key.ConnectionId}", StringComparer.Ordinal);
    private static FiscalDocumentReference Raw(FiscalDocumentReference reference) => reference with
        { VerifiedReceiptId = "", VerifiedAccountContext = "" };
    private static string Normalize(string id) => Guid.TryParse(id, out var uuid) ? uuid.ToString("D") : id;
    private static string ReceiptFingerprint(ReceiptRecord receipt) => Hash(JsonSerializer.Serialize(new
    {
        Id = Normalize(receipt.Id), receipt.Type, receipt.Status, receipt.Serial, receipt.FiscalCode,
        Organization = Normalize(receipt.OrganizationId), receipt.CashRegisterFiscalNumber,
        FiscalDate = receipt.FiscalDate?.ToUniversalTime(), CreatedAt = receipt.CreatedAt?.ToUniversalTime(),
        receipt.TotalKnown, receipt.TotalSumMinor, receipt.AmountComparisonIssue
    }));
    private static string FiscalFingerprint(MarketplaceOrder order) => Hash(JsonSerializer.Serialize(new
    {
        order.Key,
        ReceiptIds = order.ReceiptIds.Select(Normalize).Distinct().Order(StringComparer.Ordinal).ToArray(),
        Numbers = order.FiscalReceiptNumbers.Distinct().Order(StringComparer.Ordinal).ToArray(),
        Urls = order.FiscalReceiptUrls.Distinct().Order(StringComparer.Ordinal).ToArray(),
        References = order.FiscalReferences.Select(reference => JsonSerializer.Serialize(Raw(reference)))
            .Distinct().Order(StringComparer.Ordinal).ToArray()
    }));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
