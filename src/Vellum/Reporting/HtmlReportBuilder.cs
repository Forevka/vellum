using System.Globalization;
using System.Net;
using System.Text;
using Vellum.Models;
using SkiaSharp;

namespace Vellum.Reporting;

/// <summary>
/// Produces a single, self-contained HTML document that shows each OCR'd page
/// image with hover-highlightable word boxes synchronised to an extracted-text
/// sidebar — visually analogous to AWS Textract's result viewer.  All assets
/// (images, CSS, JS) are inlined; the output works offline from a single file.
/// </summary>
public sealed class HtmlReportBuilder
{
    private readonly string _title;
    private readonly List<PageData> _pages = [];

    public HtmlReportBuilder(string title)
    {
        _title = string.IsNullOrWhiteSpace(title) ? "OCR Results" : title;
    }

    /// <summary>Encode <paramref name="bitmap"/> as PNG and append it with <paramref name="page"/>'s OCR data.</summary>
    public void AddPage(OcrPage page, SKBitmap bitmap)
    {
        using var img = SKImage.FromBitmap(bitmap);
        using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        _pages.Add(new PageData(
            Page: page,
            Width: bitmap.Width,
            Height: bitmap.Height,
            PngBase64: Convert.ToBase64String(data.ToArray())));
    }

    public string Build()
    {
        var sb = new StringBuilder(64 * 1024);
        AppendHeader(sb);
        AppendBody(sb);
        AppendFooter(sb);
        return sb.ToString();
    }

    // ---------------------------------------------------------------------
    // HTML assembly
    // ---------------------------------------------------------------------

    private void AppendHeader(StringBuilder sb)
    {
        sb.Append("""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>
            """);
        sb.Append(WebUtility.HtmlEncode(_title));
        sb.Append("""
            </title>
            <style>
              :root {
                --bg: #1c1f24; --panel: #242932; --text: #e6e6e6; --muted: #9aa0a6;
                --accent: #4ca3ff; --accent-bg: rgba(76,163,255,.18); --hover-bg: rgba(255,215,0,.25);
                --word-stroke: rgba(76,163,255,.35); --word-hover: rgba(255,215,0,.8);
              }
              * { box-sizing: border-box; }
              body { margin: 0; font-family: -apple-system, "Segoe UI", Roboto, sans-serif; background: var(--bg); color: var(--text); }
              header { padding: 12px 20px; background: var(--panel); border-bottom: 1px solid #333; position: sticky; top: 0; z-index: 10; display: flex; gap: 20px; align-items: baseline; flex-wrap: wrap; }
              header h1 { font-size: 16px; margin: 0; font-weight: 600; }
              header .meta { color: var(--muted); font-size: 13px; }
              header input[type="search"] { background: #14171c; color: var(--text); border: 1px solid #444; border-radius: 4px; padding: 6px 10px; font-size: 13px; min-width: 240px; }
              .page { display: grid; grid-template-columns: minmax(0, 1fr) 360px; gap: 0; border-bottom: 1px solid #333; }
              .page-img { position: relative; background: #111; overflow: auto; padding: 16px; }
              .page-img .canvas { position: relative; margin: auto; }
              .page-img img { display: block; width: 100%; height: auto; }
              .page-img svg { position: absolute; inset: 0; width: 100%; height: 100%; }
              .word-box { fill: transparent; stroke: var(--word-stroke); stroke-width: 1; cursor: pointer; pointer-events: all; }
              .word-box:hover, .word-box.hl { fill: var(--hover-bg); stroke: var(--word-hover); stroke-width: 2; }
              .sidebar { background: var(--panel); border-left: 1px solid #333; overflow-y: auto; max-height: calc(100vh - 52px); position: sticky; top: 52px; }
              .sidebar h2 { font-size: 13px; color: var(--muted); text-transform: uppercase; letter-spacing: .05em; margin: 12px 16px 4px; }
              .block { padding: 6px 16px 12px; border-bottom: 1px solid #2a2f38; }
              .line { line-height: 1.5; font-size: 14px; word-wrap: break-word; }
              .word { display: inline; padding: 0 1px; border-radius: 2px; cursor: pointer; transition: background 0.1s ease; }
              .word:hover, .word.hl { background: var(--hover-bg); color: #fff; }
              .word.dim { opacity: .3; }
              .word-box.dim { opacity: .15; }
            </style>
            </head>
            <body>
            """);
        sb.Append("""
            <header>
              <h1>
            """);
        sb.Append(WebUtility.HtmlEncode(_title));
        sb.Append("""
            </h1>
              <span class="meta" id="stats"></span>
              <input type="search" id="filter" placeholder="Filter words…" />
            </header>
            """);
    }

    private void AppendBody(StringBuilder sb)
    {
        var totalWords = 0;
        for (var p = 0; p < _pages.Count; p++)
        {
            var page = _pages[p];
            var pageIndex = p;

            sb.Append($"""<section class="page" data-page="{page.Page.PageNumber}"><div class="page-img"><div class="canvas" style="aspect-ratio: {page.Width}/{page.Height};"><img alt="Page {page.Page.PageNumber}" src="data:image/png;base64,""");
            sb.Append(page.PngBase64);
            sb.Append($"""
                "><svg viewBox="0 0 {page.Width} {page.Height}" preserveAspectRatio="none">
                """);

            // Emit one <rect> per word with bounding box.
            var wordIndex = 0;
            foreach (var block in page.Page.Blocks)
            {
                foreach (var line in block.Lines)
                {
                    foreach (var word in line.Words)
                    {
                        if (word.BoundingBox is null) continue;
                        var bb = word.BoundingBox;
                        var id = $"w-{pageIndex}-{wordIndex++}";
                        sb.Append(CultureInfo.InvariantCulture,
                            $"""<rect class="word-box" data-id="{id}" x="{bb.X:F1}" y="{bb.Y:F1}" width="{bb.Width:F1}" height="{bb.Height:F1}"></rect>""");
                    }
                }
            }

            sb.Append("</svg></div></div>");
            sb.Append($"""<aside class="sidebar" data-page="{page.Page.PageNumber}"><h2>Page {page.Page.PageNumber}</h2>""");

            wordIndex = 0;
            foreach (var block in page.Page.Blocks)
            {
                sb.Append("""<div class="block">""");
                foreach (var line in block.Lines)
                {
                    sb.Append("""<div class="line">""");
                    for (var i = 0; i < line.Words.Count; i++)
                    {
                        var word = line.Words[i];
                        var id = word.BoundingBox is null ? string.Empty : $"w-{pageIndex}-{wordIndex++}";
                        if (id.Length == 0)
                        {
                            sb.Append(WebUtility.HtmlEncode(word.Text));
                        }
                        else
                        {
                            sb.Append($"""<span class="word" data-id="{id}" data-text="{WebUtility.HtmlEncode(word.Text.ToLowerInvariant())}">""");
                            sb.Append(WebUtility.HtmlEncode(word.Text));
                            sb.Append("</span>");
                        }
                        if (i < line.Words.Count - 1) sb.Append(' ');
                        totalWords++;
                    }
                    sb.Append("</div>");
                }
                sb.Append("</div>");
            }

            sb.Append("</aside></section>");
        }

        sb.Append("<script>window.__vellumStats={pages:");
        sb.Append(_pages.Count);
        sb.Append(",words:");
        sb.Append(totalWords);
        sb.Append("};</script>");
    }

    private static void AppendFooter(StringBuilder sb)
    {
        sb.Append("""
            <script>
            (function () {
              const stats = document.getElementById('stats');
              if (stats) stats.textContent = `${window.__vellumStats.pages} page(s), ${window.__vellumStats.words} words`;

              const byId = new Map();
              document.querySelectorAll('[data-id]').forEach(el => {
                if (!byId.has(el.dataset.id)) byId.set(el.dataset.id, []);
                byId.get(el.dataset.id).push(el);
              });

              function setHl(id, on) {
                const elems = byId.get(id); if (!elems) return;
                elems.forEach(el => el.classList.toggle('hl', on));
                if (on) {
                  const sidebarEl = elems.find(e => e.classList.contains('word'));
                  if (sidebarEl) sidebarEl.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
                }
              }

              document.addEventListener('mouseover', (e) => {
                const el = e.target.closest('[data-id]'); if (!el) return;
                setHl(el.dataset.id, true);
              });
              document.addEventListener('mouseout', (e) => {
                const el = e.target.closest('[data-id]'); if (!el) return;
                setHl(el.dataset.id, false);
              });

              const filter = document.getElementById('filter');
              if (filter) {
                filter.addEventListener('input', () => {
                  const q = filter.value.trim().toLowerCase();
                  const allWords = document.querySelectorAll('.word');
                  const allBoxes = document.querySelectorAll('.word-box');
                  if (!q) {
                    allWords.forEach(w => w.classList.remove('dim'));
                    allBoxes.forEach(w => w.classList.remove('dim'));
                    return;
                  }
                  allWords.forEach(w => {
                    const match = (w.dataset.text || '').includes(q);
                    w.classList.toggle('dim', !match);
                  });
                  const activeIds = new Set();
                  allWords.forEach(w => { if (!w.classList.contains('dim')) activeIds.add(w.dataset.id); });
                  allBoxes.forEach(b => b.classList.toggle('dim', !activeIds.has(b.dataset.id)));
                });
              }
            })();
            </script>
            </body>
            </html>
            """);
    }

    private sealed record PageData(OcrPage Page, int Width, int Height, string PngBase64);
}
