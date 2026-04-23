using System.Runtime.InteropServices;

namespace Vellum.Platform;

/// <summary>
/// Platform-specific constants and path helpers — the .NET equivalent of <c>locro/_platform.py</c>.
/// </summary>
public static class PlatformPaths
{
    /// <summary>Name of the shared library shipped by Chrome's screen-ai component.</summary>
    public static string LibraryName { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? "chrome_screen_ai.dll"
        : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? "libchromescreenai.so"
            : throw new PlatformNotSupportedException(
                $"Unsupported platform: {RuntimeInformation.RuntimeIdentifier}");

    /// <summary>"windows" or "linux" — used for Dropbox zip name lookup.</summary>
    public static string PlatformTag { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? "windows"
        : "linux";

    /// <summary>Candidate base directories for Chrome's screen-ai component.</summary>
    public static IReadOnlyList<string> ChromeComponentBases()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // Python uses %USERPROFILE%\AppData\Local\Google\Chrome\User Data\screen_ai —
            // %LOCALAPPDATA% resolves to the same path, but prefer it to match Windows redirection.
            var baseDir = string.IsNullOrEmpty(localAppData)
                ? Path.Combine(home, "AppData", "Local")
                : localAppData;

            return [Path.Combine(baseDir, "Google", "Chrome", "User Data", "screen_ai")];
        }

        // Linux: Google Chrome then Chromium.
        return
        [
            Path.Combine(home, ".config", "google-chrome", "screen_ai"),
            Path.Combine(home, ".config", "chromium", "screen_ai"),
        ];
    }

    /// <summary>Base directory for Vellum's own downloaded / copied models.</summary>
    public static string DefaultModelDir()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(localAppData))
                return Path.Combine(localAppData, "vellum");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdg))
            return Path.Combine(xdg, "vellum");

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "vellum");
    }

    /// <summary>Human-readable form of <see cref="DefaultModelDir"/> for <c>--help</c> text.</summary>
    public static string DefaultModelDirDisplay() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "%LOCALAPPDATA%/vellum"
            : "~/.local/share/vellum";

    private static string FindDropboxDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var homeDrop = Path.Combine(home, "Dropbox");
        if (Directory.Exists(homeDrop)) return homeDrop;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            const string rootDrop = @"C:\Dropbox";
            if (Directory.Exists(rootDrop)) return rootDrop;
        }

        return homeDrop; // non-existing default, same as Python
    }

    /// <summary>Full path to the Dropbox-hosted portable zip for the current platform.</summary>
    public static string DropboxZipPath() =>
        Path.Combine(FindDropboxDir(), "bin", $"screen-ai-{PlatformTag}.zip");
}
