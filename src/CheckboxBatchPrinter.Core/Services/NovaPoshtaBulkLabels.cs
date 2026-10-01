using System.Security.Cryptography;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

// One carrier response can contain several TTNs and several places for one TTN.
// Associate pages by their printed number, never by the server's page order.
internal static class NovaPoshtaBulkLabels
{
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
