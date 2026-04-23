using System.CommandLine;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Vellum;
using Vellum.Cli;
using Vellum.Download;
using Vellum.Models;
using Vellum.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

var root = new RootCommand("OCR documents and images using Chrome's screen-ai library.");

root.AddCommand(OcrCommand.Build());
root.AddCommand(DownloadCommand.Build());
root.AddCommand(ExportCommand.Build());

return await root.InvokeAsync(args);

namespace Vellum.Cli
{
    internal static class OcrCommand
    {
        public static Command Build()
        {
            var file = new Argument<FileInfo>("file", "PDF or image file to OCR.");

            var outputDir = new Option<DirectoryInfo?>(
                aliases: ["-o", "--output-dir"],
                description: "Output directory (default: same as input).");

            var text = new Option<bool>(
                name: "--text",
                description: "Print extracted text to stdout instead of writing files.");

            var pagesSpec = new Option<string?>(
                aliases: ["-p", "--pages"],
                description: "Pages to OCR (PDF only). Examples: 1 | 1-10 | 1,3,5 | 1-5,10-12");

            var light = new Option<bool>(
                name: "--light",
                description: "Use the smaller/faster OCR model.");

            var searchablePdf = new Option<FileInfo?>(
                aliases: ["-s", "--searchable-pdf"],
                description: "Write a searchable PDF with invisible text overlay (PDF input only).");

            var html = new Option<FileInfo?>(
                aliases: ["--html"],
                description: "Write a self-contained HTML report with hoverable word boxes and a text sidebar.");

            var verbose = new Option<bool>(
                aliases: ["-v", "--verbose"],
                description: "Verbose / debug logging.");

            var cmd = new Command("ocr", "OCR a document or image using Chrome's screen-ai.")
            {
                file, outputDir, text, pagesSpec, light, searchablePdf, html, verbose,
            };

            cmd.SetHandler(Run, file, outputDir, text, pagesSpec, light, searchablePdf, html, verbose);
            return cmd;
        }

        private static void Run(
            FileInfo file, DirectoryInfo? outputDir, bool text, string? pagesSpec,
            bool light, FileInfo? searchablePdf, FileInfo? html, bool verbose)
        {
            CliLogging.Setup(verbose);

            if (!file.Exists)
            {
                Console.Error.WriteLine($"Error: file not found: {file.FullName}");
                Environment.Exit(1);
                return;
            }

            var suffix = file.Extension.ToLowerInvariant();
            var supported = new[] { ".pdf", ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tiff", ".tif", ".gif" };
            if (!supported.Contains(suffix, StringComparer.Ordinal))
            {
                Console.Error.WriteLine(
                    $"Error: unsupported file type '{file.Extension}' (expected {string.Join(", ", supported)})");
                Environment.Exit(1);
                return;
            }

            List<int>? pages = null;
            if (!string.IsNullOrEmpty(pagesSpec))
            {
                try { pages = ParsePages(pagesSpec); }
                catch (FormatException ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    Environment.Exit(2);
                    return;
                }

                if (suffix != ".pdf")
                {
                    Console.Error.WriteLine("Warning: --pages is ignored for image files.");
                    pages = null;
                }
            }

            if (searchablePdf is not null && suffix != ".pdf")
            {
                Console.Error.WriteLine("Error: --searchable-pdf requires a PDF input.");
                Environment.Exit(1);
                return;
            }

            var sw = Stopwatch.StartNew();
            using var ai = new ScreenAI(lightMode: light, log: CliLogging.LoggerFor<ScreenAI>());

            OcrResult result;
            if (searchablePdf is not null)
            {
                result = ai.OcrToSearchablePdf(file.FullName, searchablePdf.FullName, pages);
                Console.WriteLine($"  Searchable PDF -> {searchablePdf.FullName}");
                if (html is not null)
                {
                    // Searchable PDF + HTML at once: run a fresh pass that keeps the bitmaps.
                    ai.OcrToHtml(file.FullName, html.FullName, pages);
                    Console.WriteLine($"  HTML report    -> {html.FullName}");
                }
            }
            else if (html is not null)
            {
                result = ai.OcrToHtml(file.FullName, html.FullName, pages);
                Console.WriteLine($"  HTML report    -> {html.FullName}");
            }
            else
            {
                result = ai.Ocr(file.FullName, pages);
            }

            var pageCount = result.Pages.Count;
            var totalBlocks = result.Pages.Sum(p => p.Blocks.Count);

            if (text)
            {
                Console.WriteLine(result.ToText());
            }
            else if (searchablePdf is null && html is null)
            {
                if (outputDir is not null)
                    outputDir.Create();
                WriteOutputs(result, file, outputDir);
            }

            var elapsed = sw.Elapsed;
            var timeStr = elapsed.TotalSeconds >= 60
                ? $"{elapsed.TotalMinutes:F1} minutes"
                : $"{elapsed.TotalSeconds:F1} seconds";
            Console.WriteLine($"Done. {pageCount} page(s), {totalBlocks} block(s). Total time: {timeStr}.");
        }

        private static void WriteOutputs(OcrResult result, FileInfo input, DirectoryInfo? outputDir)
        {
            var baseName = Path.GetFileNameWithoutExtension(input.Name);
            var dir = outputDir?.FullName ?? input.DirectoryName ?? Environment.CurrentDirectory;

            var txtPath = Path.Combine(dir, $"{baseName}_ocr.txt");
            File.WriteAllText(txtPath, result.ToText());

            var jsonPath = Path.Combine(dir, $"{baseName}_ocr.json");
            File.WriteAllText(jsonPath, result.ToJson(indented: true));

            Console.WriteLine($"  Text -> {txtPath}");
            Console.WriteLine($"  JSON -> {jsonPath}");
        }

        /// <summary>Parse a page spec like <c>"1-5"</c>, <c>"1,3,5"</c>, or <c>"1-5,10-12"</c>.</summary>
        internal static List<int> ParsePages(string spec)
        {
            var result = new SortedSet<int>();
            foreach (var raw in spec.Split(','))
            {
                var part = raw.Trim();
                if (part.Length == 0) continue;

                var range = Regex.Match(part, @"^(\d+)\s*-\s*(\d+)$");
                if (range.Success)
                {
                    var lo = int.Parse(range.Groups[1].Value);
                    var hi = int.Parse(range.Groups[2].Value);
                    for (var i = lo; i <= hi; i++) result.Add(i);
                }
                else if (int.TryParse(part, out var single))
                {
                    result.Add(single);
                }
                else
                {
                    throw new FormatException($"Invalid page spec: '{part}'");
                }
            }
            return [.. result];
        }
    }

    internal static class DownloadCommand
    {
        public static Command Build()
        {
            var verbose = new Option<bool>(aliases: ["-v", "--verbose"], description: "Verbose / debug logging.");
            var modelDir = new Option<DirectoryInfo?>(
                name: "--model-dir",
                description: $"Directory to store the component (default: {PlatformPaths.DefaultModelDirDisplay()}).");

            var cmd = new Command("download", "Install the screen-ai component (library + models).")
            {
                verbose, modelDir,
            };
            cmd.SetHandler(async (v, m) =>
            {
                CliLogging.Setup(v);
                try
                {
                    var dir = await ComponentDownloader.DownloadComponentAsync(
                        targetDir: m?.FullName,
                        log: CliLogging.LoggerFor("download"));
                    Console.WriteLine($"Installed to {dir}");
                }
                catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    Environment.Exit(1);
                }
            }, verbose, modelDir);
            return cmd;
        }
    }

    internal static class ExportCommand
    {
        public static Command Build()
        {
            var output = new Option<FileInfo?>(
                aliases: ["-o", "--output"],
                description: "Output zip path (default: ~/Dropbox/bin/screen-ai-{platform}.zip).");
            var verbose = new Option<bool>(aliases: ["-v", "--verbose"], description: "Verbose / debug logging.");

            var cmd = new Command("export", "Export the installed screen-ai component as a zip file.")
            {
                output, verbose,
            };
            cmd.SetHandler((o, v) =>
            {
                CliLogging.Setup(v);
                try
                {
                    var result = ComponentDownloader.ExportToZip(
                        zipPath: o?.FullName, log: CliLogging.LoggerFor("export"));
                    Console.WriteLine($"Exported to {result}");
                }
                catch (FileNotFoundException ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    Environment.Exit(1);
                }
            }, output, verbose);
            return cmd;
        }
    }

    internal static class CliLogging
    {
        private static ILoggerFactory? _factory;

        public static void Setup(bool verbose)
        {
            _factory = LoggerFactory.Create(b => b
                .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information)
                .AddSimpleConsole(o =>
                {
                    o.SingleLine = true;
                    o.TimestampFormat = null;
                    o.IncludeScopes = false;
                }));
        }

        public static ILogger<T> LoggerFor<T>() =>
            (_factory ?? NullLoggerFactory()).CreateLogger<T>();

        public static ILogger LoggerFor(string category) =>
            (_factory ?? NullLoggerFactory()).CreateLogger(category);

        private static ILoggerFactory NullLoggerFactory()
        {
            _factory = LoggerFactory.Create(_ => { });
            return _factory;
        }
    }
}
