using System.ComponentModel;
using System.IO;
using System.IO.Packaging;
using System.Printing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Services;

public sealed class WindowsPrintService : IPrintService
{
    private readonly StaPrintWorker _worker = new();

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

    public Task<PrintSubmissionResult> PrintReceiptAsync(
        byte[] png,
        string receiptId,
        AppSettings settings,
        CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var server = new LocalPrintServer();
            using var queue = FindQueue(server, settings.PrinterName);
            return SubmitReceiptCore(queue, DecodeGrayscale(png), receiptId, settings, cancellationToken);
        }, cancellationToken);

    public Task<PrintSubmissionResult> PrintReceiptsAsSingleJobAsync(
        IReadOnlyList<(byte[] Png, string ReceiptId)> receipts,
        AppSettings settings,
        CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (receipts.Count == 0) throw new ArgumentException("Пачка не містить чеків.", nameof(receipts));
            using var server = new LocalPrintServer();
            using var queue = FindQueue(server, settings.PrinterName);
            var sources = receipts.Select(x => (Source: DecodeGrayscale(x.Png), x.ReceiptId)).ToArray();
            var longest = sources.MaxBy(x => (double)x.Source.PixelHeight / x.Source.PixelWidth).Source;
            var prepared = PreparePage(queue, longest, settings);
            var document = new FixedDocument();
            foreach (var item in sources)
            {
                var geometry = PrintGeometry.Calculate(item.Source.PixelWidth, item.Source.PixelHeight,
                    settings.PrintableWidthMm, settings.EffectivePaperWidthMm);
                if (geometry.HeightDip + geometry.MarginDip * 2 > prepared.Layout.Validation.ImageableHeightDip)
                    throw new UnsupportedPrinterPageException("Один із чеків не вміщується у прийняту драйвером сторінку.");
                var pageLayout = prepared.Layout with
                {
                    ImageHeightDip = geometry.HeightDip,
                    ImageTopDip = prepared.Layout.Validation.ImageableOriginYDip + geometry.MarginDip
                };
                document.Pages.Add(ToPageContent(BuildPage(item.Source, pageLayout)));
            }
            return SubmitDocument(queue, document, prepared.Ticket,
                $"Checkbox batch {receipts.Count} {Guid.NewGuid():N}", prepared.Layout.Validation, cancellationToken);
        }, cancellationToken);

    public Task<PrintSubmissionResult> PrintTestAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = CreateTestBitmap(settings);
            using var server = new LocalPrintServer();
            using var queue = FindQueue(server, settings.PrinterName);
            return SubmitReceiptCore(queue, source, "TEST", settings, cancellationToken);
        }, cancellationToken);

    public Task<WindowsPrintJobObservation> GetJobStatusAsync(
        PrintSubmissionResult submission,
        CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync(() =>
        {
            using var server = new LocalPrintServer();
            using var queue = FindQueue(server, submission.PrinterName);
            queue.Refresh();
            PrintSystemJobInfo? found = null;
            try
            {
                found = queue.GetPrintJobInfoCollection()
                    .FirstOrDefault(x => x.JobIdentifier == submission.JobId);
                if (found is null)
                    return new WindowsPrintJobObservation(submission.JobId, WindowsPrintJobState.Disappeared,
                        "Завдання зникло з черги Windows; фізичний результат не підтверджено.", true);
                found.Refresh();
                return MapObservation(found.JobIdentifier, found.JobStatus);
            }
            finally { found?.Dispose(); }
        }, cancellationToken);

    public void Dispose() => _worker.Dispose();

    private static PrintSubmissionResult SubmitReceiptCore(
        PrintQueue queue,
        BitmapSource source,
        string receiptId,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var prepared = PreparePage(queue, source, settings);
        var document = new FixedDocument();
        document.Pages.Add(ToPageContent(BuildPage(source, prepared.Layout)));
        var safeReceiptId = string.Concat(receiptId.Take(24).Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));
        return SubmitDocument(queue, document, prepared.Ticket,
            $"Checkbox {safeReceiptId} {Guid.NewGuid():N}", prepared.Layout.Validation, cancellationToken);
    }

    private static PreparedPage PreparePage(PrintQueue queue, BitmapSource source, AppSettings settings)
    {
        var geometry = PrintGeometry.Calculate(source.PixelWidth, source.PixelHeight,
            settings.PrintableWidthMm, settings.EffectivePaperWidthMm);
        var baseTicket = queue.DefaultPrintTicket?.Clone() ?? new PrintTicket();
        var baseCapabilities = queue.GetPrintCapabilities(baseTicket);
        var baseArea = baseCapabilities.PageImageableArea ??
            throw new UnsupportedPrinterPageException("Драйвер не повідомив доступну область друку.");
        var verticalDriverLoss = Math.Max(0,
            (baseTicket.PageMediaSize?.Height ?? baseArea.ExtentHeight) - baseArea.ExtentHeight);

        var requestedWidth = MillimetersToDip(settings.EffectivePaperWidthMm);
        var requestedHeight = geometry.HeightDip + geometry.MarginDip * 2 + verticalDriverLoss;
        var requested = new RequestedPageLayout(
            requestedWidth, requestedHeight, geometry.WidthDip, geometry.HeightDip, geometry.MarginDip);

        var deltaTicket = baseTicket.Clone();
        deltaTicket.PageMediaSize = new PageMediaSize(requestedWidth, requestedHeight);
        deltaTicket.PageOrientation = PageOrientation.Portrait;
        if (baseCapabilities.OutputColorCapability?.Contains(OutputColor.Grayscale) == true)
            deltaTicket.OutputColor = OutputColor.Grayscale;
        if (baseCapabilities.OutputQualityCapability?.Contains(OutputQuality.High) == true)
            deltaTicket.OutputQuality = OutputQuality.High;

        var validationResult = queue.MergeAndValidatePrintTicket(baseTicket, deltaTicket);
        var acceptedTicket = validationResult.ValidatedPrintTicket;
        var acceptedSize = acceptedTicket.PageMediaSize;
        var acceptedWidth = acceptedSize?.Width ?? 0;
        var acceptedHeight = acceptedSize?.Height ?? 0;
        var acceptedCapabilities = queue.GetPrintCapabilities(acceptedTicket);
        var area = acceptedCapabilities.PageImageableArea ??
            throw new UnsupportedPrinterPageException("Драйвер не повідомив доступну область для прийнятого формату.");
        var metrics = new DriverPageMetrics(
            acceptedWidth, acceptedHeight,
            area.OriginWidth, area.OriginHeight, area.ExtentWidth, area.ExtentHeight,
            acceptedCapabilities.PageMediaSizeCapability?.Count ?? 0,
            validationResult.ConflictStatus == ConflictStatus.ConflictResolved);
        var layout = PrinterPageValidator.Validate(requested, metrics);
        return new PreparedPage(acceptedTicket, layout);
    }

    private static PrintSubmissionResult SubmitDocument(
        PrintQueue queue,
        FixedDocument document,
        PrintTicket ticket,
        string uniqueJobName,
        PrinterPageValidation validation,
        CancellationToken cancellationToken)
    {
        var temporaryXps = Path.Combine(Path.GetTempPath(), $"checkbox-print-{Guid.NewGuid():N}.xps");
        try
        {
            using (var xps = new XpsDocument(temporaryXps, FileAccess.ReadWrite, CompressionOption.Maximum))
            {
                var writer = XpsDocument.CreateXpsDocumentWriter(xps);
                writer.Write(document, ticket);
            }

            // This is the last safe cancellation point. After AddJob returns, a physical copy may already print.
            cancellationToken.ThrowIfCancellationRequested();
            var printerName = queue.FullName;
            using var job = queue.AddJob(uniqueJobName, temporaryXps, false);
            var jobId = job.JobIdentifier;
            var observation = new WindowsPrintJobObservation(jobId, WindowsPrintJobState.Queued,
                "Завдання прийнято чергою Windows; фізичний результат ще невідомий.", false);
            return new PrintSubmissionResult(jobId, uniqueJobName, printerName, validation, observation);
        }
        finally
        {
            try { if (File.Exists(temporaryXps)) File.Delete(temporaryXps); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static FixedPage BuildPage(BitmapSource source, ValidatedPageLayout layout)
    {
        var image = new Image
        {
            Source = source,
            Width = layout.ImageWidthDip,
            Height = layout.ImageHeightDip,
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        FixedPage.SetLeft(image, layout.ImageLeftDip);
        FixedPage.SetTop(image, layout.ImageTopDip);
        var page = new FixedPage { Width = layout.PageWidthDip, Height = layout.PageHeightDip, Background = Brushes.White };
        page.Children.Add(image);
        page.Measure(new Size(layout.PageWidthDip, layout.PageHeightDip));
        page.Arrange(new Rect(0, 0, layout.PageWidthDip, layout.PageHeightDip));
        page.UpdateLayout();
        return page;
    }

    private static PageContent ToPageContent(FixedPage page)
    {
        var content = new PageContent();
        ((IAddChild)content).AddChild(page);
        return content;
    }

    private static WindowsPrintJobObservation MapObservation(int jobId, System.Printing.PrintJobStatus status)
    {
        if (status.HasFlag(System.Printing.PrintJobStatus.Paused))
            return new(jobId, WindowsPrintJobState.Paused, "Завдання призупинене у Windows.", false);
        if (status.HasFlag(System.Printing.PrintJobStatus.Error) ||
            status.HasFlag(System.Printing.PrintJobStatus.PaperOut) ||
            status.HasFlag(System.Printing.PrintJobStatus.Offline) ||
            status.HasFlag(System.Printing.PrintJobStatus.UserIntervention) ||
            status.HasFlag(System.Printing.PrintJobStatus.Blocked))
            return new(jobId, WindowsPrintJobState.Error, $"Windows повідомляє стан: {status}.", true);
        if (status.HasFlag(System.Printing.PrintJobStatus.Completed) ||
            status.HasFlag(System.Printing.PrintJobStatus.Printed) ||
            status.HasFlag(System.Printing.PrintJobStatus.Deleted))
            return new(jobId, WindowsPrintJobState.CompletedBySpooler,
                "Windows завершила обробку завдання; фізичний друк не підтверджено.", true);
        if (status.HasFlag(System.Printing.PrintJobStatus.Printing))
            return new(jobId, WindowsPrintJobState.Printing, "Windows передає завдання принтеру.", false);
        if (status.HasFlag(System.Printing.PrintJobStatus.Spooling))
            return new(jobId, WindowsPrintJobState.Spooling, "Windows формує завдання друку.", false);
        return new(jobId, WindowsPrintJobState.Queued, $"Завдання у черзі Windows ({status}).", false);
    }

    private static PrintQueue FindQueue(LocalPrintServer server, string printerName)
    {
        try { return new PrintQueue(server, printerName); }
        catch (PrintSystemException exception)
        {
            throw new InvalidOperationException($"Принтер «{printerName}» не знайдено.", exception);
        }
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

    private static BitmapSource CreateTestBitmap(AppSettings settings)
    {
        var width = Math.Max(200, (int)Math.Round(settings.PrintableWidthMm / 25.4 * 203));
        var panel = new StackPanel
        {
            Width = width,
            Height = 310,
            Background = Brushes.White,
            Children =
            {
                new TextBlock { Text = "CHECKBOX BATCH PRINTER", FontSize = 24, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center, Margin = new Thickness(6, 12, 6, 5) },
                new TextBlock { Text = "Тестовий друк", FontSize = 22, TextAlignment = TextAlignment.Center, Margin = new Thickness(6) },
                new TextBlock { Text = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"), FontSize = 18, TextAlignment = TextAlignment.Center, Margin = new Thickness(6) },
                new TextBlock { Text = $"Папір: {settings.EffectivePaperWidthMm:0.#} мм\nОбласть: {settings.PrintableWidthMm:0.#} мм", FontSize = 16, TextAlignment = TextAlignment.Center, Margin = new Thickness(6) },
                new TextBlock { Text = "Результат підтверджує користувач", FontSize = 14, TextAlignment = TextAlignment.Center, Margin = new Thickness(6) }
            }
        };
        panel.Measure(new Size(width, 310));
        panel.Arrange(new Rect(0, 0, width, 310));
        var bitmap = new RenderTargetBitmap(width, 310, 203, 203, PixelFormats.Pbgra32);
        bitmap.Render(panel);
        bitmap.Freeze();
        return bitmap;
    }

    private static double MillimetersToDip(double value) => value * PrintGeometry.DipPerMillimeter;

    private static IReadOnlyList<string> EnumeratePrinterNames()
    {
        const int flags = 0x2 | 0x4; // PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS
        _ = EnumPrinters(flags, null, 4, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0) return [];
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumPrinters(flags, null, 4, buffer, needed, out _, out var returned))
                throw new Win32Exception(Marshal.GetLastWin32Error());
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

    private sealed record PreparedPage(PrintTicket Ticket, ValidatedPageLayout Layout);

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
