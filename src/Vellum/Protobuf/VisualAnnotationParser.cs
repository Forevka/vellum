using System.Buffers.Binary;

namespace Vellum.Protobuf;

/// <summary>
/// Wire-format parser for Chromium's <c>chrome_screen_ai.VisualAnnotation</c> protobuf.
/// Ported directly from <c>locro/_protobuf.py</c> — no compiled .proto required.
/// Field numbers match Chromium's <c>services/screen_ai/proto/chrome_screen_ai.proto</c>.
/// </summary>
public static class VisualAnnotationParser
{
    /// <summary>Parse a serialised <c>VisualAnnotation</c> protobuf into a list of line results.</summary>
    public static List<LineResult> Parse(ReadOnlySpan<byte> data)
    {
        var lines = new List<LineResult>();
        foreach (var (fn, wt, val) in DecodeRaw(data))
        {
            if (fn == 2 && wt == WireType.LengthDelimited && val.IsBytes)
                lines.Add(ParseLine(val.Bytes));
        }
        return lines;
    }

    // ---------------------------------------------------------------------
    // Wire-format primitives
    // ---------------------------------------------------------------------

    internal enum WireType
    {
        Varint = 0,
        Fixed64 = 1,
        LengthDelimited = 2,
        Fixed32 = 5,
    }

    internal readonly struct FieldValue
    {
        private readonly ulong _varint;
        private readonly double _f64;
        private readonly float _f32;
        private readonly byte[]? _bytes;
        private readonly byte _tag;

        public FieldValue(ulong v) { _varint = v; _f64 = 0; _f32 = 0; _bytes = null; _tag = 0; }
        public FieldValue(double v) { _varint = 0; _f64 = v; _f32 = 0; _bytes = null; _tag = 1; }
        public FieldValue(byte[] v) { _varint = 0; _f64 = 0; _f32 = 0; _bytes = v; _tag = 2; }
        public FieldValue(float v) { _varint = 0; _f64 = 0; _f32 = v; _bytes = null; _tag = 5; }

        public bool IsVarint => _tag == 0;
        public bool IsFixed64 => _tag == 1;
        public bool IsBytes => _tag == 2;
        public bool IsFixed32 => _tag == 5;

        public ulong Varint => _varint;
        public long SignedVarint => (long)_varint;
        public int Int32 => (int)_varint;
        public double Double => _f64;
        public float Single => _f32;
        public ReadOnlySpan<byte> Bytes => _bytes ?? [];
    }

    internal static bool TryReadVarint(ReadOnlySpan<byte> data, ref int pos, out ulong value)
    {
        value = 0;
        var shift = 0;
        while (pos < data.Length)
        {
            var b = data[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
            if (shift >= 64) return false;
        }
        return false;
    }

    // Python uses `yield`; we just materialise a list.  Messages are small.
    internal static List<(int FieldNumber, WireType Wire, FieldValue Value)> DecodeRaw(ReadOnlySpan<byte> data)
    {
        var items = new List<(int, WireType, FieldValue)>();
        var pos = 0;
        while (pos < data.Length)
        {
            if (!TryReadVarint(data, ref pos, out var tag)) break;

            var fn = (int)(tag >> 3);
            var wt = (WireType)(tag & 7);

            switch (wt)
            {
                case WireType.Varint:
                    if (!TryReadVarint(data, ref pos, out var v)) return items;
                    items.Add((fn, wt, new FieldValue(v)));
                    break;

                case WireType.Fixed64:
                    if (pos + 8 > data.Length) return items;
                    items.Add((fn, wt, new FieldValue(BinaryPrimitives.ReadDoubleLittleEndian(data.Slice(pos, 8)))));
                    pos += 8;
                    break;

                case WireType.LengthDelimited:
                    if (!TryReadVarint(data, ref pos, out var length)) return items;
                    if (pos + (int)length > data.Length) return items;
                    items.Add((fn, wt, new FieldValue(data.Slice(pos, (int)length).ToArray())));
                    pos += (int)length;
                    break;

                case WireType.Fixed32:
                    if (pos + 4 > data.Length) return items;
                    items.Add((fn, wt, new FieldValue(BinaryPrimitives.ReadSingleLittleEndian(data.Slice(pos, 4)))));
                    pos += 4;
                    break;

                default:
                    // unknown wire type: bail
                    return items;
            }
        }
        return items;
    }

    // ---------------------------------------------------------------------
    // Sub-message parsers
    // ---------------------------------------------------------------------

    /// <summary>Returns <c>(x, y, width, height)</c> from a <c>Rect</c> sub-message.</summary>
    internal static (int X, int Y, int W, int H) ParseRect(ReadOnlySpan<byte> data)
    {
        int x = 0, y = 0, w = 0, h = 0;
        foreach (var (fn, wt, val) in DecodeRaw(data))
        {
            if (wt != WireType.Varint) continue;
            switch (fn)
            {
                case 1: x = val.Int32; break;
                case 2: y = val.Int32; break;
                case 3: w = val.Int32; break;
                case 4: h = val.Int32; break;
            }
        }
        return (x, y, w, h);
    }

    internal static WordResult ParseWord(ReadOnlySpan<byte> data)
    {
        var w = new WordResult();
        foreach (var (fn, wt, val) in DecodeRaw(data))
        {
            switch ((fn, wt))
            {
                case (2, WireType.LengthDelimited):
                    var (rx, ry, rw, rh) = ParseRect(val.Bytes);
                    w.X = rx; w.Y = ry; w.Width = rw; w.Height = rh;
                    break;
                case (3, WireType.LengthDelimited):
                    w.Text = System.Text.Encoding.UTF8.GetString(val.Bytes);
                    break;
                case (5, WireType.LengthDelimited):
                    w.Language = System.Text.Encoding.UTF8.GetString(val.Bytes);
                    break;
                case (15, WireType.Fixed32):
                    w.Confidence = val.Single;
                    break;
            }
        }
        return w;
    }

    internal static LineResult ParseLine(ReadOnlySpan<byte> data)
    {
        var ln = new LineResult();
        foreach (var (fn, wt, val) in DecodeRaw(data))
        {
            switch ((fn, wt))
            {
                case (1, WireType.LengthDelimited):
                    ln.Words.Add(ParseWord(val.Bytes));
                    break;
                case (2, WireType.LengthDelimited):
                    var (rx, ry, rw, rh) = ParseRect(val.Bytes);
                    ln.X = rx; ln.Y = ry; ln.Width = rw; ln.Height = rh;
                    break;
                case (3, WireType.LengthDelimited):
                    ln.Text = System.Text.Encoding.UTF8.GetString(val.Bytes);
                    break;
                case (4, WireType.LengthDelimited):
                    ln.Language = System.Text.Encoding.UTF8.GetString(val.Bytes);
                    break;
                case (5, WireType.Varint):
                    ln.BlockId = val.Int32; break;
                case (7, WireType.Varint):
                    ln.Direction = val.Int32; break;
                case (8, WireType.Varint):
                    ln.ContentType = val.Int32; break;
                case (10, WireType.Fixed32):
                    ln.Confidence = val.Single; break;
                case (11, WireType.Varint):
                    ln.ParagraphId = val.Int32; break;
            }
        }
        return ln;
    }
}

/// <summary>Intermediate OCR line result, prior to grouping into blocks.</summary>
public sealed class LineResult
{
    public string Text { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public int BlockId { get; set; }
    public int ParagraphId { get; set; }
    public float Confidence { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Direction { get; set; }
    public int ContentType { get; set; }
    public List<WordResult> Words { get; } = [];
}

/// <summary>Intermediate OCR word result.</summary>
public sealed class WordResult
{
    public string Text { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}
