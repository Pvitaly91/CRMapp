using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

/// <summary>Memoizes complete graph results, never individual edges that could hide a new competitor.</summary>
public sealed class CachedReceiptOrderMatchingService(TimeProvider? timeProvider = null)
{
    public const string AlgorithmVersion = "same-kyiv-day-product-names-v3";
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
        IReadOnlyDictionary<string, ReceiptDetails>? details = null, AmountMatchScope? scope = null, IReadOnlySet<string>? loading = null)
    {
        if (!BindAccount(cache, accountContext)) throw new ArgumentException("Обліковий контекст кешу не збігається.", nameof(cache));
        Prune(cache, _clock.GetUtcNow());
        // Incomplete snapshots can never resurrect a previously suggested edge.
        if (coverageComplete && TryRestore(cache, receipts, accountContext, orders, decisions, out var restored, details, scope, loading))
            return new(restored, true);
        var matches = _matcher.MatchAll(receipts, accountContext, orders, decisions, coverageComplete, details, scope, loading);
        if (coverageComplete && loading?.Count is not > 0 && matches.Count <= MaximumMatchesPerSnapshot)
        {
            var fingerprint = Fingerprint(receipts, accountContext, orders, decisions, details, scope, loading);
            cache.Snapshots.RemoveAll(s => s.InputFingerprint == fingerprint);
            cache.Snapshots.Add(new(AlgorithmVersion, fingerprint, _clock.GetUtcNow(), matches.ToDictionary(p => p.Key, p => Pack(p.Value))));
            Prune(cache, _clock.GetUtcNow());
        }
        return new(matches, false);
    }

    /// <summary>Only replays a complete, unchanged graph. Caller must verify cached connection/range coverage first.</summary>
    public bool TryRestore(AutomaticMatchCache cache, IReadOnlyList<ReceiptRecord> receipts, string accountContext,
        IReadOnlyList<MarketplaceOrder> orders, IReadOnlyList<ReceiptOrderDecision> decisions,
        out IReadOnlyDictionary<string, ReceiptOrderMatch> matches,
        IReadOnlyDictionary<string, ReceiptDetails>? details = null, AmountMatchScope? scope = null, IReadOnlySet<string>? loading = null)
    {
        matches = new Dictionary<string, ReceiptOrderMatch>();
        if (!BindAccount(cache, accountContext) || loading?.Count is > 0) return false;
        Prune(cache, _clock.GetUtcNow());
        var fingerprint = Fingerprint(receipts, accountContext, orders, decisions, details, scope, loading);
        var entry = cache.Snapshots.FirstOrDefault(s => s.InputFingerprint == fingerprint);
        if (entry is null) return false;
        var currentOrders = orders.GroupBy(o => o.Key).ToDictionary(g => g.Key, g => g.MaxBy(o => o.RetrievedAtUtc)!);
        if (!entry.Matches.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(receipts.DistinctBy(r => Normalize(r.Id)).Select(r => r.Id)) ||
            entry.Matches.Values.SelectMany(m => m.Candidates.Concat(m.GroupOrders).Concat(m.Order is null ? [] : [m.Order]))
                .Any(k => !currentOrders.ContainsKey(k))) return false;
        matches = entry.Matches.ToDictionary(p => p.Key, p => Unpack(p.Value, currentOrders, scope?.Description ?? ""));
        return true;
    }

    internal static void Prune(AutomaticMatchCache cache, DateTimeOffset now)
    {
        cache.RetentionDays = Math.Clamp(cache.RetentionDays, 1, 90);
        var cutoff = now.AddDays(-cache.RetentionDays);
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

    private static string ReceiptFingerprint(ReceiptRecord receipt) => Hash(JsonSerializer.Serialize(receipt));
    private static string Normalize(string id) => Guid.TryParse(id, out var uuid) ? uuid.ToString("D") : id;
    private static string Hash(string input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));

    private static string Fingerprint(IReadOnlyList<ReceiptRecord> receipts, string account,
        IReadOnlyList<MarketplaceOrder> orders, IReadOnlyList<ReceiptOrderDecision> decisions,
        IReadOnlyDictionary<string, ReceiptDetails>? details, AmountMatchScope? scope, IReadOnlySet<string>? loading)
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

    private static CachedReceiptMatch Pack(ReceiptOrderMatch match) => new(match.State, match.Order?.Key, match.Explanation,
        match.Candidates.Select(o => o.Key).ToArray(), match.Basis, match.Products, match.Ambiguous,
        match.CompetingReceiptIds.ToArray(), match.CompetingOrderCount, match.GroupOrders.Select(o => o.Key).ToArray(), match.Scope);

    private static ReceiptOrderMatch Unpack(CachedReceiptMatch cached, IReadOnlyDictionary<OrderKey, MarketplaceOrder> orders, string scope) =>
        new(cached.State, cached.Order is null ? null : orders[cached.Order], cached.Explanation, cached.Candidates.Select(k => orders[k]).ToArray())
        {
            Basis = cached.Basis, Products = cached.Products, Ambiguous = cached.Ambiguous,
            CompetingReceiptIds = cached.CompetingReceiptIds, CompetingOrderCount = cached.CompetingOrderCount,
            GroupOrders = cached.GroupOrders.Select(k => orders[k]).ToArray(), Scope = scope.Length > 0 ? scope : cached.Scope
        };
}
