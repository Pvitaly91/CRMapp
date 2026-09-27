using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using PDFiumCore;

namespace CheckboxBatchPrinter.Services;

// Embedded PDFium; no external viewer/process or plaintext PDF cache.
public sealed class WindowsShippingLabelRenderer : IShippingLabelRenderer
{
    private static readonly SemaphoreSlim Gate = new(1,1); // PDFium is not thread safe.
    public async Task<IReadOnlyList<ShippingLabelPage>> RenderAsync(ShippingLabelDocument document, double dpiX, double dpiY,
        CancellationToken cancellationToken = default)
    {
        ShippingLabelHttp.ValidatePdf(document.Pdf);
        if (dpiX is < 96 or > 1200 || dpiY is < 96 or > 1200) throw new InvalidOperationException("Некоректний DPI принтера.");
        await Gate.WaitAsync(cancellationToken);
        try
        {
            fpdfview.FPDF_InitLibrary();
            var pinned = GCHandle.Alloc(document.Pdf,GCHandleType.Pinned);
            try
            {
                var pdf = fpdfview.FPDF_LoadMemDocument(pinned.AddrOfPinnedObject(),document.Pdf.Length,null);
                if (pdf is null || pdf.__Instance == IntPtr.Zero) throw Invalid("PDF пошкоджений або захищений паролем.");
                try
                {
                    if (fpdf_formfill.FPDF_GetFormType(pdf) != 0)
                        throw new ShippingLabelException(LabelFailureKind.UnsupportedFormat,"PDF містить інтерактивну форму, а не готову етикетку. Потрібна офіційна пласка друкована форма.");
                    var count = fpdfview.FPDF_GetPageCount(pdf);
                    if (count is < 1 or > 100 || document.ExpectedPages > 0 && count < document.ExpectedPages)
                        throw Invalid("Кількість сторінок менша за кількість місць або перевищує 100.");
                    var pages = new List<ShippingLabelPage>();
                    for (var index = 0; index < count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var page = fpdfview.FPDF_LoadPage(pdf,index);
                        if (page is null || page.__Instance == IntPtr.Zero) throw Invalid("Не вдалося прочитати сторінку PDF.");
                        try
                        {
                            double pointsX = 0, pointsY = 0;
                            if (fpdfview.FPDF_GetPageSizeByIndex(pdf,index,ref pointsX,ref pointsY) == 0) throw Invalid("PDF не має коректного розміру.");
                            // PDF points (including rotation) are not WPF DIP or raster pixels.
                            var widthMm = pointsX * 25.4 / 72; var heightMm = pointsY * 25.4 / 72;
                            if (!double.IsFinite(widthMm) || !double.IsFinite(heightMm) || widthMm is < 20 or > 300 || heightMm is < 20 or > 400)
                                throw new ShippingLabelException(LabelFailureKind.UnsupportedFormat,"Непідтримуваний фізичний розмір PDF.");
                            var px = checked((int)Math.Ceiling(pointsX * dpiX / 72)); var py = checked((int)Math.Ceiling(pointsY * dpiY / 72));
                            if ((long)px * py > 25_000_000) throw new ShippingLabelException(LabelFailureKind.UnsupportedFormat,"Растер етикетки занадто великий.");
                            var bitmap = fpdfview.FPDFBitmapCreateEx(px,py,3,IntPtr.Zero,0); // opaque BGRx on white paper
                            if (bitmap is null || bitmap.__Instance == IntPtr.Zero) throw Invalid("Не вдалося створити растер PDF.");
                            try
                            {
                                fpdfview.FPDFBitmapFillRect(bitmap,0,0,px,py,uint.MaxValue);
                                fpdfview.FPDF_RenderPageBitmap(bitmap,page,0,0,px,py,0,0x801);
                                cancellationToken.ThrowIfCancellationRequested();
                                var stride = fpdfview.FPDFBitmapGetStride(bitmap);
                                var pixels = new byte[checked(stride*py)]; Marshal.Copy(fpdfview.FPDFBitmapGetBuffer(bitmap),pixels,0,pixels.Length);
                                var source = BitmapSource.Create(px,py,dpiX,dpiY,PixelFormats.Bgr32,null,pixels,stride); source.Freeze();
                                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
                                using var output = new MemoryStream(); encoder.Save(output);
                                pages.Add(new(document.Shipment,document.DocumentId,document.Fingerprint,index+1,widthMm,heightMm,output.ToArray())
                                { ConnectionId = document.ConnectionId, Source = document.Source });
                            }
                            finally { fpdfview.FPDFBitmapDestroy(bitmap); }
                        }
                        finally { fpdfview.FPDF_ClosePage(page); }
                        await Task.Yield();
                    }
                    return pages;
                }
                finally { fpdfview.FPDF_CloseDocument(pdf); }
            }
            finally { pinned.Free(); fpdfview.FPDF_DestroyLibrary(); }
        }
        finally { Gate.Release(); }
    }
    private static ShippingLabelException Invalid(string message) => new(LabelFailureKind.InvalidDocument,message);
}
