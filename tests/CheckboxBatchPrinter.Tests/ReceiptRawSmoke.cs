using System.IO;
using System.Printing;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.Tests;

// Opt-in diagnostics only. No StartDoc, WritePrinter, API client, or profile write.
internal static class ReceiptRawSmoke
{
    private static AppSettings Settings => new() { PaperWidth = PaperWidth.Custom, CustomPaperWidthMm = 57, PrintableWidthMm = 48 };

    internal static void Preview(string inputPath, string outputPath)
    {
        var source = File.ReadAllBytes(inputPath);
        var data = EscPosReceiptEncoder.Build([new PrintReceiptDocument(source, "preview", "TEST", "TEST-TTN")], Settings);
        var (pixels, width, height) = Decode(data);
        var image = BitmapSource.Create(width, height, 203, 203, PixelFormats.Gray8, null, pixels, width);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var output = File.Create(outputPath); encoder.Save(output);
        Console.WriteLine($"PASS decoded RAW preview: {width}x{height} dots, {data.Length} bytes; no print job created.");
    }

    internal static void Driver(string printerName)
    {
        using var server = new LocalPrintServer();
        using var queue = new PrintQueue(server, printerName);
        if (!WindowsPrintService.UsesDirectReceiptRaster(queue.QueueDriver.Name))
            throw new InvalidOperationException("Queue does not select the direct receipt route.");
        WindowsRawPrinter.ValidatePrinter(printerName);
        var source = new byte[384 * 200]; Array.Fill(source, (byte)255);
        for (var x = 0; x < 384; x++) { source[x] = 0; source[source.Length - 384 + x] = 0; }
        var image = BitmapSource.Create(384, 200, 203, 203, PixelFormats.Gray8, null, source, 384);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream(); png.Save(stream);
        var receipts = Enumerable.Range(1, 8).Select(n => new PrintReceiptDocument(stream.ToArray(), $"test-{n}", $"TEST-{n}")).ToArray();
        var jobs = receipts.Chunk(ReceiptPrintPolicy.MaxReceiptsPerJob).Select(group => EscPosReceiptEncoder.Build(group, Settings)).ToArray();
        if (jobs.Length != receipts.Length) throw new InvalidOperationException("RAW preparation must create one job per receipt.");
        foreach (var job in jobs) _ = Decode(job);
        Console.WriteLine($"PASS readonly RPP210 driver verified on native handle; 8 receipts prepared in {jobs.Length} separate RAW jobs; no job created.");
    }

    private static (byte[] Pixels, int Width, int Height) Decode(byte[] data)
    {
        if (!data.AsSpan(0, 8).SequenceEqual(new byte[] { 0x1b, 0x40, 0x1b, 0x61, 0, 0x1b, 0x21, 0 }) ||
            !data.AsSpan(data.Length - 3).SequenceEqual(new byte[] { 0x1b, 0x4a, 16 }))
            throw new InvalidDataException("Unexpected RAW job boundary.");
        using var output = new MemoryStream();
        var width = 0;
        for (var offset = 8; offset < data.Length - 3;)
        {
            if (offset + 8 > data.Length - 3 || !data.AsSpan(offset, 4).SequenceEqual(new byte[] { 0x1d, 0x76, 0x30, 0 }))
                throw new InvalidDataException("Unexpected data outside raster frame.");
            var stride = data[offset + 4] + data[offset + 5] * 256;
            var rows = data[offset + 6] + data[offset + 7] * 256;
            offset += 8;
            if (stride is < 1 or > 48 || rows is < 1 or > 24 || offset + stride * rows > data.Length - 3 ||
                width != 0 && width != stride * 8) throw new InvalidDataException("Invalid raster dimensions.");
            width = stride * 8;
            for (var i = 0; i < stride * rows; i++)
                for (var bit = 7; bit >= 0; bit--)
                    output.WriteByte((data[offset + i] & (1 << bit)) != 0 ? (byte)0 : (byte)255);
            offset += stride * rows;
        }
        return (output.ToArray(), width, checked((int)output.Length / width));
    }
}
