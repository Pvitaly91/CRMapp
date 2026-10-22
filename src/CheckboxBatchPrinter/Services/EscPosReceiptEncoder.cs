using System.Buffers.Binary;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Services;

// This stream targets the RPP210 384-dot ESC/POS head. It contains binary raster
// commands only: receipt text and our order/TTN header are never sent as text.
internal static class EscPosReceiptEncoder
{
    internal const int HeadWidthDots = 384;
    internal const int NativeDpi = 203;
    internal const int RowsPerBlock = 24;
    internal const int MaximumReceiptHeightDots = 20000;
    private const long MaximumDecodedBytes = 64L * 1024 * 1024;
    private const int OuterMarginDots = 5;
    private const int SeparatorPaddingDots = 6;
    private const int SeparatorThicknessDots = 2;
    private const int HeaderGapDots = 4;
    private const int FinalFeedDots = 16;

    internal sealed class Raster(int width, int height, byte[] bits)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public int Stride { get; } = (width + 7) / 8;
        public ReadOnlyMemory<byte> Bits { get; } = bits;
    }

    internal static byte[] Build(IReadOnlyList<PrintReceiptDocument> receipts, AppSettings settings)
    {
        var raster = BuildRaster(receipts, settings);
        using var output = new MemoryStream();
        // Initialize once at the job boundary; left alignment and normal mode.
        output.Write([0x1b, 0x40, 0x1b, 0x61, 0, 0x1b, 0x21, 0]);
        for (var top = 0; top < raster.Height; top += RowsPerBlock)
        {
            var count = Math.Min(RowsPerBlock, raster.Height - top);
            output.Write([0x1d, 0x76, 0x30, 0, (byte)raster.Stride, (byte)(raster.Stride >> 8),
                (byte)count, (byte)(count >> 8)]);
            output.Write(raster.Bits.Span.Slice(top * raster.Stride, count * raster.Stride));
        }
        // The raster advances by its own height. Only a short tear-edge feed is
        // added here; no form feed, cutter command or printer-resident text.
        output.Write([0x1b, 0x4a, FinalFeedDots]);
        return output.ToArray();
    }

    internal static Raster BuildRaster(IReadOnlyList<PrintReceiptDocument> receipts, AppSettings settings)
    {
        if (receipts.Count is < 1 or > ReceiptPrintPolicy.MaxReceiptsPerJob)
            throw new ArgumentException("Одне завдання має містити рівно один чек.", nameof(receipts));
        if (!double.IsFinite(settings.PrintableWidthMm) || settings.PrintableWidthMm <= 0 ||
            settings.PrintableWidthMm > 48 || settings.PrintableWidthMm > settings.EffectivePaperWidthMm)
            throw new InvalidOperationException("Для RPP210 ширина друку має бути не більшою за 48 мм (384 точки).");
        // The hardware is eight dots/mm. Use an integral head width, never a
        // fractional DIP rectangle that would resample the image a second time.
        var width = (int)Math.Round(settings.PrintableWidthMm * 8, MidpointRounding.AwayFromZero);
        if (width < 1 || width > HeadWidthDots)
            throw new InvalidOperationException("Некоректна ширина зображення чека для RPP210.");
        var stride = (width + 7) / 8;
        using var rows = new MemoryStream();
        WriteBlankRows(rows, stride, OuterMarginDots);
        for (var index = 0; index < receipts.Count; index++)
        {
            var receipt = receipts[index];
            var bitmap = DecodeReceipt(receipt.Png, width);
            var header = RenderHeader(width, receipt.OrderNumber, receipt.TrackingText);
            if (bitmap.Height + (header?.Height ?? 0) + HeaderGapDots + OuterMarginDots * 2 > MaximumReceiptHeightDots)
                throw new InvalidOperationException("Чек перевищує допустиму довжину друку RPP210. Завдання не передано принтеру.");
            if (header is not null)
            {
                rows.Write(header.Bits.Span);
                WriteBlankRows(rows, stride, HeaderGapDots);
            }
            rows.Write(bitmap.Bits.Span);
            if (index + 1 < receipts.Count)
            {
                WriteBlankRows(rows, stride, SeparatorPaddingDots);
                var separator = new byte[stride];
                for (var x = (int)Math.Round(width * 0.09); x < (int)Math.Round(width * 0.91); x++)
                    separator[x / 8] |= (byte)(0x80 >> (x % 8));
                for (var line = 0; line < SeparatorThicknessDots; line++) rows.Write(separator);
                WriteBlankRows(rows, stride, SeparatorPaddingDots);
            }
        }
        WriteBlankRows(rows, stride, OuterMarginDots);
        return new Raster(width, checked((int)(rows.Length / stride)), rows.ToArray());
    }

    internal static Raster DecodeReceipt(byte[] png, int targetWidth)
    {
        if (targetWidth is < 1 or > HeadWidthDots) throw new ArgumentOutOfRangeException(nameof(targetWidth));
        // Inspect IHDR before asking WPF to allocate/decompress the PNG. The
        // source and converted BGRA buffers together must fit the decode bound.
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length < 33 || png.Length > MaximumDecodedBytes || !png.AsSpan(0, 8).SequenceEqual(signature) ||
            BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8, 4)) != 13 ||
            !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidOperationException("Отримано некоректне PNG-зображення чека.");
        var sourceWidth = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        var sourceHeight = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        if (sourceWidth == 0 || sourceHeight == 0 || sourceWidth > int.MaxValue || sourceHeight > int.MaxValue ||
            (long)sourceWidth * sourceHeight > MaximumDecodedBytes / 8)
            throw new InvalidOperationException("Зображення чека завелике для безпечної підготовки.");
        var scaledHeight = Math.Max(1, Math.Round(sourceHeight * (double)targetWidth / sourceWidth));
        if (scaledHeight > MaximumReceiptHeightDots)
            throw new InvalidOperationException("Чек перевищує допустиму довжину друку RPP210.");
        var height = (int)scaledHeight;
        using var input = new MemoryStream(png, writable: false);
        var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth != sourceWidth || frame.PixelHeight != sourceHeight)
            throw new InvalidOperationException("Розміри PNG-зображення чека не відповідають заголовку.");
        return ToMonochrome(frame, targetWidth, height);
    }

    private static Raster? RenderHeader(int width, string orderNumber, string trackingText)
    {
        var text = ReceiptPrintHeader.Text(orderNumber, trackingText);
        if (text.Length == 0) return null;
        if (text.Length > 4096) throw new InvalidOperationException("Заголовок чека завеликий.");
        var widthDip = width * 96d / NativeDpi;
        var header = new TextBlock
        {
            Text = text, Width = widthDip, FontFamily = new FontFamily("Segoe UI"), FontSize = ReceiptPrintHeader.FontSizeDip,
            FontWeight = FontWeights.Normal, Foreground = Brushes.Black, Background = Brushes.White,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            SnapsToDevicePixels = true, UseLayoutRounding = true
        };
        TextOptions.SetTextFormattingMode(header, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(header, TextRenderingMode.Aliased);
        header.Measure(new Size(widthDip, double.PositiveInfinity));
        var height = (int)Math.Ceiling(header.DesiredSize.Height * NativeDpi / 96d);
        if (height < 1 || height > 4096) throw new InvalidOperationException("Заголовок чека завеликий.");
        header.Arrange(new Rect(0, 0, widthDip, height * 96d / NativeDpi)); header.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, NativeDpi, NativeDpi, PixelFormats.Pbgra32);
        bitmap.Render(header); bitmap.Freeze();
        return ToMonochrome(bitmap, width, height);
    }

    private static Raster ToMonochrome(BitmapSource source, int width, int height)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var sourceStride = checked(source.PixelWidth * 4);
        var bgra = new byte[checked(sourceStride * source.PixelHeight)];
        converted.CopyPixels(bgra, sourceStride, 0);
        var stride = (width + 7) / 8;
        var packed = new byte[checked(stride * height)];
        var native = source.PixelWidth == width && source.PixelHeight == height;
        double Luminance(int x, int y)
        {
            var offset = y * sourceStride + x * 4;
            var gray = (77 * bgra[offset + 2] + 150 * bgra[offset + 1] + 29 * bgra[offset] + 128) >> 8;
            var alpha = bgra[offset + 3];
            return (gray * alpha + 255 * (255 - alpha) + 127) / 255;
        }
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            double gray;
            if (native) gray = Luminance(x, y);
            else
            {
                // One area resampling directly from original PNG pixels onto
                // the integer head grid; no intermediate page bitmap or dither.
                var left = x * (double)source.PixelWidth / width;
                var right = (x + 1) * (double)source.PixelWidth / width;
                var top = y * (double)source.PixelHeight / height;
                var bottom = (y + 1) * (double)source.PixelHeight / height;
                var sum = 0d;
                for (var sy = (int)Math.Floor(top); sy < Math.Ceiling(bottom); sy++)
                for (var sx = (int)Math.Floor(left); sx < Math.Ceiling(right); sx++)
                    sum += Luminance(Math.Min(sx, source.PixelWidth - 1), Math.Min(sy, source.PixelHeight - 1)) *
                        (Math.Min(right, sx + 1) - Math.Max(left, sx)) *
                        (Math.Min(bottom, sy + 1) - Math.Max(top, sy));
                gray = sum / ((right - left) * (bottom - top));
            }
            if (gray < 128) packed[y * stride + x / 8] |= (byte)(0x80 >> (x % 8));
        }
        return new Raster(width, height, packed);
    }

    private static void WriteBlankRows(Stream output, int stride, int height) => output.Write(new byte[stride * height]);
}
