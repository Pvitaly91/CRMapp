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
        var count = 0;
        var heights = new List<double>();
        foreach (var document in WindowsPrintService.PrepareReceiptDocuments(queue, receipts, settings))
        {
            count++;
            var page = document.Pages.Single().Child;
            if (page.Children.OfType<TextBlock>().Single().Text != $"Замовлення №TEST-{count:00}" ||
                page.Children.OfType<Image>().Count() != 1 ||
                ((PrintTicket)document.PrintTicket).CopyCount != 1 || ((PrintTicket)page.PrintTicket).CopyCount != 1)
                throw new Exception("Prepared receipt job lost, repeated or reordered a receipt or changed its copy count.");
            heights.Add(page.Height * 25.4 / 96);
        }
        if (count != receipts.Length) throw new Exception("Prepared receipt job count differs from receipt count.");
        Console.WriteLine($"PASS readonly receipt driver: {count} separate one-receipt documents in order, one page and one copy each; no print job created.");
        Console.WriteLine("Content heights mm: " + string.Join(", ", heights.Select(height => height.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))));
    }
}
