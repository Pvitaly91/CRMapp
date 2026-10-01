using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Infrastructure;
using CheckboxBatchPrinter.Services;
using static CheckboxBatchPrinter.Tests.ShippingLabelsTests;

namespace CheckboxBatchPrinter.Tests;

internal static class NovaPoshtaBulkRenderTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("STA NP shared PDF renders selected pages in order with TTN-local history numbers", () => MarketplaceViewModelTests.StaAsync(SelectedPages)),
        ("STA NP shared PDF rejects duplicate missing or incomplete page selections", () => MarketplaceViewModelTests.StaAsync(InvalidSelections)),
        ("STA NP empty page selection preserves every original page and expected-place checks", () => MarketplaceViewModelTests.StaAsync(AllPages)),
        ("STA NP selected pages preserve PDF size DPI and raster-memory limits", () => MarketplaceViewModelTests.StaAsync(RenderLimits))
    ];

    private const string FirstTtn = "20400000000001", SecondTtn = "20400000000002";

    private static ShippingLabelDocument Document(byte[] pdf, int expectedPages = 0) => new(
        ShippingCarrierNames.ForOrder(Order("bulk-render", ShippingCarrier.NovaPoshta, FirstTtn)).Single(),
        "synthetic-np-account", "synthetic-first-document", pdf, Convert.ToHexString(SHA256.HashData(pdf)),
        DateTimeOffset.UtcNow, expectedPages) { Source = "synthetic-bulk-pdf" };

    private static byte[] Pixels(ShippingLabelPage page)
    {
        using var stream = new MemoryStream(page.Png);
        var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var image = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        return pixels;
    }

    private static void SameRaster(ShippingLabelPage actual, ShippingLabelPage expected)
    {
        Check(actual.WidthMm == expected.WidthMm && actual.HeightMm == expected.HeightMm &&
            Pixels(actual).SequenceEqual(Pixels(expected)), "Selected page raster differs from its original PDF page.");
    }

    private static async Task SelectedPages()
    {
        string[] texts = [FirstTtn + " PLACE 2", SecondTtn + " PLACE 1", FirstTtn + " PLACE 1", SecondTtn + " PLACE 2"];
        var pdf = SyntheticShippingPdf.Create(pages: 4, pageTexts: texts);
        var original = pdf.ToArray();
        var inspection = await new WindowsLabelPdfInspector().InspectAsync(pdf);
        Check(inspection.Pages.Count == texts.Length && inspection.Pages.Select((page, index) =>
            page.Text.Contains(texts[index], StringComparison.Ordinal)).All(found => found),
            "Synthetic PDF does not contain the known distinct page identities.");
        var document = Document(pdf, 2);
        var renderer = new WindowsShippingLabelRenderer();
        var all = await renderer.RenderAsync(document, 203, 300);
        var firstDocument = document with { SourcePageNumbers = [3, 1] };
        var first = await renderer.RenderAsync(firstDocument, 203, 300);
        Check(first.Count == 2 && first.Select(page => page.PageNumber).SequenceEqual([1, 2]),
            "A TTN selection rendered other pages or kept original PDF indices in print history.");
        SameRaster(first[0], all[2]); SameRaster(first[1], all[0]);
        Check(!Pixels(first[0]).SequenceEqual(Pixels(first[1])), "Distinct source pages collapsed to the same image.");
        Check(first.All(page => page.Shipment == document.Shipment && page.DocumentId == document.DocumentId &&
            page.Fingerprint == document.Fingerprint && page.ConnectionId == document.ConnectionId && page.Source == document.Source),
            "Selected pages lost their shipment, source, or print-history identity.");

        var secondDocument = document with
        {
            Shipment = document.Shipment with { TrackingNumber = SecondTtn },
            DocumentId = "synthetic-second-document", SourcePageNumbers = [2, 4]
        };
        var second = await renderer.RenderAsync(secondDocument, 203, 300);
        Check(second.Select(page => page.PageNumber).SequenceEqual([1, 2]) &&
            second.All(page => page.Shipment.TrackingNumber == SecondTtn && page.DocumentId == secondDocument.DocumentId),
            "The next TTN inherited the first TTN's numbering or identity.");
        SameRaster(second[0], all[1]); SameRaster(second[1], all[3]);
        Check(ReferenceEquals(firstDocument.Pdf, pdf) && ReferenceEquals(secondDocument.Pdf, pdf) && pdf.SequenceEqual(original),
            "Rendering split, replaced, or mutated the shared original PDF payload.");
    }

    private static async Task Reject(ShippingLabelDocument document, LabelFailureKind kind = LabelFailureKind.InvalidDocument,
        double dpiX = 203, double dpiY = 203)
    {
        try
        {
            await new WindowsShippingLabelRenderer().RenderAsync(document, dpiX, dpiY);
            throw new Exception("Invalid PDF selection or renderer limit was accepted.");
        }
        catch (ShippingLabelException exception)
        {
            Check(exception.Kind == kind, "Invalid PDF selection or renderer limit was misclassified.");
        }
    }

    private static async Task InvalidSelections()
    {
        var document = Document(SyntheticShippingPdf.Create(pages: 3), 1);
        IReadOnlyList<int>[] invalid = [[0], [-1], [4], [int.MaxValue], [1, 1], [3, 3, 1], [1, 4]];
        foreach (var selection in invalid) await Reject(document with { SourcePageNumbers = selection });
        // Three pages in the shared PDF cannot satisfy two places when this TTN owns only one page.
        await Reject(document with { SourcePageNumbers = [3], ExpectedPages = 2 });
        await Reject(document with { SourcePageNumbers = [3, 1], ExpectedPages = 3 });
        var allSelected = await new WindowsShippingLabelRenderer().RenderAsync(
            document with { SourcePageNumbers = [3, 1, 2], ExpectedPages = 2 }, 203, 203);
        Check(allSelected.Count == 3 && allSelected.Select(page => page.PageNumber).SequenceEqual([1, 2, 3]),
            "Expected places truncated explicitly selected partner or additional pages.");
    }

    private static async Task AllPages()
    {
        var document = Document(SyntheticShippingPdf.Create(pages: 3,
            pageTexts: [FirstTtn + " PAGE 1", FirstTtn + " PAGE 2", FirstTtn + " PARTNER"]), 2);
        Check(document.SourcePageNumbers.Count == 0, "The default selection no longer means all pages.");
        var renderer = new WindowsShippingLabelRenderer();
        var implicitAll = await renderer.RenderAsync(document, 203, 203);
        var explicitEmpty = await renderer.RenderAsync(document with { SourcePageNumbers = [] }, 203, 203);
        Check(implicitAll.Count == 3 && explicitEmpty.Count == 3 &&
            implicitAll.Select(page => page.PageNumber).SequenceEqual([1, 2, 3]),
            "The legacy path dropped partner pages or changed original page order.");
        for (var index = 0; index < implicitAll.Count; index++) SameRaster(explicitEmpty[index], implicitAll[index]);
        await Reject(document with { ExpectedPages = 4 });
        await Reject(Document(SyntheticShippingPdf.Create(pages: 101)) with { SourcePageNumbers = [1] });
    }

    private static async Task RenderLimits()
    {
        await Reject(Document(SyntheticShippingPdf.Create(301, 100)) with { SourcePageNumbers = [1] },
            LabelFailureKind.UnsupportedFormat);
        await Reject(Document(SyntheticShippingPdf.Create(200, 200)) with { SourcePageNumbers = [1] },
            LabelFailureKind.UnsupportedFormat, 1200, 1200);
        try
        {
            await new WindowsShippingLabelRenderer().RenderAsync(
                Document(SyntheticShippingPdf.Create()) with { SourcePageNumbers = [1] }, 95, 203);
            throw new Exception("Selecting a PDF page bypassed DPI validation.");
        }
        catch (InvalidOperationException) { }
    }
}
