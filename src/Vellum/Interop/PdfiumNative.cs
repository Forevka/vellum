using System.Runtime.InteropServices;

namespace Vellum.Interop;

/// <summary>
/// Minimal P/Invoke surface over PDFium (native binaries from the bblanchon.PDFium.* packages).
/// PDFium is not thread-safe, so every call must be made while holding <see cref="Sync"/>.
/// </summary>
internal static partial class PdfiumNative
{
    private const string Lib = "pdfium";

    public const int FPDFBitmap_BGRA = 4;
    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_REVERSE_BYTE_ORDER = 0x10;

    public static readonly Lock Sync = new();

    private static bool _initialized;

    public static void EnsureInitialized()
    {
        lock (Sync)
        {
            if (_initialized) return;
            FPDF_InitLibrary();
            _initialized = true;
        }
    }

    [LibraryImport(Lib)] private static partial void FPDF_InitLibrary();
    [LibraryImport(Lib)] public static partial uint FPDF_GetLastError();

    [LibraryImport(Lib)] public static partial IntPtr FPDF_LoadMemDocument64(IntPtr data, nuint size, IntPtr password);
    [LibraryImport(Lib)] public static partial void FPDF_CloseDocument(IntPtr document);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageCount(IntPtr document);

    [LibraryImport(Lib)] public static partial IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);
    [LibraryImport(Lib)] public static partial void FPDF_ClosePage(IntPtr page);
    [LibraryImport(Lib)] public static partial float FPDF_GetPageWidthF(IntPtr page);
    [LibraryImport(Lib)] public static partial float FPDF_GetPageHeightF(IntPtr page);

    [LibraryImport(Lib)]
    public static partial IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);
    [LibraryImport(Lib)] public static partial void FPDFBitmap_Destroy(IntPtr bitmap);
    [LibraryImport(Lib)]
    public static partial void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [LibraryImport(Lib)]
    public static partial void FPDF_RenderPageBitmap(
        IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
}
