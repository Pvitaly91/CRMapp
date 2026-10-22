using System.Globalization;
using System.IO;
using System.Printing;
using System.Windows;
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
            const int width = 384, height = 650;
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                var text = $"CHECKBOX TEST RECEIPT {index:00}\n\nSynthetic shop\n\nProduct A  1 x 100.00\nProduct B  2 x 50.00\n\nTOTAL      200.00 UAH\n\nReceipt ID TEST-{index:00}\n\nFOOTER MARKER {index:00}";
                context.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Consolas"), 20, Brushes.Black, 1), new Point(12, 12));
                context.DrawRectangle(Brushes.Black, null, new Rect(0, height - 3, width, 3));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing); bitmap.Freeze();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream);
            return new PrintReceiptDocument(stream.ToArray(), $"synthetic-{index}", $"TEST-{index:00}", $"TEST-TTN-{index:00}");
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
            if (((PrintTicket)document.PrintTicket).CopyCount != 1)
                throw new Exception("Prepared receipt job changed its copy count.");
            foreach (var content in document.Pages)
            {
                var page = content.Child;
                if (page.Children.Count != 1 || page.Children[0] is not Image image ||
                    ((PrintTicket)page.PrintTicket).CopyCount != 1 ||
                    ((PrintTicket)page.PrintTicket).PageResolution?.X is not > 0)
                    throw new Exception("Prepared receipt page still contains vectors or lost its one-copy driver-resolution ticket.");
                heights.Add(page.Height * 25.4 / 96);
                if (count == 1 && ReferenceEquals(content, document.Pages[0]))
                {
                    var previewPath = Path.GetFullPath(Path.Combine("artifacts", "receipt-group-preview.png"));
                    Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create((BitmapSource)image.Source));
                    using var output = File.Create(previewPath); encoder.Save(output);
                    Console.WriteLine("Preview: " + previewPath);
                    Console.WriteLine($"Raster resolution: {((BitmapSource)image.Source).DpiX} x {((BitmapSource)image.Source).DpiY} dpi");
                }
            }
        }
        var expectedJobs = (receipts.Length + ReceiptPrintPolicy.MaxReceiptsPerJob - 1) / ReceiptPrintPolicy.MaxReceiptsPerJob;
        if (count != expectedJobs) throw new Exception("Prepared receipt job count differs from three-receipt grouping.");
        Console.WriteLine($"PASS readonly receipt driver: {count} ordered jobs for {receipts.Length} receipts in groups of up to three; all pages are full rasters with one copy; no print job created.");
        Console.WriteLine("Content heights mm: " + string.Join(", ", heights.Select(height => height.ToString("0.###", CultureInfo.InvariantCulture))));
    }
}
