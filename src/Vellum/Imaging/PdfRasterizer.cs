using System.Runtime.InteropServices;
using SkiaSharp;
using Vellum.Interop;

namespace Vellum.Imaging;

/// <summary>
/// Rasterises PDF pages with PDFium, rendering each page straight into an opaque
/// BGRA <see cref="SKBitmap"/> at the size OCR will consume (no PNG round-trip).
/// </summary>
/// <remarks>
/// This replaced a Poppler/Cairo backend whose Type 3 font glyph caches live in
/// Cairo's process-global state and outlive the document — gigabytes per
/// document on some filings, reclaimable only by ending the process.
/// </remarks>
internal static class PdfRasterizer
{
    private const float MaxDpi = 300f;

    public static IEnumerable<RasterizedPdfPage> Rasterize(
        string pdfPath,
        int maxDim,
        IReadOnlySet<int>? pages = null)
    {
        var pdfBytes = File.ReadAllBytes(pdfPath);
        return RasterizeBytes(pdfBytes, maxDim, pages);
    }

    public static IEnumerable<RasterizedPdfPage> RasterizeBytes(
        byte[] pdfBytes,
        int maxDim,
        IReadOnlySet<int>? pages = null)
    {
        PdfiumNative.EnsureInitialized();

        // PDFium reads from the buffer for the document's whole lifetime, so it stays pinned until close.
        var pin = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
        IntPtr doc;
        int pageCount;
        lock (PdfiumNative.Sync)
        {
            doc = PdfiumNative.FPDF_LoadMemDocument64(pin.AddrOfPinnedObject(), (nuint)pdfBytes.Length, IntPtr.Zero);
            pageCount = doc == IntPtr.Zero ? 0 : PdfiumNative.FPDF_GetPageCount(doc);
        }

        if (doc == IntPtr.Zero)
        {
            var err = PdfiumNative.FPDF_GetLastError();
            pin.Free();
            throw new InvalidDataException($"Could not open PDF (PDFium error {err}).");
        }

        return Enumerate(doc, pin, pageCount, maxDim, pages);
    }

    private static IEnumerable<RasterizedPdfPage> Enumerate(
        IntPtr doc, GCHandle pin, int pageCount, int maxDim, IReadOnlySet<int>? pages)
    {
        try
        {
            for (var index = 1; index <= pageCount; index++)
            {
                if (pages is not null && !pages.Contains(index)) continue;
                yield return RenderPage(doc, index, maxDim);
            }
        }
        finally
        {
            lock (PdfiumNative.Sync) PdfiumNative.FPDF_CloseDocument(doc);
            pin.Free();
        }
    }

    private static RasterizedPdfPage RenderPage(IntPtr doc, int pageNumber, int maxDim)
    {
        lock (PdfiumNative.Sync)
        {
            var page = PdfiumNative.FPDF_LoadPage(doc, pageNumber - 1);
            if (page == IntPtr.Zero)
                throw new InvalidDataException($"Could not load PDF page {pageNumber}.");

            try
            {
                var wPt = PdfiumNative.FPDF_GetPageWidthF(page);
                var hPt = PdfiumNative.FPDF_GetPageHeightF(page);

                // Render at 300 DPI, capped so the longer side fits the OCR engine's max dimension.
                var scale = Math.Min(MaxDpi / 72f, maxDim / Math.Max(wPt, hPt));
                var w = Math.Max(1, (int)Math.Round(wPt * scale));
                var h = Math.Max(1, (int)Math.Round(hPt * scale));

                // Premul with alpha forced to 255 by the white fill, so ToBgraBytes needs no conversion.
                var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
                var pdfBmp = PdfiumNative.FPDFBitmap_CreateEx(
                    w, h, PdfiumNative.FPDFBitmap_BGRA, bmp.GetPixels(), bmp.RowBytes);
                if (pdfBmp == IntPtr.Zero)
                {
                    bmp.Dispose();
                    throw new InvalidOperationException($"PDFium could not allocate a {w}x{h} bitmap.");
                }

                try
                {
                    PdfiumNative.FPDFBitmap_FillRect(pdfBmp, 0, 0, w, h, 0xFFFFFFFF);
                    PdfiumNative.FPDF_RenderPageBitmap(pdfBmp, page, 0, 0, w, h, 0, PdfiumNative.FPDF_ANNOT);
                }
                finally
                {
                    PdfiumNative.FPDFBitmap_Destroy(pdfBmp);
                }

                return new RasterizedPdfPage(pageNumber, bmp, wPt, hPt);
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }
    }
}

internal sealed record RasterizedPdfPage(int PageNumber, SKBitmap Bitmap, float PointsWidth, float PointsHeight) : IDisposable
{
    public void Dispose() => Bitmap.Dispose();
}
