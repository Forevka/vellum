# Changelog

## 0.3.0 — Optional PDFium rasteriser

### Why

Production OCR processes grew until they were OOM-killed.
The memory was attributed to Chrome's screen-ai library; bisecting in a
linux/amd64 container against the production documents showed otherwise:

| Stage tested in isolation | Result |
|---|---|
| screen-ai only (same 30 pages OCR'd 4×) | +235 MB once for model load, then flat: 235 → 235 → 235 → 235 MB |
| PDF rendering only, no OCR (167-page document, 3 passes) | peak 3.1 GB, 1.26 GB still allocated after close; pass 2 peaked at 5 GB; pass 3 OOM at 8 GB |

Root cause: `PDF2SVG.PopplerCairo.Bindings` statically links Poppler and Cairo.
Poppler renders Type 3 fonts through Cairo user fonts, and Cairo caches the
rendered glyphs in process-global scaled-font caches (plus up to 256 released
"holdover" fonts). That memory outlives the document, is not released by GC or
`malloc_trim`, and is only evicted slowly as later documents create new fonts.
On the incident document only the five pages that use Type 3 fonts (79, 80, 84,
85, 88) cost memory — 0.3–1.9 GB each. Calling Cairo's
`cairo_debug_reset_static_data()` after a document dropped retained memory from
939 MB to 4 MB (it then crashed the process, so it is not a usable fix). The
wrapper's own native code frees everything correctly; the retention is inside
Poppler/Cairo.

### Added

- `VellumOptions.PdfRasterizer` / `PdfRasterizerKind` selects the engine that renders
  PDF pages before OCR (strategy: `IPdfRasterizer` with `Pdf2SvgRasterizer` and
  `PdfiumRasterizer`).
  - `Pdf2Svg` — **default**, Poppler/Cairo, behaviour unchanged.
  - `Pdfium` — PDFium (`bblanchon.PDFium.Linux` / `bblanchon.PDFium.Win32`
    156.0.8066) via a small P/Invoke layer (`Interop/PdfiumNative.cs`).
- New `ScreenAI(modelDir, lightMode, log, pdfRasterizer)` constructor overload. The
  existing constructor is unchanged and uses `Pdf2Svg`.

With `Pdfium`, pages are rendered directly into an opaque BGRA `SKBitmap` at 300 DPI
capped to the engine's max dimension (2048 px) — no PNG round-trip — and pages
outside a requested page range are not rendered.

No breaking changes: existing callers keep the Poppler/Cairo path unless they opt in.

### Measured: `Pdf2Svg` vs `Pdfium` (production documents, 4 GB container, linux/amd64)

| | `Pdf2Svg` (0.2.7 behaviour) | `Pdfium` |
|---|---|---|
| 167-page document, rendering only: peak / retained | 3.1 GB / 1.26 GB, growing per pass | 200 MB / 3 MB, flat |
| All 30 distinct documents with OCR in one process | OOM-killed on document 13 | all complete; process peak 1.6 GB; native heap stays 200–530 MB |
| 167-page document, full OCR | OOM-killed | 70 s, process peak 1.1 GB |
| OCR text on the 12 documents both versions finished | — | 98–100 % word overlap |

Text differences are mostly block ordering plus a few words either way.

### Known, not addressed in this release

- `OcrBitmap` disposes the caller's bitmap when it has to be downscaled.
- `SkiaImageOps.ToBgraBytes` leaks a temporary bitmap when it converts the pixel format.
- Windows has not been tested with the PDFium backend yet.
- The `Pdf2Svg` path still retains memory on PDFs with Type 3 fonts; fixing it needs
  changes to the native pdf2svg wrapper (e.g. rasterising via Poppler's Splash backend
  instead of Cairo) or a newer Poppler/Cairo build.
