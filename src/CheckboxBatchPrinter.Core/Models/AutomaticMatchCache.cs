namespace CheckboxBatchPrinter.Core.Models;

/// <summary>Disposable, account-scoped optimization; never a source of manual decisions.</summary>
public sealed class AutomaticMatchCache
{
    public string AccountContext { get; set; } = "";
    public int RetentionDays { get; set; } = 30;
    public Dictionary<string, CachedReceiptDetails> Details { get; set; } = new(StringComparer.Ordinal);
    public List<CachedMatchingSnapshot> Snapshots { get; set; } = [];
}

public sealed record CachedReceiptDetails(string ReceiptFingerprint, ReceiptDetails Details, DateTimeOffset CachedAtUtc);

public sealed record CachedMatchingSnapshot(string AlgorithmVersion, string InputFingerprint,
    DateTimeOffset CachedAtUtc, IReadOnlyDictionary<string, CachedReceiptMatch> Matches);

// Keys only: buyer/contact/order payloads are not duplicated into the matching cache.
public sealed record CachedReceiptMatch(ReceiptLinkState State, OrderKey? Order, string Explanation,
    IReadOnlyList<OrderKey> Candidates, AutomaticLinkBasis Basis, ProductComparison Products, bool Ambiguous,
    IReadOnlyList<string> CompetingReceiptIds, int CompetingOrderCount, IReadOnlyList<OrderKey> GroupOrders, string Scope);

public sealed record AutomaticMatchingResult(IReadOnlyDictionary<string, ReceiptOrderMatch> Matches, bool CacheHit);
