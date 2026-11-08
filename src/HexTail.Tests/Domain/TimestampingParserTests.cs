using HexTail.Domain;

namespace HexTail.Tests.Domain;

public sealed class TimestampingParserTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-07T12:00:00Z");

    private static TimestampingParser Plain(Func<TimeZoneInfo>? zone = null) =>
        new(new PlainTextParser(), zone ?? (() => TimeZoneInfo.Utc), () => Now);

    [Fact]
    public void FirstTimestampedLine_FixesTheStrategy()
    {
        var parser = Plain();

        var line = parser.Parse("2026-11-07T10:11:12Z hello");

        Assert.Equal(DateTimeOffset.Parse("2026-11-07T10:11:12Z"), line.Timestamp);
        Assert.False(line.TimestampInherited);
        Assert.Equal(TimestampSource.IsoPrefix, parser.Strategy!.Value.Source);
    }

    [Fact]
    public void LineWithoutTimestamp_InheritsThePreviousOne()
    {
        var parser = Plain();
        var first = parser.Parse("2026-11-07T10:11:12Z boom");

        var continuation = parser.Parse("    at Foo.Bar()");

        Assert.Equal(first.Timestamp, continuation.Timestamp);
        Assert.True(continuation.TimestampInherited);
        Assert.Equal("    at Foo.Bar()", continuation.Raw);
    }

    [Fact]
    public void LineBeforeAnyTimestamp_HasNone()
    {
        var parser = Plain();

        var line = parser.Parse("no timestamp yet");

        Assert.Null(line.Timestamp);
        Assert.False(line.TimestampInherited);
        Assert.Null(parser.Strategy);
    }

    [Fact]
    public void Inherited_ThenFreshTimestamp_ReplacesTheInheritedValue()
    {
        var parser = Plain();
        parser.Parse("2026-11-07T10:11:12Z a");
        parser.Parse("continuation");

        var next = parser.Parse("2026-11-07T10:11:13Z b");

        Assert.Equal(DateTimeOffset.Parse("2026-11-07T10:11:13Z"), next.Timestamp);
        Assert.False(next.TimestampInherited);
    }

    [Fact]
    public void JsonlInner_UsesTheFieldStrategy()
    {
        var parser = new TimestampingParser(new JsonlParser(), () => TimeZoneInfo.Utc, () => Now);

        var first = parser.Parse("""{"@timestamp":"2026-11-07T10:11:12Z","m":"x"}""");
        var second = parser.Parse("""{"m":"no time"}""");

        Assert.Equal(new TimestampStrategy(TimestampSource.Field, "@timestamp"), parser.Strategy);
        Assert.Equal(first.Timestamp, second.Timestamp);
        Assert.True(second.TimestampInherited);
        Assert.Equal("x", first.ParsedFields!["m"]);
    }

    [Fact]
    public void Inner_IsExposed()
    {
        var inner = new JsonlParser();

        Assert.Same(inner, new TimestampingParser(inner, () => TimeZoneInfo.Utc).Inner);
    }

    [Fact]
    public void TwoHundredConsecutiveMisses_ResetTheStrategy()
    {
        var parser = Plain();
        parser.Parse("2026-11-07T10:11:12Z first");
        for (var i = 0; i < 199; i++)
            parser.Parse("filler");
        Assert.NotNull(parser.Strategy);

        parser.Parse("filler");

        Assert.Null(parser.Strategy);
        var syslog = parser.Parse("Nov  7 10:11:13 host app: x");
        Assert.Equal(TimestampSource.SyslogPrefix, parser.Strategy!.Value.Source);
        Assert.False(syslog.TimestampInherited);
        Assert.Equal(DateTimeOffset.Parse("2026-11-07T10:11:13Z"), syslog.Timestamp);
    }

    [Fact]
    public void AHit_ResetsTheMissCounter()
    {
        var parser = Plain();
        parser.Parse("2026-11-07T10:11:12Z first");
        for (var i = 0; i < 150; i++)
            parser.Parse("filler");
        parser.Parse("2026-11-07T10:11:13Z again");
        for (var i = 0; i < 150; i++)
            parser.Parse("filler");

        Assert.NotNull(parser.Strategy);
    }

    [Fact]
    public void ZoneDelegate_IsReadPerParse()
    {
        var zone = TimeZoneInfo.Utc;
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("p2", TimeSpan.FromHours(2), "p2", "p2");
        var parser = Plain(() => zone);

        var utcLine = parser.Parse("2026-11-07 10:00:00 a");
        zone = plusTwo;
        var shifted = parser.Parse("2026-11-07 10:00:00 b");

        Assert.Equal(DateTimeOffset.Parse("2026-11-07T10:00:00Z"), utcLine.Timestamp);
        Assert.Equal(DateTimeOffset.Parse("2026-11-07T08:00:00Z"), shifted.Timestamp);
    }

    [Fact]
    public void Garbage_NeverThrows()
    {
        var parser = Plain();
        var inputs = new[]
        {
            "",
            "\0\0\0",
            "[",
            "[]",
            "[99/Zzz/9999:99:99:99 +9999]",
            "2026-99-99T99:99:99Z",
            "Nov",
            "Nov  ",
            new string('[', 5000),
            "\ud800 broken surrogate 2026-11-07",
            "{\"ts\":\"" + new string('9', 400) + "\"}",
        };

        foreach (var input in inputs)
            Assert.NotNull(parser.Parse(input));
        var json = new TimestampingParser(new JsonlParser(), () => TimeZoneInfo.Utc, () => Now);
        foreach (var input in inputs)
            Assert.NotNull(json.Parse(input));
    }

    [Fact]
    public void ZoneDelegateThrowing_DoesNotEscape()
    {
        var parser = new TimestampingParser(
            new PlainTextParser(),
            () => throw new InvalidOperationException("boom"),
            () => Now
        );

        var line = parser.Parse("2026-11-07 10:00:00 a");

        Assert.Equal("2026-11-07 10:00:00 a", line.Raw);
    }

    [Fact]
    public async Task ParallelParse_DoesNotCorruptState()
    {
        var parser = Plain();
        // The strategy is fixed by a first hit; every continuation parsed afterwards must inherit.
        parser.Parse("2026-11-07T09:00:00Z seed");
        var results = new System.Collections.Concurrent.ConcurrentBag<Line>();

        await Task.WhenAll(
            Enumerable
                .Range(0, 4)
                .Select(worker =>
                    Task.Run(() =>
                    {
                        for (var i = 0; i < 2000; i++)
                        {
                            var raw =
                                i % 3 == 0
                                    ? $"2026-11-07T10:{i % 60:D2}:{worker:D2}Z w{worker} {i}"
                                    : $"continuation {worker} {i}";
                            results.Add(parser.Parse(raw));
                        }
                    })
                )
        );

        Assert.Equal(8000, results.Count);
        Assert.Equal(TimestampSource.IsoPrefix, parser.Strategy!.Value.Source);
        var own = results.Where(line => !line.TimestampInherited).ToList();
        var continuations = results.Where(line => line.TimestampInherited).ToList();
        Assert.Equal(8000 - own.Count, continuations.Count);
        Assert.Equal(4 * 667, own.Count);
        Assert.All(
            own,
            line =>
            {
                Assert.StartsWith("2026-11-07T10:", line.Raw);
                Assert.Equal(DateTimeOffset.Parse(line.Raw[..20]), line.Timestamp);
            }
        );
        Assert.All(continuations, line => Assert.NotNull(line.Timestamp));
    }
}
