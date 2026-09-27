using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Views;
using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.Infrastructure;

internal static class PackageVerification
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? report = null;
        try
        {
            if (args.Length != 2) return 2;
            var root = Path.GetFullPath(args[1]);
            var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("cbp-package-smoke-", StringComparison.Ordinal) ||
                !File.Exists(Path.Combine(root, "allow-offline-verification"))) return 2;
            // Refuse symlink/junction redirection to any real profile.
            for (var directory = new DirectoryInfo(root); directory != null; directory = directory.Parent)
                if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) return 2;
            report = Path.Combine(root, "verification.json");
            var data = AppEnvironment.GetDataRoot(root, AppEnvironment.Channel);
            var settingsPath = Path.Combine(data, "settings.json");
            var store = new JsonSettingsService(settingsPath);
            var restarted = File.Exists(settingsPath);
            if (!restarted) await store.SaveAsync(new AppSettings { PrinterName = "synthetic-offline-printer" });
            if ((await new JsonSettingsService(settingsPath).LoadAsync()).PrinterName != "synthetic-offline-printer")
                throw new InvalidOperationException("Settings did not survive restart.");
            var windows = new Window[] { new MainWindow(), new SettingsWindow() };
            foreach (var window in windows)
            {
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(1280, 720));
                content.Arrange(new Rect(0, 0, 1280, 720));
                content.UpdateLayout();
                // Exercise compiled BAML, themes, merged resources and tab content from the bundle.
                foreach (var tabs in Descendants(content).OfType<TabControl>().ToArray())
                    for (var i = 0; i < tabs.Items.Count; i++) { tabs.SelectedIndex = i; content.UpdateLayout(); }
                var bitmap = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(Path.Combine(root, window.GetType().Name + ".png"))) encoder.Save(output);
                window.Close();
            }
            // Exercise the bundled PDFium native engine from the copied single-file EXE.
            // Synthetic bytes only: this offline mode must not open a real profile or spool a job.
            var shipment = new ShipmentReference(new(MarketplaceKind.Prom, "synthetic", "1"), "SYNTHETIC-1", ShippingCarrier.NovaPoshta, "SYNTHETIC-TTN");
            var pdf = SyntheticShippingPdf.Create(pages: 2);
            var document = new ShippingLabelDocument(shipment,"synthetic","synthetic-document",pdf,"synthetic-hash",DateTimeOffset.UtcNow,2);
            var labelPages = await new WindowsShippingLabelRenderer().RenderAsync(document,203,203);
            using var notices = typeof(PackageVerification).Assembly.GetManifestResourceStream("CheckboxBatchPrinter.PdfiumNotices")
                ?? throw new InvalidOperationException("PDFium licenses missing from bundle.");
            if (labelPages.Count != 2 || labelPages.Any(p => p.Png.Length == 0 || Math.Abs(p.WidthMm - 100) > 0.05))
                throw new InvalidOperationException("Bundled PDF renderer failed.");
            foreach (var page in labelPages)
            {
                using var input = new MemoryStream(page.Png);
                var frame = new PngBitmapDecoder(input,BitmapCreateOptions.None,BitmapCacheOption.OnLoad).Frames[0];
                var gray = new FormatConvertedBitmap(frame,PixelFormats.Gray8,null,0);
                var pixels = new byte[gray.PixelWidth * gray.PixelHeight]; gray.CopyPixels(pixels,gray.PixelWidth,0);
                if (pixels.Count(p => p < 100) < 5000) throw new InvalidOperationException("Bundled PDF content missing.");
                var interiorInk = 0;
                for (var y = 20; y < gray.PixelHeight-20; y++) for (var x = 20; x < gray.PixelWidth-20; x++)
                    if (pixels[y * gray.PixelWidth+x] < 100) interiorInk++;
                if (interiorInk < 5000) throw new InvalidOperationException("Bundled PDF barcode/content missing inside edges.");
                await File.WriteAllBytesAsync(Path.Combine(root,$"SyntheticLabel-{page.PageNumber}.png"),page.Png);
            }
            var labelSettings = new LabelPrintSettings { PrinterName = "synthetic-offline-printer", WidthMm = 101.5, HeightMm = 101.5 };
            var labelGeometry = new LabelPrinterGeometry(101.5,101.5,0,0,101.5,101.5,203,203);
            var preview = new LabelPreviewWindow(new("synthetic-batch",labelPages,labelSettings),labelGeometry,[],false);
            var previewContent = (FrameworkElement)preview.Content;
            previewContent.Measure(new Size(980,720)); previewContent.Arrange(new Rect(0,0,980,720)); previewContent.UpdateLayout();
            var previewBitmap = new RenderTargetBitmap(980,720,96,96,PixelFormats.Pbgra32); previewBitmap.Render(previewContent);
            var previewEncoder = new PngBitmapEncoder(); previewEncoder.Frames.Add(BitmapFrame.Create(previewBitmap));
            using (var output = File.Create(Path.Combine(root,"ShippingLabelPreview.png"))) previewEncoder.Save(output);
            preview.Close();
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new {
                Success = true, AppEnvironment.Channel, AppEnvironment.Version, AppEnvironment.Commit,
                ShippingPdfPages = labelPages.Count,
                AppEnvironment.WindowTitle, DefaultDataRoot = AppEnvironment.DataRoot, TestDataRoot = data,
                Restarted = restarted, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Runtime = RuntimeEnvironment.GetRuntimeDirectory(), Framework = RuntimeInformation.FrameworkDescription
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            if (report != null) await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Success = false, Error = ex.GetType().Name }));
            return 1;
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
