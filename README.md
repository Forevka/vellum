# Vellum

**Chrome screen-ai OCR for .NET 9.**
Loads Google Chrome's built-in OCR engine (`chrome_screen_ai.dll` /
`libchromescreenai.so`) directly from managed code and extracts text from PDFs
and images — no browser window, no Tesseract, no cloud API.

A .NET port of [sergiocorreia/clv-locro](https://github.com/sergiocorreia/clv-locro).

> **Note — the native Chrome OCR library is not redistributed.** You need either a local
> Chrome install (the wrapper finds it automatically) or a one-time `vellum download`
> copy step. See [Screen AI binaries](#screen-ai-binaries--one-time-setup) below.

---

## Features

- **Fast, local OCR** via the same on-device engine Chrome ships for accessibility.
- **PDF and image input** — `.pdf`, `.jpg`, `.jpeg`, `.png`, `.webp`, `.bmp`, `.tiff`, `.tif`, `.gif`.
- **Structured results** — pages → blocks → lines → words, each with bounding box and confidence.
- **Searchable PDF output** — overlay an invisible text layer on the original pages.
- **Interactive HTML report** — single self-contained file with hoverable word boxes
  and a synchronised sidebar (AWS Textract-style viewer).
- **ASP.NET Core ready** — `services.AddVellumOcr()` with lazy initialisation;
  your app starts even before the binaries are installed.
- **Cross-platform** — `win-x64` and `linux-x64`.

---

## Install

### Library

```bash
dotnet add package Vellum
```

### CLI (global tool)

```bash
dotnet tool install -g Vellum.Cli
```

Exposes a `vellum` command with three subcommands: `ocr`, `download`, `export`.

---

## Screen AI binaries — one-time setup

The Chrome OCR library (~33 MB DLL plus ~100–250 MB of TFLite models) is licensed
to ship *only* with Chrome. Vellum does not redistribute it. Pick one of:

| Option                                | Setup                                                                 |
| ------------------------------------- | --------------------------------------------------------------------- |
| **A. Use Chrome in place**            | Install Chrome, open `chrome://components`, trigger **Screen AI** download. Vellum finds it. |
| **B. Copy out of Chrome**             | `vellum download` — copies into `%LOCALAPPDATA%\vellum\<version>\` (Windows) or `~/.local/share/vellum/<version>/` (Linux). |
| **C. Ship a portable zip**            | On a machine with the component installed, run `vellum export -o bundle.zip`. On target machines, drop it at `~/Dropbox/bin/screen-ai-{windows,linux}.zip` or pass `--zip`. |

Discovery order used at first OCR call: Chrome → Vellum cache → Dropbox zip → Omaha server (currently restricted to Chrome).

---

## Quick start

### Library

```csharp
using Vellum;

using var ai = new ScreenAI();               // auto-discovers the component
var result = ai.Ocr("invoice.pdf");

Console.WriteLine(result.ToText());           // plain text, pages joined by \f
File.WriteAllText("invoice.json", result.ToJson());
```

### CLI

```bash
vellum ocr document.pdf                    # writes document_ocr.txt + document_ocr.json
vellum ocr photo.jpg --text                # pipe-friendly text to stdout
vellum ocr big.pdf -p 1-5,10                # specific pages
vellum ocr scan.png --light                 # smaller/faster model
vellum ocr doc.pdf -s doc_searchable.pdf    # invisible text overlay
vellum ocr doc.pdf --html doc.html          # interactive HTML report
```

Full page-spec grammar: `1`, `1-10`, `1,3,5`, `1-5,10-12` — ranges and single pages, comma-separated.

---

## ASP.NET Core / generic host

`AddVellumOcr` registers an `IOcrEngine` singleton. Startup succeeds even when the
native binaries aren't installed yet — the DLL is only loaded on the first OCR
call, which makes it safe to wire up and deploy to machines that will pick up
Chrome / the model cache later.

```csharp
using Vellum;

builder.Services.AddVellumOcr(options =>
{
    options.LightMode       = false;   // true = smaller, faster model
    options.AutoDownload    = true;    // copy from Chrome on first use if missing
    options.SerializeCalls  = true;    // (default) lock around PerformOCR for thread safety
    // options.ModelDir     = "/opt/screen_ai/140.20";   // pin explicitly if you want
});
```

Inject and use anywhere:

```csharp
app.MapPost("/ocr", async (IOcrEngine ocr, IFormFile file, CancellationToken ct) =>
{
    await ocr.EnsureReadyAsync(ct);            // optional warm-up

    var tmp = Path.GetTempFileName();
    await using (var fs = File.Create(tmp))
        await file.CopyToAsync(fs, ct);

    try   { return Results.Text(ocr.Ocr(tmp).ToText()); }
    finally { File.Delete(tmp); }
});
```

`IsReady` tells you whether the library is loaded; `EnsureReadyAsync(ct)` is a
good fit for a health check or a `BackgroundService` warm-up.

---

## Output formats

### `OcrResult` (model)

```
OcrResult
└─ Pages: IReadOnlyList<OcrPage>
   └─ OcrPage { PageNumber, Width, Height, Blocks }
      └─ OcrBlock { BlockType, Lines }
         └─ OcrLine { Text, BoundingBox?, Words }
            └─ OcrWord { Text, Confidence?, BoundingBox? }
```

- `result.ToText()` — plain text; pages separated by `\f` (form feed).
- `result.ToJson(indented: true)` — structured JSON.

### JSON

```json
{
  "pages": [
    {
      "pageNumber": 1,
      "width": 1377,
      "height": 2048,
      "blocks": [
        {
          "blockType": "paragraph",
          "lines": [
            {
              "text": "Hello world",
              "boundingBox": { "x": 50, "y": 100, "width": 300, "height": 30 },
              "words": [
                { "text": "Hello", "confidence": 0.98, "boundingBox": { "x": 50, "y": 100, "width": 120, "height": 30 } }
              ]
            }
          ]
        }
      ]
    }
  ]
}
```

Coordinates are in pixels of the image actually OCR'd (which may have been
downscaled to fit the library's 2048 px maximum).

### HTML report

`vellum ocr file --html out.html` produces a single self-contained HTML file:

- Original page image on the left (inlined as base64 PNG).
- Transparent SVG rectangles over every word; hovering highlights the matching
  sidebar entry and scrolls it into view.
- Sidebar on the right with blocks → lines → words; hovering in either
  direction highlights the other.
- Top-bar filter input that dims non-matching words everywhere.

Caveat: size is roughly `page_count × image_size × 1.33` (base64 overhead). Fine for
up to a few dozen pages; large PDFs can produce very large HTML files.

### Searchable PDF

```bash
vellum ocr scanned.pdf -s scanned_searchable.pdf
vellum ocr scanned.pdf -s scanned_searchable.pdf -p 1-10
```

Writes a copy of the input PDF with invisible (fully-transparent) text layered
over each word, so the file is selectable and searchable in any PDF viewer
while still rendering visually identical to the original.

---

## CLI reference

### `vellum ocr <file> [options]`

| Option                        | Description                                                       |
| ----------------------------- | ----------------------------------------------------------------- |
| `-o`, `--output-dir <dir>`    | Directory for `*_ocr.txt` / `*_ocr.json` (default: same as input) |
| `--text`                      | Print extracted text to stdout instead of writing files           |
| `-p`, `--pages <spec>`        | Pages to OCR (PDF only): `1`, `1-10`, `1,3,5`, `1-5,10-12`        |
| `--light`                     | Use the smaller / faster model                                    |
| `-s`, `--searchable-pdf <p>`  | Write a searchable PDF to the given path (PDF input only)         |
| `--html <path>`               | Write a self-contained interactive HTML report                    |
| `-v`, `--verbose`             | Debug-level logging, including native library output              |

### `vellum download [options]`

Copies the screen-ai component from Chrome's user-data directory into Vellum's
cache. Falls back to Dropbox zip and then Omaha if Chrome isn't installed.

| Option                       | Description                                            |
| ---------------------------- | ------------------------------------------------------ |
| `--model-dir <dir>`          | Override the cache location (default `%LOCALAPPDATA%/vellum` or `~/.local/share/vellum`) |
| `-v`, `--verbose`            | Verbose logging                                        |

### `vellum export [options]`

Packages the installed component as a portable zip for other machines.

| Option                       | Description                                                   |
| ---------------------------- | ------------------------------------------------------------- |
| `-o`, `--output <path>`      | Output zip path (default `~/Dropbox/bin/screen-ai-{platform}.zip`) |
| `-v`, `--verbose`            | Verbose logging                                               |

---

## Threading and lifecycle

- The native library spawns its own worker threads but **does not document
  concurrent `PerformOCR` safety**. Vellum's `LazyOcrEngine` serialises calls
  under a `SemaphoreSlim` by default (`VellumOptions.SerializeCalls = true`).
- The native library keeps **process-global state**. Do not create and
  dispose multiple `ScreenAI` instances in the same process — the second
  `InitOCR` will crash. Use `AddVellumOcr()` (singleton) in DI.
- Max supported image dimension is **2048 px**. Larger images are
  downscaled before OCR.

---

## Building from source

```bash
git clone --recurse-submodules https://github.com/<you>/vellum.git
cd vellum
dotnet build
dotnet test
dotnet pack -c Release           # → artifacts/nupkg/Vellum.*.nupkg + Vellum.Cli.*.nupkg
```

Run the CLI from source:

```bash
dotnet run --project src/Vellum.Cli -- ocr sample.pdf
```

Submodule: [`external/pdf2svg_poppler_cairo`](https://github.com/Forevka/pdf2svg_poppler_cairo) —
Poppler/Cairo-backed PDF rasteriser with prebuilt Win-x64 / Linux-x64 natives.

---

## How it works

- **Interop** — `NativeLibrary.Load` resolves the exported C functions;
  `delegate* unmanaged[Cdecl]` function pointers call them without marshalling.
- **SkBitmap layout** — `PerformOCR` accepts a C++ `SkBitmap&`; Vellum rebuilds
  its 56-byte memory layout (and a 104-byte fake `SkPixelRef` to pass the
  null check) as `[StructLayout(LayoutKind.Explicit)]` structs. The exact
  offsets were reverse-engineered empirically by the Python project; see
  [`CHROME_SCREEN_AI_DLL.md`](https://github.com/sergiocorreia/clv-locro/blob/master/CHROME_SCREEN_AI_DLL.md)
  upstream.
- **Model files** — the DLL reads models via host-provided callbacks; Vellum
  supplies them from whatever directory the component lives in.
- **Protobuf** — `PerformOCR` returns a serialised
  `chrome_screen_ai.VisualAnnotation` message; Vellum decodes it directly with
  a ~150-line wire-format parser, no `.proto` compilation step.
- **PDF rasterisation** — pages are rendered to PNG via
  `pdf2svg_poppler_cairo` then decoded into `SKBitmap` before OCR.

---

## Project layout

```
ocr_playground/
├── src/
│   ├── Vellum/                       # library (NuGet: Vellum)
│   │   ├── ScreenAI.cs               # public facade
│   │   ├── LazyOcrEngine.cs          # DI-friendly lazy wrapper
│   │   ├── IOcrEngine.cs             # shared abstraction
│   │   ├── VellumOptions.cs          # options for AddVellumOcr
│   │   ├── ServiceCollectionExtensions.cs
│   │   ├── Models/OcrModels.cs       # records
│   │   ├── Protobuf/VisualAnnotationParser.cs
│   │   ├── Interop/                  # SkBitmap structs, native bindings, Linux stubs
│   │   ├── Imaging/                  # SkiaSharp + pdf2svg wrappers
│   │   ├── Download/ComponentDownloader.cs
│   │   ├── Reporting/HtmlReportBuilder.cs
│   │   └── Platform/PlatformPaths.cs
│   ├── Vellum.Cli/                   # `vellum` global tool (NuGet: Vellum.Cli)
│   └── native/chromium_stubs/        # C source for Linux link-time stubs
├── tests/Vellum.Tests/               # xUnit tests (no native DLL required)
└── external/pdf2svg_poppler_cairo/   # submodule
```

---

## License and attribution

Vellum is MIT-licensed, matching the upstream Python project.

- **Python implementation and reverse-engineering notes:** © Sergio Correia,
  [sergiocorreia/clv-locro](https://github.com/sergiocorreia/clv-locro), MIT.
- **Chrome screen-ai library** (`chrome_screen_ai.dll` /
  `libchromescreenai.so`): © Google, distributed as a Chrome component. Not
  redistributed by this package. See Chromium's licenses for terms of use.
- **pdf2svg_poppler_cairo**:
  [Forevka/pdf2svg_poppler_cairo](https://github.com/Forevka/pdf2svg_poppler_cairo),
  bundling Poppler (GPL) and Cairo (LGPL) natives.
