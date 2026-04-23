using Vellum.Models;
using SkiaSharp;

namespace Vellum;

/// <summary>
/// Abstraction over a screen-ai OCR engine.  Implemented by <see cref="ScreenAI"/>
/// (eagerly initialised) and <see cref="LazyOcrEngine"/> (ASP.NET-Core-friendly
/// lazy wrapper that defers loading the native library until the first OCR call).
/// </summary>
public interface IOcrEngine : IDisposable
{
    /// <summary>True once the underlying native library has been loaded and the pipeline is ready.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Returns the version of the loaded library.  Forces lazy initialisation if needed.
    /// </summary>
    (uint Major, uint Minor) Version { get; }

    /// <summary>Max input dimension (typically 2048).  Forces lazy initialisation if needed.</summary>
    uint MaxImageDimension { get; }

    /// <summary>
    /// Proactively load the native library.  Safe to call from a hosted-service
    /// warmup step so the first real OCR request doesn't pay the ~50 ms init cost.
    /// </summary>
    Task EnsureReadyAsync(CancellationToken ct = default);

    /// <summary>OCR a PDF or image file.  Page numbers are 1-based and ignored for images.</summary>
    OcrResult Ocr(string file, IEnumerable<int>? pages = null);

    /// <summary>OCR a single <see cref="SKBitmap"/>.  Returns one page's worth of results.</summary>
    OcrPage OcrBitmap(SKBitmap bitmap);

    /// <summary>OCR a PDF and write a searchable copy with an invisible text overlay.</summary>
    OcrResult OcrToSearchablePdf(string inputPdf, string outputPdf, IEnumerable<int>? pages = null);

    /// <summary>OCR a file and produce a self-contained interactive HTML report.</summary>
    OcrResult OcrToHtml(string file, string outputHtml, IEnumerable<int>? pages = null, string? title = null);

    // -----------------------------------------------------------------------
    // Async variants — recommended in ASP.NET Core hot paths.
    // The OCR work itself is CPU-bound; these methods free the caller's
    // thread while waiting on the internal serialisation lock, then execute
    // the CPU work on the thread pool so the request thread is returned.
    // -----------------------------------------------------------------------

    /// <summary>Async version of <see cref="Ocr"/>.</summary>
    Task<OcrResult> OcrAsync(string file, IEnumerable<int>? pages = null, CancellationToken ct = default);

    /// <summary>Async version of <see cref="OcrBitmap"/>.</summary>
    Task<OcrPage> OcrBitmapAsync(SKBitmap bitmap, CancellationToken ct = default);

    /// <summary>Async version of <see cref="OcrToSearchablePdf"/>.</summary>
    Task<OcrResult> OcrToSearchablePdfAsync(
        string inputPdf, string outputPdf, IEnumerable<int>? pages = null, CancellationToken ct = default);

    /// <summary>Async version of <see cref="OcrToHtml"/>.</summary>
    Task<OcrResult> OcrToHtmlAsync(
        string file, string outputHtml, IEnumerable<int>? pages = null, string? title = null, CancellationToken ct = default);
}
