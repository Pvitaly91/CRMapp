using System.Globalization;
using System.IO;
using System.Text;

namespace CheckboxBatchPrinter.Infrastructure;

// Offline QA only, never used as a substitute for carrier documents.
internal static class SyntheticShippingPdf
{
    internal static byte[] Create(double widthMm = 100, double heightMm = 100, int pages = 1, int rotation = 0)
    {
        string N(double n) => n.ToString("0.###", CultureInfo.InvariantCulture);
        var width = widthMm * 72 / 25.4; var height = heightMm * 72 / 25.4;
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "" };
        var kids = new List<string>();
        for (var index = 0; index < pages; index++)
        {
            var pageId = objects.Count + 1; var contentId = pageId + 1;
            kids.Add($"{pageId} 0 R");
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(width)} {N(height)}] /Rotate {rotation} /Resources << >> /Contents {contentId} 0 R >>");
            var ink = new StringBuilder($"0 g 0 G 1 w 3 3 {N(width - 6)} {N(height - 6)} re S\n");
            // Barcode-like stripe pattern plus quiet zones and four full edge markers.
            for (var bar = 0; bar < 40; bar++) ink.Append($"{N(20 + bar * 4)} 35 {(bar % 3 == 0 ? 2 : 1)} 45 re f\n");
            ink.Append($"12 {N(height - 30)} {N(20 + index * 10)} 8 re f\n");
            var stream = ink.ToString();
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream");
        }
        objects[1] = $"<< /Type /Pages /Count {pages} /Kids [{string.Join(' ', kids)}] >>";
        using var output = new MemoryStream();
        void Write(string s) { var b = Encoding.ASCII.GetBytes(s); output.Write(b); }
        Write("%PDF-1.4\n");
        var offsets = new List<long> { 0 };
        for (var index = 0; index < objects.Count; index++) { offsets.Add(output.Position); Write($"{index + 1} 0 obj\n{objects[index]}\nendobj\n"); }
        var xref = output.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) Write(offset.ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }
}
