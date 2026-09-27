using System.IO;
using System.Printing;
using System.Printing.Interop;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Services;

public sealed record LabelDriverFormat(string Name, double WidthMm, double HeightMm)
{ public override string ToString() => $"{Name}: {WidthMm:0.##} × {HeightMm:0.##} мм"; }

public interface ILabelPrinter : ILabelBatchBackend
{
    IReadOnlyList<string> Printers();
    IReadOnlyList<LabelDriverFormat> Formats(string printer);
    LabelPrinterGeometry Inspect(LabelPrintSettings settings);
}
internal interface ILabelPageDevice : IDisposable
{
    LabelPrinterGeometry Geometry { get; }
    int Begin(string name);
    bool BeginPage();
    bool Draw(byte[] pixels, int pixelWidth, int pixelHeight, int x, int y, int width, int height);
    bool FinishPage();
    bool Finish();
    void Abort();
}
public sealed class WindowsLabelPrinter : ILabelPrinter
{
    private readonly Func<LabelPrintSettings, ILabelPageDevice> _deviceFactory;
    public WindowsLabelPrinter() { _deviceFactory = Open; }
    internal WindowsLabelPrinter(Func<LabelPrintSettings, ILabelPageDevice> deviceFactory) { _deviceFactory = deviceFactory; }
    public IReadOnlyList<string> Printers() => new WindowsPrintService().GetInstalledPrinters();
    public IReadOnlyList<LabelDriverFormat> Formats(string printer)
    {
        using var server = new LocalPrintServer();
        using var queue = server.GetPrintQueue(printer);
        return MediaSizes(queue.GetPrintCapabilities().PageMediaSizeCapability, queue.DefaultPrintTicket.PageMediaSize)
            .Select(s => new LabelDriverFormat(s.PageMediaSizeName is null or PageMediaSizeName.Unknown ? "Поточний/користувацький формат драйвера" : s.PageMediaSizeName.ToString()!,
                s.Width!.Value * 25.4 / 96, s.Height!.Value * 25.4 / 96)).ToArray();
    }
    internal static IReadOnlyList<PageMediaSize> MediaSizes(IEnumerable<PageMediaSize> advertised, PageMediaSize? current)
    {
        bool Valid(PageMediaSize s) => s.Width is > 0 && s.Height is > 0 && double.IsFinite(s.Width.Value) && double.IsFinite(s.Height.Value);
        var values = advertised.Where(Valid).ToList();
        // Legacy GDI label drivers may expose no capability list but have a real sized default ticket.
        // This is read from that driver, not a fabricated 100x100 format or a global setting change.
        if (current is not null && Valid(current) && !values.Any(s => Math.Abs(s.Width!.Value-current.Width!.Value) < 0.01 && Math.Abs(s.Height!.Value-current.Height!.Value) < 0.01)) values.Add(current);
        return values;
    }
    internal static bool MatchesMedia(LabelPrinterGeometry frame, double width, double height)
    {
        var widthMatches = Math.Abs(frame.WidthMm-width) <= 0.8;
        // Some label drivers report a virtual frame including symmetric margins outside their
        // ticket's label width. Accept only when its printable width agrees with that ticket,
        // both margins explain the extra frame, and the feed height still agrees.
        var symmetricFrame = Math.Abs(frame.PrintableWidthMm-width) <= 0.8 && frame.LeftMm >= 0 &&
            Math.Abs(frame.WidthMm-frame.PrintableWidthMm-2*frame.LeftMm) <= 0.8;
        return (widthMatches || symmetricFrame) && Math.Abs(frame.HeightMm-height) <= 0.8;
    }
    public LabelPrinterGeometry Inspect(LabelPrintSettings settings)
    {
        using var device = _deviceFactory(settings);
        return device.Geometry;
    }
    public async Task<int?> SubmitAsync(LabelPrintBatch batch, string jobName, Func<Task> beforeTransfer,
        Func<int, Task> jobCreated, CancellationToken cancellationToken = default)
    {
        // UI calls this on STA. Prepare driver and pixel buffers completely before crossing the boundary.
        using var device = _deviceFactory(batch.Settings);
        if (batch.RasterDpiX > 0 && (batch.RasterDpiX != device.Geometry.DpiX || batch.RasterDpiY != device.Geometry.DpiY))
            throw new ShippingLabelException(LabelFailureKind.UnsupportedFormat,"DPI драйвера змінився після підготовки. Підготуйте пакет заново.");
        long decodedBytes = 0;
        var prepared = batch.Pages.Select(page => Prepare(page, batch.Settings, device.Geometry, ref decodedBytes)).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        await beforeTransfer();
        // StartDoc can register a job before the caller gets a result. Journal is already Unknown.
        var jobId = device.Begin(jobName);
        if (jobId <= 0) throw new InvalidOperationException("Windows не підтвердив створення завдання наклейок.");
        var completed = false;
        try
        {
            await jobCreated(jobId);
            foreach (var page in prepared)
                for (var copy = 0; copy < batch.Settings.Copies; copy++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!device.BeginPage()) throw new InvalidOperationException("Windows не почав сторінку наклейки.");
                    if (!device.Draw(page.Pixels, page.PixelWidth, page.PixelHeight, page.X, page.Y, page.Width, page.Height) || !device.FinishPage())
                        throw new InvalidOperationException("Windows не підтвердив передавання сторінки.");
                }
            if (!device.Finish()) throw new InvalidOperationException("Windows не підтвердив завершення пакета.");
            completed = true;
            return jobId;
        }
        finally
        {
            // Only this job/device, never other queue jobs or the spooler service.
            if (!completed) device.Abort();
        }
    }
    private static Device Open(LabelPrintSettings settings)
    {
        settings.Validate();
        using var server = new LocalPrintServer();
        using var queue = server.GetPrintQueue(settings.PrinterName);
        var capabilities = queue.GetPrintCapabilities();
        var matching = MediaSizes(capabilities.PageMediaSizeCapability, queue.DefaultPrintTicket.PageMediaSize).FirstOrDefault(s =>
            Math.Abs(s.Width!.Value * 25.4 / 96 - settings.WidthMm) < 0.2 &&
            Math.Abs(s.Height!.Value * 25.4 / 96 - settings.HeightMm) < 0.2);
        if (matching is null) throw new ShippingLabelException(LabelFailureKind.UnsupportedFormat, "Вибраний фізичний формат відсутній у драйвері. Оновіть формати принтера.");
        var ticket = queue.DefaultPrintTicket.Clone();
        ticket.PageMediaSize = matching;
        ticket.PageOrientation = settings.Landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
        ticket.CopyCount = 1;
        var validated = queue.MergeAndValidatePrintTicket(queue.DefaultPrintTicket, ticket).ValidatedPrintTicket;
        using var converter = new PrintTicketConverter(queue.FullName, 1);
        var bytes = converter.ConvertPrintTicketToDevMode(validated, BaseDevModeType.UserDefault);
        var memory = Marshal.AllocHGlobal(bytes.Length);
        IntPtr hdc;
        try { Marshal.Copy(bytes, 0, memory, bytes.Length); hdc = CreateDC("WINSPOOL", settings.PrinterName, null, memory); }
        finally { Marshal.FreeHGlobal(memory); }
        if (hdc == IntPtr.Zero) throw new InvalidOperationException("Не вдалося відкрити принтер наклейок.");
        Device device;
        try { device = new Device(hdc); }
        catch { DeleteDC(hdc); throw; }
        var expectedWidth = settings.Landscape ? settings.HeightMm : settings.WidthMm;
        var expectedHeight = settings.Landscape ? settings.WidthMm : settings.HeightMm;
        if (!MatchesMedia(device.Geometry, expectedWidth, expectedHeight))
        { device.Dispose(); throw new ShippingLabelException(LabelFailureKind.UnsupportedFormat, $"Драйвер підмінив фізичний формат: запитано {expectedWidth:0.##}×{expectedHeight:0.##} мм, фактично {device.Geometry.WidthMm:0.##}×{device.Geometry.HeightMm:0.##} мм; область {device.Geometry.PrintableWidthMm:0.##}×{device.Geometry.PrintableHeightMm:0.##}, поля {device.Geometry.LeftMm:0.##}/{device.Geometry.TopMm:0.##} мм. Друк зупинено."); }
        return device;
    }
    private static PreparedPage Prepare(ShippingLabelPage page, LabelPrintSettings settings, LabelPrinterGeometry geometry, ref long decodedBytes)
    {
        var placement = LabelPageLayout.Place(page, settings, geometry);
        using var input = new MemoryStream(page.Png, false);
        var bitmap = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        decodedBytes += checked((long)bitmap.PixelWidth * bitmap.PixelHeight * 4);
        if (decodedBytes > 256L * 1024 * 1024)
            throw new ShippingLabelException(LabelFailureKind.UnsupportedFormat, "Растери пакета перевищують 256 МіБ. Виберіть менший пакет.");
        var source = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(source.PixelWidth * source.PixelHeight * 4)];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return new(pixels, source.PixelWidth, source.PixelHeight,
            (int)Math.Round((placement.LeftMm - geometry.LeftMm) * geometry.DpiX / 25.4),
            (int)Math.Round((placement.TopMm - geometry.TopMm) * geometry.DpiY / 25.4),
            (int)Math.Round(placement.WidthMm * geometry.DpiX / 25.4), (int)Math.Round(placement.HeightMm * geometry.DpiY / 25.4));
    }
    private sealed record PreparedPage(byte[] Pixels, int PixelWidth, int PixelHeight, int X, int Y, int Width, int Height);
    private sealed class Device(IntPtr handle) : ILabelPageDevice
    {
        public IntPtr Handle { get; } = handle;
        public LabelPrinterGeometry Geometry { get; } = GeometryFor(handle);
        public int Begin(string name)
        {
            var info = new DocInfo { Size = Marshal.SizeOf<DocInfo>(), Name = name };
            return StartDoc(Handle, ref info);
        }
        public bool BeginPage() => StartPage(Handle) > 0;
        public bool FinishPage() => EndPage(Handle) > 0;
        public bool Finish() => EndDoc(Handle) > 0;
        public void Abort() => AbortDoc(Handle);
        public bool Draw(byte[] pixels, int pixelWidth, int pixelHeight, int x, int y, int width, int height)
        {
            var bitmap = new BitmapInfo { Size = 40, Width = pixelWidth, Height = -pixelHeight, Planes = 1, Bits = 32 };
            SetStretchBltMode(Handle, 3);
            return StretchDIBits(Handle, x, y, width, height, 0, 0, pixelWidth, pixelHeight, pixels, ref bitmap, 0, 0x00CC0020) > 0;
        }
        public void Dispose() => DeleteDC(Handle);
        private static LabelPrinterGeometry GeometryFor(IntPtr hdc)
        {
            var x = GetDeviceCaps(hdc, 88); var y = GetDeviceCaps(hdc, 90);
            if (x <= 0 || y <= 0) throw new InvalidOperationException("Драйвер не надав DPI.");
            return new(GetDeviceCaps(hdc, 110) * 25.4 / x, GetDeviceCaps(hdc, 111) * 25.4 / y,
                GetDeviceCaps(hdc, 112) * 25.4 / x, GetDeviceCaps(hdc, 113) * 25.4 / y,
                GetDeviceCaps(hdc, 8) * 25.4 / x, GetDeviceCaps(hdc, 10) * 25.4 / y, x, y);
        }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DocInfo
    { public int Size; [MarshalAs(UnmanagedType.LPWStr)] public string Name; public IntPtr Output, DataType; public uint Type; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int Xppm, Yppm; public uint Used, Important; }
    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr devMode);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);
    [DllImport("gdi32.dll", EntryPoint = "StartDocW", CharSet = CharSet.Unicode)] private static extern int StartDoc(IntPtr hdc, ref DocInfo info);
    [DllImport("gdi32.dll")] private static extern int StartPage(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int EndPage(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int EndDoc(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int AbortDoc(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern int StretchDIBits(IntPtr hdc, int x, int y, int width, int height,
        int sourceX, int sourceY, int sourceWidth, int sourceHeight, byte[] bits, ref BitmapInfo info, uint usage, uint operation);
}
