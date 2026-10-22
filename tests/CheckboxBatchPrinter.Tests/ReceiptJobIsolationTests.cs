using System.IO;
using System.IO.Compression;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;
using System.Xml.Linq;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class ReceiptJobIsolationTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("STA seven and eight receipts produce separate ordered jobs with raster-only XPS", () => Sta(IndividualJobs)),
        ("STA individual preflight is lazy and rejects corrupt or oversized final receipts", () => Sta(InvalidFinalReceipt)),
        ("STA receipt raster preserves noninteger page bounds and bottom footer at driver DPI", () => Sta(FooterAndBounds)),
        ("STA receipt raster preserves bounded multipage composition and rejects multi-receipt submission", () => Sta(BoundedPages)),
        ("STA receipt raster rejects missing resolution and excessive surface before allocation", () => Sta(InvalidRaster))
    ];

    private static Task Sta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { test(); } catch (Exception exception) { failure = exception; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    private static AppSettings Settings() => new() { PaperWidth = PaperWidth.Mm50, PrintableWidthMm = 48 };

    private static PrintReceiptDocument Receipt(int index, int height = 300)
    {
        const int width = 120;
        var pixels = Enumerable.Repeat((byte)(index * 20), width * height).ToArray();
        var bitmap = BitmapSource.Create(width, height, 203, 203, PixelFormats.Gray8, null, pixels, width);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream);
        return new(stream.ToArray(), $"receipt-{index}", $"ORDER-{index:00}", $"TEST-TTN-{index:00}");
    }

    private static PrintTicket Ticket(double width, double height, PrintTicket? defaults = null, int dpiX = 203, int dpiY = 203)
    {
        var ticket = WindowsPrintService.BuildTicket(defaults, width, height);
        ticket.PageResolution = new PageResolution(dpiX, dpiY);
        return ticket;
    }

    private static void IndividualJobs()
    {
        foreach (var count in new[] { 7, 8 })
        {
            var receipts = Enumerable.Range(1, count).Select(index => Receipt(index, 120 + index)).ToArray();
            var defaults = new PrintTicket { CopyCount = 8, PageOrder = PageOrder.Reverse };
            var sizes = new List<int>();
            var ordered = new List<string>();
            foreach (var document in WindowsPrintService.BuildReceiptDocuments(receipts, Settings(), 3000,
                (width, height) => Ticket(width, height, defaults)))
            {
                Check(document.Pages.Count == 1, "A short receipt unexpectedly contains multiple pages.");
                var original = document.Pages[0].Child;
                var headers = original.Children.OfType<TextBlock>().Select(header => header.Text).ToArray();
                sizes.Add(headers.Length); ordered.AddRange(headers);
                Check(headers.Length == 1 && !original.Children.OfType<Border>().Any(), "A receipt job includes another receipt or a separator.");
                var raster = WindowsPrintService.RasterizeReceiptDocument(document);
                var page = raster.Pages.Single().Child;
                Check(page.Children.Count == 1 && page.Children[0] is Image, "Prepared page still contains vector text or separators.");
                Check(page.Width == original.Width && page.Height == original.Height, "Rasterization changed the page size.");
                Check(((BitmapSource)((Image)page.Children[0]).Source).DpiX == 203 &&
                    ((PrintTicket)page.PrintTicket).PageResolution?.X == 203, "Effective driver resolution was lost.");
                foreach (var ticket in new[] { (PrintTicket)raster.PrintTicket, (PrintTicket)page.PrintTicket })
                    Check(ticket.CopyCount == 1 && ticket.PageOrder == PageOrder.Standard &&
                        ticket.PagesPerSheet == 1 && ticket.PageScalingFactor == 100,
                        "A receipt job may repeat copies, scale or reverse pages.");
                CheckSerializedRaster(raster);
            }
            Check(sizes.SequenceEqual(Enumerable.Repeat(1, count)), "A print job contains more than one receipt or a receipt is missing.");
            Check(ordered.SequenceEqual(receipts.Select(item => ReceiptPrintHeader.Text(item.OrderNumber, item.TrackingText))),
                "Receipt order or TTN text changed between individual jobs.");
            Check(defaults.CopyCount == 8 && defaults.PageOrder == PageOrder.Reverse,
                "Receipt preparation changed the printer defaults.");
        }
    }

    private static void CheckSerializedRaster(FixedDocument document)
    {
        // Serialize only to a temporary file. No PrintQueue or physical job is used.
        var path = Path.Combine(Path.GetTempPath(), "checkbox-grouped-receipts-" + Guid.NewGuid().ToString("N") + ".xps");
        try
        {
            using (var xps = new XpsDocument(path, FileAccess.ReadWrite))
                XpsDocument.CreateXpsDocumentWriter(xps).Write(document, (PrintTicket)document.PrintTicket);
            using (var xps = new XpsDocument(path, FileAccess.Read))
            {
                var serialized = xps.FixedDocumentSequenceReader.FixedDocuments.Single().FixedPages;
                Check(serialized.Count == document.Pages.Count && serialized.All(page => page.PrintTicket?.CopyCount == 1),
                    "Serialized receipt job lost pages or its one-copy ticket.");
            }
            using var package = ZipFile.OpenRead(path);
            Check(!package.Entries.Any(entry => entry.FullName.Contains("/Fonts/", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.EndsWith(".odttf", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)), "Raster XPS still embeds driver font resources.");
            var pages = package.Entries.Where(entry => entry.FullName.EndsWith(".fpage", StringComparison.OrdinalIgnoreCase)).ToArray();
            Check(pages.Length == document.Pages.Count, "Serialized raster page count changed.");
            for (var index = 0; index < pages.Length; index++)
            {
                using var stream = pages[index].Open(); var xml = XDocument.Load(stream);
                Check(!xml.Descendants().Any(element => element.Name.LocalName == "Glyphs"), "Raster XPS still contains Glyphs.");
                var image = xml.Descendants().Single(element => element.Name.LocalName == "ImageBrush");
                var source = (string?)image.Attribute("ImageSource") ?? "";
                var imageUri = new Uri(new Uri("http://xps/" + pages[index].FullName), source);
                var entry = package.GetEntry(Uri.UnescapeDataString(imageUri.AbsolutePath.TrimStart('/')))!;
                using var imageStream = entry.Open();
                // Zip entry streams cannot seek. WPF returns an asynchronous 1x1
                // LateBoundBitmapDecoder placeholder for those streams, even with OnLoad.
                using var imageBytes = new MemoryStream(); imageStream.CopyTo(imageBytes); imageBytes.Position = 0;
                var decoded = BitmapDecoder.Create(imageBytes, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                var expected = (BitmapSource)((Image)document.Pages[index].Child.Children[0]).Source;
                var actual = new FormatConvertedBitmap(decoded, PixelFormats.Gray8, null, 0);
                var expectedPixels = new byte[expected.PixelWidth * expected.PixelHeight];
                var actualPixels = new byte[actual.PixelWidth * actual.PixelHeight];
                expected.CopyPixels(expectedPixels, expected.PixelWidth, 0); actual.CopyPixels(actualPixels, actual.PixelWidth, 0);
                var differing = expectedPixels.Zip(actualPixels).Count(pair => pair.First != pair.Second);
                var maximumDifference = expectedPixels.Zip(actualPixels).Select(pair => Math.Abs(pair.First - pair.Second)).DefaultIfEmpty(0).Max();
                Check(expected.PixelWidth == actual.PixelWidth && expected.PixelHeight == actual.PixelHeight &&
                    expectedPixels.SequenceEqual(actualPixels), $"XPS raster resource changed receipt pixels or lost its footer: " +
                    $"expected {expected.PixelWidth}x{expected.PixelHeight} {expected.Format}; actual {actual.PixelWidth}x{actual.PixelHeight} {decoded.Format}; " +
                    $"resource {entry.FullName}; different pixels {differing}, maximum channel difference {maximumDifference}.");
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void InvalidFinalReceipt()
    {
        var good = Enumerable.Range(1, 3).Select(index => Receipt(index)).ToArray();
        foreach (var bad in new[] { Receipt(4, 2000), new PrintReceiptDocument([1, 2, 3], "corrupt", "ORDER-04") })
        {
            var ticketCalls = 0;
            using var prepared = WindowsPrintService.BuildReceiptDocuments(good.Append(bad).ToArray(), Settings(), 1500,
                (width, height) => { ticketCalls++; return Ticket(width, height); }).GetEnumerator();
            Check(ticketCalls == 0, "Preflight eagerly decoded the entire selection.");
            for (var index = 0; index < good.Length; index++)
                Check(prepared.MoveNext() && ticketCalls == index + 1, "Preflight did not validate one receipt at a time.");
            var rejected = false;
            try { prepared.MoveNext(); }
            catch (Exception) { rejected = true; }
            Check(rejected && ticketCalls == good.Length, "Invalid final receipt passed complete preflight.");
        }
    }

    private static FixedDocument MarkerDocument(int dpiX, int dpiY, double width = 123.45, double height = 78.9)
    {
        var page = new FixedPage { Width = width, Height = height, Background = Brushes.White, PrintTicket = Ticket(width, height, dpiX: dpiX, dpiY: dpiY) };
        var text = new TextBlock { Text = "Order / TTN marker", FontSize = 8, Foreground = Brushes.Black };
        page.Children.Add(text);
        var footer = new Border { Width = width, Height = 2, Background = Brushes.Black };
        FixedPage.SetTop(footer, height - footer.Height); page.Children.Add(footer);
        var document = new FixedDocument { PrintTicket = ((PrintTicket)page.PrintTicket).Clone() };
        var content = new PageContent(); ((IAddChild)content).AddChild(page); document.Pages.Add(content);
        return document;
    }

    private static void FooterAndBounds()
    {
        foreach (var (dpiX, dpiY) in new[] { (203, 203), (300, 203), (300, 300) })
        {
            var document = MarkerDocument(dpiX, dpiY);
            var flattened = WindowsPrintService.RasterizeReceiptDocument(document);
            var page = flattened.Pages.Single().Child;
            var source = (BitmapSource)((Image)page.Children[0]).Source;
            Check(source.PixelWidth == (int)Math.Ceiling(page.Width * dpiX / 96) &&
                source.PixelHeight == (int)Math.Ceiling(page.Height * dpiY / 96) &&
                source.DpiX == dpiX && source.DpiY == dpiY &&
                ((PrintTicket)page.PrintTicket).PageResolution?.X == dpiX &&
                ((PrintTicket)page.PrintTicket).PageResolution?.Y == dpiY,
                "Raster dimensions crop a fractional last row/column or ignore driver DPI.");
            var pixels = new byte[source.PixelWidth * source.PixelHeight]; source.CopyPixels(pixels, source.PixelWidth, 0);
            Check(pixels[(source.PixelHeight - 2) * source.PixelWidth + source.PixelWidth / 2] < 32 &&
                pixels[(source.PixelHeight - 1) * source.PixelWidth + source.PixelWidth / 2] < 250,
                "The bottom footer marker was clipped.");
            Check(pixels[(source.PixelHeight / 2) * source.PixelWidth + source.PixelWidth / 2] == 255,
                "White receipt background was not preserved.");
            CheckSerializedRaster(flattened);
        }
    }

    private static void BoundedPages()
    {
        var receipts = Enumerable.Range(1, 3).Select(index => Receipt(index)).ToArray();
        // The generic composer still supports bounded pages, but the submission
        // entry point must refuse more than one receipt before consulting a driver.
        foreach (var count in new[] { 2, 3, 4 })
        {
            var rejected = false;
            try { _ = WindowsPrintService.PrepareBatchDocument(null!, Enumerable.Repeat(receipts[0], count).ToArray(), Settings()); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "Multi-receipt submission reached the printer driver.");
        }
        var original = WindowsPrintService.BuildBatchDocument(receipts, Settings(), 550, (width, height) => Ticket(width, height));
        Check(original.Pages.Count == 3, "Driver page-height bound was lost inside a group.");
        Check(original.Pages.SelectMany(page => page.Child.Children.OfType<TextBlock>()).Select(header => header.Text)
            .SequenceEqual(Enumerable.Range(1, 3).Select(index => ReceiptPrintHeader.Text($"ORDER-{index:00}", $"TEST-TTN-{index:00}"))),
            "Bounded group page order changed.");
        var flattened = WindowsPrintService.RasterizeReceiptDocument(original);
        Check(flattened.Pages.Count == 3 && flattened.Pages.All(page => page.Child.Height <= 550), "Rasterization changed bounded pagination.");
        CheckSerializedRaster(flattened);
    }

    private static void InvalidRaster()
    {
        var missing = MarkerDocument(203, 203); ((PrintTicket)missing.Pages[0].Child.PrintTicket).PageResolution = null;
        foreach (var invalid in new[] { missing, MarkerDocument(1200, 1200, 2000, 10000) })
        {
            var rejected = false;
            try { _ = WindowsPrintService.RasterizeReceiptDocument(invalid); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Unknown resolution or excessive raster allocation passed preflight.");
        }
    }
}
