using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

// Session-only equivalence: an identical nonempty Prom bearer token proves that
// two enabled connections read the same account. Names/order numbers alone do not.
// No tokens, fingerprints or alias mappings are persisted.
public sealed class MarketplaceOrderDeduplication
{
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);

    public static async Task<MarketplaceOrderDeduplication> ResolveAsync(MarketplaceSettings settings,
        IMarketplaceSecretStore secrets, CancellationToken ct = default)
    {
        var result = new MarketplaceOrderDeduplication();
        var accounts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var connection in settings.Connections.Where(c => c.Enabled && c.Marketplace == MarketplaceKind.Prom)
                     .OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var credential = await secrets.LoadAsync(connection.Id, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(credential?.Token) || credential.Token.Any(char.IsControl)) continue;
                var token = credential.Token.Trim();
                if (!accounts.TryGetValue(token, out var primary)) accounts[token] = primary = connection.Id;
                result._aliases[connection.Id] = primary;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { /* Unreadable credentials do not prove account equivalence. */ }
        }
        return result;
    }

    public OrderKey NormalizeKey(OrderKey key) => key.Marketplace == MarketplaceKind.Prom &&
        _aliases.TryGetValue(key.ConnectionId, out var primary) ? key with { ConnectionId = primary } : key;

    public IReadOnlyList<MarketplaceOrder> Merge(IEnumerable<MarketplaceOrder> orders) => orders
        .Select(order =>
        {
            var key = NormalizeKey(order.Key);
            return order with { Key = key, FiscalReferences = order.FiscalReferences.Select(reference =>
                reference.Order == order.Key ? reference with { Order = key } : reference).ToArray() };
        })
        .GroupBy(order => order.Key)
        .Select(group =>
        {
            var versions = group.OrderByDescending(order => order.RetrievedAtUtc).ToArray();
            var latest = versions[0];
            // Retain every fiscal assertion, including contradictions; do not select
            // whichever duplicate would make a desired link succeed.
            return latest with
            {
                ReceiptIds = versions.SelectMany(o => o.ReceiptIds).Distinct().ToArray(),
                FiscalReceiptNumbers = versions.SelectMany(o => o.FiscalReceiptNumbers).Distinct().ToArray(),
                FiscalReceiptUrls = versions.SelectMany(o => o.FiscalReceiptUrls).Distinct().ToArray(),
                FiscalReferences = versions.SelectMany(o => o.FiscalReferences).Distinct().ToArray()
            };
        }).ToArray();

    public IReadOnlyList<ReceiptOrderDecision> NormalizeDecisions(IEnumerable<ReceiptOrderDecision> decisions) =>
        decisions.Select(decision => new ReceiptOrderDecision
        {
            AccountContext = decision.AccountContext, ReceiptId = decision.ReceiptId,
            ConfirmedOrder = decision.ConfirmedOrder is { } key ? NormalizeKey(key) : null,
            RejectedOrders = decision.RejectedOrders.Select(NormalizeKey).Distinct().ToList(),
            SuppressAutomatic = decision.SuppressAutomatic, UpdatedAtUtc = decision.UpdatedAtUtc
        }).ToArray();
}
