using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Views;

public sealed class LabelPreviewWindow : Window
{
    public LabelPreviewWindow(LabelPrintBatch batch, LabelPrinterGeometry geometry, IReadOnlyList<LabelPrintAttempt> previous, bool allowPrint)
    {
        Title = "Етикетки — перегляд і підтвердження пакета"; Width = 1000; Height = 760; MinWidth = 700; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(12), Background = Brushes.White };
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top);
        header.Children.Add(new TextBlock { Text = $"Принтер: {batch.Settings.PrinterName}\nНосій драйвера: {batch.Settings.WidthMm:0.##} × {batch.Settings.HeightMm:0.##} мм · {geometry.DpiX:0} × {geometry.DpiY:0} DPI · масштаб {batch.Settings.ScalePercent:0.##}% · копій {batch.Settings.Copies}\nGDI-рамка: {geometry.WidthMm:0.##} × {geometry.HeightMm:0.##} мм; область друку: {geometry.PrintableWidthMm:0.##} × {geometry.PrintableHeightMm:0.##} мм", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        header.Children.Add(new TextBlock { Text = $"Замовлень: {(batch.SelectedOrderCount > 0 ? batch.SelectedOrderCount : batch.Pages.Select(p => p.Shipment.Order).Distinct().Count())} · ТТН: {batch.Pages.Select(p => (p.Shipment.Carrier, p.Shipment.TrackingNumber)).Distinct().Count()} · етикеток: {batch.Pages.Count} · сторінок з копіями: {batch.Pages.Count * batch.Settings.Copies}", FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Одна етикетка — одна сторінка. Один Windows job. Фізичний вихід не гарантується статусом черги.", Margin = new Thickness(0, 6, 0, 8) });
        if (batch.Pages.Any(p => p.Source == "NovaPoshta.Direct.SDKCandidate"))
            header.Children.Add(new TextBlock { Text = "Прямий NP: маршрут досліджено за відкритим SDK, не підтверджено офіційною документацією. Ці ТТН явно перевірено та дозволено у поточному сеансі; актуальний PDF перевірено повторно.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateBlue, Margin = new Thickness(0,0,0,8) });
        root.Children.Add(header);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(footer, Dock.Bottom);
        var repeat = new CheckBox { Content = "Розумію ризик дублювання всього пакета", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6), Visibility = previous.Count > 0 && allowPrint ? Visibility.Visible : Visibility.Collapsed };
        if (previous.Count > 0)
        {
            header.Children.Add(new TextBlock { Text = "УВАГА: ці етикетки вже передавалися або могли передатися Windows. Частина пакета могла надрукуватися. Повтор потребує окремого підтвердження ризику дублювання.", Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            header.Children.Add(new TextBlock { Text = string.Join("\n", previous.Select(a => $"{a.TimeUtc.LocalDateTime:g}: {a.State}, принтер {a.PrinterName}, job {a.WindowsJobId?.ToString() ?? "невідомий"}, {a.JobName}")), FontSize = 11, TextWrapping = TextWrapping.Wrap });
        }
        var print = new Button { Content = "Підтверджую — друк усього пакета", Padding = new Thickness(10, 6, 10, 6), IsEnabled = previous.Count == 0, Visibility = allowPrint ? Visibility.Visible : Visibility.Collapsed };
        repeat.Checked += (_, _) => print.IsEnabled = true; repeat.Unchecked += (_, _) => print.IsEnabled = false;
        print.Click += (_, _) => DialogResult = true;
        var close = new Button { Content = allowPrint ? "Скасувати" : "Закрити", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(8), IsCancel = true };
        footer.Children.Add(repeat); footer.Children.Add(print); footer.Children.Add(close); root.Children.Add(footer);
        var pages = new StackPanel();
        foreach (var page in batch.Pages)
        {
            pages.Children.Add(new TextBlock { Text = $"{page.Shipment.Order.Marketplace} · {ShippingCarrierNames.Display(page.Shipment.Carrier)} · замовлення №{page.Shipment.OrderNumber} · ТТН {page.Shipment.TrackingNumber} · сторінка/частина {page.PageNumber} · PDF {page.WidthMm:0.##} × {page.HeightMm:0.##} мм", Margin = new Thickness(0, 12, 0, 5), TextWrapping = TextWrapping.Wrap });
            pages.Children.Add(BuildPreviewPage(page, batch.Settings, geometry));
        }
        root.Children.Add(new ScrollViewer { Content = pages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }
    internal static FrameworkElement BuildPreviewPage(ShippingLabelPage page, LabelPrintSettings settings, LabelPrinterGeometry geometry)
    {
        var placement = LabelPageLayout.Place(page, settings, geometry);
        const double dipPerMm = 96 / 25.4;
        var mediaWidth = settings.Landscape ? settings.HeightMm : settings.WidthMm;
        var mediaHeight = settings.Landscape ? settings.WidthMm : settings.HeightMm;
        var paper = new Canvas { Width = mediaWidth * dipPerMm, Height = mediaHeight * dipPerMm, Background = Brushes.White };
        using var input = new MemoryStream(page.Png, false);
        var source = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var ownedBitmap = new WriteableBitmap(source); ownedBitmap.Freeze();
        var image = new Image { Source = ownedBitmap, Width = placement.WidthMm * dipPerMm, Height = placement.HeightMm * dipPerMm, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(image, (placement.LeftMm - (geometry.WidthMm-mediaWidth)/2) * dipPerMm);
        Canvas.SetTop(image, (placement.TopMm - (geometry.HeightMm-mediaHeight)/2) * dipPerMm); paper.Children.Add(image);
        return new Border { Child = paper, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4) };
    }
}
