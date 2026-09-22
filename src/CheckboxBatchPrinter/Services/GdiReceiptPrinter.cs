using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Services;

internal static class GdiReceiptPrinter
{
    private const int HorzRes = 8;
    private const int VertRes = 10;
    private const int LogPixelsX = 88;
    private const int LogPixelsY = 90;
    private const int PhysicalWidth = 110;
    private const int PhysicalHeight = 111;
    private const int DibRgbColors = 0;
    private const uint Srccopy = 0x00CC0020;

    internal readonly record struct PageCapabilities(
        int WidthPixels, int HeightPixels, int DpiX, int DpiY,
        double PhysicalWidthMm, double PhysicalHeightMm);

    public static PageCapabilities Probe(string printerName, byte[] devMode)
    {
        var handle = OpenPrinterDc(printerName, devMode);
        try { return ReadCapabilities(handle); }
        finally { DeleteDC(handle); }
    }

    public static PrintSubmissionResult Submit(
        string printerName,
        IReadOnlyList<BitmapSource> sources,
        ValidatedPageLayout layout,
        byte[] devMode,
        PrintAttemptDescriptor attempt,
        Action<PrintAttemptDescriptor> markSubmissionStarted,
        Func<PrintSubmissionResult?> tryRecoverSubmittedJob,
        CancellationToken cancellationToken)
    {
        if (sources.Count == 0) throw new ArgumentException("Немає сторінок для друку.", nameof(sources));
        WindowsPrintService.DiagnosticTrace?.Invoke("GDI CreateDC");
        var dc = OpenPrinterDc(printerName, devMode);
        try
        {
            var capabilities = ReadCapabilities(dc);
            var expectedHeightMm = layout.PageHeightDip / PrintGeometry.DipPerMillimeter;
            if (Math.Abs(capabilities.PhysicalHeightMm - expectedHeightMm) > 2)
                throw new UnsupportedPrinterPageException(
                    $"GDI-драйвер прийняв висоту {capabilities.PhysicalHeightMm:0.##} мм замість {expectedHeightMm:0.##} мм.");

            var marginX = Math.Max(1, (int)Math.Ceiling(0.8 * capabilities.DpiX / 25.4));
            var marginY = Math.Max(1, (int)Math.Ceiling(0.8 * capabilities.DpiY / 25.4));
            var requestedWidth = (int)Math.Round(layout.ImageWidthDip * capabilities.DpiX / 96);
            foreach (var source in sources)
            {
                var widthByHeight = (long)(capabilities.HeightPixels - 2 * marginY) * source.PixelWidth / source.PixelHeight;
                var actualWidth = Math.Min(requestedWidth,
                    Math.Min(capabilities.WidthPixels - 2 * marginX, (int)Math.Min(int.MaxValue, widthByHeight)));
                if (actualWidth < requestedWidth * 0.8)
                    throw new UnsupportedPrinterPageException("GDI-драйвер не залишив достатньої області для повного чека.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return PrintSubmissionBoundary.Execute(attempt, markSubmissionStarted, () =>
            {
                WindowsPrintService.DiagnosticTrace?.Invoke("GDI StartDoc");
                var info = new DocInfo { Size = Marshal.SizeOf<DocInfo>(), DocumentName = attempt.UniqueJobName };
                var jobId = StartDoc(dc, ref info);
                if (jobId <= 0) throw PrinterError("StartDoc");
                WindowsPrintService.DiagnosticTrace?.Invoke($"GDI job {jobId}");
                var completed = false;
                try
                {
                    foreach (var source in sources)
                    {
                        WindowsPrintService.DiagnosticTrace?.Invoke("GDI StartPage");
                        if (StartPage(dc) <= 0) throw PrinterError("StartPage");
                        DrawPage(dc, source, layout, capabilities, marginX, marginY);
                        WindowsPrintService.DiagnosticTrace?.Invoke("GDI EndPage");
                        if (EndPage(dc) <= 0) throw PrinterError("EndPage");
                    }
                    WindowsPrintService.DiagnosticTrace?.Invoke("GDI EndDoc");
                    if (EndDoc(dc) <= 0) throw PrinterError("EndDoc");
                    completed = true;
                }
                catch (Exception exception)
                {
                    throw new PrintSubmissionUnknownException(attempt, exception, jobId);
                }
                finally
                {
                    if (!completed) AbortDoc(dc);
                }
                return new PrintSubmissionResult(attempt, jobId, layout.Validation,
                    new WindowsPrintJobObservation(jobId, WindowsPrintJobState.Queued,
                        "Завдання передано драйверу Windows; фізичний результат ще невідомий.", false));
            }, tryRecoverSubmittedJob);
        }
        finally { DeleteDC(dc); }
    }

    private static void DrawPage(IntPtr dc, BitmapSource source, ValidatedPageLayout layout,
        PageCapabilities capabilities, int marginX, int marginY)
    {
        var bitmap = source.Format == PixelFormats.Bgr32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        var requestedWidth = (int)Math.Round(layout.ImageWidthDip * capabilities.DpiX / 96);
        var widthByHeight = (long)(capabilities.HeightPixels - 2 * marginY) * bitmap.PixelWidth / bitmap.PixelHeight;
        var width = Math.Min(requestedWidth,
            Math.Min(capabilities.WidthPixels - 2 * marginX, (int)Math.Min(int.MaxValue, widthByHeight)));
        var height = (int)Math.Round((double)width * bitmap.PixelHeight / bitmap.PixelWidth);
        var x = (capabilities.WidthPixels - width) / 2;
        var y = marginY;
        var info = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = bitmap.PixelWidth,
            Height = -bitmap.PixelHeight,
            Planes = 1,
            BitCount = 32,
            Compression = 0,
            SizeImage = (uint)pixels.Length
        };
        var copied = StretchDIBits(dc, x, y, width, height, 0, 0, bitmap.PixelWidth,
            bitmap.PixelHeight, pixels, ref info, DibRgbColors, Srccopy);
        if (copied <= 0) throw PrinterError("StretchDIBits");
    }

    private static PageCapabilities ReadCapabilities(IntPtr dc)
    {
        var width = GetDeviceCaps(dc, HorzRes);
        var height = GetDeviceCaps(dc, VertRes);
        var dpiX = GetDeviceCaps(dc, LogPixelsX);
        var dpiY = GetDeviceCaps(dc, LogPixelsY);
        var physicalWidth = GetDeviceCaps(dc, PhysicalWidth);
        var physicalHeight = GetDeviceCaps(dc, PhysicalHeight);
        if (width <= 0 || height <= 0 || dpiX <= 0 || dpiY <= 0 || physicalWidth <= 0 || physicalHeight <= 0)
            throw new UnsupportedPrinterPageException("GDI-драйвер не повідомив розміри сторінки.");
        return new PageCapabilities(width, height, dpiX, dpiY,
            (double)physicalWidth * 25.4 / dpiX, (double)physicalHeight * 25.4 / dpiY);
    }

    private static IntPtr OpenPrinterDc(string printerName, byte[] devMode)
    {
        var pointer = Marshal.AllocHGlobal(devMode.Length);
        try
        {
            Marshal.Copy(devMode, 0, pointer, devMode.Length);
            var dc = CreateDC("WINSPOOL", printerName, null, pointer);
            if (dc == IntPtr.Zero) throw PrinterError("CreateDC");
            return dc;
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static Win32Exception PrinterError(string operation) =>
        new(Marshal.GetLastWin32Error(), $"{operation}: драйвер Windows не прийняв завдання друку.");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo
    {
        public int Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string DocumentName;
        public IntPtr Output;
        public IntPtr DataType;
        public int Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr devMode);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDeviceCaps(IntPtr dc, int index);

    [DllImport("gdi32.dll", EntryPoint = "StartDocW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDoc(IntPtr dc, ref DocInfo info);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int StartPage(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int EndPage(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int EndDoc(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int AbortDoc(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int StretchDIBits(IntPtr dc, int destX, int destY, int destWidth, int destHeight,
        int srcX, int srcY, int srcWidth, int srcHeight, byte[] bits,
        ref BitmapInfoHeader bitmapInfo, int usage, uint rasterOperation);
}
