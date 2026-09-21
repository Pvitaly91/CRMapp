using System.IO;
using System.Printing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Windows.Xps;
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
            var document = new FixedDocument();
            double maxWidth = 0, maxHeight = 0;
            foreach (var item in receipts)
            {
                var page = BuildPage(DecodeGrayscale(item.Png), settings, out var width, out var height);
                maxWidth = Math.Max(maxWidth, width);
                maxHeight = Math.Max(maxHeight, height);
                var content = new PageContent();
                ((IAddChild)content).AddChild(page);
                document.Pages.Add(content);
            }
            var ticket = BuildTicket(queue, maxWidth, maxHeight);
            var writer = PrintQueue.CreateXpsDocumentWriter(queue);
            writer.Write(document, ticket);
        }).Task;

    public Task PrintTestAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var server = new LocalPrintServer();
            using var queue = FindQueue(server, settings.PrinterName);
            var width = MillimetersToDip(settings.EffectivePaperWidthMm);
            var height = MillimetersToDip(45);
            var panel = new StackPanel
            {
                Width = width,
                Height = height,
                Background = Brushes.White,
                Children =
                {
                    new TextBlock { Text = "CHECKBOX BATCH PRINTER", FontSize = 14, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center, Margin = new Thickness(3, 6, 3, 2) },
                    new TextBlock { Text = "Тестовий друк", FontSize = 12, TextAlignment = TextAlignment.Center, Margin = new Thickness(3) },
                    new TextBlock { Text = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"), FontSize = 10, TextAlignment = TextAlignment.Center, Margin = new Thickness(3) },
                    new TextBlock { Text = $"Папір: {settings.EffectivePaperWidthMm:0.#} мм\nОбласть: {settings.PrintableWidthMm:0.#} мм", FontSize = 9, TextAlignment = TextAlignment.Center, Margin = new Thickness(3) }
                }
            };
            var ticket = BuildTicket(queue, width, height);
            new PrintDialog { PrintQueue = queue, PrintTicket = ticket }.PrintVisual(panel, "Checkbox Batch Printer — тест");
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
        pageWidth = MillimetersToDip(settings.EffectivePaperWidthMm);
        pageHeight = geometry.HeightDip + geometry.MarginDip * 2;
        var horizontalOffset = Math.Max(0, (pageWidth - geometry.WidthDip) / 2);
        var image = new Image
        {
            Source = source,
            Width = geometry.WidthDip,
            Height = geometry.HeightDip,
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        FixedPage.SetLeft(image, horizontalOffset);
        FixedPage.SetTop(image, geometry.MarginDip);
        var page = new FixedPage { Width = pageWidth, Height = pageHeight, Background = Brushes.White };
        page.Children.Add(image);
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
