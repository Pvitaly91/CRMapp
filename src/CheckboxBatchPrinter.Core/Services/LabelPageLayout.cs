using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public sealed record LabelPrinterGeometry(double WidthMm, double HeightMm, double LeftMm, double TopMm,
    double PrintableWidthMm, double PrintableHeightMm, double DpiX, double DpiY);
public sealed record LabelPagePlacement(double LeftMm, double TopMm, double WidthMm, double HeightMm);
public static class LabelPageLayout
{
    public static LabelPagePlacement Place(ShippingLabelPage page, LabelPrintSettings settings, LabelPrinterGeometry printer)
    {
        settings.Validate();
        var width = page.WidthMm * settings.ScalePercent / 100;
        var height = page.HeightMm * settings.ScalePercent / 100;
        var x = (printer.WidthMm - width) / 2 + settings.OffsetXmm;
        var y = (printer.HeightMm - height) / 2 + settings.OffsetYmm;
        const double roundingTolerance = 0.08;
        if (x < printer.LeftMm - roundingTolerance || y < printer.TopMm - roundingTolerance ||
            x + width > printer.LeftMm + printer.PrintableWidthMm + roundingTolerance ||
            y + height > printer.TopMm + printer.PrintableHeightMm + roundingTolerance)
            throw new ShippingLabelException(LabelFailureKind.UnsupportedFormat,
                "Етикетка не поміщається в доступну область принтера. Виберіть сумісний формат або явно зменште масштаб у налаштуваннях; A4 не обрізається автоматично.");
        return new(x, y, width, height);
    }
}
