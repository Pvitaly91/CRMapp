using System.IO;
using System.IO.Compression;
using System.Printing;
using System.Windows.Controls;
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
        ("STA twenty receipt jobs contain one ordered receipt and one copy each after XPS serialization", () => Sta(IsolatedJobs)),
        ("STA per-receipt preflight is lazy and rejects corrupt or oversized final receipts", () => Sta(InvalidFinalReceipt))
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
        var pixels = Enumerable.Repeat((byte)index, width * height).ToArray();
        var bitmap = BitmapSource.Create(width, height, 203, 203, PixelFormats.Gray8, null, pixels, width);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream);
        return new(stream.ToArray(), $"receipt-{index}", $"ORDER-{index:00}");
    }

    private static void IsolatedJobs()
    {
        var receipts = Enumerable.Range(1, 20).Select(index => Receipt(index, 300 + index)).ToArray();
        var defaults = new PrintTicket { CopyCount = 8, PageOrder = PageOrder.Reverse };
        var count = 0;
        foreach (var document in WindowsPrintService.BuildReceiptDocuments(receipts, Settings(), 1000,
            (width, height) => WindowsPrintService.BuildTicket(defaults, width, height)))
        {
            count++;
            Check(document.Pages.Count == 1, "One receipt job contains multiple pages.");
            var page = document.Pages[0].Child;
            var header = page.Children.OfType<TextBlock>().Single();
            Check(header.Text == $"Замовлення №ORDER-{count:00}", "Receipt job order changed or a previous receipt was repeated.");
            var image = page.Children.OfType<Image>().Single();
            var source = (BitmapSource)image.Source;
            var pixels = new byte[source.PixelWidth * source.PixelHeight];
            source.CopyPixels(pixels, source.PixelWidth, 0);
            Check(pixels.All(pixel => pixel == count), "A receipt job contains a different receipt image.");
            Check(!page.Children.OfType<Border>().Any(), "Single-receipt jobs contain batch separators.");
            foreach (var ticket in new[] { (PrintTicket)document.PrintTicket, (PrintTicket)page.PrintTicket })
                Check(ticket.CopyCount == 1 && ticket.PageOrder == PageOrder.Standard,
                    "A receipt job may repeat copies or reverse pages.");

            // Serialize to a temporary XPS file only. No PrintQueue or physical job is used.
            var path = Path.Combine(Path.GetTempPath(), "checkbox-isolated-receipt-" + Guid.NewGuid().ToString("N") + ".xps");
            try
            {
                using (var xps = new XpsDocument(path, FileAccess.ReadWrite))
                    XpsDocument.CreateXpsDocumentWriter(xps).Write(document, (PrintTicket)document.PrintTicket);
                using (var xps = new XpsDocument(path, FileAccess.Read))
                {
                    var serialized = xps.FixedDocumentSequenceReader.FixedDocuments.Single().FixedPages.Single();
                    Check(serialized.PrintTicket?.CopyCount == 1,
                        "Serialized receipt job lost its one-copy ticket.");
                }
                using var package = ZipFile.OpenRead(path);
                var fixedPage = package.Entries.Single(entry => entry.FullName.EndsWith(".fpage", StringComparison.OrdinalIgnoreCase));
                using var stream = fixedPage.Open(); var xml = XDocument.Load(stream);
                var text = string.Join("", xml.Descendants().Where(element => element.Name.LocalName == "Glyphs")
                    .Select(element => (string?)element.Attribute("UnicodeString") ?? ""));
                Check(text == header.Text && xml.Descendants().Count(element => element.Name.LocalName == "ImageBrush") == 1,
                    "Serialized receipt job contains another receipt or an extra image.");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
        Check(count == 20 && defaults.CopyCount == 8 && defaults.PageOrder == PageOrder.Reverse,
            "Receipt preparation changed the job count or printer defaults.");
    }

    private static void InvalidFinalReceipt()
    {
        var good = Receipt(1);
        foreach (var bad in new[] { Receipt(2, 2000), new PrintReceiptDocument([1, 2, 3], "corrupt", "ORDER-02") })
        {
            var ticketCalls = 0;
            using var prepared = WindowsPrintService.BuildReceiptDocuments([good, bad], Settings(), 1000,
                (width, height) => { ticketCalls++; return WindowsPrintService.BuildTicket((PrintTicket?)null, width, height); })
                .GetEnumerator();
            Check(ticketCalls == 0, "Preflight eagerly decoded the entire selection.");
            Check(prepared.MoveNext() && ticketCalls == 1, "Preflight did not validate the first individual job.");
            var rejected = false;
            try { prepared.MoveNext(); }
            catch (Exception) { rejected = true; }
            Check(rejected && ticketCalls == 1, "Invalid final receipt passed complete preflight.");
        }
    }
}
