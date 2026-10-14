using System.Text.Json.Serialization;
using System.Net;

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
    public int HistoryDays { get; set; }
    public int CacheDays { get; set; } = 30;
    public List<MarketplaceConnection> Connections { get; set; } = [];
    // Explicitly consolidated connections keep their historical order/link identities.
    // These mappings contain no credentials and are never inferred from order numbers.
    public List<MarketplaceConnectionAlias> ConnectionAliases { get; set; } = [];
}

public sealed record MarketplaceConnectionAlias(MarketplaceKind Marketplace, string SourceConnectionId, string TargetConnectionId);

public sealed record MarketplaceCredentials(string Token = "", string Login = "", string Password = "");
public sealed record OrderPerson(string Name = "", string Phone = "");
public sealed record OrderItem(string Name, string Sku, decimal? Quantity, decimal? UnitPrice, decimal? Total = null);
public sealed record OrderShipment(string Carrier, string TrackingNumber, string Destination = "")
{
    public string ShipmentId { get; init; } = "";
    public string Source { get; init; } = "";
    public int Places { get; init; }
    public string DeliveryServiceId { get; init; } = "";
    public string ShippingService { get; init; } = "";
    public string SenderWarehouseId { get; init; } = "";
    public string RecipientWarehouseId { get; init; } = "";
}

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
    public int? SourceStatusGroup { get; init; }
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
    [JsonIgnore] public bool IsCanceled
    {
        get
        {
            if (Key.Marketplace == MarketplaceKind.Rozetka)
            {
                // Seller status_group: 1 processing, 2 successful, 3 canceled.
                if (SourceStatusGroup is 1 or 2 or 3) return SourceStatusGroup == 3;
                if (CanceledRozetkaCode(string.IsNullOrWhiteSpace(SourceStatus) ? Status : SourceStatus)) return true;
            }
            return CanceledStatus(SourceStatus) || CanceledStatus(Status);
        }
    }
    [JsonIgnore] public string TrackingDisplay => string.Join(", ", Shipments.Select(s => s.TrackingNumber).Where(s => s.Length > 0).Distinct());

    // Verified Seller /order-statuses/search?status_group=3, both pages, 2026-10-11.
    // Fallback for old caches / missing expansion; never apply these IDs to Prom.
    private static bool CanceledRozetkaCode(string? status) =>
        int.TryParse(status?.Trim(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var code) && code is
        10 or 11 or 12 or 13 or 16 or 17 or 18 or 19 or 20 or 24 or 25 or 28 or 29 or 30 or 31 or 32 or
        33 or 34 or 35 or 36 or 37 or 38 or 39 or 40 or 41 or 42 or 43 or 44 or 45 or 49 or 50 or 51 or
        71 or 72 or 75 or 77 or 85 or 88;

    // Unknown codes and payment statuses must not silently remove an order.
    private static bool CanceledStatus(string? status) => status?.Trim().ToUpperInvariant() is
        "CANCELED" or "CANCELLED" or "СКАСОВАНО" or "СКАСОВАНЕ" or "СКАСОВАНИЙ" or
        "ЗАМОВЛЕННЯ СКАСОВАНО" or "ОТМЕНЕН" or "ОТМЕНЁН" or "ОТМЕНЕНО" or "ОТМЕНЁНО";
}

public sealed record MarketplaceRange(DateTimeOffset From, DateTimeOffset ToExclusive);
public sealed record OrdersFetchResult(IReadOnlyList<MarketplaceOrder> Orders, bool Complete, string Message = "")
{
    public HttpStatusCode? FailureStatus { get; init; }
    public TimeSpan? RetryAfter { get; init; }
    // A failed list must not fan out into hundreds of cached-order requests.
    // Keep successfully read pages available while the connection is paused.
    public bool SuppressDetailRefresh { get; init; }
}
public sealed record ConnectionSyncState(string ConnectionId, MarketplaceRange Range, bool Complete,
    DateTimeOffset? LastSuccessUtc, string Message, DateTimeOffset AttemptedAtUtc, string? DetailCursor = null)
{
    public HttpStatusCode? FailureStatus { get; init; }
    public TimeSpan? RetryAfter { get; init; }
}
public sealed record MarketplaceSnapshot(IReadOnlyList<MarketplaceOrder> Orders, IReadOnlyList<ConnectionSyncState> States);

public enum ReceiptLinkState { NotChecked, Exact, Manual, Candidates, NotFound, Conflict, Incomplete, Suggested }
public enum ProductComparison { Match, Contradiction, Insufficient, Loading }
public enum AutomaticLinkBasis { None, UniqueAmount, AmountAndProducts, AmountAndQuantity, AmountAndPayment }
public enum PaymentComparison { Insufficient, Match, Contradiction }
public sealed record AmountMatchScope(int HistoryDays = 30, MarketplaceRange? OrderRange = null, string Description = "");
public sealed record ReceiptOrderMatch(ReceiptLinkState State, MarketplaceOrder? Order, string Explanation,
    IReadOnlyList<MarketplaceOrder> Candidates)
{
    public AutomaticLinkBasis Basis { get; init; }
    public ProductComparison Products { get; init; } = ProductComparison.Insufficient;
    public PaymentComparison Payments { get; init; } = PaymentComparison.Insufficient;
    public bool Ambiguous { get; init; }
    public IReadOnlyList<string> CompetingReceiptIds { get; init; } = [];
    public int CompetingOrderCount { get; init; }
    public IReadOnlyList<MarketplaceOrder> GroupOrders { get; init; } = [];
    public string Scope { get; init; } = "";
    public string StatusLabel => State switch
    {
        ReceiptLinkState.Manual => "Підтверджено вручну",
        ReceiptLinkState.Exact => "Точний фіскальний зв’язок",
        ReceiptLinkState.Suggested => Basis switch
        {
            AutomaticLinkBasis.AmountAndPayment => "Автозв’язок: сума й оплата",
            AutomaticLinkBasis.AmountAndProducts => "Автозв’язок: сума й товари",
            AutomaticLinkBasis.AmountAndQuantity => "Автозв’язок: сума й кількість товарів",
            _ => "Автозв’язок: унікальна сума"
        },
        ReceiptLinkState.Incomplete => "Перевірка неповна",
        ReceiptLinkState.Conflict => "Конфлікт фіскальних даних",
        ReceiptLinkState.Candidates when Ambiguous => "Неоднозначно: кілька замовлень / чеків",
        ReceiptLinkState.Candidates when Payments == PaymentComparison.Contradiction => "Сума збігається, оплата відрізняється",
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int AmountValidationVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReceiptDiscountReconciliation? DiscountReconciliation { get; init; }
}

public sealed record ReceiptDiscountReconciliation(decimal GrossTotal, decimal DiscountTotal, decimal NetTotal,
    IReadOnlyList<decimal> ItemDiscounts, decimal ReceiptDiscount)
{
    public int Schema { get; init; } = 1;
}
