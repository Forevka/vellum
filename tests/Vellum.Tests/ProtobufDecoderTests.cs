using Vellum.Protobuf;
using Xunit;

namespace Vellum.Tests;

public class ProtobufDecoderTests
{
    [Fact]
    public void Varint_SingleByte_DecodesValue()
    {
        // tag = (field 1, wire 0) = 0x08, value = 0x05 — proto: `int32 field1 = 1; field1 = 5`
        var bytes = new byte[] { 0x08, 0x05 };
        var items = VisualAnnotationParser.DecodeRaw(bytes);

        Assert.Single(items);
        Assert.Equal(1, items[0].FieldNumber);
        Assert.Equal(VisualAnnotationParser.WireType.Varint, items[0].Wire);
        Assert.Equal(5ul, items[0].Value.Varint);
    }

    [Fact]
    public void Varint_MultiByte_DecodesLargeValue()
    {
        // tag (field 1, varint), value = 300 = 0xAC 0x02
        var bytes = new byte[] { 0x08, 0xAC, 0x02 };
        var items = VisualAnnotationParser.DecodeRaw(bytes);

        Assert.Equal(300, items[0].Value.Int32);
    }

    [Fact]
    public void LengthDelimited_DecodesBytes()
    {
        // field 3 length-delimited "abc"
        // tag = (3 << 3) | 2 = 0x1A
        var bytes = new byte[] { 0x1A, 0x03, (byte)'a', (byte)'b', (byte)'c' };
        var items = VisualAnnotationParser.DecodeRaw(bytes);

        Assert.Single(items);
        Assert.Equal("abc", System.Text.Encoding.UTF8.GetString(items[0].Value.Bytes));
    }

    [Fact]
    public void Fixed32_DecodesFloat()
    {
        // field 15, wire 5 (fixed32): tag = (15 << 3) | 5 = 0x7D
        // encode float 1.5 little-endian
        var bytes = new byte[] { 0x7D }.Concat(BitConverter.GetBytes(1.5f)).ToArray();
        var items = VisualAnnotationParser.DecodeRaw(bytes);

        Assert.Single(items);
        Assert.Equal(15, items[0].FieldNumber);
        Assert.Equal(1.5f, items[0].Value.Single);
    }

    [Fact]
    public void ParseRect_ExtractsAllCoordinates()
    {
        // Build: Rect { x=10, y=20, width=100, height=50 }
        // field n varint: tag = (n << 3); values: 10, 20, 100, 50
        var bytes = new List<byte> { 0x08, 10, 0x10, 20, 0x18, 100, 0x20, 50 };
        var (x, y, w, h) = VisualAnnotationParser.ParseRect(bytes.ToArray());

        Assert.Equal(10, x);
        Assert.Equal(20, y);
        Assert.Equal(100, w);
        Assert.Equal(50, h);
    }

    [Fact]
    public void ParseWord_DecodesTextAndRectAndConfidence()
    {
        // bytes for inner rect(x=10,y=20,w=30,h=40) — same pattern as above
        byte[] rectBytes = [0x08, 10, 0x10, 20, 0x18, 30, 0x20, 40];

        var word = new List<byte>();
        // field 2 (rect, length-delimited): tag 0x12, length, data
        word.Add(0x12); word.Add((byte)rectBytes.Length); word.AddRange(rectBytes);
        // field 3 (utf8_string "hi"): tag (3<<3)|2 = 0x1A
        word.AddRange([0x1A, 0x02, (byte)'h', (byte)'i']);
        // field 15 (confidence float32): tag 0x7D
        word.Add(0x7D); word.AddRange(BitConverter.GetBytes(0.95f));

        var parsed = VisualAnnotationParser.ParseWord(word.ToArray());
        Assert.Equal("hi", parsed.Text);
        Assert.Equal(10, parsed.X);
        Assert.Equal(40, parsed.Height);
        Assert.Equal(0.95f, parsed.Confidence);
    }

    [Fact]
    public void Parse_EmptyData_ReturnsEmptyList()
    {
        Assert.Empty(VisualAnnotationParser.Parse(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Parse_MalformedTrailingVarint_DoesNotCrash()
    {
        // Incomplete varint at the end — should be tolerated (matches _decode_raw's try/except).
        var bytes = new byte[] { 0x08, 0x05, 0x08, 0xFF };
        var items = VisualAnnotationParser.DecodeRaw(bytes);
        Assert.True(items.Count >= 1);
    }
}
