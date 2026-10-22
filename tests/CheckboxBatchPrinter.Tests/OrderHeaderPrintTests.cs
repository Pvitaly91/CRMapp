using System.IO;
using System.IO.Compression;
using System.Printing;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;
using System.Xml.Linq;
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
        ("STA mixed batch keeps each TTN above its own receipt and omits absent tracking", () => Sta(BatchTracking)),
        ("STA receipt tickets force one copy without changing printer defaults", () => Sta(SingleCopyTicket)),
        ("STA long receipt batch is bounded once in order including its final receipt", () => Sta(BoundedBatch)),
        ("STA oversized or corrupt last receipt stops complete packet before page ticket creation", () => Sta(InvalidBoundedBatch)),
        ("STA receipt page limits use driver microns and reject media shrink or repeat settings", () => Sta(DriverPageLimits)),
        ("STA serialized bounded receipt XPS preserves each page size and every order once", () => Sta(SerializedBoundedBatch))
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

    private static void SingleCopyTicket()
    {
        var defaults = new PrintTicket
        {
            CopyCount = 7,
            PageOrder = PageOrder.Reverse,
            PagesPerSheet = 4,
            Duplexing = Duplexing.TwoSidedLongEdge,
            PageScalingFactor = 50,
            PageMediaSize = new PageMediaSize(420, 595),
            PageOrientation = PageOrientation.Landscape,
            OutputColor = OutputColor.Color
        };
        // A batch of any size still has one physical copy, even if the user last
        // printed seven copies from another program with this same Windows printer.
        foreach (var height in new[] { 300d, 9000d })
        {
            var ticket = WindowsPrintService.BuildTicket(defaults, 180, height);
            Check(ticket.CopyCount == 1, "Driver copy count duplicated receipt output.");
            Check(ticket.PageOrder == PageOrder.Standard && ticket.PagesPerSheet == 1 &&
                ticket.Duplexing == Duplexing.OneSided && ticket.PageScalingFactor == 100,
                "Driver defaults reordered, combined or scaled receipt pages.");
            Check(!ReferenceEquals(defaults, ticket), "Receipt ticket reused mutable printer defaults.");
            Near(180, ticket.PageMediaSize!.Width!.Value);
            Near(height, ticket.PageMediaSize.Height!.Value);
            Check(ticket.PageOrientation == PageOrientation.Portrait && ticket.OutputColor == OutputColor.Grayscale,
                "Receipt page settings changed.");
        }
        Check(defaults.CopyCount == 7 && defaults.PageOrientation == PageOrientation.Landscape &&
            defaults.OutputColor == OutputColor.Color && defaults.PageOrder == PageOrder.Reverse &&
            defaults.PagesPerSheet == 4 && defaults.Duplexing == Duplexing.TwoSidedLongEdge && defaults.PageScalingFactor == 50,
            "Receipt printing changed global printer preferences.");
        Near(420, defaults.PageMediaSize!.Width!.Value);
        Near(595, defaults.PageMediaSize.Height!.Value);
        Check(WindowsPrintService.BuildTicket((PrintTicket?)null, 180, 300).CopyCount == 1,
            "A printer without a default ticket must also receive one copy.");
    }

    private static (PrintReceiptDocument[] Receipts, AppSettings Settings, double Limit) LongBatch()
    {
        var receipts = Enumerable.Range(1, 20).Select(index => new PrintReceiptDocument(
            Png(Source(100, 270 + index)), "receipt-" + index, (1000 + index).ToString())).ToArray();
        return (receipts, new AppSettings { PaperWidth = PaperWidth.Mm50, PrintableWidthMm = 48 }, 2527.487 * 96 / 25.4);
    }

    private static void BoundedBatch()
    {
        var (receipts, settings, maximum) = LongBatch();
        var expectedHeaders = receipts.Select(receipt => "Замовлення №" + receipt.OrderNumber).ToArray();
        var document = WindowsPrintService.BuildBatchDocument(receipts, settings, maximum);
        Check(document.Pages.Count == 2, "Synthetic batch must exercise the driver-length boundary.");
        var pages = document.Pages.Select(content => content.Child).ToArray();
        Check(pages.SelectMany(page => page.Children.OfType<TextBlock>()).Select(header => header.Text).SequenceEqual(expectedHeaders),
            "Bounded output duplicated, omitted or reordered an order header.");
        Check(pages.Sum(page => page.Children.OfType<Image>().Count()) == receipts.Length,
            "Bounded output duplicated or omitted a receipt image.");
        foreach (var page in pages)
        {
            Check(page.Height <= maximum && page.Children.OfType<Image>().Any(), "Blank or oversized chunk.");
            var images = page.Children.OfType<Image>().ToArray();
            var headers = page.Children.OfType<TextBlock>().ToArray();
            Check(page.Children.OfType<Border>().Count() == images.Length - 1,
                "Unexpected leading/trailing separator or separator-only page.");
            for (var index = 0; index < images.Length; index++)
            {
                Check(FixedPage.GetTop(headers[index]) + headers[index].ActualHeight <= FixedPage.GetTop(images[index]),
                    "Receipt header moved inside its image.");
                if (index > 0) Check(FixedPage.GetTop(headers[index]) > FixedPage.GetTop(images[index - 1]) + images[index - 1].Height,
                    "Receipt overlaps preceding receipt after pagination.");
            }
            Check(FixedPage.GetTop(images[^1]) + images[^1].Height <= page.Height, "Final receipt extends past the page.");
            var ticket = (PrintTicket)page.PrintTicket;
            Check(ticket.CopyCount == 1 && ticket.PageOrder == PageOrder.Standard, "Page may print repeated or reversed.");
            Near(page.Width, ticket.PageMediaSize!.Width!.Value);
            Near(page.Height, ticket.PageMediaSize.Height!.Value);
            var raster = Raster(page, 96);
            VerifyInk(raster, 96, images, headers);
            SaveSample(raster, page == pages[0] ? "bounded-receipts-first.png" : "bounded-receipts-last.png");
        }
        var shorter = WindowsPrintService.BuildBatchDocument(receipts.Take(2).ToArray(), settings, maximum);
        Check(shorter.Pages.Count == 1, "Short batch introduced an unnecessary page break.");
        var original = WindowsPrintService.BuildBatchPage(receipts.Take(2).ToArray(), settings, out _, out var originalHeight);
        Near(originalHeight, shorter.Pages[0].Child.Height);
    }

    private static void InvalidBoundedBatch()
    {
        var settings = new AppSettings { PaperWidth = PaperWidth.Mm50, PrintableWidthMm = 48 };
        var good = new PrintReceiptDocument(Png(Source()), "good", "1001");
        var tooTall = new PrintReceiptDocument(Png(Source(100, 500)), "tall", "1002");
        var ticketCalls = 0;
        PrintTicket Ticket(double width, double height) { ticketCalls++; return WindowsPrintService.BuildTicket((PrintTicket?)null, width, height); }
        bool rejected = false;
        try { _ = WindowsPrintService.BuildBatchDocument([good, tooTall], settings, 600, Ticket); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && ticketCalls == 0, "An oversized tail receipt allowed partial preparation/submission.");
        rejected = false;
        try { _ = WindowsPrintService.BuildBatchDocument([good, new([1, 2, 3], "corrupt")], settings, 1000, Ticket); }
        catch (Exception) { rejected = true; }
        Check(rejected && ticketCalls == 0, "A corrupt tail receipt allowed partial preparation/submission.");
    }

    private static void DriverPageLimits()
    {
        const string schema = """
            <f:PrintCapabilities xmlns:f="http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework"
                xmlns:k="http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords">
                <f:ParameterDef name="k:PageMediaSizeMediaSizeHeight">
                    <f:Property name="f:MinValue"><f:Value>100098</f:Value></f:Property>
                    <f:Property name="f:MaxValue"><f:Value>2527487</f:Value></f:Property>
                    <f:Property name="f:UnitType"><f:Value>microns</f:Value></f:Property>
                </f:ParameterDef>
            </f:PrintCapabilities>
            """;
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(schema)))
            Near(2527.487 * 96 / 25.4, WindowsPrintService.ReadMaximumPageHeight(input)!.Value);
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(schema.Replace("MediaSizeMediaSizeHeight", "MediaSizeHeight"))))
            Check(WindowsPrintService.ReadMaximumPageHeight(input) is > 0, "Standard height parameter was not recognized.");
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(schema.Replace("microns", "unknown"))))
            Check(WindowsPrintService.ReadMaximumPageHeight(input) is null, "Unknown units silently became millimetres.");
        Near(299.123, WindowsPrintService.ReceiptPageHeightLimit(400, 350, 299.1239));
        var ticket = WindowsPrintService.BuildTicket((PrintTicket?)null, 200, 500);
        WindowsPrintService.ValidateReceiptPageFit(180, 499, ticket, null);
        var smaller = ticket.Clone(); smaller.PageMediaSize = new PageMediaSize(200, 499);
        bool smallerRejected = false;
        try { WindowsPrintService.ValidateReceiptPageFit(180, 500, smaller, null); }
        catch (InvalidOperationException) { smallerRejected = true; }
        Check(smallerRejected, "Driver media shrink was accepted and could clip a receipt.");
        foreach (var change in new Action<PrintTicket>[] { value => value.CopyCount = 2,
            value => value.PageOrder = PageOrder.Reverse, value => value.PagesPerSheet = 2,
            value => value.Duplexing = Duplexing.TwoSidedShortEdge, value => value.PageScalingFactor = 50 })
        {
            var invalid = ticket.Clone(); change(invalid); var rejected = false;
            try { WindowsPrintService.ValidateReceiptPageFit(180, 499, invalid, null); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Driver changed receipt copies, order or scale without stopping print.");
        }
    }

    private static void SerializedBoundedBatch()
    {
        var (receipts, settings, maximum) = LongBatch();
        var document = WindowsPrintService.BuildBatchDocument(receipts, settings, maximum);
        var path = Path.Combine(Path.GetTempPath(), "checkbox-bounded-receipts-" + Guid.NewGuid().ToString("N") + ".xps");
        try
        {
            using (var xps = new XpsDocument(path, FileAccess.ReadWrite))
                XpsDocument.CreateXpsDocumentWriter(xps).Write(document, (PrintTicket)document.PrintTicket);
            using (var xps = new XpsDocument(path, FileAccess.Read))
            {
                var reader = xps.FixedDocumentSequenceReader.FixedDocuments.Single();
                var pages = reader.FixedPages.ToArray();
                Check(pages.Length == 2, "XPS serializer changed the number of bounded pages.");
                for (var index = 0; index < pages.Length; index++)
                {
                    var ticket = pages[index].PrintTicket;
                    Check(ticket is not null && ticket.CopyCount == 1, "XPS omitted the per-page one-copy ticket.");
                    Near(document.Pages[index].Child.Height, ticket!.PageMediaSize!.Height!.Value);
                    Near(document.Pages[index].Child.Width, ticket.PageMediaSize.Width!.Value);
                }
                Check(pages[1].PrintTicket!.PageMediaSize!.Height < pages[0].PrintTicket!.PageMediaSize!.Height,
                    "Last page inherited the first long page size.");
            }
            using var package = ZipFile.OpenRead(path);
            var fixedPages = package.Entries.Where(entry => entry.FullName.EndsWith(".fpage", StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.FullName, StringComparer.Ordinal).ToArray();
            Check(fixedPages.Length == 2, "Serialized XPS contains extra service pages.");
            var text = new List<string>(); var imageCount = 0;
            foreach (var entry in fixedPages)
            {
                using var stream = entry.Open(); var xml = XDocument.Load(stream);
                text.AddRange(xml.Descendants().Where(element => element.Name.LocalName == "Glyphs")
                    .Select(element => (string?)element.Attribute("UnicodeString") ?? ""));
                imageCount += xml.Descendants().Count(element => element.Name.LocalName == "ImageBrush");
            }
            var joined = string.Join(" ", text);
            foreach (var receipt in receipts)
                Check(joined.Split(receipt.OrderNumber, StringSplitOptions.None).Length == 2,
                    "XPS lost or repeated order " + receipt.OrderNumber + ".");
            Check(imageCount == receipts.Length, "XPS lost or repeated a receipt image.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
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
            Check(header.Text == "Замовлення №" + number && header.FontSize == 12, "Order number/enlarged font lost.");
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
            Check(header.FontSize == 12 && header.TextWrapping == TextWrapping.Wrap, "Enlarged wrapping header changed.");
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
