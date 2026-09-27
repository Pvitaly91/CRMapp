using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Views;

// No printer service, print button, job coordinator or persistence; documents remain operation-local.
public sealed class NovaPoshtaDiagnosticPreviewWindow : Window
{
    public NovaPoshtaDiagnosticPreviewWindow(NovaPoshtaDiagnosticResult result, IReadOnlyList<ShippingLabelPage> pages)
    {
        Title = "Нова пошта — діагностичний PDF (без друку)"; Width = 900; Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(12), Background = Brushes.White };
        var header = new TextBlock { Text = $"{result.Message}\nТТН: {result.Lookup.TrackingNumber} · Ref: {(result.Lookup.DocumentRef.Length > 0 ? result.Lookup.DocumentRef : "не надано")}\nМісць за API: {result.Lookup.Places?.ToString() ?? "не надано"} · PDF-сторінок: {pages.Count}\nМетоди взято з відкритого SDK; офіційна документація не підтверджена. Фізичний друк не запускається.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) };
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(footer, Dock.Bottom);
        var accept = new Button { Content = "Підтверджений документ — дозволити в пакеті цього сеансу", Visibility = result.CanUseInBatch ? Visibility.Visible : Visibility.Collapsed,
            Margin = new Thickness(6), Padding = new Thickness(10,6,10,6) };
        accept.Click += (_, _) => DialogResult = true;
        footer.Children.Add(accept); footer.Children.Add(new Button { Content = "Закрити без дозволу пакета", IsCancel = true, Margin = new Thickness(6), Padding = new Thickness(10,6,10,6) });
        root.Children.Add(footer);
        var images = new StackPanel();
        foreach (var page in pages)
        {
            images.Children.Add(new TextBlock { Text = $"Сторінка {page.PageNumber}: {page.WidthMm:0.##} × {page.HeightMm:0.##} мм", Margin = new Thickness(4,12,4,4) });
            var settings = new LabelPrintSettings { PrinterName = "Діагностика — принтер не викликається", WidthMm = page.WidthMm, HeightMm = page.HeightMm };
            var geometry = new LabelPrinterGeometry(page.WidthMm, page.HeightMm, 0, 0, page.WidthMm, page.HeightMm, 203, 203);
            images.Children.Add(LabelPreviewWindow.BuildPreviewPage(page, settings, geometry));
        }
        root.Children.Add(new ScrollViewer { Content = images, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.White });
        Content = root;
    }
}
