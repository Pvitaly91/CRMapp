using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public sealed class CombinedShipmentTrackingService(params IShipmentTrackingService[] services) : IShipmentTrackingService
{
    public async Task<ShipmentTrackingFetchResult> FetchAsync(IReadOnlyList<MarketplaceOrder> orders,
        CancellationToken cancellationToken = default)
    {
        var results = await Task.WhenAll(services.Select(service => service.FetchAsync(orders, cancellationToken)))
            .ConfigureAwait(false);
        return new(results.SelectMany(result => result.Statuses).ToArray(),
            results.SelectMany(result => result.Errors).Distinct(StringComparer.Ordinal).ToArray());
    }
}
