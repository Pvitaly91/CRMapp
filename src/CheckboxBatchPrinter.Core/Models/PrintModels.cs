namespace CheckboxBatchPrinter.Core.Models;

public enum PrintItemStatus
{
    Waiting,
    Downloading,
    Printing,
    Done,
    Error
}

public sealed record BatchItemResult<T>(T Item, bool Success, Exception? Error);

public sealed record BatchResult<T>(IReadOnlyList<BatchItemResult<T>> Items)
{
    public int SuccessCount => Items.Count(x => x.Success);
    public int ErrorCount => Items.Count - SuccessCount;
}

public sealed record PrintedReceiptRecord(
    string AccountContext,
    string ReceiptId,
    string PrinterName,
    DateTimeOffset PrintedAtUtc);

public readonly record struct PrintGeometry(double WidthDip, double HeightDip, double MarginDip)
{
    private const double DipPerMillimeter = 96d / 25.4d;

    public static PrintGeometry Calculate(
        int sourcePixelWidth,
        int sourcePixelHeight,
        double printableWidthMm,
        double paperWidthMm,
        double marginMm = 0.8)
    {
        if (sourcePixelWidth <= 0 || sourcePixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourcePixelWidth), "PNG dimensions must be positive.");
        if (paperWidthMm < 20 || printableWidthMm <= 0 || printableWidthMm > paperWidthMm)
            throw new ArgumentOutOfRangeException(nameof(printableWidthMm), "Printable width must fit the paper.");

        var margin = Math.Max(0, marginMm) * DipPerMillimeter;
        var width = printableWidthMm * DipPerMillimeter;
        var height = width * sourcePixelHeight / sourcePixelWidth;
        return new PrintGeometry(width, height, margin);
    }
}
