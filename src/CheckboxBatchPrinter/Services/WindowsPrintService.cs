using System.IO;
using System.Printing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Services;

public sealed class WindowsPrintService : IPrintService
{
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

    public Task PrintReceiptAsync(byte[] png, string receiptId, AppSettings settings, CancellationToken cancellationToken = default) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var server = new LocalPrintServer();
            using var queue = FindQueue(server, settings.PrinterName);
            var source = DecodeGrayscale(png);
            var page = BuildPage(source, settings, out var pageWidth, out var pageHeight);
            var ticket = BuildTicket(queue, pageWidth, pageHeight);
            var dialog = new PrintDialog { PrintQueue = queue, PrintTicket = ticket };
            dialog.PrintVisual(page, $"Checkbox чек {receiptId}");
        }).Task;

    public Task PrintReceiptsAsSingleJobAsync(
        IReadOnlyList<(byte[] Png, string ReceiptId)> receipts,
        AppSettings settings,
        CancellationToken cancellationToken = default) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (receipts.Count == 0) return;
            using var server = new LocalPrintServer();
            using var queue = FindQueue(server, settings.PrinterName);
            var page = BuildBatchPage(receipts, settings, out var pageWidth, out var pageHeight);
            var ticket = BuildTicket(queue, pageWidth, pageHeight);
            new PrintDialog { PrintQueue = queue, PrintTicket = ticket }
                .PrintVisual(page, $"Checkbox — пачка з {receipts.Count} чеків");
        }).Task;

    public Task PrintTestAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var server = new LocalPrintServer();
            using var queue = FindQueue(server, settings.PrinterName);
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
            var ticket = BuildTicket(queue, width, height);
            new PrintDialog { PrintQueue = queue, PrintTicket = ticket }.PrintVisual(page, "Checkbox Batch Printer — тест");
        }).Task;

    private static PrintQueue FindQueue(LocalPrintServer server, string printerName)
    {
        try { return new PrintQueue(server, printerName); }
        catch (PrintSystemException exception)
        {
            throw new InvalidOperationException($"Принтер «{printerName}» не знайдено.", exception);
        }
    }

    private static FixedPage BuildPage(BitmapSource source, AppSettings settings, out double pageWidth, out double pageHeight)
    {
        var geometry = PrintGeometry.Calculate(source.PixelWidth, source.PixelHeight, settings.PrintableWidthMm, settings.EffectivePaperWidthMm);
        // Size the print ticket to the printable head width. The physical roll
        // can be wider, but centering on that width shifts or clips the image.
        pageWidth = geometry.WidthDip;
        pageHeight = geometry.HeightDip + geometry.MarginDip * 2;
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
        FixedPage.SetTop(image, geometry.MarginDip);
        var page = new FixedPage { Width = pageWidth, Height = pageHeight, Background = Brushes.White };
        page.Children.Add(image);
        page.Measure(new Size(pageWidth, pageHeight));
        page.Arrange(new Rect(0, 0, pageWidth, pageHeight));
        page.UpdateLayout();
        return page;
    }

    internal static FixedPage BuildBatchPage(
        IReadOnlyList<(byte[] Png, string ReceiptId)> receipts,
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
        var outerMargin = MillimetersToDip(0.6);
        var separatorPadding = MillimetersToDip(0.8);
        var separatorThickness = MillimetersToDip(0.3);
        var separatorHeight = separatorPadding * 2 + separatorThickness;
        pageHeight = outerMargin * 2 + geometries.Sum(item => item.HeightDip) +
                     separatorHeight * (receipts.Count - 1);

        var page = new FixedPage { Width = pageWidth, Height = pageHeight, Background = Brushes.White };
        var top = outerMargin;
        for (var index = 0; index < sources.Length; index++)
        {
            var geometry = geometries[index];
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

    private static PrintTicket BuildTicket(PrintQueue queue, double widthDip, double heightDip)
    {
        var ticket = queue.DefaultPrintTicket?.Clone() ?? new PrintTicket();
        ticket.PageMediaSize = new PageMediaSize(widthDip, heightDip);
        ticket.PageOrientation = PageOrientation.Portrait;
        ticket.OutputColor = OutputColor.Grayscale;
        ticket.OutputQuality = OutputQuality.High;
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
