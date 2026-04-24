using PDF2SVG.PopplerCairo.Bindings;
using SkiaSharp;

namespace Vellum.Imaging;

/// <summary>
/// Rasterises a PDF to a stream of bitmap pages.  Uses the pdf2svg_poppler_cairo
/// .NET bindings with <c>isForceToPng=true</c>, so every page comes back as PNG
/// bytes regardless of whether it could have been SVG — OCR needs a bitmap.
/// </summary>
internal static class PdfRasterizer
{
    /// <summary>
    /// Yield <c>(pageNumber, bitmap, pdfPointSize)</c> for each requested page.
    /// Disposes the internal document handle when the enumeration ends (or the
    /// consumer disposes it early).
    /// </summary>
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
        // Two-pass so we know the page count up front (needed for DPI clamping per-page).
        // 300 DPI gives us roughly 2480 px / US-letter width — good margin above maxDim.
        using var doc = new PdfPageEnumerable(pdfBytes, isForceToPng: true, dpi: 300);
        if (!doc.IsDocumentOpened)
            throw new InvalidDataException("Could not open PDF.");

        var index = 0;
        foreach (var page in doc)
        {
            index++;
            try
            {
                if (pages is not null && !pages.Contains(index)) continue;

                // page.IsSvg should always be false because we passed isForceToPng=true,
                // but defend against future changes.
                if (page.IsSvg)
                    throw new InvalidOperationException(
                        "pdf2svg returned SVG data despite isForceToPng=true; cannot OCR vector data.");

                var bmp = DecodeOntoWhite(page.Data);

                // We don't have direct access to the PDF point size here; derive
                // it from 300 DPI → 1 point = 300/72 px.  Close enough for overlay
                // scaling; exact overlay coordinates are fixed up by the caller.
                var pointsWidth = bmp.Width * 72f / 300f;
                var pointsHeight = bmp.Height * 72f / 300f;

                yield return new RasterizedPdfPage(
                    PageNumber: index,
                    Bitmap: bmp,
                    PointsWidth: pointsWidth,
                    PointsHeight: pointsHeight);
            }
            finally
            {
                page.Data.Dispose();
            }
        }
    }

    /// <summary>
    /// Decode the page PNG and flatten it onto a fully opaque white background.
    /// PDF pages rendered by pdf2svg can have a transparent background; screen_ai
    /// misses glyphs on those pages, so we composite onto white before OCR.
    /// Returns an opaque BGRA bitmap owned by the caller.
    /// </summary>
    private static SKBitmap DecodeOntoWhite(MemoryStream pngStream)
    {
        pngStream.Position = 0;
        using var data = SKData.Create(pngStream);
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("Could not decode page image — unrecognised format.");

        var sourceInfo = new SKImageInfo(
            codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var source = new SKBitmap(sourceInfo);
        var result = codec.GetPixels(sourceInfo, source.GetPixels());
        if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
            throw new InvalidDataException($"SKCodec.GetPixels failed: {result}");

        var target = new SKBitmap(new SKImageInfo(
            codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(target))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(source, 0, 0);
        }
        return target;
    }
}

internal sealed record RasterizedPdfPage(int PageNumber, SKBitmap Bitmap, float PointsWidth, float PointsHeight) : IDisposable
{
    public void Dispose() => Bitmap.Dispose();
}

