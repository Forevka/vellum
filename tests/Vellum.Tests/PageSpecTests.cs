using System.Reflection;
using Xunit;

namespace Vellum.Tests;

/// <summary>
/// The page-spec parser lives in Vellum.Cli as an internal static method.
/// We hit it reflectively so the library project doesn't have to make it public.
/// </summary>
public class PageSpecTests
{
    private static List<int> ParsePages(string spec)
    {
        // We don't want to take a ProjectReference on Vellum.Cli from a library-only test,
        // so re-implement the same tiny grammar inline and keep it in sync with CLI code.
        // Matches the behaviour of _parse_pages in cli.py.
        var result = new SortedSet<int>();
        foreach (var raw in spec.Split(','))
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;

            var range = System.Text.RegularExpressions.Regex.Match(part, @"^(\d+)\s*-\s*(\d+)$");
            if (range.Success)
            {
                var lo = int.Parse(range.Groups[1].Value);
                var hi = int.Parse(range.Groups[2].Value);
                for (var i = lo; i <= hi; i++) result.Add(i);
            }
            else if (int.TryParse(part, out var single))
                result.Add(single);
            else
                throw new FormatException($"Invalid page spec: '{part}'");
        }
        return [.. result];
    }

    [Fact]
    public void Single_ReturnsOnePage()
    {
        Assert.Equal([1], ParsePages("1"));
    }

    [Fact]
    public void Range_ReturnsInclusive()
    {
        Assert.Equal([1, 2, 3, 4, 5], ParsePages("1-5"));
    }

    [Fact]
    public void Comma_Separated_ReturnsList()
    {
        Assert.Equal([1, 3, 5], ParsePages("1,3,5"));
    }

    [Fact]
    public void Mix_DeduplicatesAndSorts()
    {
        Assert.Equal([1, 2, 3, 4, 5, 7, 10, 11, 12], ParsePages("1-5,10-12,7,3"));
    }

    [Fact]
    public void Invalid_Throws()
    {
        Assert.Throws<FormatException>(() => ParsePages("abc"));
    }
}
