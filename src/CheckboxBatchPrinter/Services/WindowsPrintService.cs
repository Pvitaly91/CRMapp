using System.IO;
using System.Printing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Services;

public sealed partial class WindowsPrintService : IPrintService
{
    private readonly IAppLogger? _logger;
    public WindowsPrintService(IAppLogger? logger = null) => _logger = logger;

    // The queue name is user-editable; only the actual supported driver selects RAW.
    internal static bool UsesDirectReceiptRaster(string? driverName) =>
        WindowsRawPrinter.IsSupportedDriver(driverName);

    public IReadOnlyList<string> GetInstalledPrinters()
    {
        try
        {
            return EnumeratePrinterNames()
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        catch (Exception exception) when (exception is SystemException or InvalidOperationException) { return []; }
    }

    public bool PrinterExists(string printerName) =>
        GetInstalledPrinters().Contains(printerName, StringComparer.CurrentCultureIgnoreCase);

    public void ValidateReceiptsForPrint(IReadOnlyList<PrintReceiptDocument> receipts, AppSettings settings)
    {
        if (receipts.Count == 0) throw new ArgumentException("Пачка друку порожня.", nameof(receipts));
        using var server = new LocalPrintServer();
        using var queue = FindQueue(server, settings.PrinterName);
        if (UsesDirectReceiptRaster(queue.QueueDriver.Name))
        {
            for (var offset = 0; offset < receipts.Count; offset += ReceiptPrintPolicy.MaxReceiptsPerJob)
                _ = EscPosReceiptEncoder.Build(receipts.Skip(offset).Take(ReceiptPrintPolicy.MaxReceiptsPerJob).ToArray(), settings);
            return;
        }
        // Validate every eventual job before the caller starts submitting any receipt.
        // Do not retain all decoded bitmap documents for the entire selection.
        foreach (var document in PrepareReceiptDocuments(queue, receipts, settings))
            _ = document;
    }

    public Task PrintReceiptAsync(PrintReceiptDocument receipt, AppSettings settings, CancellationToken cancellationToken = default) =>
        PrintReceiptsAsSingleJobAsync([receipt], settings, cancellationToken);

    public async Task PrintReceiptsAsSingleJobAsync(
        IReadOnlyList<PrintReceiptDocument> receipts,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (receipts.Count == 0) return;
        if (receipts.Count > ReceiptPrintPolicy.MaxReceiptsPerJob)
            throw new ArgumentException("Одне завдання може містити лише один чек.", nameof(receipts));
        var printerName = settings.PrinterName;
        var jobName = $"Checkbox чек {receipts[0].ReceiptId}";
        var raw = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var server = new LocalPrintServer();
            using var queue = FindQueue(server, printerName);
            if (UsesDirectReceiptRaster(queue.QueueDriver.Name))
                return EscPosReceiptEncoder.Build(receipts, settings);
            var document = PrepareBatchDocument(queue, receipts, settings);
            cancellationToken.ThrowIfCancellationRequested();
            WriteReceiptDocument(queue, document, jobName);
            return (byte[]?)null;
        });
        if (raw is not null)
            await SubmitDirectReceiptAsync(printerName, raw, jobName, receipts.Count, cancellationToken);
    }

    private async Task SubmitDirectReceiptAsync(string printerName, byte[] raw, string jobName, int count, CancellationToken cancellationToken)
    {
        // Native spool calls may block. Only bitmap preparation requires the STA/UI thread.
        var job = await Task.Run(() => WindowsRawPrinter.Write(printerName, raw, jobName, cancellationToken), cancellationToken);
        try { _logger?.Info("receipt.raw.accepted", printStatus: $"job={job};count={count};bytes={raw.Length};driver=RPP210;dpi=203;head_width=384"); }
        catch (Exception) { /* A logging failure must never turn an accepted job into a retry. */ }
    }

    public async Task PrintTestAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var printerName = settings.PrinterName;
        var raw = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var server = new LocalPrintServer();
            using var queue = FindQueue(server, printerName);
            var width = MillimetersToDip(settings.PrintableWidthMm);
            var edgeLabels = new Grid { Width = width };
            edgeLabels.ColumnDefinitions.Add(new ColumnDefinition());
            edgeLabels.ColumnDefinitions.Add(new ColumnDefinition());
            edgeLabels.Children.Add(new TextBlock { Text = "ЛІВИЙ КРАЙ", FontSize = 8, FontWeight = FontWeights.Bold });
            var rightLabel = new TextBlock { Text = "ПРАВИЙ КРАЙ", FontSize = 8, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Right };
            Grid.SetColumn(rightLabel, 1);
            edgeLabels.Children.Add(rightLabel);
            var panel = new StackPanel
            {
                Width = width,
                Background = Brushes.White,
                Children =
                {
                    new Border { Width = width, Height = MillimetersToDip(0.5), Background = Brushes.Black },
                    edgeLabels,
                    new TextBlock { Text = "CHECKBOX BATCH PRINTER", FontSize = 14, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center, Margin = new Thickness(3, 6, 3, 2) },
                    new TextBlock { Text = "Тестовий друк", FontSize = 12, TextAlignment = TextAlignment.Center, Margin = new Thickness(3) },
                    new TextBlock { Text = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"), FontSize = 10, TextAlignment = TextAlignment.Center, Margin = new Thickness(3) },
                    new TextBlock { Text = $"Папір: {settings.EffectivePaperWidthMm:0.#} мм\nОбласть: {settings.PrintableWidthMm:0.#} мм", FontSize = 9, TextAlignment = TextAlignment.Center, Margin = new Thickness(3) },
                    new Border { Width = width, Height = MillimetersToDip(0.5), Background = Brushes.Black }
                }
            };
            panel.Measure(new Size(width, double.PositiveInfinity));
            // Leave only enough paper to separate the print from the tear edge.
            // A fixed 45 mm page left a large blank area after the test content.
            var height = panel.DesiredSize.Height + MillimetersToDip(2);
            var page = new FixedPage { Width = width, Height = height, Background = Brushes.White };
            FixedPage.SetLeft(panel, 0);
            FixedPage.SetTop(panel, 0);
            page.Children.Add(panel);
            page.Measure(new Size(width, height));
            page.Arrange(new Rect(0, 0, width, height));
            page.UpdateLayout();
            if (UsesDirectReceiptRaster(queue.QueueDriver.Name))
            {
                // Render this synthetic test at native dots once, then use the exact same
                // encoder and spool path as actual receipts, without issuing a fiscal receipt.
                var dots = (int)Math.Round(settings.PrintableWidthMm * 8);
                var rows = (int)Math.Ceiling(height * dots / width);
                var preview = new RenderTargetBitmap(dots, rows, dots * 96d / width, dots * 96d / width, PixelFormats.Pbgra32);
                preview.Render(page); preview.Freeze();
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(preview));
                using var stream = new MemoryStream(); png.Save(stream);
                return EscPosReceiptEncoder.Build([new PrintReceiptDocument(stream.ToArray(), "synthetic-test")], settings);
            }
            var (maximumHeight, pageTicket) = PrepareReceiptDriver(queue, settings);
            if (height > maximumHeight)
                throw new InvalidOperationException("Тестовий чек довший за допустиму сторінку принтера. Перевірте формат паперу.");
            page.PrintTicket = pageTicket(width, height);
            var document = new FixedDocument { PrintTicket = ((PrintTicket)page.PrintTicket).Clone() };
            var content = new PageContent(); ((IAddChild)content).AddChild(page); document.Pages.Add(content);
            cancellationToken.ThrowIfCancellationRequested();
            WriteReceiptDocument(queue, RasterizeReceiptDocument(document), "Checkbox Batch Printer — тест");
            return (byte[]?)null;
        });
        if (raw is not null)
            await SubmitDirectReceiptAsync(printerName, raw, "Checkbox Batch Printer — тест", 1, cancellationToken);
    }

    private static PrintQueue FindQueue(LocalPrintServer server, string printerName)
    {
        try { return new PrintQueue(server, printerName); }
        catch (PrintSystemException exception)
        {
            throw new InvalidOperationException($"Принтер «{printerName}» не знайдено.", exception);
        }
    }

    internal static FixedPage BuildPage(BitmapSource source, AppSettings settings, out double pageWidth, out double pageHeight,
        string orderNumber = "", string trackingText = "")
    {
        var geometry = PrintGeometry.Calculate(source.PixelWidth, source.PixelHeight, settings.PrintableWidthMm, settings.EffectivePaperWidthMm);
        // Size the print ticket to the printable head width. The physical roll
        // can be wider, but centering on that width shifts or clips the image.
        pageWidth = geometry.WidthDip;
        var header = CreateOrderHeader(pageWidth, orderNumber, trackingText);
        var headerHeight = OrderHeaderHeight(header);
        pageHeight = geometry.HeightDip + geometry.MarginDip * 2 + headerHeight;
        var image = new Image
        {
            Source = source,
            Width = geometry.WidthDip,
            Height = geometry.HeightDip,
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        FixedPage.SetLeft(image, 0);
        FixedPage.SetTop(image, geometry.MarginDip + headerHeight);
        var page = new FixedPage { Width = pageWidth, Height = pageHeight, Background = Brushes.White };
        if (header is not null)
        {
            FixedPage.SetLeft(header, 0);
            FixedPage.SetTop(header, geometry.MarginDip);
            page.Children.Add(header);
        }
        page.Children.Add(image);
        page.Measure(new Size(pageWidth, pageHeight));
        page.Arrange(new Rect(0, 0, pageWidth, pageHeight));
        page.UpdateLayout();
        return page;
    }

    internal static FixedPage BuildBatchPage(
        IReadOnlyList<PrintReceiptDocument> receipts,
        AppSettings settings,
        out double pageWidth,
        out double pageHeight)
    {
        if (receipts.Count == 0) throw new ArgumentException("Пачка друку порожня.", nameof(receipts));

        var sources = receipts.Select(item => DecodeGrayscale(item.Png)).ToArray();
        var geometries = sources.Select(source =>
            PrintGeometry.Calculate(source.PixelWidth, source.PixelHeight,
                settings.PrintableWidthMm, settings.EffectivePaperWidthMm, marginMm: 0)).ToArray();

        pageWidth = geometries.Max(item => item.WidthDip);
        var headerWidth = pageWidth;
        var headers = receipts.Select(item => CreateOrderHeader(headerWidth, item.OrderNumber, item.TrackingText)).ToArray();
        var outerMargin = MillimetersToDip(0.6);
        var separatorPadding = MillimetersToDip(0.8);
        var separatorThickness = MillimetersToDip(0.3);
        var separatorHeight = separatorPadding * 2 + separatorThickness;
        pageHeight = outerMargin * 2 + geometries.Sum(item => item.HeightDip) + headers.Sum(OrderHeaderHeight) +
                     separatorHeight * (receipts.Count - 1);

        var page = new FixedPage { Width = pageWidth, Height = pageHeight, Background = Brushes.White };
        var top = outerMargin;
        for (var index = 0; index < sources.Length; index++)
        {
            var geometry = geometries[index];
            if (headers[index] is { } header)
            {
                FixedPage.SetLeft(header, 0);
                FixedPage.SetTop(header, top);
                page.Children.Add(header);
                top += OrderHeaderHeight(header);
            }
            var image = new Image
            {
                Source = sources[index],
                Width = geometry.WidthDip,
                Height = geometry.HeightDip,
                Stretch = Stretch.Fill,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            FixedPage.SetLeft(image, Math.Max(0, (pageWidth - geometry.WidthDip) / 2));
            FixedPage.SetTop(image, top);
            page.Children.Add(image);
            top += geometry.HeightDip;

            if (index == sources.Length - 1) continue;
            top += separatorPadding;
            var separator = new Border
            {
                Width = pageWidth * 0.82,
                Height = separatorThickness,
                Background = Brushes.Black,
                SnapsToDevicePixels = true
            };
            FixedPage.SetLeft(separator, pageWidth * 0.09);
            FixedPage.SetTop(separator, top);
            page.Children.Add(separator);
            top += separatorThickness + separatorPadding;
        }

        page.Measure(new Size(pageWidth, pageHeight));
        page.Arrange(new Rect(0, 0, pageWidth, pageHeight));
        page.UpdateLayout();
        return page;
    }

    private static TextBlock? CreateOrderHeader(double width, string orderNumber, string trackingText)
    {
        var text = ReceiptPrintHeader.Text(orderNumber, trackingText);
        if (text.Length == 0) return null;
        var header = new TextBlock
        {
            Text = text,
            Width = width,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = ReceiptPrintHeader.FontSizeDip,
            FontWeight = FontWeights.Normal,
            Foreground = Brushes.Black,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        header.Measure(new Size(width, double.PositiveInfinity));
        return header;
    }

    private static double OrderHeaderHeight(TextBlock? header) =>
        header is null ? 0 : header.DesiredSize.Height + MillimetersToDip(0.5);

    private static PrintTicket BuildTicket(PrintQueue queue, double widthDip, double heightDip) =>
        BuildTicket(queue.DefaultPrintTicket, widthDip, heightDip);

    internal static PrintTicket BuildTicket(PrintTicket? defaultTicket, double widthDip, double heightDip)
    {
        var ticket = defaultTicket?.Clone() ?? new PrintTicket();
        ticket.PageMediaSize = new PageMediaSize(widthDip, heightDip);
        ticket.PageOrientation = PageOrientation.Portrait;
        ticket.OutputColor = OutputColor.Grayscale;
        ticket.OutputQuality = OutputQuality.High;
        // Each confirmed receipt occurs once in the prepared document. A driver default
        // from another program must not silently duplicate the entire receipt batch.
        ticket.CopyCount = 1;
        ticket.PagesPerSheet = 1;
        ticket.PageOrder = PageOrder.Standard;
        ticket.Duplexing = Duplexing.OneSided;
        ticket.PageScalingFactor = 100;
        return ticket;
    }

    private static BitmapSource DecodeGrayscale(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if (frame.Format == PixelFormats.Gray8 || frame.Format == PixelFormats.BlackWhite)
        {
            frame.Freeze();
            return frame;
        }
        var grayscale = new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0);
        grayscale.Freeze();
        return grayscale;
    }

    private static double MillimetersToDip(double value) => value * 96d / 25.4d;

    private static IReadOnlyList<string> EnumeratePrinterNames()
    {
        const int flags = 0x2 | 0x4; // PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS
        _ = EnumPrinters(flags, null, 4, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0) return [];
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumPrinters(flags, null, 4, buffer, needed, out _, out var returned))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var size = Marshal.SizeOf<PrinterInfo4>();
            var names = new List<string>((int)returned);
            for (var index = 0; index < returned; index++)
            {
                var info = Marshal.PtrToStructure<PrinterInfo4>(IntPtr.Add(buffer, index * size));
                var name = Marshal.PtrToStringUni(info.PrinterName);
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
            return names;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PrinterInfo4
    {
        public IntPtr PrinterName;
        public IntPtr ServerName;
        public uint Attributes;
    }

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumPrinters(int flags, string? name, int level, IntPtr buffer, uint bufferSize,
        out uint bytesNeeded, out uint printersReturned);
}
