using Vellum.Models;
using Vellum.Reporting;
using SkiaSharp;
using Xunit;

namespace Vellum.Tests;

public class HtmlReportBuilderTests
{
    [Fact]
    public void Build_EmitsSelfContainedDocumentWithWordBoxesAndSidebar()
    {
        // Build a small fake page: two words on one line, one block.
        var words = new[]
        {
            new OcrWord("Hello", 0.98f, new BoundingBox(10, 20, 60, 30)),
            new OcrWord("world", 0.95f, new BoundingBox(80, 20, 70, 30)),
        };
        var line = new OcrLine("Hello world", words, new BoundingBox(10, 20, 140, 30));
        var block = new OcrBlock("paragraph", new[] { line });
        var page = new OcrPage(1, new[] { block }, Width: 200, Height: 100);

        using var bmp = new SKBitmap(new SKImageInfo(200, 100, SKColorType.Bgra8888, SKAlphaType.Premul));
        bmp.Erase(SKColors.White);

        var builder = new HtmlReportBuilder("Test Doc");
        builder.AddPage(page, bmp);
        var html = builder.Build();

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("data:image/png;base64,", html);
        Assert.Contains("Hello", html);
        Assert.Contains("world", html);
        Assert.Contains(@"<rect class=""word-box""", html);
        Assert.Contains(@"<span class=""word""", html);
        Assert.Contains("window.__vellumStats", html);
        Assert.EndsWith("</html>", html.Trim());
    }

    [Fact]
    public void Build_EscapesHtmlSpecialCharactersInExtractedText()
    {
        var words = new[] { new OcrWord("<script>alert(1)</script>", 1.0f, new BoundingBox(0, 0, 10, 10)) };
        var line = new OcrLine("<script>alert(1)</script>", words);
        var page = new OcrPage(1, new[] { new OcrBlock("paragraph", new[] { line }) }, 10, 10);

        using var bmp = new SKBitmap(new SKImageInfo(10, 10, SKColorType.Bgra8888, SKAlphaType.Premul));
        var builder = new HtmlReportBuilder("x");
        builder.AddPage(page, bmp);
        var html = builder.Build();

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
    }
}
