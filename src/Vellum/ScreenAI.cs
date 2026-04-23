using Vellum.Download;
using Vellum.Imaging;
using Vellum.Interop;
using Vellum.Models;
using Vellum.Platform;
using Vellum.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using SkiaSharp;

namespace Vellum;

/// <summary>
/// High-level OCR facade — the .NET equivalent of Python <c>vellum.ScreenAI</c>.
/// </summary>
public sealed class ScreenAI : IOcrEngine
{
    /// <inheritdoc />
    public bool IsReady => true;

    /// <inheritdoc />
    public Task EnsureReadyAsync(CancellationToken ct = default) => Task.CompletedTask;


    private readonly ILogger _log;
    private readonly ScreenAiNative _native;
    private readonly uint _maxDim;

    /// <summary>
    /// Create an OCR engine, loading the screen-ai library from <paramref name="modelDir"/>
    /// (or auto-discovering it from Chrome / Vellum's local model cache).
    /// </summary>
    public ScreenAI(string? modelDir = null, bool lightMode = false, ILogger<ScreenAI>? log = null)
    {
        _log = log ?? NullLogger<ScreenAI>.Instance;
        modelDir ??= FindScreenAiDir();

        _native = new ScreenAiNative(modelDir, _log);

        if (lightMode) _native.SetLightMode(true);

        if (!_native.InitOcr())
            throw new InvalidOperationException("Failed to initialize screen-ai OCR pipeline");

        _maxDim = _native.GetMaxImageDimension();
        _log.LogInformation("OCR ready (max dimension: {MaxDim})", _maxDim);
    }

    /// <summary>Major + minor version reported by the loaded library.</summary>
    public (uint Major, uint Minor) Version => _native.GetVersion();

    /// <summary>Maximum supported image dimension (typically 2048).</summary>
    public uint MaxImageDimension => _maxDim;

    // ---------------------------------------------------------------------
    // Discovery
    // ---------------------------------------------------------------------

    /// <summary>Locate the screen-ai component directory — Chrome first, then Vellum's cache.</summary>
    public static string FindScreenAiDir()
    {
        foreach (var baseDir in PlatformPaths.ChromeComponentBases())
        {
            if (!Directory.Exists(baseDir)) continue;
            var dir = Directory.EnumerateDirectories(baseDir)
                .Where(d => File.Exists(Path.Combine(d, PlatformPaths.LibraryName)))
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .FirstOrDefault();
            if (dir is not null) return dir;
        }

        var local = ComponentDownloader.FindLocalModelDir();
        if (local is not null) return local;

        throw new FileNotFoundException(
            """
            screen-ai component not found.
              Run:  vellum download
              Or install Chrome and visit chrome://components to trigger the download.
            """);
    }

    // ---------------------------------------------------------------------
    // Public OCR API
    // ---------------------------------------------------------------------

    /// <summary>OCR a PDF or image file.  <paramref name="pages"/> (1-based) is ignored for images.</summary>
    public OcrResult Ocr(string file, IEnumerable<int>? pages = null)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext == ".pdf") return OcrPdf(file, pages);
        return OcrImageFile(file);
    }

    /// <summary>OCR a raw <see cref="SKBitmap"/> — returns one page's worth of results.</summary>
    public OcrPage OcrBitmap(SKBitmap bitmap)
    {
        var (lines, size) = PerformOcr(bitmap);
        return LinesToPage(lines, pageNumber: 1, imgSize: size);
    }

    /// <summary>
    /// OCR a file and produce a self-contained interactive HTML report — the
    /// source image(s) on the left with hoverable word boxes, a synchronised
    /// sidebar with the extracted text on the right.  All images are inlined
    /// as base64 PNG, no external assets.
    /// </summary>
    public OcrResult OcrToHtml(string file, string outputHtml, IEnumerable<int>? pages = null, string? title = null)
    {
        var pageSet = pages is null ? null : new HashSet<int>(pages);
        var ext = Path.GetExtension(file).ToLowerInvariant();
        var builder = new Vellum.Reporting.HtmlReportBuilder(title ?? Path.GetFileName(file));
        var ocrPages = new List<OcrPage>();

        if (ext == ".pdf")
        {
            foreach (var raster in PdfRasterizer.Rasterize(file, (int)_maxDim, pageSet))
            {
                try
                {
                    var (lines, size, working) = PerformOcrKeepBitmap(raster.Bitmap);
                    var page = LinesToPage(lines, raster.PageNumber, size);
                    ocrPages.Add(page);
                    builder.AddPage(page, working);
                    if (!ReferenceEquals(working, raster.Bitmap)) working.Dispose();
                }
                finally
                {
                    raster.Dispose();
                }
            }
        }
        else
        {
            using var bmp = SkiaImageOps.DecodeFile(file);
            var (lines, size, working) = PerformOcrKeepBitmap(bmp);
            var page = LinesToPage(lines, pageNumber: 1, imgSize: size);
            ocrPages.Add(page);
            builder.AddPage(page, working);
            if (!ReferenceEquals(working, bmp)) working.Dispose();
        }

        File.WriteAllText(outputHtml, builder.Build());
        _log.LogInformation("HTML report -> {OutputHtml}", outputHtml);
        return new OcrResult(ocrPages);
    }

    /// <summary>
    /// OCR a PDF and write a searchable copy with an invisible text overlay.
    /// Scales OCR pixel coords → PDF point coords per page.
    /// </summary>
    public OcrResult OcrToSearchablePdf(
        string inputPdf, string outputPdf, IEnumerable<int>? pages = null)
    {
        var pageSet = pages is null ? null : new HashSet<int>(pages);
        using var pdfSource = PdfReader.Open(inputPdf, PdfDocumentOpenMode.Modify);
        var ocrPages = new List<OcrPage>();

        // Rasterize + OCR each requested page, then overlay text onto the matching PdfSharp page.
        var rasterPages = PdfRasterizer.Rasterize(inputPdf, (int)_maxDim, pageSet)
            .ToDictionary(p => p.PageNumber, p => p);

        try
        {
            for (var i = 0; i < pdfSource.PageCount; i++)
            {
                var pageNum = i + 1;
                if (pageSet is not null && !pageSet.Contains(pageNum)) continue;
                if (!rasterPages.TryGetValue(pageNum, out var raster)) continue;

                var (lines, ocrSize) = PerformOcr(raster.Bitmap);
                var ocrPage = LinesToPage(lines, pageNum, ocrSize);
                ocrPages.Add(ocrPage);

                var pdfPage = pdfSource.Pages[i];
                var sx = (float)pdfPage.Width.Point / ocrSize.Width;
                var sy = (float)pdfPage.Height.Point / ocrSize.Height;

                OverlayText(pdfPage, lines, sx, sy);

                _log.LogInformation(
                    "Page {PageNum}: {Blocks} blocks, {Lines} lines (overlaid)",
                    pageNum, ocrPage.Blocks.Count, ocrPage.Blocks.Sum(b => b.Lines.Count));
            }
        }
        finally
        {
            foreach (var p in rasterPages.Values) p.Dispose();
        }

        pdfSource.Save(outputPdf);
        _log.LogInformation("Searchable PDF -> {OutputPdf}", outputPdf);
        return new OcrResult(ocrPages);
    }

    // ---------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------

    private (List<LineResult> Lines, (int Width, int Height) Size) PerformOcr(SKBitmap bitmap)
    {
        var (lines, size, working) = PerformOcrKeepBitmap(bitmap);
        if (!ReferenceEquals(working, bitmap))
            working.Dispose();
        return (lines, size);
    }

    /// <summary>
    /// OCR variant that returns the exact bitmap sent to PerformOCR (post-resize).
    /// The caller owns the returned bitmap and is responsible for disposing it —
    /// unless it is the same instance as <paramref name="bitmap"/>.
    /// Used by the HTML report path so image pixels line up with word coordinates.
    /// </summary>
    private (List<LineResult> Lines, (int Width, int Height) Size, SKBitmap Working) PerformOcrKeepBitmap(SKBitmap bitmap)
    {
        var working = SkiaImageOps.ResizeIfLarger(bitmap, (int)_maxDim);
        var width = working.Width;
        var height = working.Height;

        var bgra = SkiaImageOps.ToBgraBytes(working);

        var raw = _native.PerformOcr(bgra, width, height);
        if (raw is null) return (new List<LineResult>(), (width, height), working);
        return (VisualAnnotationParser.Parse(raw), (width, height), working);
    }

    private OcrResult OcrImageFile(string path)
    {
        using var bmp = SkiaImageOps.DecodeFile(path);
        var (lines, size) = PerformOcr(bmp);
        var page = LinesToPage(lines, pageNumber: 1, imgSize: size);
        return new OcrResult([page]);
    }

    private OcrResult OcrPdf(string path, IEnumerable<int>? pages)
    {
        var pageSet = pages is null ? null : new HashSet<int>(pages);
        var ocrPages = new List<OcrPage>();

        foreach (var raster in PdfRasterizer.Rasterize(path, (int)_maxDim, pageSet))
        {
            try
            {
                var (lines, size) = PerformOcr(raster.Bitmap);
                var ocrPage = LinesToPage(lines, raster.PageNumber, size);
                ocrPages.Add(ocrPage);
                _log.LogInformation(
                    "Page {PageNum}: {Blocks} blocks, {Lines} lines",
                    raster.PageNumber, ocrPage.Blocks.Count, ocrPage.Blocks.Sum(b => b.Lines.Count));
            }
            finally
            {
                raster.Dispose();
            }
        }

        return new OcrResult(ocrPages);
    }

    // ---------------------------------------------------------------------
    // Shape conversion
    // ---------------------------------------------------------------------

    private static OcrPage LinesToPage(
        List<LineResult> lines, int pageNumber, (int Width, int Height) imgSize)
    {
        var byBlock = lines
            .GroupBy(l => l.BlockId)
            .OrderBy(g => g.Key);

        var blocks = new List<OcrBlock>();
        foreach (var group in byBlock)
        {
            var ocrLines = new List<OcrLine>();
            foreach (var ln in group)
            {
                var words = ln.Words.Select(w => new OcrWord(
                    Text: w.Text,
                    Confidence: w.Confidence == 0f ? null : w.Confidence,
                    BoundingBox: w.Width == 0
                        ? null
                        : new BoundingBox(w.X, w.Y, w.Width, w.Height))).ToList();

                ocrLines.Add(new OcrLine(
                    Text: ln.Text,
                    Words: words,
                    BoundingBox: ln.Width == 0
                        ? null
                        : new BoundingBox(ln.X, ln.Y, ln.Width, ln.Height)));
            }

            blocks.Add(new OcrBlock(BlockType: "paragraph", Lines: ocrLines));
        }

        return new OcrPage(
            PageNumber: pageNumber,
            Blocks: blocks,
            Width: imgSize.Width,
            Height: imgSize.Height);
    }

    // ---------------------------------------------------------------------
    // PdfSharpCore text overlay (invisible-render-mode equivalent: tiny/transparent)
    // ---------------------------------------------------------------------

    private static void OverlayText(PdfPage pdfPage, List<LineResult> lines, float sx, float sy)
    {
        using var gfx = XGraphics.FromPdfPage(pdfPage, XGraphicsPdfPageOptions.Append);
        // Invisible = render with fully transparent fill (PdfSharpCore has no render-mode 3 on Append).
        var invisibleBrush = new XSolidBrush(XColor.FromArgb(0, 0, 0, 0));

        foreach (var ln in lines)
        {
            foreach (var w in ln.Words)
            {
                if (string.IsNullOrEmpty(w.Text) || w.Width == 0) continue;

                var x0 = w.X * sx;
                var y0 = w.Y * sy;
                var x1 = (w.X + w.Width) * sx;
                var y1 = (w.Y + w.Height) * sy;
                var fontSize = Math.Max(1.0, (y1 - y0) * 0.8);

                try
                {
                    var font = new XFont("Arial", fontSize, XFontStyle.Regular);
                    var point = new XPoint(x0, y1 - (fontSize * 0.1));
                    gfx.DrawString(w.Text, font, invisibleBrush, point);
                }
                catch
                {
                    // Font resolution / glyph issues shouldn't derail the whole overlay.
                }
            }
        }
    }

    public void Dispose() => _native.Dispose();
}
