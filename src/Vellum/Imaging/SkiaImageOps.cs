using SkiaSharp;

namespace Vellum.Imaging;

/// <summary>
/// SkiaSharp helpers that mirror the PIL operations in <c>vellum/ocr.py</c>:
/// open from file or bytes, resize with Lanczos when above the max dimension,
/// and flatten to raw BGRA_8888 bytes with row-byte stride = width * 4.
/// </summary>
internal static class SkiaImageOps
{
    public static SKBitmap DecodeBitmap(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("Could not decode image — unrecognised format.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bmp = new SKBitmap(info);
        var result = codec.GetPixels(info, bmp.GetPixels());
        if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
        {
            bmp.Dispose();
            throw new InvalidDataException($"SKCodec.GetPixels failed: {result}");
        }
        return bmp;
    }

    public static SKBitmap DecodeFile(string path) => DecodeBitmap(File.ReadAllBytes(path));

    /// <summary>
    /// Resize <paramref name="bmp"/> so that the larger dimension is at most
    /// <paramref name="maxDim"/>, using a high-quality filter (SkiaSharp's
    /// equivalent of PIL's LANCZOS).  Returns the input unchanged if already small enough.
    /// </summary>
    public static SKBitmap ResizeIfLarger(SKBitmap bmp, int maxDim)
    {
        var w = bmp.Width;
        var h = bmp.Height;
        if (Math.Max(w, h) <= maxDim) return bmp;

        var scale = (double)maxDim / Math.Max(w, h);
        var nw = (int)(w * scale);
        var nh = (int)(h * scale);

        var target = new SKBitmap(new SKImageInfo(nw, nh, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var src = SKImage.FromBitmap(bmp);
        using var surface = SKSurface.Create(target.Info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint
        {
            IsAntialias = true,
        };
        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
        canvas.DrawImage(src, SKRect.Create(0, 0, nw, nh), sampling, paint);
        canvas.Flush();

        using var snap = surface.Snapshot();
        using var pix = snap.PeekPixels() ?? throw new InvalidOperationException("SKSurface.PeekPixels returned null.");
        pix.ReadPixels(target.Info, target.GetPixels(), target.RowBytes, 0, 0);

        bmp.Dispose();
        return target;
    }

    /// <summary>
    /// Return tightly-packed BGRA_8888 bytes (row stride = width * 4) for the given bitmap,
    /// converting format / copying out if SkiaSharp used a different stride internally.
    /// </summary>
    public static byte[] ToBgraBytes(SKBitmap bmp)
    {
        // Ensure we're in BGRA premul; re-encode if not.
        if (bmp.ColorType != SKColorType.Bgra8888 || bmp.AlphaType != SKAlphaType.Premul)
        {
            var want = new SKImageInfo(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var tmp = new SKBitmap(want);
            if (!bmp.CopyTo(tmp, SKColorType.Bgra8888))
                throw new InvalidOperationException("Failed to convert bitmap to BGRA_8888.");
            bmp.Dispose();
            bmp = tmp;
        }

        var width = bmp.Width;
        var height = bmp.Height;
        var expectedRowBytes = width * 4;
        var srcRowBytes = bmp.RowBytes;

        var result = new byte[expectedRowBytes * height];
        var ptr = bmp.GetPixels();
        if (srcRowBytes == expectedRowBytes)
        {
            System.Runtime.InteropServices.Marshal.Copy(ptr, result, 0, result.Length);
        }
        else
        {
            // Repack row-by-row to strip stride padding.
            var row = new byte[expectedRowBytes];
            for (var y = 0; y < height; y++)
            {
                var srcAddr = ptr + (y * (int)srcRowBytes);
                System.Runtime.InteropServices.Marshal.Copy(srcAddr, row, 0, expectedRowBytes);
                Buffer.BlockCopy(row, 0, result, y * expectedRowBytes, expectedRowBytes);
            }
        }
        return result;
    }
}
