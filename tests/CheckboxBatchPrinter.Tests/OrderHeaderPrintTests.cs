using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class OrderHeaderPrintTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("STA printed order number fits above single receipt without cropping at96/203/300dpi", () => Sta(Single)),
        ("STA mixed batch prints each linked number above its own unchanged image without blank headers", () => Sta(Batch)),
        ("STA NP Rozetka and multiple TTNs fit full single-receipt header at96/203/300dpi", () => Sta(SingleTracking)),
        ("STA mixed batch keeps each TTN above its own receipt and omits absent tracking", () => Sta(BatchTracking))
    ];

    private static Task Sta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { test(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
        return Task.CompletedTask;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Near(double a, double b) => Check(Math.Abs(a - b) < 0.01, $"Layout differs: {a} / {b}.");
    private static BitmapSource Source(int width = 180, int height = 240)
    {
        var pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            if (x < 4 || x >= width - 4 || y < 6 || y >= height - 6) pixels[y * width + x] = 0;
        var source = BitmapSource.Create(width, height, 203, 203, PixelFormats.Gray8, null, pixels, width);
        source.Freeze(); return source;
    }
    private static byte[] Png(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    private static void Single()
    {
        foreach (var printableWidth in new[] { 32d, 48d })
        foreach (var number in new[] { "430000001", "ORDER-2026-VERY-LONG-NUMBER-12345678901234567890" })
        {
            var settings = new AppSettings { PaperWidth = PaperWidth.Mm50, PrintableWidthMm = printableWidth };
            var source = Source();
            var page = WindowsPrintService.BuildPage(source, settings, out var width, out var height, number);
            var image = page.Children.OfType<Image>().Single();
            var header = page.Children.OfType<TextBlock>().Single();
            Check(header.Text == "Замовлення №" + number && header.FontSize <= 10, "Order number/font lost.");
            Check(header.ActualHeight > 0 && FixedPage.GetTop(header) + header.ActualHeight < FixedPage.GetTop(image), "Header overlaps receipt.");
            Check(header.ActualWidth <= width && FixedPage.GetTop(image) + image.Height <= height, "Header/body outside paper.");
            Check(ReferenceEquals(source, image.Source), "Receipt source replaced.");
            var geometry = PrintGeometry.Calculate(source.PixelWidth, source.PixelHeight, printableWidth, 50);
            Near(geometry.HeightDip, image.Height); Near(geometry.WidthDip, image.Width);
            var plain = WindowsPrintService.BuildPage(source, settings, out _, out var plainHeight, "  ");
            Check(!plain.Children.OfType<TextBlock>().Any(), "Unlinked receipt has an empty header.");
            Near(height - plainHeight, header.ActualHeight + 0.5 * 96 / 25.4);
            foreach (var dpi in new[] { 96d, 203d, 300d })
            {
                var raster = Raster(page, dpi); VerifyInk(raster, dpi, [image], [header]);
                if (printableWidth == 48 && number == "430000001" && dpi == 203) SaveSample(raster, "printed-order-single.png");
            }
        }
    }

    private static void Batch()
    {
        var png = Png(Source()); var before = png.ToArray();
        var documents = new[] { new PrintReceiptDocument(png, "synthetic-first", "430000001"),
            new PrintReceiptDocument(png, "synthetic-unlinked"), new PrintReceiptDocument(png, "synthetic-third", "430000003") };
        var settings = new AppSettings { PaperWidth = PaperWidth.Mm50, PrintableWidthMm = 48 };
        var page = WindowsPrintService.BuildBatchPage(documents, settings, out var width, out var height);
        var images = page.Children.OfType<Image>().ToArray(); var headers = page.Children.OfType<TextBlock>().ToArray();
        Check(images.Length == 3 && headers.Length == 2 && page.Children.OfType<Border>().Count() == 2, "Mixed page count/separators changed.");
        Check(headers.Select(h => h.Text).SequenceEqual(new[] { "Замовлення №430000001", "Замовлення №430000003" }), "Number attached to wrong receipt.");
        for (var i = 0; i < 3; i++)
        {
            var preceding = page.Children.Cast<UIElement>().TakeWhile(child => child != images[i]).LastOrDefault();
            Check(i == 1 ? preceding is Border : preceding is TextBlock, "Header ordering changed.");
            Near(width, images[i].Width); Near(width * 240 / 180, images[i].Height);
        }
        Check(FixedPage.GetTop(headers[1]) > FixedPage.GetTop(images[1]) + images[1].Height, "Last order number belongs to preceding receipt.");
        var plain = WindowsPrintService.BuildBatchPage(documents.Select(d => d with { OrderNumber = "" }).ToArray(), settings, out _, out var plainHeight);
        Near(height - plainHeight, headers.Sum(h => h.ActualHeight + 0.5 * 96 / 25.4));
        Check(png.SequenceEqual(before) && !plain.Children.OfType<TextBlock>().Any(), "Original PNG or unlinked layout changed.");
        foreach (var dpi in new[] { 96d, 203d, 300d })
        {
            var raster = Raster(page, dpi); VerifyInk(raster, dpi, images, headers);
            if (dpi == 203) SaveSample(raster, "printed-order-batch.png");
        }
    }

    private static RenderTargetBitmap Raster(FixedPage page, double dpi)
    {
        var raster = new RenderTargetBitmap((int)Math.Ceiling(page.Width * dpi / 96), (int)Math.Ceiling(page.Height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        raster.Render(page); return raster;
    }

    private static void SingleTracking()
    {
        var source = Source();
        foreach (var printableWidth in new[] { 32d, 48d })
        foreach (var tracking in new[] { "НП: 20400000000000", "Rozetka Delivery: 100000000000000001",
            "НП: 20400000000000; НП: 20400000000001; Rozetka Delivery: 100000000000000001" })
        {
            var settings = new AppSettings { PaperWidth = PaperWidth.Mm50, PrintableWidthMm = printableWidth };
            var page = WindowsPrintService.BuildPage(source, settings, out var width, out var height,
                "430000001", tracking);
            var image = page.Children.OfType<Image>().Single();
            var header = page.Children.OfType<TextBlock>().Single();
            Check(header.Text == "Замовлення №430000001 · ТТН " + tracking, "TTN changed or truncated.");
            Check(header.FontSize == 10 && header.TextWrapping == TextWrapping.Wrap, "Small wrapping header changed.");
            Check(header.ActualWidth <= width && FixedPage.GetTop(header) + header.ActualHeight < FixedPage.GetTop(image),
                "TTN header cropped or overlaps receipt.");
            Check(FixedPage.GetTop(image) + image.Height <= height && ReferenceEquals(source, image.Source), "Receipt source or edges changed.");
            foreach (var dpi in new[] { 96d, 203d, 300d })
            {
                var raster = Raster(page, dpi);
                VerifyInk(raster, dpi, [image], [header]);
                VerifyLastHeaderLine(raster, dpi, header);
                if (printableWidth == 48 && tracking == "НП: 20400000000000" && dpi == 203)
                    SaveSample(raster, "printed-order-ttn-single.png");
            }
            var unlinked = WindowsPrintService.BuildPage(source, settings, out _, out _, "", tracking);
            Check(!unlinked.Children.OfType<TextBlock>().Any(), "Unlinked receipt printed a TTN header.");
        }
    }

    private static void BatchTracking()
    {
        var png = Png(Source()); var original = png.ToArray();
        var docs = new[] { new PrintReceiptDocument(png, "np", "430000001", "НП: 20400000000000"),
            new PrintReceiptDocument(png, "rz", "430000002", "Rozetka Delivery: 100000000000000001"),
            new PrintReceiptDocument(png, "no-ttn", "430000003"), new PrintReceiptDocument(png, "unlinked") };
        var page = WindowsPrintService.BuildBatchPage(docs,
            new() { PaperWidth = PaperWidth.Mm50, PrintableWidthMm = 48 }, out _, out var height);
        var images = page.Children.OfType<Image>().ToArray(); var headers = page.Children.OfType<TextBlock>().ToArray();
        Check(images.Length == 4 && headers.Length == 3 && page.Children.OfType<Border>().Count() == 3,
            "Batch image/header/separator count changed.");
        var expected = new[] { "Замовлення №430000001 · ТТН НП: 20400000000000",
            "Замовлення №430000002 · ТТН Rozetka Delivery: 100000000000000001", "Замовлення №430000003" };
        Check(headers.Select(h => h.Text).SequenceEqual(expected), "Batch TTN attached to another receipt.");
        for (var i = 0; i < headers.Length; i++)
        {
            Check(FixedPage.GetTop(headers[i]) + headers[i].ActualHeight < FixedPage.GetTop(images[i]), "TTN overlaps its receipt.");
            if (i > 0) Check(FixedPage.GetTop(headers[i]) > FixedPage.GetTop(images[i-1]) + images[i-1].Height,
                "TTN moved above a preceding receipt.");
        }
        Check(FixedPage.GetTop(images[^1]) + images[^1].Height <= height && png.SequenceEqual(original), "Batch PNG changed or bottom cropped.");
        foreach (var dpi in new[] { 96d, 203d, 300d })
        {
            var raster = Raster(page, dpi); VerifyInk(raster, dpi, images, headers);
            foreach (var header in headers) VerifyLastHeaderLine(raster, dpi, header);
            if (dpi == 203) SaveSample(raster, "printed-order-ttn-batch.png");
        }
    }

    private static void VerifyLastHeaderLine(BitmapSource raster, double dpi, TextBlock header)
    {
        var scale = dpi / 96; var stride = raster.PixelWidth * 4; var pixels = new byte[stride * raster.PixelHeight];
        raster.CopyPixels(pixels, stride, 0);
        var ink = 0;
        // Verify the last wrapped line contains actual glyphs, not only a correctly sized file.
        var bottom = FixedPage.GetTop(header) + header.ActualHeight;
        for (var y = (int)((bottom - Math.Min(10, header.ActualHeight * 0.7)) * scale); y < Math.Ceiling(bottom * scale); y++)
        for (var x = 0; x < raster.PixelWidth; x++)
            if (y >= 0 && y < raster.PixelHeight && pixels[y * stride + x * 4] < 140 && pixels[y * stride + x * 4 + 3] > 200) ink++;
        Check(ink > 10, $"Last TTN/order line not rasterized at {dpi}dpi.");
    }

    private static void VerifyInk(BitmapSource raster, double dpi, Image[] images, TextBlock[] headers)
    {
        var scale = dpi / 96; var stride = raster.PixelWidth * 4; var pixels = new byte[stride * raster.PixelHeight];
        raster.CopyPixels(pixels, stride, 0);
        bool Dark(int x, int y) => x >= 0 && x < raster.PixelWidth && y >= 0 && y < raster.PixelHeight &&
            pixels[y * stride + x * 4] < 140 && pixels[y * stride + x * 4 + 1] < 140 && pixels[y * stride + x * 4 + 2] < 140 && pixels[y * stride + x * 4 + 3] > 200;
        foreach (var header in headers)
        {
            var ink = 0;
            for (var y = (int)(FixedPage.GetTop(header) * scale); y < (FixedPage.GetTop(header) + header.ActualHeight) * scale; y++)
            for (var x = 0; x < header.ActualWidth * scale; x++) if (Dark(x, y)) ink++;
            Check(ink > 20, $"Header text not rasterized at {dpi}dpi.");
        }
        foreach (var image in images)
        {
            var left = FixedPage.GetLeft(image); var top = FixedPage.GetTop(image);
            var source = (BitmapSource)image.Source;
            // Inspect actual ink at both horizontal and vertical receipt edges, not only file size.
            foreach (var yDip in new[] { top + 2 * image.Height / source.PixelHeight, top + image.Height - 3 * image.Height / source.PixelHeight })
            {
                var xStart = (int)((left + image.Width * 0.1) * scale); var xEnd = (int)((left + image.Width * 0.9) * scale);
                var dark = Enumerable.Range(xStart, xEnd - xStart).Count(x => Dark(x, (int)(yDip * scale)));
                Check(dark > (xEnd - xStart) * 0.7, $"Top/bottom receipt edge cropped at {dpi}dpi.");
            }
            foreach (var xDip in new[] { left + image.Width / source.PixelWidth, left + image.Width - 2 * image.Width / source.PixelWidth })
            {
                var yStart = (int)((top + image.Height * 0.1) * scale); var yEnd = (int)((top + image.Height * 0.9) * scale);
                var dark = Enumerable.Range(yStart, yEnd - yStart).Count(y => Dark((int)(xDip * scale), y));
                Check(dark > (yEnd - yStart) * 0.7, $"Left/right receipt edge cropped at {dpi}dpi.");
            }
        }
    }

    private static void SaveSample(BitmapSource source, string name)
    {
        var directory = Environment.GetEnvironmentVariable("CHECKBOX_TEST_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
    }
}
