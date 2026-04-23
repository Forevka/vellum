using Vellum.Download;
using Vellum.Models;
using Vellum.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Vellum;

/// <summary>
/// Lazy-initialising <see cref="IOcrEngine"/> suitable for registration in an
/// ASP.NET Core DI container.  Construction never touches the native library;
/// the first OCR call locates (or optionally downloads) the screen-ai component
/// and loads <c>chrome_screen_ai.dll</c>.
/// </summary>
public sealed class LazyOcrEngine : IOcrEngine
{
    private readonly VellumOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim? _callLock;

    private ScreenAI? _inner;
    private bool _disposed;

    public LazyOcrEngine(VellumOptions options, ILoggerFactory? loggerFactory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _log = _loggerFactory.CreateLogger<LazyOcrEngine>();

        if (_options.SerializeCalls) _callLock = new SemaphoreSlim(1, 1);
    }

    public bool IsReady => _inner is not null;

    public (uint Major, uint Minor) Version
    {
        get
        {
            var engine = EnsureReady();
            return engine.Version;
        }
    }

    public uint MaxImageDimension
    {
        get
        {
            var engine = EnsureReady();
            return engine.MaxImageDimension;
        }
    }

    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        if (_inner is not null) return;

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_inner is not null) return;
            _inner = await BuildEngineAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _initLock.Release();
        }
    }

    public OcrResult Ocr(string file, IEnumerable<int>? pages = null) =>
        Run(e => e.Ocr(file, pages));

    public OcrPage OcrBitmap(SKBitmap bitmap) => Run(e => e.OcrBitmap(bitmap));

    public OcrResult OcrToSearchablePdf(string inputPdf, string outputPdf, IEnumerable<int>? pages = null) =>
        Run(e => e.OcrToSearchablePdf(inputPdf, outputPdf, pages));

    public OcrResult OcrToHtml(string file, string outputHtml, IEnumerable<int>? pages = null, string? title = null) =>
        Run(e => e.OcrToHtml(file, outputHtml, pages, title));

    public Task<OcrResult> OcrAsync(string file, IEnumerable<int>? pages = null, CancellationToken ct = default) =>
        RunAsync(e => e.Ocr(file, pages), ct);

    public Task<OcrPage> OcrBitmapAsync(SKBitmap bitmap, CancellationToken ct = default) =>
        RunAsync(e => e.OcrBitmap(bitmap), ct);

    public Task<OcrResult> OcrToSearchablePdfAsync(
        string inputPdf, string outputPdf, IEnumerable<int>? pages = null, CancellationToken ct = default) =>
        RunAsync(e => e.OcrToSearchablePdf(inputPdf, outputPdf, pages), ct);

    public Task<OcrResult> OcrToHtmlAsync(
        string file, string outputHtml, IEnumerable<int>? pages = null, string? title = null, CancellationToken ct = default) =>
        RunAsync(e => e.OcrToHtml(file, outputHtml, pages, title), ct);

    // ---------------------------------------------------------------------
    // Initialisation
    // ---------------------------------------------------------------------

    private ScreenAI EnsureReady()
    {
        if (_inner is not null) return _inner;
        EnsureReadyAsync().GetAwaiter().GetResult();
        return _inner!;
    }

    private async Task<ScreenAI> BuildEngineAsync(CancellationToken ct)
    {
        ThrowIfDisposed();

        string? modelDir;

        // Explicit ModelDir wins unconditionally.  If it's set but invalid, fail
        // fast rather than silently falling back to Chrome — that would be a
        // surprising behaviour change for anyone pointing at a custom location.
        if (!string.IsNullOrEmpty(_options.ModelDir))
        {
            if (!File.Exists(Path.Combine(_options.ModelDir, PlatformPaths.LibraryName)))
            {
                throw new FileNotFoundException(
                    $"Vellum: VellumOptions.ModelDir was set to '{_options.ModelDir}' but " +
                    $"'{PlatformPaths.LibraryName}' was not found there.");
            }
            modelDir = _options.ModelDir;
        }
        else
        {
            modelDir = TryFindModelDir();

            if (modelDir is null && _options.AutoDownload)
            {
                _log.LogInformation("screen-ai component not found locally; attempting auto-download...");
                modelDir = await ComponentDownloader.DownloadComponentAsync(
                    targetDir: null, log: _loggerFactory.CreateLogger("Vellum.Download"), ct: ct)
                    .ConfigureAwait(false);
            }

            if (modelDir is null)
            {
                throw new FileNotFoundException(
                    """
                    Vellum: screen-ai component not found.
                      - Install Chrome and visit chrome://components to trigger the download, OR
                      - run `vellum download` once to copy it locally, OR
                      - set VellumOptions.AutoDownload = true for automatic discovery + copy, OR
                      - set VellumOptions.ModelDir to an explicit directory.
                    """);
            }
        }

        var screenAiLogger = _loggerFactory.CreateLogger<ScreenAI>();
        return new ScreenAI(modelDir, _options.LightMode, screenAiLogger);
    }

    private static string? TryFindModelDir()
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
        return ComponentDownloader.FindLocalModelDir();
    }

    // ---------------------------------------------------------------------
    // Call dispatch (with optional serialisation)
    // ---------------------------------------------------------------------

    private T Run<T>(Func<ScreenAI, T> op)
    {
        var engine = EnsureReady();
        if (_callLock is null) return op(engine);

        _callLock.Wait();
        try { return op(engine); }
        finally { _callLock.Release(); }
    }

    /// <summary>
    /// Async equivalent of <see cref="Run{T}"/>:
    /// <list type="bullet">
    ///   <item>awaits lazy init without blocking the caller thread,</item>
    ///   <item>awaits the serialisation semaphore via <c>WaitAsync</c>,</item>
    ///   <item>off-loads the CPU-bound OCR call to the thread pool so the
    ///         caller (e.g. an ASP.NET request thread) is returned while the
    ///         native DLL is busy.</item>
    /// </list>
    /// </summary>
    private async Task<T> RunAsync<T>(Func<ScreenAI, T> op, CancellationToken ct)
    {
        await EnsureReadyAsync(ct).ConfigureAwait(false);
        var engine = _inner!;

        if (_callLock is null)
            return await Task.Run(() => op(engine), ct).ConfigureAwait(false);

        await _callLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => op(engine), ct).ConfigureAwait(false);
        }
        finally
        {
            _callLock.Release();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LazyOcrEngine));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _inner?.Dispose();
        _initLock.Dispose();
        _callLock?.Dispose();
    }
}
