using System.Text.Json.Serialization;

namespace CheckboxBatchPrinter.Core.Models;

/// <summary>Disposable, account-scoped optimization; never a source of manual decisions.</summary>
public sealed class AutomaticMatchCache
{
    public string AccountContext { get; set; } = "";
    public int RetentionDays { get; set; } = 30;
    public Dictionary<string, CachedReceiptDetails> Details { get; set; } = new(StringComparer.Ordinal);
    public List<CachedMatchingSnapshot> Snapshots { get; set; } = [];
    public Dictionary<string, CachedAutomaticLink> Links { get; set; } = new(StringComparer.Ordinal);
    public int LinkSchema { get; set; }
    public Dictionary<string, CachedExactReceiptLink> ExactLinks { get; set; } = new(StringComparer.Ordinal);
    public int ExactLinkSchema { get; set; } = 1;
    public Dictionary<string, DateTimeOffset> ExactLinkMigrationAttempts { get; set; } = new(StringComparer.Ordinal);
    [JsonIgnore] public bool ExactLinksChanged { get; set; }
    [JsonIgnore] public long ExactLinksRevision { get; set; }
}

// An accepted relationship, bound to the surrounding Checkbox account and unchanged
// raw fiscal documents. Generic MarketplaceOrder verification flags remain JsonIgnore.
public sealed record CachedExactReceiptLink(OrderKey Order, string ReceiptFingerprint, string FiscalFingerprint,
    IReadOnlyList<CachedFiscalAttestation> Attestations, IReadOnlyList<string> EnabledConnectionIds, DateTimeOffset AcceptedAtUtc)
{
    public int Schema { get; init; } = 1;
}

public sealed record CachedFiscalAttestation(FiscalDocumentReference Reference, string ReceiptId);

public sealed record CachedAutomaticLink(CachedReceiptMatch Match, DateTimeOffset LinkedAtUtc);

public sealed record CachedReceiptDetails(string ReceiptFingerprint, ReceiptDetails Details, DateTimeOffset CachedAtUtc);

public sealed record CachedMatchingSnapshot(string AlgorithmVersion, string InputFingerprint,
    DateTimeOffset CachedAtUtc, IReadOnlyDictionary<string, CachedReceiptMatch> Matches)
{
    public int HistoryDays { get; init; } = 30;
    public MarketplaceRange? OrderRange { get; init; }
    public IReadOnlyList<string> EnabledConnectionIds { get; init; } = [];
}

// Keys only: buyer/contact/order payloads are not duplicated into the matching cache.
public sealed record CachedReceiptMatch(ReceiptLinkState State, OrderKey? Order, string Explanation,
    IReadOnlyList<OrderKey> Candidates, AutomaticLinkBasis Basis, ProductComparison Products, bool Ambiguous,
    IReadOnlyList<string> CompetingReceiptIds, int CompetingOrderCount, IReadOnlyList<OrderKey> GroupOrders, string Scope)
{
    public string ReceiptFingerprint { get; init; } = "";
    public PaymentComparison Payments { get; init; } = PaymentComparison.Insufficient;
    public string OrderFingerprint { get; init; } = "";
}

public sealed record AutomaticMatchingResult(IReadOnlyDictionary<string, ReceiptOrderMatch> Matches, bool CacheHit);
