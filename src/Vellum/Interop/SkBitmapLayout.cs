using System.Runtime.InteropServices;

namespace Vellum.Interop;

/*
 * Exact in-memory layout of Skia's SkBitmap / SkImageInfo / SkPixmap as compiled
 * into chrome_screen_ai.dll (component v140, 64-bit x86).  See
 * CHROME_SCREEN_AI_DLL.md in the source repo for how these offsets were
 * reverse-engineered.
 *
 * Critical: the SkImageInfo field order does NOT match current public Skia headers.
 * Layout is: { fColorSpace, fColorType, fAlphaType, fWidth, fHeight }.
 */

internal static class SkConstants
{
    public const int KBgra8888 = 6;  // SkColorType — kN32 on little-endian
    public const int KPremul = 2;    // SkAlphaType
}

[StructLayout(LayoutKind.Explicit, Size = 56)]
internal struct SkBitmap
{
    [FieldOffset(0)] public IntPtr PixelRef;       // sk_sp<SkPixelRef> (bare ptr)
    [FieldOffset(8)] public IntPtr Pixels;         // fPixmap.fPixels
    [FieldOffset(16)] public nuint RowBytes;       // fPixmap.fRowBytes (size_t)
    [FieldOffset(24)] public IntPtr ColorSpace;    // fPixmap.fInfo.fColorSpace (null = sRGB)
    [FieldOffset(32)] public int ColorType;        // fPixmap.fInfo.fColorType
    [FieldOffset(36)] public int AlphaType;        // fPixmap.fInfo.fAlphaType
    [FieldOffset(40)] public int Width;            // fPixmap.fInfo.fWidth
    [FieldOffset(44)] public int Height;           // fPixmap.fInfo.fHeight
    [FieldOffset(48)] public byte Flags;           // fFlags
}

[StructLayout(LayoutKind.Explicit, Size = 104)]
internal struct FakeSkPixelRef
{
    [FieldOffset(0)] public IntPtr VtablePtr;     // must be non-null — never dereferenced during PerformOCR
    [FieldOffset(8)] public int RefCount;
    [FieldOffset(12)] public int Padding;
    [FieldOffset(16)] public int Width;
    [FieldOffset(20)] public int Height;
    [FieldOffset(24)] public IntPtr Pixels;
    [FieldOffset(32)] public nuint RowBytes;
    // 40..104 is intentional padding — zero-initialised.
}
