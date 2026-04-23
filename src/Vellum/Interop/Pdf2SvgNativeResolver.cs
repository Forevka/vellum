using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PDF2SVG.PopplerCairo.Bindings;

namespace Vellum.Interop;

/// <summary>
/// Intercepts the <c>PDF2SVG.PopplerCairo.Bindings</c> P/Invoke for
/// <c>native-svg2pdf/pdf2svgwrapper</c> and loads the native DLL from its
/// flat RID folder (<c>runtimes/&lt;rid&gt;/native/</c>).
///
/// Two problems this fixes:
///
/// 1. Path mismatch: the bindings DllImport string includes a
///    <c>native-svg2pdf/</c> subdirectory, but the NuGet ships the native
///    flat at <c>runtimes/&lt;rid&gt;/native/pdf2svgwrapper.{so,dll}</c>.
///
/// 2. Windows sibling collisions: <c>pdf2svgwrapper.dll</c> imports
///    <c>glib-2.0-0.dll</c>, <c>gobject-2.0-0.dll</c>, <c>gio-2.0-0.dll</c>,
///    <c>cairo-2.dll</c>, <c>poppler.dll</c>, etc. If the user has another
///    Poppler install on <c>PATH</c> (common via winget), Windows picks up
///    the wrong glib and GLib crashes with
///    <c>"cannot register existing type 'GWin32RegistryKey'"</c>.
///    <see cref="NativeLibrary.Load(string)"/> of an absolute path uses
///    <c>LOAD_WITH_ALTERED_SEARCH_PATH</c>, so siblings are resolved from
///    the DLL's own directory rather than <c>PATH</c>.
/// </summary>
internal static class Pdf2SvgNativeResolver
{
    private const string LibraryName = "native-svg2pdf/pdf2svgwrapper";

    // Module initializers are the only way to install the DllImport resolver
    // before the bindings assembly's P/Invokes fire at runtime.
    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage", "CA2255",
        Justification = "Resolver must be installed before any PDF2SVG.PopplerCairo.Bindings P/Invoke fires.")]
    internal static void Initialize()
    {
        // typeof(PdfPageEnumerable) forces the bindings assembly to load so
        // we can attach a resolver to it before any of its P/Invokes fire.
        var bindingsAssembly = typeof(PdfPageEnumerable).Assembly;
        try
        {
            NativeLibrary.SetDllImportResolver(bindingsAssembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // A resolver is already installed on this assembly (e.g. another
            // Vellum host in the same process). Let the existing one handle it.
        }
    }

    private static readonly bool DebugLog =
        Environment.GetEnvironmentVariable("VELLUM_DEBUG_NATIVE") == "1";

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibraryName) return IntPtr.Zero;

        foreach (var candidate in CandidatePaths())
        {
            if (!File.Exists(candidate)) continue;
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                if (DebugLog) Console.Error.WriteLine($"[Vellum] loaded {candidate}");
                return handle;
            }
            if (DebugLog) Console.Error.WriteLine($"[Vellum] TryLoad failed: {candidate}");
        }
        if (DebugLog) Console.Error.WriteLine($"[Vellum] resolver found no candidate for {libraryName}");
        return IntPtr.Zero;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var asmDir = Path.GetDirectoryName(typeof(Pdf2SvgNativeResolver).Assembly.Location);
        if (string.IsNullOrEmpty(asmDir)) yield break;

        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "pdf2svgwrapper.dll"
            : "pdf2svgwrapper.so";
        var rid = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64" : "linux-x64";

        yield return Path.Combine(asmDir, "runtimes", rid, "native", fileName);
        yield return Path.Combine(asmDir, fileName);
        yield return Path.Combine(asmDir, "runtimes", rid, "native", "native-svg2pdf", fileName);
        yield return Path.Combine(asmDir, "native-svg2pdf", fileName);
    }
}
