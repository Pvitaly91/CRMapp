using System.IO;
using System.Printing;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.Tests;

// Explicit opt-in read-only diagnostic. No printer writer, StartDoc, or actual print.
internal static class ReceiptDriverSmoke
{
    public static void Run(string printerName)
    {
        var receipts = Enumerable.Range(1, 20).Select(index =>
        {
            const int width = 384, height = 1050;
            var pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
            for (var x = 0; x < width; x++) pixels[x] = pixels[(height - 1) * width + x] = (byte)index;
            var bitmap = BitmapSource.Create(width, height, 203, 203, PixelFormats.Gray8, null, pixels, width);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream);
            return new PrintReceiptDocument(stream.ToArray(), $"synthetic-{index}", $"TEST-{index:00}");
        }).ToArray();
        var settings = new AppSettings { PrinterName = printerName, PaperWidth = PaperWidth.Custom,
            CustomPaperWidthMm = 57, PrintableWidthMm = 48 };
        using var server = new LocalPrintServer();
        using var queue = new PrintQueue(server, printerName);
        var document = WindowsPrintService.PrepareBatchDocument(queue, receipts, settings);
        var pages = document.Pages.Select(content => content.Child).ToArray();
        var headers = pages.SelectMany(page => page.Children.OfType<TextBlock>()).Select(header => header.Text).ToArray();
        var expected = Enumerable.Range(1, 20).Select(index => $"Замовлення №TEST-{index:00}");
        if (!headers.SequenceEqual(expected) || pages.Sum(page => page.Children.OfType<Image>().Count()) != 20)
            throw new Exception("Prepared driver document lost or repeated a receipt.");
        Console.WriteLine($"PASS readonly receipt driver: 20 unique receipts in order, {pages.Length} pages, one copy; no print job created.");
        Console.WriteLine("Content heights mm: " + string.Join(", ", pages.Select(page => (page.Height * 25.4 / 96).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))));
    }
}
