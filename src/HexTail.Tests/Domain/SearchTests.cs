using HexTail.Domain;

namespace HexTail.Tests.Domain;

public class SearchTests
{
    [Theory]
    [InlineData("error", MatchMode.Literal)]
    [InlineData(@"error\\s+id", MatchMode.Regex)]
    public void DetectMode_UsesRegexOnlyForRegexSyntax(string query, MatchMode expected)
    {
        Assert.Equal(expected, CompiledQuery.DetectMode(query));
    }

    private static FileBuffer BufferWith(params string[] lines)
    {
        var buffer = new FileBuffer();
        buffer.Append(lines.Select(l => new Line(l)));
        return buffer;
    }

    [Fact]
    public void LiteralSearch_FindsMatchingLineIndices()
    {
        var buffer = BufferWith("alpha", "beta error", "gamma", "error again");
        var search = new Search(
            new CompiledQuery("error", MatchMode.Literal, caseSensitive: true),
            "red",
            buffer
        );

        Assert.Equal([1, 3], search.Results);
    }

    [Fact]
    public void LiteralSearch_CaseSensitive_SkipsDifferentCase()
    {
        var buffer = BufferWith("Error", "error", "ERROR");
        var search = new Search(
            new CompiledQuery("error", MatchMode.Literal, caseSensitive: true),
            "red",
            buffer
        );

        Assert.Equal([1], search.Results);
    }

    [Fact]
    public void LiteralSearch_CaseInsensitive_MatchesAllCases()
    {
        var buffer = BufferWith("Error", "error", "ERROR", "none");
        var search = new Search(
            new CompiledQuery("error", MatchMode.Literal, caseSensitive: false),
            "red",
            buffer
        );

        Assert.Equal([0, 1, 2], search.Results);
    }

    [Fact]
    public void RegexSearch_MatchesPattern()
    {
        var buffer = BufferWith("user=alice", "user= bob", "admin=root");
        var search = new Search(
            new CompiledQuery(@"user\s*=\s*\w+", MatchMode.Regex, caseSensitive: true),
            "blue",
            buffer
        );

        Assert.Equal([0, 1], search.Results);
    }

    [Fact]
    public void RegexSearch_CaseInsensitive()
    {
        var buffer = BufferWith("WARN one", "warn two", "info");
        var search = new Search(
            new CompiledQuery("^warn", MatchMode.Regex, caseSensitive: false),
            "yellow",
            buffer
        );

        Assert.Equal([0, 1], search.Results);
    }

    [Fact]
    public void InvalidRegex_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new CompiledQuery("(unclosed", MatchMode.Regex, caseSensitive: true)
        );
    }

    [Fact]
    public void Highlights_Literal_ReportsAllOccurrences()
    {
        var query = new CompiledQuery("foo", MatchMode.Literal, caseSensitive: true);

        var ranges = query.GetHighlights("foo bar foo");

        Assert.Equal([new HighlightRange(0, 3), new HighlightRange(8, 3)], ranges);
    }

    [Fact]
    public void Highlights_Literal_CaseInsensitive_KeepsOriginalPositions()
    {
        var query = new CompiledQuery("error", MatchMode.Literal, caseSensitive: false);

        var ranges = query.GetHighlights("xxERRORxxerror");

        Assert.Equal([new HighlightRange(2, 5), new HighlightRange(9, 5)], ranges);
    }

    [Fact]
    public void Highlights_Regex_UsesMatchSpans()
    {
        var query = new CompiledQuery(@"\d+", MatchMode.Regex, caseSensitive: true);

        var ranges = query.GetHighlights("a1 bb234 c");

        Assert.Equal([new HighlightRange(1, 1), new HighlightRange(5, 3)], ranges);
    }

    [Fact]
    public void Highlights_NoMatch_ReturnsEmpty()
    {
        var query = new CompiledQuery("nope", MatchMode.Literal, caseSensitive: true);

        Assert.Empty(query.GetHighlights("nothing here"));
    }

    [Fact]
    public void EmptyQuery_MatchesNothing()
    {
        var buffer = BufferWith("anything");
        var search = new Search(
            new CompiledQuery("", MatchMode.Literal, caseSensitive: true),
            "red",
            buffer
        );

        Assert.Empty(search.Results);
        Assert.Empty(search.GetHighlights(buffer[0]));
    }

    [Fact]
    public void MultipleSearches_OverlapOnSameLine()
    {
        var buffer = BufferWith("error: disk full", "all good");
        var errorSearch = new Search(
            new CompiledQuery("error", MatchMode.Literal, caseSensitive: true),
            "red",
            buffer
        );
        var diskSearch = new Search(
            new CompiledQuery("disk", MatchMode.Literal, caseSensitive: true),
            "blue",
            buffer
        );
        buffer.AddSearch(errorSearch);
        buffer.AddSearch(diskSearch);

        Assert.Equal([0], errorSearch.Results);
        Assert.Equal([0], diskSearch.Results);
    }

    [Fact]
    public void IncrementalScan_OnlyAppendsNewMatches()
    {
        var buffer = BufferWith("error one", "fine");
        var search = new Search(
            new CompiledQuery("error", MatchMode.Literal, caseSensitive: true),
            "red",
            buffer
        );
        buffer.AddSearch(search);

        buffer.Append(new Line("still fine"));
        buffer.Append(new Line("error two"));

        Assert.Equal([0, 3], search.Results);
    }

    [Fact]
    public void GetHighlights_ExposesPerLineRanges()
    {
        var buffer = BufferWith("foo foo", "bar");
        var search = new Search(
            new CompiledQuery("foo", MatchMode.Literal, caseSensitive: true),
            "red",
            buffer
        );

        Assert.Equal(
            [new HighlightRange(0, 3), new HighlightRange(4, 3)],
            search.GetHighlights(buffer[0])
        );
        Assert.Empty(search.GetHighlights(buffer[1]));
    }
}
