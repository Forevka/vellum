using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Vellum.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vellum.Download;

/// <summary>
/// Screen-AI component installer.  Tries three strategies in order, matching <c>_download.py</c>:
///   1. copy from a local Chrome install (<see cref="CopyFromChrome"/>);
///   2. install from a Dropbox-hosted portable zip (<see cref="InstallFromZip"/>);
///   3. download from Google's Omaha update server (<see cref="DownloadFromServerAsync"/>).
/// </summary>
public static class ComponentDownloader
{
    /// <summary>The Chrome Component ID for the Screen AI package.</summary>
    public const string ComponentId = "mfhmdacoffpmifoibamicehhklffanao";

    private const string UpdateUrl = "https://update.googleapis.com/service/update2";

    private static readonly string[] ExcludedExportFiles = { "_chromium_stubs.so" };

    // ---------------------------------------------------------------------
    // Discovery
    // ---------------------------------------------------------------------

    /// <summary>Default base directory for Vellum-managed models (version sub-dirs live underneath).</summary>
    public static string GetDefaultModelDir() => PlatformPaths.DefaultModelDir();

    /// <summary>Newest locally-installed version directory, or <c>null</c>.</summary>
    public static string? FindLocalModelDir()
    {
        var baseDir = GetDefaultModelDir();
        if (!Directory.Exists(baseDir)) return null;

        return Directory.EnumerateDirectories(baseDir)
            .Where(d => File.Exists(Path.Combine(d, PlatformPaths.LibraryName)))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>Newest Chrome-managed version directory holding the library, or <c>null</c>.</summary>
    public static string? FindChromeComponentDir()
    {
        foreach (var baseDir in PlatformPaths.ChromeComponentBases())
        {
            if (!Directory.Exists(baseDir)) continue;
            var hit = Directory.EnumerateDirectories(baseDir)
                .Where(d => File.Exists(Path.Combine(d, PlatformPaths.LibraryName)))
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .FirstOrDefault();
            if (hit is not null) return hit;
        }
        return null;
    }

    // ---------------------------------------------------------------------
    // Strategy 1: copy from Chrome
    // ---------------------------------------------------------------------

    public static string CopyFromChrome(string? targetDir = null, ILogger? log = null)
    {
        log ??= NullLogger.Instance;

        var src = FindChromeComponentDir()
            ?? throw new FileNotFoundException(
                """
                screen-ai component not found in Chrome's directory.
                Open Chrome, visit chrome://components, find 'Screen AI',
                and click 'Check for update', then try again.
                """);

        var version = Path.GetFileName(src)!;
        var baseDir = targetDir ?? GetDefaultModelDir();
        var dest = Path.Combine(baseDir, version);

        if (File.Exists(Path.Combine(dest, PlatformPaths.LibraryName)))
        {
            log.LogInformation("Already installed at {Dest}", dest);
            return dest;
        }

        log.LogInformation("Copying from {Src} -> {Dest}", src, dest);
        CopyDirectoryRecursive(src, dest);
        log.LogInformation("Installed screen-ai {Version}", version);
        return dest;
    }

    // ---------------------------------------------------------------------
    // Strategy 2: portable zip (Dropbox / manual)
    // ---------------------------------------------------------------------

    public static string ExportToZip(string? zipPath = null, ILogger? log = null)
    {
        log ??= NullLogger.Instance;

        var src = FindLocalModelDir() ?? FindChromeComponentDir()
            ?? throw new FileNotFoundException("No installed screen-ai component found to export.");

        var dest = zipPath ?? PlatformPaths.DropboxZipPath();
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        var version = Path.GetFileName(src)!;
        log.LogInformation("Exporting {Src} -> {Dest} (version {Version})", src, dest, version);

        if (File.Exists(dest)) File.Delete(dest);

        using (var fs = File.Create(dest))
        using (var zf = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                if (ExcludedExportFiles.Contains(name, StringComparer.Ordinal)) continue;

                var rel = Path.GetRelativePath(src, file).Replace(Path.DirectorySeparatorChar, '/');
                var entryName = $"{version}/{rel}";
                zf.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
            }
        }

        var sizeMb = new FileInfo(dest).Length / (1024.0 * 1024.0);
        log.LogInformation("Exported {Dest} ({SizeMb:F1} MB)", dest, sizeMb);
        return dest;
    }

    public static string InstallFromZip(string? zipPath = null, string? targetDir = null, ILogger? log = null)
    {
        log ??= NullLogger.Instance;

        var src = zipPath ?? PlatformPaths.DropboxZipPath();
        if (!File.Exists(src))
            throw new FileNotFoundException($"Zip not found: {src}", src);

        var baseDir = targetDir ?? GetDefaultModelDir();

        log.LogInformation("Installing from {Src} ...", src);
        using var fs = File.OpenRead(src);
        using var zf = new ZipArchive(fs, ZipArchiveMode.Read);

        var topDirs = zf.Entries
            .Where(e => e.FullName.Contains('/'))
            .Select(e => e.FullName.Split('/', 2)[0])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (topDirs.Count != 1)
            throw new InvalidOperationException(
                $"Expected exactly one top-level directory in zip, got [{string.Join(", ", topDirs)}]");

        var version = topDirs[0];
        var versionDir = Path.Combine(baseDir, version);

        if (File.Exists(Path.Combine(versionDir, PlatformPaths.LibraryName)))
        {
            log.LogInformation("Already installed at {VersionDir}", versionDir);
            return versionDir;
        }

        Directory.CreateDirectory(versionDir);
        ExtractZipSafely(zf, baseDir);

        log.LogInformation("Installed screen-ai {Version} from zip", version);
        return versionDir;
    }

    // ---------------------------------------------------------------------
    // Strategy 3: Omaha protocol + CRX3 download
    // ---------------------------------------------------------------------

    public sealed record UpdateInfo(string Version, string Url, long Size, string Sha256);

    public static async Task<UpdateInfo> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var (plat, osVer) = GetOmahaPlatform();

        var requestBody =
            $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <request protocol="3.1" updater="chromium" prodversion="130.0.6723.91" ismachine="0" dedup="cr" acceptformat="crx2,crx3">
            <os platform="{plat}" version="{osVer}" arch="x86_64"/>
            <app appid="{ComponentId}" version="0.0.0.0" installsource="ondemand">
            <updatecheck/>
            </app>
            </request>
            """;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var req = new HttpRequestMessage(HttpMethod.Post, UpdateUrl)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/xml"),
        };

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var xml = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var root = XDocument.Parse(xml).Root
            ?? throw new InvalidOperationException("Empty Omaha response");
        var uc = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "updatecheck")
            ?? throw new InvalidOperationException("Unexpected response: no <updatecheck> element");

        var status = uc.Attribute("status")?.Value ?? string.Empty;
        if (status == "noupdate")
        {
            throw new InvalidOperationException(
                """
                Google's server returned 'noupdate' for this component.
                The screen-ai component is currently only served to Chrome.
                Use `vellum download` to copy from a local Chrome install.
                """);
        }
        if (status != "ok")
            throw new InvalidOperationException($"Update check failed: status={status}");

        var urlEl = uc.Descendants().FirstOrDefault(e => e.Name.LocalName == "url")
            ?? throw new InvalidOperationException("No download URL in response");
        var codebase = urlEl.Attribute("codebase")?.Value ?? string.Empty;

        var manifest = uc.Descendants().FirstOrDefault(e => e.Name.LocalName == "manifest");
        var version = manifest?.Attribute("version")?.Value ?? "unknown";

        var pkg = uc.Descendants().FirstOrDefault(e => e.Name.LocalName == "package")
            ?? throw new InvalidOperationException("No package info in response");

        return new UpdateInfo(
            Version: version,
            Url: codebase + (pkg.Attribute("name")?.Value ?? string.Empty),
            Size: long.TryParse(pkg.Attribute("size")?.Value, out var s) ? s : 0,
            Sha256: pkg.Attribute("hash_sha256")?.Value ?? string.Empty);
    }

    public static async Task<string> DownloadFromServerAsync(
        string? targetDir = null,
        IProgress<(long Current, long Total)>? progress = null,
        ILogger? log = null,
        CancellationToken ct = default)
    {
        log ??= NullLogger.Instance;

        var info = await CheckForUpdateAsync(ct).ConfigureAwait(false);
        log.LogInformation("Latest version: {Version} ({SizeMb} MB)", info.Version, info.Size / (1024 * 1024));

        var baseDir = targetDir ?? GetDefaultModelDir();
        var versionDir = Path.Combine(baseDir, info.Version);
        var libPath = Path.Combine(versionDir, PlatformPaths.LibraryName);

        if (File.Exists(libPath))
        {
            log.LogInformation("Already installed at {VersionDir}", versionDir);
            return versionDir;
        }

        log.LogInformation("Downloading {Url} ...", info.Url);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var resp = await http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? info.Size;
        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        using var ms = new MemoryStream();
        var buf = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buf, 0, read);
            progress?.Report((ms.Length, total));
        }

        log.LogInformation("Downloaded {Bytes} bytes", ms.Length);

        // CRX3 = magic + header + ZIP.  .NET's ZipArchive locates the EOCD from the
        // tail, so prepended CRX header bytes are tolerated.
        Directory.CreateDirectory(versionDir);
        log.LogInformation("Extracting to {VersionDir} ...", versionDir);

        ms.Position = 0;
        try
        {
            using var zf = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
            ExtractZipSafely(zf, versionDir);
        }
        catch (InvalidDataException)
        {
            // Some CRX3 headers confuse the EOCD scan — trim anything before the first
            // PK signature (0x04034b50 little-endian) and retry.
            var trimmed = TrimToZip(ms.ToArray())
                ?? throw new InvalidDataException(
                    "Downloaded CRX3 payload does not contain a recognisable zip archive.");
            using var ms2 = new MemoryStream(trimmed, writable: false);
            using var zf = new ZipArchive(ms2, ZipArchiveMode.Read);
            ExtractZipSafely(zf, versionDir);
        }

        if (!File.Exists(libPath))
            throw new InvalidOperationException(
                $"Extraction finished but {PlatformPaths.LibraryName} not found in {versionDir}");

        log.LogInformation("Installed screen-ai {Version}", info.Version);
        return versionDir;
    }

    // ---------------------------------------------------------------------
    // Public facade: try the best strategy available.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Install the screen-ai component using the best available method — matches
    /// <c>download_component()</c> in <c>_download.py</c>.
    /// </summary>
    public static async Task<string> DownloadComponentAsync(
        string? targetDir = null, ILogger? log = null, CancellationToken ct = default)
    {
        log ??= NullLogger.Instance;

        try { return CopyFromChrome(targetDir, log); }
        catch (FileNotFoundException)
        {
            log.LogInformation("Chrome component not found locally, trying Dropbox zip...");
        }

        try { return InstallFromZip(targetDir: targetDir, log: log); }
        catch (FileNotFoundException)
        {
            log.LogInformation("Dropbox zip not found, trying server download...");
        }

        return await DownloadFromServerAsync(targetDir, log: log, ct: ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static (string Platform, string OsVersion) GetOmahaPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ("win", Environment.OSVersion.Version.ToString());
        return ("linux", Environment.OSVersion.Version.ToString());
    }

    private static void CopyDirectoryRecursive(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, dir);
            Directory.CreateDirectory(Path.Combine(dest, rel));
        }
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void ExtractZipSafely(ZipArchive zf, string destDir)
    {
        var destFull = Path.GetFullPath(destDir);
        foreach (var entry in zf.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry

            var target = Path.GetFullPath(Path.Combine(destFull, entry.FullName));
            if (!target.StartsWith(destFull, StringComparison.Ordinal))
                throw new InvalidDataException($"Zip entry escapes target dir: {entry.FullName}");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>Locate the local-file-header PK signature and return bytes from that offset onward.</summary>
    private static byte[]? TrimToZip(ReadOnlySpan<byte> bytes)
    {
        // local file header magic: 0x50 0x4B 0x03 0x04
        for (var i = 0; i < bytes.Length - 3; i++)
        {
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x03 && bytes[i + 3] == 0x04)
                return bytes[i..].ToArray();
        }
        return null;
    }
}
