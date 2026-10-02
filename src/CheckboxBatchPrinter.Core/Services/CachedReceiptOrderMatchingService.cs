using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

/// <summary>Keep assigned pairs and search only documents without a link. Manual/fiscal decisions have priority.</summary>
public sealed class CachedReceiptOrderMatchingService(TimeProvider? timeProvider = null)
{
    public const string AlgorithmVersion = "same-kyiv-day-unlinked-allocation-v10";
    private const string PreviousAlgorithmVersion = "same-kyiv-day-clean-names-payment-v8";
    public const int MaximumDetails = 5000;
    public const int MaximumSnapshots = 8;
    private const int MaximumMatchesPerSnapshot = 5000;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly ReceiptOrderMatchingService _matcher = new();

    public bool TryGetDetails(AutomaticMatchCache cache, string accountContext, ReceiptRecord receipt, out ReceiptDetails details)
    {
        details = null!;
        if (!BindAccount(cache, accountContext)) return false;
        Prune(cache, _clock.GetUtcNow());
        if (!cache.Details.TryGetValue(Normalize(receipt.Id), out var entry) ||
            entry.ReceiptFingerprint != ReceiptFingerprint(receipt)) return false;
        details = entry.Details;
        return true;
    }

    public void RememberDetails(AutomaticMatchCache cache, string accountContext, ReceiptRecord receipt, ReceiptDetails details)
    {
        if (!BindAccount(cache, accountContext)) throw new ArgumentException("Обліковий контекст кешу не збігається.", nameof(cache));
        if (Normalize(receipt.Id) != Normalize(details.Id)) throw new ArgumentException("Деталі належать іншому чеку.", nameof(details));
        // Do not slide TTL when the same locally cached value is presented again.
        var key = Normalize(receipt.Id);
        var fingerprint = ReceiptFingerprint(receipt);
        if (!cache.Details.TryGetValue(key, out var old) || old.ReceiptFingerprint != fingerprint ||
            JsonSerializer.Serialize(old.Details) != JsonSerializer.Serialize(details))
            cache.Details[key] = new(fingerprint, details, _clock.GetUtcNow());
        Prune(cache, _clock.GetUtcNow());
    }

    public AutomaticMatchingResult MatchAll(AutomaticMatchCache cache, IReadOnlyList<ReceiptRecord> receipts, string accountContext,
        IReadOnlyList<MarketplaceOrder> orders, IReadOnlyList<ReceiptOrderDecision> decisions, bool coverageComplete,
        IReadOnlyDictionary<string, ReceiptDetails>? details = null, AmountMatchScope? scope = null, IReadOnlySet<string>? loading = null,
        IReadOnlyList<string>? enabledConnectionIds = null)
    {
        if (!BindAccount(cache, accountContext)) throw new ArgumentException("Обліковий контекст кешу не збігається.", nameof(cache));
        Prune(cache, _clock.GetUtcNow());
        // Only complete checks create snapshots; a caller may display a validated
        // last-known pair while a newer API check is still pending.
        if (coverageComplete && TryRestore(cache, receipts, accountContext, orders, decisions, out var restored,
                details, scope, loading, enabledConnectionIds))
            return new(restored, true);
        var existing = ResolveSavedLinks(cache, receipts, orders, decisions, scope);
        var matches = _matcher.MatchAll(receipts, accountContext, orders, decisions, coverageComplete, details, scope, loading, existing);
        RememberLinks(cache, receipts, matches, loading);
        if (coverageComplete && loading?.Count is not > 0 && matches.Count <= MaximumMatchesPerSnapshot)
        {
            var fingerprint = Fingerprint(receipts, accountContext, orders, decisions, details, scope, loading,
                ResolveSavedLinks(cache, receipts, orders, decisions, scope));
            var historyDays = scope?.HistoryDays ?? 30;
            var orderRange = scope?.OrderRange;
            // A newer complete graph supersedes older conclusions for this same range.
            cache.Snapshots.RemoveAll(s => s.InputFingerprint == fingerprint ||
                s.HistoryDays == historyDays && Equals(s.OrderRange, orderRange));
            var currentReceipts = receipts.DistinctBy(r => Normalize(r.Id)).ToDictionary(r => r.Id);
            cache.Snapshots.Add(new(AlgorithmVersion, fingerprint, _clock.GetUtcNow(),
                matches.ToDictionary(p => p.Key, p => Pack(p.Value, currentReceipts[p.Key])))
            { HistoryDays = historyDays, OrderRange = orderRange,
                EnabledConnectionIds = NormalizeConnections(enabledConnectionIds) });
            Prune(cache, _clock.GetUtcNow());
        }
        return new(matches, false);
    }

    /// <summary>Only replays a complete, unchanged graph. Caller must verify cached connection/range coverage first.</summary>
    public bool TryRestore(AutomaticMatchCache cache, IReadOnlyList<ReceiptRecord> receipts, string accountContext,
        IReadOnlyList<MarketplaceOrder> orders, IReadOnlyList<ReceiptOrderDecision> decisions,
        out IReadOnlyDictionary<string, ReceiptOrderMatch> matches,
        IReadOnlyDictionary<string, ReceiptDetails>? details = null, AmountMatchScope? scope = null, IReadOnlySet<string>? loading = null,
        IReadOnlyList<string>? enabledConnectionIds = null, bool legacyCoverageVerified = false)
    {
        matches = new Dictionary<string, ReceiptOrderMatch>();
        if (!BindAccount(cache, accountContext) || loading?.Count is > 0) return false;
        Prune(cache, _clock.GetUtcNow());
        var fingerprint = Fingerprint(receipts, accountContext, orders, decisions, details, scope, loading,
            ResolveSavedLinks(cache, receipts, orders, decisions, scope));
        var entry = cache.Snapshots.FirstOrDefault(s => s.InputFingerprint == fingerprint &&
            (ConnectionsMatch(s.EnabledConnectionIds, enabledConnectionIds) ||
                legacyCoverageVerified && s.EnabledConnectionIds.Count == 0));
        if (entry is null) return false;
        var currentOrders = orders.GroupBy(o => o.Key).ToDictionary(g => g.Key, g => g.MaxBy(o => o.RetrievedAtUtc)!);
        if (!entry.Matches.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(receipts.DistinctBy(r => Normalize(r.Id)).Select(r => r.Id)) ||
            entry.Matches.Values.SelectMany(m => m.Candidates.Concat(m.GroupOrders).Concat(m.Order is null ? [] : [m.Order]))
                .Any(k => !currentOrders.ContainsKey(k))) return false;
        matches = entry.Matches.ToDictionary(p => p.Key, p => Unpack(p.Value, currentOrders, scope?.Description ?? ""));
        return true;
    }

    public bool HasVerifiedSnapshot(AutomaticMatchCache cache, string accountContext, AmountMatchScope scope,
        IReadOnlyList<string> enabledConnectionIds)
    {
        if (!BindAccount(cache, accountContext)) return false;
        Prune(cache, _clock.GetUtcNow());
        return cache.Snapshots.Any(s => s.OrderRange == scope.OrderRange && s.HistoryDays == scope.HistoryDays &&
            ConnectionsMatch(s.EnabledConnectionIds, enabledConnectionIds));
    }

    /// <summary>
    /// Restore saved pairs without comparing their baskets to other documents again.
    /// Changed documents and explicit decisions still invalidate their own saved link.
    /// </summary>
    public IReadOnlyDictionary<string, ReceiptOrderMatch> RestoreKnownSuggestions(AutomaticMatchCache cache,
        IReadOnlyList<ReceiptRecord> receipts, string accountContext, IReadOnlyList<MarketplaceOrder> orders,
        IReadOnlyList<ReceiptOrderDecision> decisions, IReadOnlyDictionary<string, ReceiptDetails>? details = null,
        AmountMatchScope? scope = null, IReadOnlySet<string>? loading = null,
        IReadOnlyList<string>? enabledConnectionIds = null)
    {
        if (!BindAccount(cache, accountContext)) return new Dictionary<string, ReceiptOrderMatch>();
        Prune(cache, _clock.GetUtcNow());
        var existing = ResolveSavedLinks(cache, receipts, orders, decisions, scope);
        var current = _matcher.MatchAll(receipts, accountContext, orders, decisions, false, details, scope, loading, existing);
        return current.Where(p => existing.TryGetValue(p.Key, out var saved) &&
                p.Value.State == ReceiptLinkState.Suggested && p.Value.Order?.Key == saved.Order?.Key)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    public IReadOnlySet<OrderKey> LinkedOrderKeys(AutomaticMatchCache cache, string accountContext,
        IReadOnlyList<ReceiptRecord> receipts, IReadOnlyList<MarketplaceOrder> orders,
        IReadOnlyList<ReceiptOrderDecision> decisions)
    {
        if (!BindAccount(cache, accountContext)) return new HashSet<OrderKey>();
        Prune(cache, _clock.GetUtcNow());
        return ResolveSavedLinks(cache, receipts, orders, decisions, null).Values.Select(m => m.Order!.Key).ToHashSet();
    }

    private Dictionary<string, ReceiptOrderMatch> ResolveSavedLinks(AutomaticMatchCache cache,
        IReadOnlyList<ReceiptRecord> receipts, IReadOnlyList<MarketplaceOrder> orders,
        IReadOnlyList<ReceiptOrderDecision> decisions, AmountMatchScope? scope)
    {
        var byReceipt = receipts.DistinctBy(r => Normalize(r.Id)).ToDictionary(r => Normalize(r.Id));
        var byOrder = orders.GroupBy(o => o.Key).ToDictionary(g => g.Key, g => g.MaxBy(o => o.RetrievedAtUtc)!);
        var currentDecisions = decisions.Where(d => d.AccountContext == cache.AccountContext)
            .GroupBy(d => Normalize(d.ReceiptId)).ToDictionary(g => g.Key, g => g.MaxBy(d => d.UpdatedAtUtc)!);
        var manualOrders = currentDecisions.Values.Where(d => d.ConfirmedOrder is not null)
            .Select(d => d.ConfirmedOrder!).ToHashSet();
        var valid = new Dictionary<string, ReceiptOrderMatch>(StringComparer.Ordinal);
        foreach (var (id, link) in cache.Links.ToArray())
        {
            var saved = link.Match;
            currentDecisions.TryGetValue(id, out var decision);
            if (saved.Order is null || decision?.ConfirmedOrder is not null || decision?.SuppressAutomatic == true ||
                decision?.RejectedOrders.Contains(saved.Order) == true || manualOrders.Contains(saved.Order) ||
                byReceipt.TryGetValue(id, out var receipt) && saved.ReceiptFingerprint != ReceiptFingerprint(receipt))
            { cache.Links.Remove(id); continue; }
            if (!byOrder.TryGetValue(saved.Order, out var order)) continue;
            if (saved.OrderFingerprint != OrderFingerprint(order)) { cache.Links.Remove(id); continue; }
            var receiptId = byReceipt.TryGetValue(id, out var currentReceipt) ? currentReceipt.Id : id;
            const string prefix = "Збережений автозв’язок; новий пошук виконується лише серед замовлень без зв’язку. ";
            valid[receiptId] = new(ReceiptLinkState.Suggested, order,
                saved.Explanation.StartsWith(prefix, StringComparison.Ordinal) ? saved.Explanation : prefix + saved.Explanation, [order])
            {
                Basis = saved.Basis, Products = saved.Products, Payments = saved.Payments,
                CompetingOrderCount = 1, CompetingReceiptIds = [receiptId], GroupOrders = [order],
                Scope = scope?.Description ?? saved.Scope
            };
        }
        // Conflicting cache entries cannot reserve one order for two independent automatic links.
        var duplicates = valid.Values.GroupBy(m => m.Order!.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return valid.Where(p => !duplicates.Contains(p.Value.Order!.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    private void RememberLinks(AutomaticMatchCache cache, IReadOnlyList<ReceiptRecord> receipts,
        IReadOnlyDictionary<string, ReceiptOrderMatch> matches, IReadOnlySet<string>? loading)
    {
        var authoritativeOrders = matches.Values.Where(m => m.State is ReceiptLinkState.Manual or ReceiptLinkState.Exact)
            .Where(m => m.Order is not null).Select(m => m.Order!.Key).ToHashSet();
        foreach (var (id, link) in cache.Links.ToArray())
            if (authoritativeOrders.Contains(link.Match.Order!) ||
                matches.TryGetValue(id, out var match) && match.State != ReceiptLinkState.Suggested) cache.Links.Remove(id);
        foreach (var receipt in receipts.DistinctBy(r => Normalize(r.Id)))
        {
            if (loading?.Contains(Normalize(receipt.Id)) == true ||
                !matches.TryGetValue(receipt.Id, out var match) || match.State != ReceiptLinkState.Suggested || match.Order is null) continue;
            var id = Normalize(receipt.Id);
            var saved = Pack(match, receipt);
            var linkedAt = cache.Links.TryGetValue(id, out var previous) && previous.Match.Order == saved.Order
                ? previous.LinkedAtUtc : _clock.GetUtcNow();
            cache.Links[id] = new(saved, linkedAt);
        }
    }

    internal static void Prune(AutomaticMatchCache cache, DateTimeOffset now)
    {
        cache.RetentionDays = Math.Clamp(cache.RetentionDays, 1, 90);
        var cutoff = now.AddDays(-cache.RetentionDays);
        if (cache.LinkSchema == 0)
        {
            // Import assigned pairs from the immediately previous release once, before invalidating its graph cache.
            foreach (var snapshot in cache.Snapshots.Where(s => s.AlgorithmVersion == PreviousAlgorithmVersion &&
                         s.CachedAtUtc >= cutoff && s.CachedAtUtc <= now).OrderByDescending(s => s.CachedAtUtc))
                foreach (var (id, match) in snapshot.Matches)
                    if (match.State == ReceiptLinkState.Suggested && match.Order is not null &&
                        match.ReceiptFingerprint.Length > 0 && match.OrderFingerprint.Length > 0)
                        cache.Links.TryAdd(Normalize(id), new(match, snapshot.CachedAtUtc));
            cache.LinkSchema = 1;
        }
        cache.Links = cache.Links.Where(p => p.Value.LinkedAtUtc >= cutoff && p.Value.LinkedAtUtc <= now)
            .OrderByDescending(p => p.Value.LinkedAtUtc).ThenBy(p => p.Key, StringComparer.Ordinal).Take(MaximumDetails)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        cache.Details = cache.Details.Where(p => p.Value.CachedAtUtc >= cutoff && p.Value.CachedAtUtc <= now)
            .OrderByDescending(p => p.Value.CachedAtUtc).ThenBy(p => p.Key, StringComparer.Ordinal).Take(MaximumDetails)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        cache.Snapshots = cache.Snapshots.Where(s => s.AlgorithmVersion == AlgorithmVersion && s.CachedAtUtc >= cutoff && s.CachedAtUtc <= now &&
                s.Matches.Count <= MaximumMatchesPerSnapshot)
            .OrderByDescending(s => s.CachedAtUtc).Take(MaximumSnapshots).ToList();
    }

    private static bool BindAccount(AutomaticMatchCache cache, string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        if (cache.AccountContext.Length == 0) cache.AccountContext = account;
        return cache.AccountContext == account;
    }

    private static IReadOnlyList<string> NormalizeConnections(IReadOnlyList<string>? ids) =>
        (ids ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static bool ConnectionsMatch(IReadOnlyList<string>? stored, IReadOnlyList<string>? current) =>
        (stored ?? []).SequenceEqual(NormalizeConnections(current), StringComparer.Ordinal);

    private static string ReceiptFingerprint(ReceiptRecord receipt) => Hash(JsonSerializer.Serialize(receipt));
    private static string Normalize(string id) => Guid.TryParse(id, out var uuid) ? uuid.ToString("D") : id;
    private static string Hash(string input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));

    private static string Fingerprint(IReadOnlyList<ReceiptRecord> receipts, string account,
        IReadOnlyList<MarketplaceOrder> orders, IReadOnlyList<ReceiptOrderDecision> decisions,
        IReadOnlyDictionary<string, ReceiptDetails>? details, AmountMatchScope? scope, IReadOnlySet<string>? loading,
        IReadOnlyDictionary<string, ReceiptOrderMatch> existingLinks)
    {
        // Hash all model fields conservatively, but never persist buyer payloads or churn from retrieval timestamps.
        // Memory-only verified fiscal evidence MUST be explicit despite its JsonIgnore attribute.
        var relevantOrders = orders.GroupBy(o => o.Key).Select(g => g.MaxBy(o => o.RetrievedAtUtc)!).Select(o => new
        {
            Order = o with { Buyer = null, Recipient = null, RetrievedAtUtc = default },
            Verified = o.FiscalReferences.Select(r => new { r.VerifiedReceiptId, r.VerifiedAccountContext }).ToArray()
        }).Select(o => JsonSerializer.Serialize(o)).Order(StringComparer.Ordinal).ToArray();
        return Hash(JsonSerializer.Serialize(new
        {
            AlgorithmVersion, Account = account, Complete = true,
            Links = existingLinks.OrderBy(p => Normalize(p.Key), StringComparer.Ordinal)
                .Select(p => new { Receipt = Normalize(p.Key), Order = p.Value.Order!.Key }).ToArray(),
            Receipts = receipts.DistinctBy(r => Normalize(r.Id)).Select(r => JsonSerializer.Serialize(r)).Order(StringComparer.Ordinal).ToArray(),
            Orders = relevantOrders,
            // Preserve tie precedence for malformed duplicate decisions with identical timestamps.
            Decisions = decisions.Select(d => JsonSerializer.Serialize(d)).ToArray(),
            Details = (details?.Values ?? []).GroupBy(d => Normalize(d.Id)).Select(g => g.First())
                .Select(d => JsonSerializer.Serialize(d)).Order(StringComparer.Ordinal).ToArray(),
            HistoryDays = scope?.HistoryDays ?? 30, Range = scope?.OrderRange,
            Loading = loading?.Order(StringComparer.Ordinal).ToArray() ?? []
        }));
    }

    private static string OrderFingerprint(MarketplaceOrder order) => Hash(JsonSerializer.Serialize(new
    {
        order.Key, order.Total, order.Currency, order.CreatedAt, order.Items,
        order.PaymentMethod, order.DeliveryMethod, ShippingCarriers = order.Shipments.Select(s => s.Carrier).Order(StringComparer.Ordinal).ToArray(),
        order.ItemsComplete, order.ItemListComplete, order.AmountComparisonIssue,
        order.Discount, order.DeliveryCost, order.ReceiptIds, order.FiscalReceiptNumbers,
        order.FiscalReceiptUrls, order.FiscalReferences,
        Verified = order.FiscalReferences.Select(r => new { r.VerifiedReceiptId, r.VerifiedAccountContext }).ToArray()
    }));

    private static CachedReceiptMatch Pack(ReceiptOrderMatch match, ReceiptRecord receipt) => new(match.State, match.Order?.Key, match.Explanation,
        match.Candidates.Select(o => o.Key).ToArray(), match.Basis, match.Products, match.Ambiguous,
        match.CompetingReceiptIds.ToArray(), match.CompetingOrderCount, match.GroupOrders.Select(o => o.Key).ToArray(), match.Scope)
    { Payments = match.Payments, ReceiptFingerprint = ReceiptFingerprint(receipt), OrderFingerprint = match.Order is null ? "" : OrderFingerprint(match.Order) };

    private static ReceiptOrderMatch Unpack(CachedReceiptMatch cached, IReadOnlyDictionary<OrderKey, MarketplaceOrder> orders, string scope) =>
        new(cached.State, cached.Order is null ? null : orders[cached.Order], cached.Explanation, cached.Candidates.Select(k => orders[k]).ToArray())
        {
            Basis = cached.Basis, Products = cached.Products, Payments = cached.Payments, Ambiguous = cached.Ambiguous,
            CompetingReceiptIds = cached.CompetingReceiptIds, CompetingOrderCount = cached.CompetingOrderCount,
            GroupOrders = cached.GroupOrders.Select(k => orders[k]).ToArray(), Scope = scope.Length > 0 ? scope : cached.Scope
        };
}
