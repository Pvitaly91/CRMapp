using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public interface IMarketplaceOrdersClient
{
    MarketplaceKind Marketplace { get; }
    Task TestConnectionAsync(MarketplaceConnection connection, MarketplaceCredentials credentials, CancellationToken cancellationToken = default);
    Task<OrdersFetchResult> FetchAsync(MarketplaceConnection connection, MarketplaceCredentials credentials,
        MarketplaceRange range, CancellationToken cancellationToken = default);
    Task<MarketplaceOrder?> GetOrderAsync(MarketplaceConnection connection, MarketplaceCredentials credentials,
        string orderId, CancellationToken cancellationToken = default);
}

public interface IMarketplaceSettingsStore
{
    Task<MarketplaceSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(MarketplaceSettings settings, CancellationToken cancellationToken = default);
}

public interface IMarketplaceSecretStore
{
    Task<MarketplaceCredentials?> LoadAsync(string connectionId, CancellationToken cancellationToken = default);
    Task SaveAsync(string connectionId, MarketplaceCredentials credentials, CancellationToken cancellationToken = default);
}

public interface IMarketplaceCacheStore
{
    Task<MarketplaceSnapshot> LoadAsync(int retentionDays, CancellationToken cancellationToken = default);
    Task SaveAsync(MarketplaceSnapshot snapshot, CancellationToken cancellationToken = default);
}

public interface IReceiptOrderLinkStore
{
    Task<IReadOnlyList<ReceiptOrderDecision>> LoadAsync(string accountContext, CancellationToken cancellationToken = default);
    Task SaveDecisionAsync(ReceiptOrderDecision decision, CancellationToken cancellationToken = default);
}

public interface IReceiptDetailsService
{
    Task<ReceiptDetails> GetAsync(string receiptId, CancellationToken cancellationToken = default);
}
