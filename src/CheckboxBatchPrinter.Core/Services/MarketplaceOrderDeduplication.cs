using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

// Session-only equivalence: identical nonempty marketplace API tokens prove
// that enabled connections read the same account.
// Names/order numbers alone do not. Explicitly consolidated historical connections
// additionally resolve through the non-secret mappings saved in marketplace settings.
public sealed class MarketplaceOrderDeduplication
{
    private readonly Dictionary<(MarketplaceKind Marketplace, string ConnectionId), string> _aliases = new();

    public static async Task<MarketplaceOrderDeduplication> ResolveAsync(MarketplaceSettings settings,
        IMarketplaceSecretStore secrets, CancellationToken ct = default)
    {
        var result = new MarketplaceOrderDeduplication();
        var historicalAliases = MarketplaceConnectionAliases.Resolve(settings);
        // Tuple string equality is ordinal; identities stay separate for each marketplace.
        var accounts = new Dictionary<(MarketplaceKind Marketplace, string Login, string Secret), string>();
        foreach (var connection in settings.Connections.Where(c => c.Enabled &&
                         c.Marketplace is MarketplaceKind.Prom or MarketplaceKind.Rozetka)
                     .OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var credential = await secrets.LoadAsync(connection.Id, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                (MarketplaceKind Marketplace, string Login, string Secret) identity;
                if (connection.Marketplace == MarketplaceKind.Prom)
                {
                    if (string.IsNullOrWhiteSpace(credential?.Token) || credential.Token.Any(char.IsControl)) continue;
                    identity = (MarketplaceKind.Prom, "", credential.Token.Trim());
                }
                else
                {
                    if (!RozetkaApiCredentials.ValidToken(credential?.Token)) continue;
                    identity = (MarketplaceKind.Rozetka, "", RozetkaApiCredentials.RequireToken(credential));
                }
                if (!accounts.TryGetValue(identity, out var primary)) accounts[identity] = primary = connection.Id;
                result._aliases[(connection.Marketplace, connection.Id)] = primary;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { /* Unreadable credentials do not prove account equivalence. */ }
        }
        var enabled = settings.Connections.Where(c => c.Enabled).Select(c => (c.Marketplace, c.Id)).ToHashSet();
        foreach (var (source, target) in historicalAliases)
            if (enabled.Contains((source.Marketplace, target)))
                result._aliases[source] = result.NormalizeKey(new(source.Marketplace, target, "")).ConnectionId;
        return result;
    }

    public OrderKey NormalizeKey(OrderKey key)
    {
        if (_aliases.TryGetValue((key.Marketplace, key.ConnectionId), out var primary) ||
            Guid.TryParse(key.ConnectionId, out var id) && _aliases.TryGetValue((key.Marketplace, id.ToString("N")), out primary))
            return key with { ConnectionId = primary };
        return key;
    }

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
