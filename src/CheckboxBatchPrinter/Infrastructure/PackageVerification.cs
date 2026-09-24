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
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new {
                Success = true, AppEnvironment.Channel, AppEnvironment.Version, AppEnvironment.Commit,
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
