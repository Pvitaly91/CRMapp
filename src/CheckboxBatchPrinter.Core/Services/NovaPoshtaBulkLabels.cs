using System.Security.Cryptography;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

// One carrier response can contain several TTNs and several places for one TTN.
// Associate pages by their printed number, never by the server's page order.
internal sealed class NovaPoshtaBulkLabels(NovaPoshtaReadOnlyClient client, ILabelPdfInspector inspector)
{
    // Shared by all NP accounts in one preparation. Use the usual bulk request unless the carrier returned no body.
    private const int MaximumSingleRequests = 10;
    private int _remainingSingleRequests = MaximumSingleRequests;
    private long _remainingSinglePdfBytes = ShippingLabelHttp.MaximumBytes;

    public async Task<IReadOnlyDictionary<string, ShippingLabelDocument>> ReadAsync(NovaPoshtaConnection connection,
        IReadOnlyList<ShipmentReference> shipments, CancellationToken cancellationToken)
    {
        byte[] pdf;
        try { pdf = await client.ReadMarkingsAsync(connection, shipments, cancellationToken).ConfigureAwait(false); }
        catch (ShippingLabelException exception) when (exception.EmptyNovaPoshtaBulkResponse)
        {
            var byTrackingNumber = shipments.GroupBy(shipment => shipment.TrackingNumber, StringComparer.Ordinal).ToArray();
            if (byTrackingNumber.Length > _remainingSingleRequests)
                throw Invalid($"Нова пошта повернула порожню відповідь для пакета наклейок. Виберіть до {MaximumSingleRequests} ТТН Нової пошти за раз.");

            _remainingSingleRequests -= byTrackingNumber.Length;
            var documents = new Dictionary<string, ShippingLabelDocument>(StringComparer.Ordinal);
            foreach (var group in byTrackingNumber)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var selected = group.ToArray();
                    var singlePdf = await client.ReadMarkingsAsync(connection, selected, cancellationToken).ConfigureAwait(false);
                    _remainingSinglePdfBytes -= singlePdf.LongLength;
                    if (_remainingSinglePdfBytes < 0)
                        throw Invalid("PDF етикеток перевищують 20 МіБ. Виберіть менший пакет.");
                    var inspection = await inspector.InspectAsync(singlePdf, cancellationToken).ConfigureAwait(false);
                    documents.Add(group.Key, Map(connection.Id, selected, singlePdf, inspection)[group.Key]);
                }
                catch (ShippingLabelException singleError)
                {
                    throw new ShippingLabelException(singleError.Kind, $"ТТН {group.Key}: {singleError.Message}")
                    { RetryAtUtc = singleError.RetryAtUtc };
                }
            }
            return documents;
        }

        var bulkInspection = await inspector.InspectAsync(pdf, cancellationToken).ConfigureAwait(false);
        return Map(connection.Id, shipments, pdf, bulkInspection);
    }

    public static IReadOnlyDictionary<string, ShippingLabelDocument> Map(string connectionId,
        IReadOnlyList<ShipmentReference> shipments, byte[] pdf, LabelPdfInspection inspection)
    {
        var requested = shipments.GroupBy(s => s.TrackingNumber, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var pageNumbers = requested.Keys.ToDictionary(number => number, _ => new List<int>(), StringComparer.Ordinal);
        if (inspection.Pages.Count == 0) throw Invalid("Нова пошта повернула PDF без етикеток.");
        foreach (var page in inspection.Pages)
        {
            var numbers = NovaPoshtaDiagnostics.TrackingNumbers(page.Text);
            if (numbers.Length != 1 || !pageNumbers.TryGetValue(numbers[0], out var matchingPages))
                throw Invalid("У PDF є сторінка без номера вибраної ТТН або з іншим номером. Перегляньте вибір накладних.");
            if (!(Math.Abs(page.WidthMm - 100) <= .8 && Math.Abs(page.HeightMm - 100) <= .8 ||
                Math.Abs(page.WidthMm - 101) <= .1 && Math.Abs(page.HeightMm - 101.5) <= .1))
                throw Invalid("Нова пошта повернула PDF непідтримуваного формату наклейок.");
            matchingPages.Add(page.Number);
        }
        var fingerprint = Convert.ToHexString(SHA256.HashData(pdf));
        var retrieved = DateTimeOffset.UtcNow;
        var documents = new Dictionary<string, ShippingLabelDocument>(StringComparer.Ordinal);
        foreach (var (number, selected) in requested)
        {
            var pages = pageNumbers[number];
            if (pages.Count == 0) throw Invalid($"У PDF немає вибраної накладної ТТН {number}.");
            if (selected.Any(s => s.ExpectedPlaces > pages.Count))
                throw Invalid($"У PDF недостатньо етикеток для всіх місць ТТН {number}.");
            documents[number] = new(selected[0], connectionId, "ttn:" + number, pdf, fingerprint, retrieved)
            { Source = "NovaPoshta.Direct.Bulk", SourcePageNumbers = Array.AsReadOnly(pages.ToArray()) };
        }
        return documents;
    }
    private static ShippingLabelException Invalid(string message) => new(LabelFailureKind.InvalidDocument, message);
}
