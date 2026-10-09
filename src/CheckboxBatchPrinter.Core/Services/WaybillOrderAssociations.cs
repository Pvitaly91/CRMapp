using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

// An informational relation between existing documents. This does not infer or
// persist a receipt/order link, and an empty result only describes the supplied
// order snapshot; the caller may not have loaded every date or marketplace.
public static class WaybillOrderAssociations
{
    public static IReadOnlyList<MarketplaceOrder> Find(WaybillRecord waybill,
        IReadOnlyList<MarketplaceOrder> orders, ShippingLabelSettings settings)
    {
        if (waybill.Carrier is not (ShippingCarrier.NovaPoshta or ShippingCarrier.RozetkaDelivery)) return [];
        return orders.Where(order => AccountMatches(waybill, order, settings) &&
                order.Shipments.Any(shipment => ShipmentMatches(waybill, shipment)))
            // Canceled orders retain actual shipment identities. Dates, prices,
            // buyer names and receipt links have no role in this relation.
            .OrderByDescending(order => order.CreatedAt)
            .ThenBy(order => order.Number, StringComparer.Ordinal)
            .ThenBy(order => order.Key.Marketplace)
            .ThenBy(order => order.Key.ConnectionId, StringComparer.Ordinal)
            .ThenBy(order => order.Key.OrderId, StringComparer.Ordinal)
            .DistinctBy(order => order.Key)
            .ToArray();
    }

    private static bool ShipmentMatches(WaybillRecord waybill, OrderShipment shipment)
    {
        if (ShippingCarrierNames.FromDocumentedDeliveryField(shipment.Carrier) != waybill.Carrier) return false;
        var number = shipment.TrackingNumber.Trim();
        var exactNumber = number.Length > 0 && waybill.TrackingNumber.Length > 0 &&
            string.Equals(number, waybill.TrackingNumber, StringComparison.Ordinal);
        if (waybill.Carrier == ShippingCarrier.NovaPoshta)
        {
            // NP Ref is an additional consistency check, never a substitute for
            // the public TTN. Typed "ttn:" fallback IDs are not fabricated Refs.
            if (Guid.TryParse(shipment.ShipmentId, out var expected) &&
                Guid.TryParse(waybill.DocumentId, out var actual) && expected != actual) return false;
            return exactNumber;
        }
        // RD's documented shipment ID can be retained while TrackingNumber is
        // the partner carrier's number. Match only the RD document itself.
        return exactNumber || shipment.ShipmentId.Length > 0 && waybill.DocumentId.Length > 0 &&
            string.Equals(shipment.ShipmentId, waybill.DocumentId, StringComparison.Ordinal);
    }

    private static bool AccountMatches(WaybillRecord waybill, MarketplaceOrder order, ShippingLabelSettings settings)
    {
        if (waybill.Carrier != ShippingCarrier.NovaPoshta) return true;
        var bindings = settings.NovaPoshtaStoreBindings
            .Where(binding => binding.MarketplaceConnectionId == order.Key.ConnectionId).ToArray();
        // A carrier TTN is globally unique. With no documented store binding,
        // exact shipment identity is useful even for an otherwise unlinked order.
        if (bindings.Length == 0) return true;
        if (bindings.Length != 1 || bindings[0].NovaPoshtaConnectionId.Length == 0 || waybill.ConnectionId.Length == 0) return false;
        var expectedId = bindings[0].NovaPoshtaConnectionId;
        if (expectedId == waybill.ConnectionId) return true;
        // Enumeration reads an account once when several aliases share a key.
        // Accept those aliases, never a different account with an equal name.
        var expected = settings.NovaPoshtaConnections.Where(connection => connection.Id == expectedId).ToArray();
        var actual = settings.NovaPoshtaConnections.Where(connection => connection.Id == waybill.ConnectionId).ToArray();
        return expected.Length == 1 && actual.Length == 1 && !string.IsNullOrWhiteSpace(expected[0].ApiKey) &&
            string.Equals(expected[0].ApiKey, actual[0].ApiKey, StringComparison.Ordinal);
    }
}
