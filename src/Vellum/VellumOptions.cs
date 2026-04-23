namespace Vellum;

/// <summary>
/// Configuration for the lazy OCR engine.  Wire up via
/// <see cref="ServiceCollectionExtensions.AddVellumOcr"/> — e.g.:
/// <code>services.AddVellumOcr(o => o.AutoDownload = true);</code>
/// </summary>
public sealed class VellumOptions
{
    /// <summary>
    /// Explicit path to the screen-ai component directory containing the DLL and
    /// models.  When <c>null</c>, the engine auto-discovers it from Chrome / the
    /// local Vellum cache at first use.
    /// </summary>
    public string? ModelDir { get; set; }

    /// <summary>Use the smaller, faster Screen AI model at the cost of some accuracy.</summary>
    public bool LightMode { get; set; }

    /// <summary>
    /// If <c>true</c> and the screen-ai component cannot be found locally at first
    /// use, copy it from Chrome (or fall back to the Dropbox zip / Omaha server).
    /// Default: <c>false</c> — keeps server startup deterministic.  Copying can
    /// take several hundred milliseconds and ~100 MB of I/O on first run.
    /// </summary>
    public bool AutoDownload { get; set; }

    /// <summary>
    /// If <c>true</c>, the lazy wrapper serialises all OCR calls with an internal
    /// lock.  The native library's thread safety isn't documented as safe for
    /// concurrent <c>PerformOCR</c> calls, so this defaults to <c>true</c> to
    /// match Chrome's single-threaded usage pattern.  Disable only if you have
    /// verified concurrent safety on your target library version.
    /// </summary>
    public bool SerializeCalls { get; set; } = true;
}
