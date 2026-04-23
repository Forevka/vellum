using System.Runtime.InteropServices;
using Vellum.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vellum.Interop;

/// <summary>
/// Low-level wrapper over <c>chrome_screen_ai.dll</c> / <c>libchromescreenai.so</c>.
/// Mirrors <c>locro/_dll.py</c>'s <c>ScreenAIDll</c> class.
/// </summary>
internal sealed class ScreenAiNative : IDisposable
{
    private readonly ILogger _log;
    private readonly string _modelDir;
    private readonly Dictionary<string, byte[]> _fileCache = new(StringComparer.Ordinal);
    private readonly IntPtr _handle;
    private readonly GetFileSizeFn _sizeFn;     // keepalive
    private readonly GetFileContentFn _contentFn; // keepalive

    // Bound function pointers (cached once during construction).
    private readonly unsafe delegate* unmanaged[Cdecl]<uint*, uint*, void> _pGetLibraryVersion;
    private readonly unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> _pSetFileContentFunctions;
    private readonly unsafe delegate* unmanaged[Cdecl]<byte> _pInitOcrUsingCallback;
    private readonly unsafe delegate* unmanaged[Cdecl]<byte, void> _pSetOcrLightMode;
    private readonly unsafe delegate* unmanaged[Cdecl]<uint> _pGetMaxImageDimension;
    private readonly unsafe delegate* unmanaged[Cdecl]<SkBitmap*, uint*, IntPtr> _pPerformOcr;
    private readonly unsafe delegate* unmanaged[Cdecl]<IntPtr, void> _pFreeLibraryAllocatedCharArray;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate uint GetFileSizeFn(IntPtr utf8Path);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate void GetFileContentFn(IntPtr utf8Path, uint bufSize, IntPtr buffer);

    public ScreenAiNative(string modelDir, ILogger? log = null)
    {
        _log = log ?? NullLogger.Instance;
        _modelDir = modelDir;

        var libPath = Path.Combine(modelDir, PlatformPaths.LibraryName);
        if (!File.Exists(libPath))
            throw new FileNotFoundException($"Library not found: {libPath}", libPath);

        SuppressNativeStderrEnvVars();
        ChromiumStubs.Ensure(modelDir, _log);

        _log.LogInformation("Loading {LibPath}", libPath);
        _handle = NativeLibrary.Load(libPath);

        _sizeFn = OnGetFileSize;
        _contentFn = OnGetFileContent;

        unsafe
        {
            _pGetLibraryVersion = (delegate* unmanaged[Cdecl]<uint*, uint*, void>)
                NativeLibrary.GetExport(_handle, "GetLibraryVersion");
            _pSetFileContentFunctions = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)
                NativeLibrary.GetExport(_handle, "SetFileContentFunctions");
            _pInitOcrUsingCallback = (delegate* unmanaged[Cdecl]<byte>)
                NativeLibrary.GetExport(_handle, "InitOCRUsingCallback");
            _pSetOcrLightMode = (delegate* unmanaged[Cdecl]<byte, void>)
                NativeLibrary.GetExport(_handle, "SetOCRLightMode");
            _pGetMaxImageDimension = (delegate* unmanaged[Cdecl]<uint>)
                NativeLibrary.GetExport(_handle, "GetMaxImageDimension");
            _pPerformOcr = (delegate* unmanaged[Cdecl]<SkBitmap*, uint*, IntPtr>)
                NativeLibrary.GetExport(_handle, "PerformOCR");
            _pFreeLibraryAllocatedCharArray = (delegate* unmanaged[Cdecl]<IntPtr, void>)
                NativeLibrary.GetExport(_handle, "FreeLibraryAllocatedCharArray");
        }
    }

    // ---------------------------------------------------------------------
    // Public thin wrappers
    // ---------------------------------------------------------------------

    public (uint Major, uint Minor) GetVersion()
    {
        uint major = 0, minor = 0;
        unsafe { _pGetLibraryVersion(&major, &minor); }
        return (major, minor);
    }

    public bool InitOcr()
    {
        var sizePtr = Marshal.GetFunctionPointerForDelegate(_sizeFn);
        var contentPtr = Marshal.GetFunctionPointerForDelegate(_contentFn);
        unsafe
        {
            _pSetFileContentFunctions(sizePtr, contentPtr);
            _log.LogInformation("Initializing OCR pipeline...");
            return _pInitOcrUsingCallback() != 0;
        }
    }

    public void SetLightMode(bool enabled)
    {
        unsafe { _pSetOcrLightMode(enabled ? (byte)1 : (byte)0); }
    }

    public uint GetMaxImageDimension()
    {
        unsafe { return _pGetMaxImageDimension(); }
    }

    /// <summary>Run OCR on raw BGRA pixel data.  Returns the serialised protobuf, or null on failure.</summary>
    public byte[]? PerformOcr(ReadOnlySpan<byte> bgraPixels, int width, int height)
    {
        var rowBytes = (nuint)(width * 4);

        // Allocate all native memory in one scope and release deterministically.
        var pixelsPtr = Marshal.AllocHGlobal(bgraPixels.Length);
        var vtablePtr = Marshal.AllocHGlobal(IntPtr.Size * 16);
        var pixelRefPtr = Marshal.AllocHGlobal(Marshal.SizeOf<FakeSkPixelRef>());
        var bitmapPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SkBitmap>());

        try
        {
            // Zero the vtable — DLL never calls through it during PerformOCR,
            // but the pointer at offset 0 of SkPixelRef must be non-null.
            for (var i = 0; i < 16; i++)
                Marshal.WriteIntPtr(vtablePtr, i * IntPtr.Size, IntPtr.Zero);

            // Copy pixel data
            unsafe
            {
                fixed (byte* src = bgraPixels)
                {
                    Buffer.MemoryCopy(src, (void*)pixelsPtr, bgraPixels.Length, bgraPixels.Length);
                }
            }

            // Build FakeSkPixelRef
            var pxref = new FakeSkPixelRef
            {
                VtablePtr = vtablePtr,
                RefCount = 1,
                Padding = 0,
                Width = width,
                Height = height,
                Pixels = pixelsPtr,
                RowBytes = rowBytes,
            };
            Marshal.StructureToPtr(pxref, pixelRefPtr, fDeleteOld: false);

            // Build SkBitmap
            var bitmap = new SkBitmap
            {
                PixelRef = pixelRefPtr,
                Pixels = pixelsPtr,
                RowBytes = rowBytes,
                ColorSpace = IntPtr.Zero,
                ColorType = SkConstants.KBgra8888,
                AlphaType = SkConstants.KPremul,
                Width = width,
                Height = height,
                Flags = 0,
            };
            Marshal.StructureToPtr(bitmap, bitmapPtr, fDeleteOld: false);

            uint outLen = 0;
            IntPtr resultPtr;
            _log.LogInformation("PerformOCR {Width}x{Height} ...", width, height);
            unsafe
            {
                resultPtr = _pPerformOcr((SkBitmap*)bitmapPtr, &outLen);
            }

            if (resultPtr == IntPtr.Zero)
            {
                _log.LogWarning("PerformOCR returned null");
                return null;
            }

            try
            {
                var data = new byte[outLen];
                Marshal.Copy(resultPtr, data, 0, (int)outLen);
                _log.LogInformation("PerformOCR returned {Bytes} bytes", data.Length);
                return data;
            }
            finally
            {
                unsafe { _pFreeLibraryAllocatedCharArray(resultPtr); }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(bitmapPtr);
            Marshal.FreeHGlobal(pixelRefPtr);
            Marshal.FreeHGlobal(vtablePtr);
            Marshal.FreeHGlobal(pixelsPtr);
        }
    }

    // ---------------------------------------------------------------------
    // Callbacks the DLL uses to read model files
    // ---------------------------------------------------------------------

    private uint OnGetFileSize(IntPtr utf8Path)
    {
        var rel = Marshal.PtrToStringUTF8(utf8Path) ?? string.Empty;
        return (uint)ReadModelFile(rel).Length;
    }

    private void OnGetFileContent(IntPtr utf8Path, uint bufSize, IntPtr buffer)
    {
        var rel = Marshal.PtrToStringUTF8(utf8Path) ?? string.Empty;
        var data = ReadModelFile(rel);
        var n = Math.Min((int)bufSize, data.Length);
        if (n > 0) Marshal.Copy(data, 0, buffer, n);
    }

    private byte[] ReadModelFile(string relativePath)
    {
        if (_fileCache.TryGetValue(relativePath, out var cached))
            return cached;

        var fullPath = Path.Combine(_modelDir, relativePath);
        if (!File.Exists(fullPath))
        {
            _log.LogWarning("Model file not found: {FullPath}", fullPath);
            _fileCache[relativePath] = [];
            return [];
        }

        var data = File.ReadAllBytes(fullPath);
        _fileCache[relativePath] = data;
        _log.LogDebug("Read model file: {RelativePath} ({Bytes} bytes)", relativePath, data.Length);
        return data;
    }

    // ---------------------------------------------------------------------
    // Native stderr chatter suppression (best-effort via env vars).
    // ---------------------------------------------------------------------

    private static bool _envSet;
    private static void SuppressNativeStderrEnvVars()
    {
        if (_envSet) return;
        _envSet = true;
        foreach (var v in new[] { "GLOG_minloglevel", "TF_CPP_MIN_LOG_LEVEL", "ABSL_MIN_LOG_LEVEL" })
        {
            if (Environment.GetEnvironmentVariable(v) is null)
                Environment.SetEnvironmentVariable(v, "3");
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
            NativeLibrary.Free(_handle);
    }
}
