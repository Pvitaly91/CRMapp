using System.Globalization;
using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Services;

public sealed partial class WindowsPrintService
{
    internal static IEnumerable<FixedDocument> PrepareReceiptDocuments(PrintQueue queue,
        IReadOnlyList<PrintReceiptDocument> receipts, AppSettings settings)
    {
        if (receipts.Count == 0) throw new ArgumentException("Пачка друку порожня.", nameof(receipts));
        var (maximumHeight, pageTicket) = PrepareReceiptDriver(queue, settings);
        return BuildReceiptDocuments(receipts, settings, maximumHeight, pageTicket)
            .Select(RasterizeReceiptDocument);
    }

    internal static FixedDocument PrepareBatchDocument(PrintQueue queue,
        IReadOnlyList<PrintReceiptDocument> receipts, AppSettings settings)
    {
        if (receipts.Count == 0) throw new ArgumentException("Пачка друку порожня.", nameof(receipts));
        if (receipts.Count > ReceiptPrintPolicy.MaxReceiptsPerJob)
            throw new ArgumentException("Одне завдання може містити не більше трьох чеків.", nameof(receipts));
        var (maximumHeight, pageTicket) = PrepareReceiptDriver(queue, settings);
        return RasterizeReceiptDocument(BuildBatchDocument(receipts, settings, maximumHeight, pageTicket));
    }

    private static (double MaximumHeight, Func<double, double, PrintTicket> PageTicket) PrepareReceiptDriver(
        PrintQueue queue, AppSettings settings)
    {
        var defaults = queue.DefaultPrintTicket;
        var capabilities = queue.GetPrintCapabilities(defaults);
        using var capabilityXml = queue.GetPrintCapabilitiesAsXml(defaults);
        var declaredMaximum = ReadMaximumPageHeight(capabilityXml);
        var width = MillimetersToDip(settings.PrintableWidthMm);
        var advertisedHeights = capabilities.PageMediaSizeCapability
            .Select(size => size.Height).Where(height => height is > 0 && double.IsFinite(height.Value))
            .Select(height => height!.Value).ToArray();
        var maximum = declaredMaximum ?? (advertisedHeights.Length > 0 ? advertisedHeights.Max() : defaults?.PageMediaSize?.Height);
        if (maximum is not > 0 || !double.IsFinite(maximum.Value))
            throw new InvalidOperationException("Драйвер принтера чеків не надав допустиму довжину сторінки. Перевірте формат паперу.");

        var maximumTicket = queue.MergeAndValidatePrintTicket(defaults, BuildTicket(defaults, width, maximum.Value)).ValidatedPrintTicket;
        var validatedMaximum = queue.GetPrintCapabilities(maximumTicket);
        maximum = ReceiptPageHeightLimit(maximum.Value, maximumTicket.PageMediaSize?.Height,
            validatedMaximum.PageImageableArea?.ExtentHeight);

        return (maximum.Value, (pageWidth, pageHeight) =>
        {
            var requested = BuildTicket(defaults, pageWidth, pageHeight);
            var validated = queue.MergeAndValidatePrintTicket(defaults, requested).ValidatedPrintTicket;
            var frame = queue.GetPrintCapabilities(validated);
            ValidateReceiptPageFit(pageWidth, pageHeight, validated, frame.PageImageableArea);
            // Rasterize at the effective driver's DPI, including application-added text.
            // If only capability resolutions are available, explicitly select one and
            // validate it before allocating the surface or submitting any job.
            requested.PageResolution = ResolveReceiptResolution(validated, frame);
            validated = queue.MergeAndValidatePrintTicket(defaults, requested).ValidatedPrintTicket;
            frame = queue.GetPrintCapabilities(validated);
            ValidateReceiptPageFit(pageWidth, pageHeight, validated, frame.PageImageableArea);
            if (validated.PageResolution is not { X: > 0, Y: > 0 } effectiveResolution)
                throw new InvalidOperationException("Драйвер не підтвердив роздільну здатність друку чеків. Пакет не передано принтеру.");
            requested.PageResolution = new PageResolution(effectiveResolution.X.Value, effectiveResolution.Y.Value);
            // Keep the compact content height in the page-level ticket. Some roll drivers
            // report their maximum feed length for every validated ticket; imposing that
            // maximum on the XPS FixedPage would introduce metres of blank paper.
            return requested;
        });
    }

    private static void WriteReceiptDocument(PrintQueue queue, FixedDocument document, string jobName)
    {
        // One Write submits one Windows job and the application does not retry it here.
        // The driver/printer may still restart that job after an interruption such as
        // paper-out. Receipt UI routes submit at most three receipts per job to limit replays.
        queue.CurrentJobSettings.Description = jobName;
        var writer = PrintQueue.CreateXpsDocumentWriter(queue);
        writer.Write(document, (PrintTicket)document.PrintTicket);
    }

    internal static IEnumerable<FixedDocument> BuildReceiptDocuments(IReadOnlyList<PrintReceiptDocument> receipts,
        AppSettings settings, double maximumPageHeight, Func<double, double, PrintTicket>? pageTicket = null)
    {
        if (receipts.Count == 0) throw new ArgumentException("Пачка друку порожня.", nameof(receipts));
        // Preflight uses the same contiguous groups as submission. Each group may
        // contain several bounded pages when the driver's feed length is small.
        for (var offset = 0; offset < receipts.Count; offset += ReceiptPrintPolicy.MaxReceiptsPerJob)
            yield return BuildBatchDocument(receipts.Skip(offset).Take(ReceiptPrintPolicy.MaxReceiptsPerJob).ToArray(),
                settings, maximumPageHeight, pageTicket);
    }

    internal static double ReceiptPageHeightLimit(double declared, double? validated, double? imageable)
    {
        var values = new[] { (double?)declared, validated, imageable }
            .Where(value => value is > 0 && double.IsFinite(value.Value)).Select(value => value!.Value).ToArray();
        if (values.Length == 0) throw new InvalidOperationException("Невідома допустима довжина сторінки принтера чеків.");
        // Downward rounding keeps conversion between microns and DIP inside the limit.
        return Math.Floor(values.Min() * 1000) / 1000;
    }

    internal static void ValidateReceiptPageFit(double width, double height, PrintTicket validated, PageImageableArea? imageable)
    {
        var media = validated.PageMediaSize;
        if (media?.Width is not > 0 || media.Height is not > 0 ||
            !double.IsFinite(media.Width.Value) || !double.IsFinite(media.Height.Value) ||
            width > media.Width.Value + 0.01 || height > media.Height.Value + 0.01 ||
            imageable is not null && (width > imageable.ExtentWidth + 0.01 || height > imageable.ExtentHeight + 0.01))
            throw new InvalidOperationException("Драйвер принтера зменшив область друку чеків. Пакет не передано, щоб не обрізати чеки.");
        if (validated.CopyCount is > 1 || validated.PagesPerSheet is > 1 || validated.PageOrder == PageOrder.Reverse ||
            validated.Duplexing is Duplexing.TwoSidedLongEdge or Duplexing.TwoSidedShortEdge ||
            validated.PageScalingFactor is { } scale && scale != 100)
            throw new InvalidOperationException("Драйвер змінив кількість копій або порядок чеків. Перевірте налаштування принтера.");
    }

    internal static double? ReadMaximumPageHeight(Stream capabilityXml)
    {
        using var reader = XmlReader.Create(capabilityXml, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CloseInput = false });
        var document = XDocument.Load(reader);
        XNamespace framework = "http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework";
        const string keywords = "http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords";
        bool Named(XElement element, string expectedNamespace, params string[] localNames)
        {
            var name = (string?)element.Attribute("name") ?? "";
            var separator = name.IndexOf(':');
            if (separator <= 0) return false;
            return element.GetNamespaceOfPrefix(name[..separator])?.NamespaceName == expectedNamespace &&
                localNames.Contains(name[(separator + 1)..], StringComparer.Ordinal);
        }
        foreach (var parameter in document.Descendants(framework + "ParameterDef")
            .Where(element => Named(element, keywords, "PageMediaSizeHeight", "PageMediaSizeMediaSizeHeight")))
        {
            var properties = parameter.Elements(framework + "Property").ToArray();
            var unit = properties.FirstOrDefault(element => Named(element, framework.NamespaceName, "UnitType"))?
                .Element(framework + "Value")?.Value.Trim();
            if (!string.Equals(unit, "microns", StringComparison.OrdinalIgnoreCase)) continue;
            var value = properties.FirstOrDefault(element => Named(element, framework.NamespaceName, "MaxValue"))?
                .Element(framework + "Value")?.Value.Trim();
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var microns) &&
                double.IsFinite(microns) && microns > 0) return microns * 96 / 25400;
        }
        return null;
    }

    internal static FixedDocument BuildBatchDocument(IReadOnlyList<PrintReceiptDocument> receipts, AppSettings settings,
        double maximumPageHeight, Func<double, double, PrintTicket>? pageTicket = null)
    {
        if (receipts.Count == 0) throw new ArgumentException("Пачка друку порожня.", nameof(receipts));
        if (!double.IsFinite(maximumPageHeight) || maximumPageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPageHeight));
        // A distinct page owns each bitmap/header. We move complete children into bounded
        // chunks, never re-use a shared visual or translate a huge page repeatedly.
        var single = receipts.Select(receipt => BuildBatchPage([receipt], settings, out _, out _)).ToArray();
        if (single.Any(page => page.Height > maximumPageHeight))
            throw new InvalidOperationException("Один із чеків довший за допустиму сторінку принтера. Пакет не передано; перевірте формат паперу.");
        var margin = MillimetersToDip(0.6);
        var padding = MillimetersToDip(0.8);
        var thickness = MillimetersToDip(0.3);
        var separatorHeight = padding * 2 + thickness;
        var chunks = new List<List<FixedPage>>();
        var current = new List<FixedPage>();
        var height = margin * 2;
        foreach (var receipt in single)
        {
            var additional = receipt.Height - margin * 2 + (current.Count == 0 ? 0 : separatorHeight);
            if (current.Count > 0 && height + additional > maximumPageHeight)
            {
                chunks.Add(current); current = []; height = margin * 2;
                additional = receipt.Height - margin * 2;
            }
            current.Add(receipt); height += additional;
        }
        if (current.Count > 0) chunks.Add(current);

        var document = new FixedDocument();
        foreach (var chunk in chunks)
        {
            var width = chunk.Max(page => page.Width);
            var pageHeight = margin * 2 + chunk.Sum(page => page.Height - margin * 2) + separatorHeight * (chunk.Count - 1);
            var page = new FixedPage { Width = width, Height = pageHeight, Background = Brushes.White };
            var top = margin;
            for (var index = 0; index < chunk.Count; index++)
            {
                var receipt = chunk[index];
                foreach (UIElement child in receipt.Children.Cast<UIElement>().ToArray())
                {
                    receipt.Children.Remove(child);
                    FixedPage.SetTop(child, FixedPage.GetTop(child) - margin + top);
                    page.Children.Add(child);
                }
                top += receipt.Height - margin * 2;
                if (index == chunk.Count - 1) continue;
                top += padding;
                var separator = new Border { Width = width * 0.82, Height = thickness, Background = Brushes.Black, SnapsToDevicePixels = true };
                FixedPage.SetLeft(separator, width * 0.09); FixedPage.SetTop(separator, top);
                page.Children.Add(separator); top += thickness + padding;
            }
            page.Measure(new Size(width, pageHeight));
            page.Arrange(new Rect(0, 0, width, pageHeight)); page.UpdateLayout();
            page.PrintTicket = pageTicket?.Invoke(width, pageHeight) ?? BuildTicket((PrintTicket?)null, width, pageHeight);
            var content = new PageContent(); ((IAddChild)content).AddChild(page); document.Pages.Add(content);
        }
        document.PrintTicket = ((PrintTicket)document.Pages[0].Child.PrintTicket).Clone();
        return document;
    }
}
