using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.ViewModels;

public sealed partial class ShippingLabelsViewModel
{
    private static bool SameCabinetShipment(ShipmentReference first, ShipmentReference second) =>
        first.Carrier == second.Carrier && first.TrackingNumber == second.TrackingNumber && first.Order == second.Order &&
        (first.ShipmentId.Length == 0 || second.ShipmentId.Length == 0 || first.ShipmentId == second.ShipmentId);

    private static LabelPrintBatch AddCabinetPrintEvidence(LabelPrintBatch batch, IEnumerable<MarketplaceOrderRowViewModel> rows)
    {
        var covered = batch.Pages.SelectMany(page => page.CoveredShipments.Append(page.Shipment)).Distinct().ToArray();
        var evidence = rows.SelectMany(row => row.GetCabinetPrintEvidence())
            .Where(print => covered.Any(shipment => SameCabinetShipment(print.Shipment, shipment)))
            .DistinctBy(print => print.Shipment).ToArray();
        return batch with { CabinetPrintedLabels = Array.AsReadOnly(evidence) };
    }

    private static void ValidateCabinetPrintDisclosure(LabelPrintBatch batch, IEnumerable<MarketplaceOrderRowViewModel> rows)
    {
        var current = AddCabinetPrintEvidence(batch, rows);
        if (current.CabinetPrintedLabels.Any(print => !batch.CabinetPrintedLabels.Any(disclosed =>
            SameCabinetShipment(print.Shipment, disclosed.Shipment))))
            throw new InvalidOperationException("Для цієї накладної з’явилася ознака друку в кабінеті НП. Підготуйте пакет заново й підтвердьте повторний друк.");
    }
}
