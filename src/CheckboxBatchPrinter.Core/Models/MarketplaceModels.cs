using System.Text.Json.Serialization;

namespace CheckboxBatchPrinter.Core.Models;

public enum MarketplaceKind { Prom, Rozetka }

public sealed record OrderKey(MarketplaceKind Marketplace, string ConnectionId, string OrderId);

public sealed class MarketplaceConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public MarketplaceKind Marketplace { get; set; }
    public bool Enabled { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class MarketplaceSettings
{
    public int HistoryDays { get; set; } = 30;
    public int CacheDays { get; set; } = 30;
    public List<MarketplaceConnection> Connections { get; set; } = [];
}

public sealed record MarketplaceCredentials(string Token = "", string Login = "", string Password = "");
public sealed record OrderPerson(string Name = "", string Phone = "");
public sealed record OrderItem(string Name, string Sku, decimal? Quantity, decimal? UnitPrice, decimal? Total = null);
public sealed record OrderShipment(string Carrier, string TrackingNumber, string Destination = "");

public enum FiscalDocumentKeyKind { CheckboxReceiptUuid, CheckboxReceiptUrl, FiscalCode }

// Keys of one document share DocumentId; different sale/return documents must not be collapsed.
public sealed record FiscalDocumentReference(FiscalDocumentKeyKind Kind, string Value, string Source,
    OrderKey Order, string Provider, string DocumentId, string CashRegisterFiscalNumber = "", string OrganizationId = "")
{
    // Authentication-scoped lookup evidence is memory-only: never trust it after restart/account change.
    [JsonIgnore] public string VerifiedReceiptId { get; init; } = "";
    [JsonIgnore] public string VerifiedAccountContext { get; init; } = "";
}

public sealed record MarketplaceOrder
{
    public required OrderKey Key { get; init; }
    public string StoreName { get; init; } = "";
    public string Number { get; init; } = "";
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string Status { get; init; } = "";
    public string SourceStatus { get; init; } = "";
    public string RawTotal { get; init; } = "";
    public string RawCreatedAt { get; init; } = "";
    public string PaymentStatus { get; init; } = "";
    public OrderPerson? Buyer { get; init; }
    public OrderPerson? Recipient { get; init; }
    public decimal? Total { get; init; }
    public string Currency { get; init; } = "";
    public string PaymentMethod { get; init; } = "";
    public string DeliveryMethod { get; init; } = "";
    public decimal? Discount { get; init; }
    public decimal? DeliveryCost { get; init; }
    public IReadOnlyList<OrderItem> Items { get; init; } = [];
    // Old cache entries did not prove that the adapter retained every product. Refresh before guessing.
    public bool ItemsComplete { get; init; }
    // Structural completeness is independent of prices and line totals. Null supports old caches.
    public bool? ItemListComplete { get; init; }
    public string AmountComparisonIssue { get; init; } = "";
    public IReadOnlyList<OrderShipment> Shipments { get; init; } = [];
    // Only IDs extracted from documented fields / strictly verified URL formats.
    public IReadOnlyList<string> ReceiptIds { get; init; } = [];
    public IReadOnlyList<string> FiscalReceiptNumbers { get; init; } = [];
    public IReadOnlyList<string> FiscalReceiptUrls { get; init; } = [];
    public IReadOnlyList<FiscalDocumentReference> FiscalReferences { get; init; } = [];
    public string FiscalDataStatus { get; init; } = "";
    public string? SellerUrl { get; init; }
    public DateTimeOffset RetrievedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public string TrackingDisplay => string.Join(", ", Shipments.Select(s => s.TrackingNumber).Where(s => s.Length > 0).Distinct());
}

public sealed record MarketplaceRange(DateTimeOffset From, DateTimeOffset ToExclusive);
public sealed record OrdersFetchResult(IReadOnlyList<MarketplaceOrder> Orders, bool Complete, string Message = "");
public sealed record ConnectionSyncState(string ConnectionId, MarketplaceRange Range, bool Complete,
    DateTimeOffset? LastSuccessUtc, string Message, DateTimeOffset AttemptedAtUtc);
public sealed record MarketplaceSnapshot(IReadOnlyList<MarketplaceOrder> Orders, IReadOnlyList<ConnectionSyncState> States);

public enum ReceiptLinkState { NotChecked, Exact, Manual, Candidates, NotFound, Conflict, Incomplete, Suggested }
public enum ProductComparison { Match, Contradiction, Insufficient, Loading }
public enum AutomaticLinkBasis { None, UniqueAmount, AmountAndProducts }
public sealed record AmountMatchScope(int HistoryDays = 30, MarketplaceRange? OrderRange = null, string Description = "");
public sealed record ReceiptOrderMatch(ReceiptLinkState State, MarketplaceOrder? Order, string Explanation,
    IReadOnlyList<MarketplaceOrder> Candidates)
{
    public AutomaticLinkBasis Basis { get; init; }
    public ProductComparison Products { get; init; } = ProductComparison.Insufficient;
    public bool Ambiguous { get; init; }
    public IReadOnlyList<string> CompetingReceiptIds { get; init; } = [];
    public int CompetingOrderCount { get; init; }
    public IReadOnlyList<MarketplaceOrder> GroupOrders { get; init; } = [];
    public string Scope { get; init; } = "";
    public string StatusLabel => State switch
    {
        ReceiptLinkState.Manual => "Підтверджено вручну",
        ReceiptLinkState.Exact => "Точний фіскальний зв’язок",
        ReceiptLinkState.Suggested => Basis == AutomaticLinkBasis.AmountAndProducts
            ? "Автозв’язок: сума й товари" : "Автозв’язок: унікальна сума",
        ReceiptLinkState.Incomplete => "Перевірка неповна",
        ReceiptLinkState.Conflict => "Конфлікт фіскальних даних",
        ReceiptLinkState.Candidates when Ambiguous => "Неоднозначно: кілька замовлень / чеків",
        ReceiptLinkState.Candidates when Products == ProductComparison.Contradiction => "Сума збігається, товари суперечать",
        ReceiptLinkState.Candidates => "Недостатньо даних",
        ReceiptLinkState.NotFound => "Не знайдено у діапазоні",
        _ => "Не перевірено"
    };
}

public sealed class ReceiptOrderDecision
{
    public string AccountContext { get; set; } = "";
    public string ReceiptId { get; set; } = "";
    public OrderKey? ConfirmedOrder { get; set; }
    public List<OrderKey> RejectedOrders { get; set; } = [];
    public bool SuppressAutomatic { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed record ReceiptDetails(string Id, IReadOnlyList<OrderItem> Items, string? RelatedReceiptId = null,
    string? CheckboxOrderId = null, bool HasUnmappedContext = false, bool ItemsComplete = true)
{
    public bool? ItemListComplete { get; init; }
    public string AmountComparisonIssue { get; init; } = "";
}
