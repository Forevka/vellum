using SkiaSharp;

namespace Vellum.Imaging;

/// <summary>Renders PDF pages to bitmaps for OCR. Pages are yielded one at a time; the caller disposes each.</summary>
internal interface IPdfRasterizer
{
    IEnumerable<RasterizedPdfPage> Rasterize(string pdfPath, int maxDim, IReadOnlySet<int>? pages = null);
}

internal static class PdfRasterizers
{
    public static IPdfRasterizer Create(PdfRasterizerKind kind) => kind switch
    {
        PdfRasterizerKind.Pdf2Svg => new Pdf2SvgRasterizer(),
        PdfRasterizerKind.Pdfium => new PdfiumRasterizer(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown PDF rasterizer."),
    };
}

internal sealed record RasterizedPdfPage(int PageNumber, SKBitmap Bitmap, float PointsWidth, float PointsHeight) : IDisposable
{
    public void Dispose() => Bitmap.Dispose();
}
