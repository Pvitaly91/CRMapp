using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public sealed class LabelBatchPreparation(IShippingLabelSource source, IShippingLabelRenderer renderer)
{
    public async Task<LabelPrintBatch> PrepareAsync(IReadOnlyList<MarketplaceOrder> selectedOrders,
        LabelPrintSettings settings, LabelPrinterGeometry geometry, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        if (selectedOrders.Count is 0 or > 100) throw new InvalidOperationException("Виберіть від 1 до 100 замовлень для наклейок.");
        var pages = new List<ShippingLabelPage>();
        var seenShipments = new HashSet<(ShippingCarrier, string, string)>();
        var seenPages = new HashSet<(ShippingCarrier, string, int)>();
        long rasterBytes = 0;
        double decodedBytes = 0;
        foreach (var order in selectedOrders)
        {
            var shipments = ShippingCarrierNames.ForOrder(order);
            if (shipments.Count == 0) throw new ShippingLabelException(LabelFailureKind.MissingDocument, $"Замовлення №{order.Number}: накладну не створено.");
            foreach (var shipment in shipments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seenShipments.Add((shipment.Carrier, shipment.TrackingNumber, shipment.ShipmentId))) continue;
                var document = await source.GetAsync(shipment, cancellationToken);
                if (document.Shipment.TrackingNumber != shipment.TrackingNumber || document.Shipment.Carrier != shipment.Carrier ||
                    document.Shipment.Order != shipment.Order || document.DocumentId.Length == 0)
                    throw new ShippingLabelException(LabelFailureKind.InvalidDocument, "Етикетка належить іншому відправленню.");
                var rendered = await renderer.RenderAsync(document, geometry.DpiX, geometry.DpiY, cancellationToken);
                if (rendered.Count == 0) throw new ShippingLabelException(LabelFailureKind.InvalidDocument, "Документ не має сторінок.");
                foreach (var page in rendered)
                {
                    if (page.Shipment != document.Shipment || page.DocumentId != document.DocumentId || page.Fingerprint != document.Fingerprint || page.ConnectionId != document.ConnectionId)
                        throw new ShippingLabelException(LabelFailureKind.InvalidDocument, "Сторінка не відповідає перевіреному документу.");
                    if (!seenPages.Add((shipment.Carrier, document.DocumentId, page.PageNumber))) continue;
                    _ = LabelPageLayout.Place(page, settings, geometry);
                    rasterBytes += page.Png.Length;
                    decodedBytes += Math.Ceiling(page.WidthMm * geometry.DpiX / 25.4) * Math.Ceiling(page.HeightMm * geometry.DpiY / 25.4) * 4;
                    if (decodedBytes > 256L * 1024 * 1024) throw new InvalidOperationException("Растери пакета перевищують 256 МіБ. Виберіть менший пакет.");
                    if (pages.Count >= 250 || rasterBytes > 80 * 1024 * 1024) throw new InvalidOperationException("Пакет перевищує 250 сторінок або 80 MiB.");
                    pages.Add(page with { Png = page.Png.ToArray() });
                }
            }
        }
        return new(Guid.NewGuid().ToString("N"), Array.AsReadOnly(pages.ToArray()), settings with { })
        { SelectedOrderCount = selectedOrders.Select(o => o.Key).Distinct().Count(), RasterDpiX = geometry.DpiX, RasterDpiY = geometry.DpiY };
    }
}
