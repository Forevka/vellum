using System.Text.Json.Serialization;

namespace Vellum.Models;

/// <summary>Axis-aligned bounding rectangle in OCR-space pixels.</summary>
public sealed record BoundingBox(float X, float Y, float Width, float Height);

/// <summary>A single OCR'd word.</summary>
public sealed record OcrWord(
    string Text,
    float? Confidence = null,
    BoundingBox? BoundingBox = null);

/// <summary>A line of text — an ordered list of <see cref="OcrWord"/>s.</summary>
public sealed record OcrLine(
    string Text,
    IReadOnlyList<OcrWord> Words,
    BoundingBox? BoundingBox = null)
{
    public OcrLine(string text) : this(text, [], null) { }
}

/// <summary>A layout block (paragraph / heading / image) of <see cref="OcrLine"/>s.</summary>
public sealed record OcrBlock(
    string BlockType,
    IReadOnlyList<OcrLine> Lines,
    BoundingBox? BoundingBox = null)
{
    [JsonIgnore]
    public string Text => string.Join('\n', Lines.Select(l => l.Text));
}

/// <summary>One page of OCR results.</summary>
public sealed record OcrPage(
    int PageNumber,
    IReadOnlyList<OcrBlock> Blocks,
    float? Width = null,
    float? Height = null)
{
    [JsonIgnore]
    public string Text => string.Join(
        "\n\n",
        Blocks.Select(b => b.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
}

/// <summary>Top-level OCR result — a list of <see cref="OcrPage"/>s.</summary>
public sealed record OcrResult(IReadOnlyList<OcrPage> Pages)
{
    public OcrResult() : this([]) { }

    /// <summary>Plain text; pages are separated by a form-feed (<c>\f</c>) character.</summary>
    public string ToText() => string.Join('\f', Pages.Select(p => p.Text));

    /// <summary>Structured JSON-ready object tree suitable for <see cref="System.Text.Json.JsonSerializer"/>.</summary>
    public string ToJson(bool indented = true) => System.Text.Json.JsonSerializer.Serialize(
        this,
        new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
}
