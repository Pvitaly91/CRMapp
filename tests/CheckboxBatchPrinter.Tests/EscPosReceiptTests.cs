using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class EscPosReceiptTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("STA RPP210 native PNG preserves every black/white dot including command bytes", () => Sta(NativePixels)),
        ("STA RPP210 855-pixel PNG is resampled once with complete footer", () => Sta(WideSource)),
        ("STA RPP210 raster composites alpha white and clears unused byte bits", () => Sta(TransparencyAndPadding)),
        ("STA RPP210 seven and eight receipts retain independent job boundaries headers and binary rows", () => Sta(IndividualFraming)),
        ("STA RPP210 headers are raster data and source receipt is unchanged below them", () => Sta(Header)),
        ("STA RPP210 rejects corrupt dimensions oversize receipts and multi-receipt jobs", () => Sta(InvalidInput))
    ];

    private static Task Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
        return Task.CompletedTask;
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static AppSettings Settings(double width = 48) => new() { PrintableWidthMm = width, PaperWidth = PaperWidth.Mm58 };
    private static byte[] Png(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> color)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = color(x, y); var offset = (y * width + x) * 4;
            pixels[offset] = value.B; pixels[offset + 1] = value.G; pixels[offset + 2] = value.R; pixels[offset + 3] = value.A;
        }
        var source = BitmapSource.Create(width, height, 203, 203, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }
    private static (byte, byte, byte, byte) Gray(bool black, byte alpha = 255) => black
        ? ((byte)0, (byte)0, (byte)0, alpha) : ((byte)255, (byte)255, (byte)255, alpha);
    private static bool Black(EscPosReceiptEncoder.Raster raster, int x, int y) =>
        (raster.Bits.Span[y * raster.Stride + x / 8] & (0x80 >> (x % 8))) != 0;

    private static void NativePixels()
    {
        var expected = new byte[48 * 97];
        new Random(74829).NextBytes(expected);
        expected[0] = 0x1b; expected[1] = 0x40; expected[2] = 0x1d; expected[3] = 0x76;
        var png = Png(384, 97, (x, y) => Gray((expected[y * 48 + x / 8] & (0x80 >> (x % 8))) != 0));
        var raster = EscPosReceiptEncoder.DecodeReceipt(png, 384);
        Check(raster.Width == 384 && raster.Height == 97 && raster.Stride == 48, "Native receipt dimensions changed.");
        Check(raster.Bits.Span.SequenceEqual(expected), "A native black/white edge was blurred, shifted or dithered.");
    }

    private static void WideSource()
    {
        // Production Checkbox images are 855 pixels wide. White right-half and
        // a full-width final rule expose clipped edges and inaccurate resizing.
        var png = Png(855, 222, (x, y) => Gray(x < 427 || y >= 214));
        var raster = EscPosReceiptEncoder.DecodeReceipt(png, 384);
        Check(raster.Height == 100 && raster.Width == 384, "855-pixel source changed aspect ratio.");
        for (var x = 0; x < 384; x++)
        {
            Check(Black(raster, x, 10) == (x < 192), "Single area reduction changed a sharp vertical boundary.");
            Check(Black(raster, x, 99), "The final receipt rule was truncated.");
        }
        var alternating = Png(768, 2, (x, _) => Gray(x % 2 == 0));
        var reduced = EscPosReceiptEncoder.DecodeReceipt(alternating, 384);
        Check(reduced.Bits.Span.ToArray().All(value => value == 255),
            "Downscaling did not average both source columns before thresholding.");
    }

    private static void TransparencyAndPadding()
    {
        var png = Png(384, 12, (x, _) => x switch
        {
            < 96 => Gray(true, 0),
            < 192 => Gray(true, 127),
            < 288 => Gray(true, 128),
            _ => Gray(true)
        });
        var raster = EscPosReceiptEncoder.DecodeReceipt(png, 384);
        for (var x = 0; x < 384; x++) Check(Black(raster, x, 5) == (x >= 192), "PNG alpha was not composited onto white.");
        var narrow = EscPosReceiptEncoder.DecodeReceipt(Png(383, 3, (_, _) => Gray(true)), 383);
        Check(narrow.Bits.Span.ToArray().Where((_, index) => index % 48 == 47).All(value => value == 254),
            "Unused final-byte bits could print outside the receipt width.");
    }

    private static void IndividualFraming()
    {
        foreach (var count in new[] { 7, 8 })
        {
            var receipts = Enumerable.Range(1, count).Select(index => new PrintReceiptDocument(
                Png(384, 35 + index, (x, y) => Gray((x + y + index) % 3 == 0 || y == 34 + index)),
                "id-" + index, $"ORDER-{index:00}", $"TTN-{index:00}")).ToArray();
            var jobs = receipts.Chunk(ReceiptPrintPolicy.MaxReceiptsPerJob).ToArray();
            Check(jobs.Length == count && jobs.All(job => job.Length == 1), "RAW preparation combines or loses receipts.");
            for (var index = 0; index < jobs.Length; index++)
            {
                var job = jobs[index];
                Check(job[0].ReceiptId == receipts[index].ReceiptId, "RAW job order changed.");
                var raster = EscPosReceiptEncoder.BuildRaster(job, Settings());
                var stream = EscPosReceiptEncoder.Build(job, Settings());
                Check(stream.AsSpan(0, 8).SequenceEqual(new byte[] { 27, 64, 27, 97, 0, 27, 33, 0 }), "Unexpected job initialization.");
                using var rows = new MemoryStream();
                var offset = 8; var block = 0;
                while (offset < stream.Length - 3)
                {
                    Check(stream.AsSpan(offset, 4).SequenceEqual(new byte[] { 29, 118, 48, 0 }), "Unframed text or wrong raster command.");
                    var widthBytes = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(offset + 4, 2));
                    var height = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(offset + 6, 2));
                    Check(widthBytes == 48 && height is > 0 and <= 24, "Raster block length does not match head geometry.");
                    offset += 8;
                    Check(offset + widthBytes * height <= stream.Length - 3, "Raster block contains truncated image bytes.");
                    rows.Write(stream.AsSpan(offset, widthBytes * height)); offset += widthBytes * height; block++;
                }
                Check(offset == stream.Length - 3 && stream.AsSpan(offset).SequenceEqual(new byte[] { 27, 74, 16 }),
                    "Job ends with unexpected text, extra initialization or excessive paper feed.");
                Check(block == (raster.Height + 23) / 24 && rows.ToArray().AsSpan().SequenceEqual(raster.Bits.Span),
                    "Framing changed bitmap bytes, omitted rows, or added a trailing print command.");
                var body = EscPosReceiptEncoder.DecodeReceipt(job[0].Png, 384);
                var bodyOffset = (raster.Height - 5 - body.Height) * raster.Stride;
                Check(bodyOffset > 5 * raster.Stride && raster.Bits.Span.Slice(5 * raster.Stride, bodyOffset - 5 * raster.Stride).ContainsAnyExcept((byte)0),
                    "An individual order/TTN header disappeared at a job boundary.");
                Check(raster.Bits.Span.Slice(bodyOffset, body.Bits.Length).SequenceEqual(body.Bits.Span),
                    "An individual job resampled or lost its receipt body.");
                for (var x = 0; x < 384; x++) Check(Black(raster, x, raster.Height - 6), "An individual job lost its receipt footer.");
            }
        }
    }

    private static void Header()
    {
        var png = Png(384, 20, (x, y) => Gray((x + y) % 2 == 0));
        var plain = EscPosReceiptEncoder.DecodeReceipt(png, 384);
        var raster = EscPosReceiptEncoder.BuildRaster([new(png, "id", "12345", "НП: 20450000000000")], Settings());
        var bodyOffset = (raster.Height - 5 - plain.Height) * raster.Stride;
        Check(raster.Height > plain.Height + 14 && raster.Bits.Span.Slice(5 * raster.Stride, bodyOffset - 5 * raster.Stride).ContainsAnyExcept((byte)0),
            "Order/TTN header disappeared from the raster.");
        Check(raster.Bits.Span.Slice(bodyOffset, plain.Bits.Length).SequenceEqual(plain.Bits.Span), "Header layout resampled receipt pixels.");
    }

    private static void InvalidInput()
    {
        var valid = Png(384, 10, (_, _) => Gray(false));
        var receipt = new PrintReceiptDocument(valid, "id");
        var invalidDimensions = valid.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(invalidDimensions.AsSpan(16, 4), 50000);
        BinaryPrimitives.WriteUInt32BigEndian(invalidDimensions.AsSpan(20, 4), 50000);
        var invalidHeight = valid.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(invalidHeight.AsSpan(16, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(invalidHeight.AsSpan(20, 4), 6000000);
        foreach (Action action in new Action[]
        {
            () => EscPosReceiptEncoder.Build([], Settings()),
            () => EscPosReceiptEncoder.Build([receipt, receipt], Settings()),
            () => EscPosReceiptEncoder.Build([receipt, receipt, receipt], Settings()),
            () => EscPosReceiptEncoder.Build([receipt, receipt, receipt, receipt], Settings()),
            () => EscPosReceiptEncoder.BuildRaster([receipt, receipt], Settings()),
            () => EscPosReceiptEncoder.Build([receipt], Settings(49)),
            () => EscPosReceiptEncoder.Build([receipt], Settings(double.NaN)),
            () => EscPosReceiptEncoder.DecodeReceipt(valid[..20], 384),
            () => EscPosReceiptEncoder.DecodeReceipt(invalidDimensions, 384),
            () => EscPosReceiptEncoder.DecodeReceipt(invalidHeight, 384),
            () => EscPosReceiptEncoder.DecodeReceipt(valid, 0)
        })
        {
            var rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Invalid or unbounded receipt raster passed preparation.");
        }
    }
}
