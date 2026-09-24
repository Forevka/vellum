namespace Vellum;

/// <summary>Which engine renders PDF pages to bitmaps before OCR.</summary>
public enum PdfRasterizerKind
{
    /// <summary>
    /// Poppler/Cairo via PDF2SVG.PopplerCairo.Bindings (default, unchanged behaviour).
    /// Pages with Type 3 fonts can grow Cairo's process-global glyph cache by hundreds
    /// of MB per page, and that memory is not released until the process exits.
    /// </summary>
    Pdf2Svg = 0,

    /// <summary>
    /// PDFium via bblanchon.PDFium.*. Renders straight to a bitmap at the OCR size,
    /// skips pages outside the requested range, and releases all memory when the
    /// document is closed.
    /// </summary>
    Pdfium = 1,
}
