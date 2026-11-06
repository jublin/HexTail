using HexTail.Application;
using HexTail.Domain;
using HexTail.Persistence;
using HexTail.Tests.Support;

namespace HexTail.Tests.Application;

public sealed class HighlightSpanCacheTests
{
    [Fact]
    public void RepeatedViewsReuseSpansWithoutRepeatingMatchAllocations()
    {
        var cache = new HighlightSpanCache(maxLines: 1);
        var rules = RuleSet.For([TestRules.Highlight(@"error\s+\d+", "red")]);
        var line = new Line(string.Join(" ", Enumerable.Repeat("error 123", 20)));
        var other = new Line(line.Raw);
        cache.Get(line, [], rules);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 1_000; i++)
            cache.Get(line, [], rules);
        var warmBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        var warmTime = timer.Elapsed;
        before = GC.GetAllocatedBytesForCurrentThread();
        timer.Restart();
        for (var i = 0; i < 1_000; i++)
            cache.Get(i % 2 == 0 ? other : line, [], rules);
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
        var settings = new AppSettings { Rules = [TestRules.Highlight("error", "red")] };
        var rules = RuleSet.For(settings.Rules);
        var spans = cache.Get(line, [], rules);
        Assert.Equal([new HighlightSpan(0, 5, "red")], spans);
        Assert.Same(spans, cache.Get(line, [], rules));
        Assert.NotSame(spans, cache.Get(new Line(line.Raw), [], rules));
        // An unrelated settings change copies the record but shares the rule list, so it keeps the cache.
        var unrelated = settings with
        {
            LogFontSize = LogFontSize.Large,
        };
        Assert.Same(spans, cache.Get(line, [], RuleSet.For(unrelated.Rules)));
        var changed = RuleSet.For([TestRules.Highlight("warn", "green")]);
        Assert.Equal([new HighlightSpan(6, 4, "green")], cache.Get(line, [], changed));
    }

    [Fact]
    public void PreservesLongestMatchAndStableSearchPriorityWithoutDuplicateGlobalRules()
    {
        var line = new Line("error warn");
        var buffer = new FileBuffer();
        buffer.Append(line);
        var rules = RuleSet.For([TestRules.Highlight("error", "red", pinned: true)]);
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
        Assert.Equal([new HighlightSpan(0, 5, "red")], cache.Get(line, [first, second], rules));
        Assert.Equal([new HighlightSpan(0, 5, "blue")], cache.Get(line, [second, first], rules));
        var longer = new Search(
            new CompiledQuery("error warn", MatchMode.Literal, true),
            "green",
            buffer
        );
        Assert.Equal([new HighlightSpan(0, 10, "green")], cache.Get(line, [first, longer], rules));
        Assert.Empty(cache.Get(line, [], RuleSet.Empty));
    }

    [Fact]
    public void EvictsLeastRecentlyUsedLineAndDoesNotCacheOversizedResults()
    {
        var cache = new HighlightSpanCache(maxLines: 2, maxSpans: 2);
        var rules = RuleSet.For([TestRules.Highlight("a")]);
        var first = new Line("a");
        var second = new Line("a");
        var third = new Line("a");
        var firstSpans = cache.Get(first, [], rules);
        var secondSpans = cache.Get(second, [], rules);
        Assert.Same(firstSpans, cache.Get(first, [], rules));
        cache.Get(third, [], rules);
        Assert.Same(firstSpans, cache.Get(first, [], rules));
        Assert.NotSame(secondSpans, cache.Get(second, [], rules));
        var oversized = new Line("aaa");
        var spans = cache.Get(oversized, [], rules);
        Assert.Equal(3, spans.Count);
        Assert.NotSame(spans, cache.Get(oversized, [], rules));
    }

    [Fact]
    public void EnforcesTotalSpanBudgetAcrossCachedLines()
    {
        var cache = new HighlightSpanCache(maxLines: 10, maxSpans: 3);
        var rules = RuleSet.For([TestRules.Highlight("a")]);
        var first = new Line("aa");
        var firstSpans = cache.Get(first, [], rules);
        cache.Get(new Line("aa"), [], rules);
        Assert.NotSame(firstSpans, cache.Get(first, [], rules));
    }
}
