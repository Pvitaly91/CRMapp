using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Services;

public sealed record RenderedTestReceipt(BitmapSource Bitmap, double WidthDip, double HeightDip, double Dpi);

public static class TestReceiptBitmapRenderer
{
    public static RenderedTestReceipt Render(AppSettings settings, double dpi)
    {
        if (!double.IsFinite(dpi) || dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi));

        var widthDip = settings.PrintableWidthMm * PrintGeometry.DipPerMillimeter;
        var content = new StackPanel
        {
            Background = Brushes.White,
            Children =
            {
                EdgeLabel("← ЛІВИЙ КРАЙ · ВЕРХ · ПРАВИЙ КРАЙ →"),
                new TextBlock
                {
                    Text = "CHECKBOX BATCH PRINTER",
                    FontSize = 18,
                    FontWeight = FontWeights.Bold,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(6, 10, 6, 4)
                },
                new TextBlock
                {
                    Text = "Тестовий друк",
                    FontSize = 17,
                    FontWeight = FontWeights.SemiBold,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(6, 3, 6, 3)
                },
                new TextBlock
                {
                    Text = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"),
                    FontSize = 14,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(6, 3, 6, 3)
                },
                new TextBlock
                {
                    Text = $"Папір: {settings.EffectivePaperWidthMm:0.#} мм\nОбласть: {settings.PrintableWidthMm:0.#} мм",
                    FontSize = 14,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(6, 3, 6, 3)
                },
                new TextBlock
                {
                    Text = "Перевірте текст, рамку, QR-зону та автообрізання",
                    FontSize = 12,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(6, 5, 6, 5)
                },
                new Border
                {
                    BorderBrush = Brushes.Black,
                    BorderThickness = new Thickness(1),
                    Height = 34,
                    Margin = new Thickness(8, 4, 8, 4),
                    Child = new TextBlock
                    {
                        Text = "КОНТРОЛЬНА QR-ЗОНА",
                        FontSize = 10,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                },
                new TextBlock
                {
                    Text = "Результат фізичного друку підтверджує користувач",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(6, 5, 6, 8)
                },
                EdgeLabel("← ЛІВИЙ КРАЙ · НИЖНЯ МЕЖА · ПРАВИЙ КРАЙ →")
            }
        };

        var frame = new Border
        {
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1.5),
            Background = Brushes.White,
            Margin = new Thickness(2),
            Child = content
        };
        var root = new Grid
        {
            Width = widthDip,
            Background = Brushes.White,
            UseLayoutRounding = false,
            Children = { frame }
        };
        root.Measure(new Size(widthDip, double.PositiveInfinity));
        var heightDip = Math.Ceiling(root.DesiredSize.Height);
        root.Arrange(new Rect(0, 0, widthDip, heightDip));
        root.UpdateLayout();

        var pixelWidth = Math.Max(1, (int)Math.Ceiling(widthDip * dpi / 96d));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(heightDip * dpi / 96d));
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        return new RenderedTestReceipt(bitmap, widthDip, heightDip, dpi);
    }

    private static TextBlock EdgeLabel(string text) => new()
    {
        Text = text,
        FontSize = 8,
        FontWeight = FontWeights.Bold,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(3, 3, 3, 3)
    };
}
