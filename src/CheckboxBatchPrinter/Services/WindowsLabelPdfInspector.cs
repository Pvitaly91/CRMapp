using System.Runtime.InteropServices;
using System.Text;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using PDFiumCore;

namespace CheckboxBatchPrinter.Services;

public sealed class WindowsLabelPdfInspector : ILabelPdfInspector
{
    public async Task<LabelPdfInspection> InspectAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        ShippingLabelHttp.ValidatePdf(bytes);
        await WindowsShippingLabelRenderer.Gate.WaitAsync(cancellationToken);
        try
        {
            fpdfview.FPDF_InitLibrary();
            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var pdf = fpdfview.FPDF_LoadMemDocument(pinned.AddrOfPinnedObject(), bytes.Length, null);
                if (pdf is null || pdf.__Instance == IntPtr.Zero) throw Invalid();
                try
                {
                    if (fpdf_formfill.FPDF_GetFormType(pdf) != 0) throw Invalid();
                    var count = fpdfview.FPDF_GetPageCount(pdf);
                    if (count is < 1 or > 100) throw Invalid();
                    var pages = new List<LabelPdfPageInfo>(); var totalChars = 0;
                    for (var index = 0; index < count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        double width = 0, height = 0;
                        if (fpdfview.FPDF_GetPageSizeByIndex(pdf, index, ref width, ref height) == 0 || !double.IsFinite(width) || !double.IsFinite(height) ||
                            width <= 0 || height <= 0 || width > 1200 || height > 1200) throw Invalid();
                        var page = fpdfview.FPDF_LoadPage(pdf, index);
                        if (page is null || page.__Instance == IntPtr.Zero) throw Invalid();
                        try
                        {
                            var textPage = fpdf_text.FPDFTextLoadPage(page);
                            if (textPage is null || textPage.__Instance == IntPtr.Zero) throw Invalid();
                            try
                            {
                                var chars = fpdf_text.FPDFTextCountChars(textPage); totalChars += chars;
                                if (chars is < 0 or > 100000 || totalChars > 1000000) throw Invalid();
                                var text = new StringBuilder(chars);
                                for (var ch = 0; ch < chars; ch++)
                                {
                                    if (ch % 128 == 0) cancellationToken.ThrowIfCancellationRequested();
                                    var unicode = fpdf_text.FPDFTextGetUnicode(textPage, ch);
                                    if (unicode is '\r' or '\n' or '\t' or ' ') { text.Append((char)unicode); continue; }
                                    double left = 0, right = 0, bottom = 0, top = 0;
                                    // Do not confirm a TTN from text wholly outside the page. Unknown glyphs break the number.
                                    if (unicode > 0xffff || fpdf_text.FPDFTextGetCharBox(textPage, ch, ref left, ref right, ref bottom, ref top) == 0 ||
                                        left < 0 || right > width || bottom < 0 || top > height || right <= left || top <= bottom)
                                        text.Append('|');
                                    else text.Append((char)unicode);
                                }
                                pages.Add(new(index + 1, width * 25.4 / 72, height * 25.4 / 72, text.ToString()));
                            }
                            finally { fpdf_text.FPDFTextClosePage(textPage); }
                        }
                        finally { fpdfview.FPDF_ClosePage(page); }
                    }
                    return new(pages.AsReadOnly());
                }
                finally { fpdfview.FPDF_CloseDocument(pdf); }
            }
            finally { pinned.Free(); fpdfview.FPDF_DestroyLibrary(); }
        }
        finally { WindowsShippingLabelRenderer.Gate.Release(); }
    }
    private static ShippingLabelException Invalid() => new(LabelFailureKind.InvalidDocument, "PDFium не підтвердив структуру PDF етикетки; пакет не дозволено.");
}
