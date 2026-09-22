using System.Drawing.Printing;
using System.Printing;
using System.Printing.Interop;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Services;

internal static class DriverCustomMediaTicket
{
    private const double MmPerHundredthInch = 25.4 / 100;
    private const double MarginMm = 0.8;

    public static bool TryCreate(PrintQueue queue, AppSettings settings, BitmapSource source,
        out WindowsPrintService.PreparedPage prepared)
    {
        prepared = null!;
        try
        {
            var printer = new PrinterSettings { PrinterName = queue.FullName };
            if (!printer.IsValid) return false;

            // This is the driver's printable stock width, which can be smaller
            // than the nominal 50/58 mm roll selected in the app.
            var widthHundredths = printer.DefaultPageSettings.PaperSize.Width;
            var stockWidthMm = widthHundredths * MmPerHundredthInch;
            if (stockWidthMm < 20 ||
                stockWidthMm > settings.EffectivePaperWidthMm + 0.5 ||
                settings.EffectivePaperWidthMm - stockWidthMm > 12)
                return false;

            var printableWidthMm = Math.Min(settings.PrintableWidthMm, stockWidthMm - 2 * MarginMm);
            if (printableWidthMm < 20) return false;
            var geometry = PrintGeometry.Calculate(source.PixelWidth, source.PixelHeight,
                printableWidthMm, stockWidthMm, MarginMm);
            var heightMm = (geometry.HeightDip + 2 * geometry.MarginDip) / PrintGeometry.DipPerMillimeter;
            var heightHundredths = checked((int)Math.Ceiling(heightMm / MmPerHundredthInch));
            if (heightHundredths <= 0 || heightHundredths > short.MaxValue) return false;

            using var converter = new PrintTicketConverter(queue.FullName, 1);
            var papers = new[] { new PaperSize("Checkbox receipt", widthHundredths, heightHundredths) }
                .Concat(printer.PaperSizes.Cast<PaperSize>()
                    .Where(paper => Math.Abs(paper.Width - widthHundredths) <= 2 &&
                                    paper.Height >= heightHundredths &&
                                    paper.Height * MmPerHundredthInch <= heightMm + 60)
                    .OrderBy(paper => paper.Height));
            foreach (var paper in papers)
            {
                try
                {
                    var pageSettings = (PageSettings)printer.DefaultPageSettings.Clone();
                    pageSettings.PaperSize = paper;
                    var devMode = GetDevMode(printer, pageSettings);
                    var candidate = converter.ConvertDevModeToPrintTicket(devMode);
                    var merged = queue.MergeAndValidatePrintTicket(queue.DefaultPrintTicket ?? new PrintTicket(), candidate);
                    var ticket = merged.ValidatedPrintTicket;
                    var size = ticket.PageMediaSize;
                    if (size?.Width is not double acceptedWidth || size.Height is not double acceptedHeight)
                        continue;

                    var area = queue.GetPrintCapabilities(ticket).PageImageableArea;
                    if (area is null) continue;
                    var metrics = new DriverPageMetrics(acceptedWidth, acceptedHeight,
                        area.OriginWidth, area.OriginHeight, area.ExtentWidth, area.ExtentHeight,
                        0, merged.ConflictStatus == ConflictStatus.ConflictResolved);
                    var request = new RequestedPageLayout(acceptedWidth, acceptedHeight,
                        geometry.WidthDip, geometry.HeightDip, geometry.MarginDip);
                    var layout = PrinterPageValidator.Validate(request, metrics);

                    // PrintTicket validation alone is insufficient for v3 roll drivers:
                    // RPP210 reports an 87 mm custom ticket but CreateDC uses its 2527 mm roll.
                    var gdi = GdiReceiptPrinter.Probe(queue.FullName, devMode);
                    if (Math.Abs(gdi.PhysicalWidthMm - acceptedWidth / PrintGeometry.DipPerMillimeter) > 2 ||
                        Math.Abs(gdi.PhysicalHeightMm - acceptedHeight / PrintGeometry.DipPerMillimeter) > 2)
                        continue;
                    prepared = new WindowsPrintService.PreparedPage(ticket, layout, devMode);
                    return true;
                }
                catch (Exception exception) when (exception is ArgumentException or OverflowException or
                    InvalidOperationException or ExternalException or PrintSystemException or UnsupportedPrinterPageException)
                {
                    // Try the next short driver-advertised form.
                }
            }
            return false;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or
            InvalidOperationException or ExternalException or PrintSystemException or UnsupportedPrinterPageException)
        {
            return false;
        }
    }

    private static byte[] GetDevMode(PrinterSettings printer, PageSettings page)
    {
        var handle = printer.GetHdevmode(page);
        IntPtr pointer = IntPtr.Zero;
        try
        {
            pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) throw new InvalidOperationException("Не вдалося прочитати формат драйвера.");
            // DEVMODEW: dmSize at 68, dmDriverExtra at 70.
            var size = Marshal.ReadInt16(pointer, 68) + Marshal.ReadInt16(pointer, 70);
            if (size is < 72 or > 65535) throw new InvalidOperationException("Некоректний DEVMODE драйвера.");
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, size);
            return bytes;
        }
        finally
        {
            if (pointer != IntPtr.Zero) GlobalUnlock(handle);
            GlobalFree(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr handle);
}
