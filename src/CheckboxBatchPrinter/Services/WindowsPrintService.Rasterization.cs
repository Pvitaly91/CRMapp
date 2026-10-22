using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CheckboxBatchPrinter.Services;

public sealed partial class WindowsPrintService
{
    // Both the temporary Pbgra32 surface and its Gray8 copy must fit this bound.
    // Receipt jobs are prepared lazily, so a large selection retains one group only.
    private const long MaximumReceiptRasterBytes = 64L * 1024 * 1024;

    internal static FixedDocument RasterizeReceiptDocument(FixedDocument document)
    {
        var flattened = new FixedDocument { PrintTicket = ((PrintTicket)document.PrintTicket).Clone() };
        foreach (var content in document.Pages)
        {
            var page = content.Child;
            var ticket = (PrintTicket)page.PrintTicket;
            var resolution = ticket.PageResolution;
            if (resolution?.X is not > 0 || resolution.Y is not > 0)
                throw new InvalidOperationException("Драйвер принтера не надав роздільну здатність для підготовки чеків. Перевірте налаштування принтера.");

            var pixelsWide = Math.Ceiling(page.Width * resolution.X.Value / 96d);
            var pixelsHigh = Math.Ceiling(page.Height * resolution.Y.Value / 96d);
            if (!double.IsFinite(pixelsWide) || !double.IsFinite(pixelsHigh) || pixelsWide < 1 || pixelsHigh < 1 ||
                pixelsWide > int.MaxValue || pixelsHigh > int.MaxValue ||
                pixelsWide * pixelsHigh > MaximumReceiptRasterBytes / 5d)
                throw new InvalidOperationException("Чек завеликий для безпечної підготовки зображення. Пакет не передано принтеру; перевірте формат паперу та роздільну здатність.");

            page.Measure(new Size(page.Width, page.Height));
            page.Arrange(new Rect(0, 0, page.Width, page.Height));
            page.UpdateLayout();
            var bounds = new Rect(0, 0, page.Width, page.Height);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                // Explicit white bounds remove alpha and include the whole prepared page,
                // including order/TTN text, receipt footer and inter-receipt separators.
                drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0,
                    pixelsWide * 96d / resolution.X.Value, pixelsHigh * 96d / resolution.Y.Value));
                var brush = new VisualBrush(page)
                {
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = bounds,
                    Stretch = Stretch.Fill
                };
                drawing.DrawRectangle(brush, null, bounds);
            }
            var rendered = new RenderTargetBitmap((int)pixelsWide, (int)pixelsHigh,
                resolution.X.Value, resolution.Y.Value, PixelFormats.Pbgra32);
            rendered.Render(visual);
            rendered.Freeze();
            var grayscale = new FormatConvertedBitmap(rendered, PixelFormats.Gray8, null, 0);
            grayscale.Freeze();
            var image = new Image
            {
                Source = grayscale, Width = page.Width, Height = page.Height,
                Stretch = Stretch.Fill, SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var rasterPage = new FixedPage
            {
                Width = page.Width, Height = page.Height, Background = Brushes.White,
                PrintTicket = ticket.Clone()
            };
            FixedPage.SetLeft(image, 0); FixedPage.SetTop(image, 0);
            rasterPage.Children.Add(image);
            rasterPage.Measure(new Size(page.Width, page.Height));
            rasterPage.Arrange(bounds); rasterPage.UpdateLayout();
            var rasterContent = new PageContent();
            ((IAddChild)rasterContent).AddChild(rasterPage); flattened.Pages.Add(rasterContent);
        }
        return flattened;
    }

    private static PageResolution ResolveReceiptResolution(PrintTicket validated, PrintCapabilities capabilities)
    {
        if (validated.PageResolution is { X: > 0, Y: > 0 } resolution)
            return new PageResolution(resolution.X.Value, resolution.Y.Value);
        var supported = capabilities.PageResolutionCapability
            .Where(item => item.X is > 0 && item.Y is > 0)
            .OrderBy(item => (long)item.X!.Value * item.Y!.Value).FirstOrDefault();
        if (supported is null)
            throw new InvalidOperationException("Драйвер принтера не надав роздільну здатність для підготовки чеків. Перевірте налаштування принтера.");
        return new PageResolution(supported.X!.Value, supported.Y!.Value);
    }
}
