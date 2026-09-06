using HexTail.Application;
using HexTail.Domain;
using HexTail.Persistence;

namespace HexTail.Tests.Application;

public sealed class HighlightSpanCacheTests
{
    [Fact]
    public void RepeatedViewsReuseSpansWithoutRepeatingMatchAllocations()
    {
        var cache = new HighlightSpanCache(maxLines: 1);
        var settings = new AppSettings
        {
            GlobalLabels =
            [
                new GlobalLabel
                {
                    Text = @"error\s+\d+",
                    Color = "red",
                    ShowInOpenFile = false,
                },
            ],
        };
        var line = new Line(string.Join(" ", Enumerable.Repeat("error 123", 20)));
        var other = new Line(line.Raw);
        cache.Get(line, [], settings);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 1_000; i++)
            cache.Get(line, [], settings);
        var warmBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        var warmTime = timer.Elapsed;
        before = GC.GetAllocatedBytesForCurrentThread();
        timer.Restart();
        for (var i = 0; i < 1_000; i++)
            cache.Get(i % 2 == 0 ? other : line, [], settings);
        var coldBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"1,000 lookups: cached {warmBytes:N0} bytes / {warmTime.TotalMilliseconds:N3} ms; uncached {coldBytes:N0} bytes / {timer.Elapsed.TotalMilliseconds:N3} ms"
        );
        Assert.True(warmBytes < coldBytes / 4);
    }

    [Fact]
    public void ReusesSpansByLineIdentityAndInvalidatesChangedRules()
    {
        var cache = new HighlightSpanCache();
        var line = new Line("error warn");
        var settings = new AppSettings
        {
            GlobalLabels =
            [
                new GlobalLabel
                {
                    Text = "error",
                    Color = "red",
                    ShowInOpenFile = false,
                },
            ],
        };
        var spans = cache.Get(line, [], settings);
        Assert.Equal([new HighlightSpan(0, 5, "red")], spans);
        Assert.Same(spans, cache.Get(line, [], settings));
        Assert.NotSame(spans, cache.Get(new Line(line.Raw), [], settings));
        Assert.Same(spans, cache.Get(line, [], settings with { LogFontSize = LogFontSize.Large }));
        settings.GlobalLabels[0] = new GlobalLabel
        {
            Text = "warn",
            Color = "green",
            ShowInOpenFile = false,
        };
        Assert.Equal([new HighlightSpan(6, 4, "green")], cache.Get(line, [], settings));
    }

    [Fact]
    public void PreservesLongestMatchAndStableSearchPriorityWithoutDuplicateGlobalLabels()
    {
        var line = new Line("error warn");
        var buffer = new FileBuffer();
        buffer.Append(line);
        var settings = new AppSettings
        {
            GlobalLabels = [new GlobalLabel { Text = "error", Color = "red" }],
        };
        var first = new Search(
            new CompiledQuery("error", MatchMode.Literal, true),
            "red",
            buffer,
            true
        );
        var second = new Search(
            new CompiledQuery("error", MatchMode.Literal, true),
            "blue",
            buffer
        );
        var cache = new HighlightSpanCache();
        Assert.Equal([new HighlightSpan(0, 5, "red")], cache.Get(line, [first, second], settings));
        Assert.Equal([new HighlightSpan(0, 5, "blue")], cache.Get(line, [second, first], settings));
        var longer = new Search(
            new CompiledQuery("error warn", MatchMode.Literal, true),
            "green",
            buffer
        );
        Assert.Equal(
            [new HighlightSpan(0, 10, "green")],
            cache.Get(line, [first, longer], settings)
        );
        Assert.Empty(cache.Get(line, [], new AppSettings()));
    }

    [Fact]
    public void EvictsLeastRecentlyUsedLineAndDoesNotCacheOversizedResults()
    {
        var cache = new HighlightSpanCache(maxLines: 2, maxSpans: 2);
        var settings = new AppSettings
        {
            GlobalLabels = [new GlobalLabel { Text = "a", ShowInOpenFile = false }],
        };
        var first = new Line("a");
        var second = new Line("a");
        var third = new Line("a");
        var firstSpans = cache.Get(first, [], settings);
        var secondSpans = cache.Get(second, [], settings);
        Assert.Same(firstSpans, cache.Get(first, [], settings));
        cache.Get(third, [], settings);
        Assert.Same(firstSpans, cache.Get(first, [], settings));
        Assert.NotSame(secondSpans, cache.Get(second, [], settings));
        var oversized = new Line("aaa");
        var spans = cache.Get(oversized, [], settings);
        Assert.Equal(3, spans.Count);
        Assert.NotSame(spans, cache.Get(oversized, [], settings));
    }

    [Fact]
    public void EnforcesTotalSpanBudgetAcrossCachedLines()
    {
        var cache = new HighlightSpanCache(maxLines: 10, maxSpans: 3);
        var settings = new AppSettings
        {
            GlobalLabels = [new GlobalLabel { Text = "a", ShowInOpenFile = false }],
        };
        var first = new Line("aa");
        var firstSpans = cache.Get(first, [], settings);
        cache.Get(new Line("aa"), [], settings);
        Assert.NotSame(firstSpans, cache.Get(first, [], settings));
    }
}
