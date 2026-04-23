using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Vellum.Interop;

/// <summary>
/// Loads the tiny <c>_chromium_stubs.so</c> with <c>RTLD_GLOBAL</c> on Linux so that
/// <c>libchromescreenai.so</c>'s unresolved symbols (gzopen stubs, threadlogger)
/// are satisfied when the dynamic linker wires it up.  No-op on Windows.
/// </summary>
internal static class ChromiumStubs
{
    private const string StubsSoName = "_chromium_stubs.so";
    private const int RtldLazy = 0x0001;
    private const int RtldGlobal = 0x0100;

    private static bool _loaded;
    private static readonly object Sync = new();

    [DllImport("libdl.so.2", EntryPoint = "dlopen", CharSet = CharSet.Ansi)]
    private static extern IntPtr DlOpen(string filename, int flags);

    [DllImport("libdl.so.2", EntryPoint = "dlerror")]
    private static extern IntPtr DlError();

    /// <summary>
    /// Ensure the stub symbols are resolvable before loading <c>libchromescreenai.so</c>.
    /// Tries, in order: bundled prebuilt under <c>runtimes/linux-x64/native/</c>,
    /// <paramref name="modelDir"/>, then runtime compile with <c>cc</c>.
    /// </summary>
    public static void Ensure(string modelDir, ILogger? log)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;

        lock (Sync)
        {
            if (_loaded) return;

            var stubsPath = LocateOrBuildStubs(modelDir, log);
            if (stubsPath is null)
            {
                log?.LogWarning(
                    "Could not locate or build {StubsSoName}; libchromescreenai.so may fail to resolve symbols.",
                    StubsSoName);
                return;
            }

            log?.LogDebug("Preloading {StubsPath} (RTLD_LAZY | RTLD_GLOBAL)", stubsPath);
            var handle = DlOpen(stubsPath, RtldLazy | RtldGlobal);
            if (handle == IntPtr.Zero)
            {
                var err = Marshal.PtrToStringAnsi(DlError()) ?? "(unknown)";
                throw new InvalidOperationException($"dlopen({stubsPath}, RTLD_GLOBAL) failed: {err}");
            }

            _loaded = true;
        }
    }

    private static string? LocateOrBuildStubs(string modelDir, ILogger? log)
    {
        // 1. Bundled under the NuGet's runtimes/linux-x64/native/ — copied next to the .dll on publish.
        var assemblyDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(assemblyDir, StubsSoName),
            Path.Combine(assemblyDir, "runtimes", "linux-x64", "native", StubsSoName),
            Path.Combine(modelDir, StubsSoName),
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        // 2. Runtime compile fallback — matches Python's behaviour.
        var target = Path.Combine(modelDir, StubsSoName);
        var source = ExtractEmbeddedStubSource(modelDir);
        if (source is null) return null;

        try
        {
            log?.LogInformation("Compiling Chromium stubs: cc -shared -fPIC -o {Target} {Source}", target, source);
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "cc",
                ArgumentList = { "-shared", "-fPIC", "-O2", "-o", target, source },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            if (proc is null) return null;
            proc.WaitForExit(30_000);
            return proc.ExitCode == 0 && File.Exists(target) ? target : null;
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Failed to invoke `cc` to build {StubsSoName}", StubsSoName);
            return null;
        }
    }

    /// <summary>
    /// Writes the baked-in stub C source to <paramref name="modelDir"/> so <c>cc</c>
    /// has something to compile.  The full source (~20 lines) lives inline below so
    /// it survives trimming / single-file publish.
    /// </summary>
    private static string? ExtractEmbeddedStubSource(string modelDir)
    {
        try
        {
            Directory.CreateDirectory(modelDir);
            var path = Path.Combine(modelDir, "_chromium_stubs.c");
            File.WriteAllText(path, StubsSource);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private const string StubsSource = """
        #include <stddef.h>
        void *unsupported_gzopen(const char *path, const char *mode) { (void)path; (void)mode; return NULL; }
        int   unsupported_gzread(void *file, void *buf, unsigned len) { (void)file; (void)buf; (void)len; return 0; }
        int   unsupported_gzclose(void *file) { (void)file; return 0; }
        void  _ZN12threadlogger21EnableThreadedLoggingEi(int x) { (void)x; }
        """;
}
