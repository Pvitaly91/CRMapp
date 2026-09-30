namespace CheckboxBatchPrinter.Core.Models;

public sealed record NotificationSource(MarketplaceKind Marketplace, string ConnectionId);
public sealed record NotificationSourceSnapshot(NotificationSource Source, bool Complete,
    DateTimeOffset? LastSuccessUtc = null);
public sealed record OrderNotificationSnapshot(IReadOnlyList<MarketplaceOrder> Orders,
    IReadOnlyList<NotificationSourceSnapshot> Sources);
public sealed record SeenNotificationOrder(OrderKey Key, DateTimeOffset SeenAtUtc);
public sealed record NotificationBaseline(NotificationSource Source, DateTimeOffset SinceUtc);

// Only stable keys and timestamps; no buyer, products, order payloads or credentials.
public sealed class OrderNotificationState
{
    public List<NotificationBaseline> Baselines { get; set; } = [];
    public List<SeenNotificationOrder> Seen { get; set; } = [];
}

public sealed record NewOrdersNotification(int Count, string Title, string Body);
