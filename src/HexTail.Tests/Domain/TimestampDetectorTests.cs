using System.Diagnostics;
using HexTail.Domain;

namespace HexTail.Tests.Domain;

public sealed class TimestampDetectorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-07T12:00:00Z");
    private static readonly DateTimeOffset Expected = DateTimeOffset.Parse("2026-11-07T10:11:12Z");

    private static Line Fields(string raw, params (string Key, string Value)[] fields) =>
        new(raw, fields.ToDictionary(f => f.Key, f => f.Value));

    private static bool Detect(
        Line line,
        out TimestampStrategy strategy,
        out DateTimeOffset timestamp,
        TimeZoneInfo? zone = null,
        DateTimeOffset? now = null
    ) =>
        TimestampDetector.TryDetect(
            line,
            zone ?? TimeZoneInfo.Utc,
            now ?? Now,
            out strategy,
            out timestamp
        );

    [Fact]
    public void Field_AtTimestamp_IsReadAsIso()
    {
        var line = Fields("raw", ("@timestamp", "2026-11-07T10:11:12.345Z"));

        Assert.True(Detect(line, out var strategy, out var ts));

        Assert.Equal(DateTimeOffset.Parse("2026-11-07T10:11:12.345Z"), ts);
        Assert.Equal(new TimestampStrategy(TimestampSource.Field, "@timestamp"), strategy);
    }

    [Fact]
    public void Field_Time_HonoursOffset()
    {
        var line = Fields("raw", ("time", "2026-11-07T12:11:12+02:00"));

        Assert.True(Detect(line, out var strategy, out var ts));

        Assert.Equal(Expected, ts);
        Assert.Equal(TimeSpan.FromHours(2), ts.Offset);
        Assert.Equal("time", strategy.FieldName);
    }

    [Theory]
    [InlineData("1762510272")]
    [InlineData("1762510272345")]
    [InlineData("1762510272345678")]
    [InlineData("1762510272345678901")]
    public void Field_Ts_NumericMagnitudeSelectsUnit(string value)
    {
        var line = Fields("raw", ("ts", value));

        Assert.True(Detect(line, out _, out var ts));

        var wholeSeconds = DateTimeOffset.FromUnixTimeSeconds(1762510272);
        Assert.Equal(wholeSeconds.ToUnixTimeSeconds(), ts.ToUnixTimeSeconds());
        Assert.Equal(TimeSpan.Zero, ts.Offset);
    }

    [Fact]
    public void Field_NumericMilliseconds_KeepsFraction() =>
        Assert.Equal(
            1762510272345,
            Stamp(Fields("raw", ("ts", "1762510272345"))).ToUnixTimeMilliseconds()
        );

    [Fact]
    public void Field_NumericDecimalSeconds_AreAccepted() =>
        Assert.Equal(
            1762510272500,
            Stamp(Fields("raw", ("ts", "1762510272.5"))).ToUnixTimeMilliseconds()
        );

    private static DateTimeOffset Stamp(Line line)
    {
        Assert.True(Detect(line, out _, out var ts));
        return ts;
    }

    [Theory]
    [InlineData("TIMESTAMP")]
    [InlineData("TimeStamp")]
    [InlineData("DateTime")]
    [InlineData("eventTime")]
    [InlineData("@t")]
    [InlineData("t")]
    [InlineData("date")]
    public void FieldNames_AreCaseInsensitive(string name)
    {
        var line = Fields("raw", (name, "2026-11-07T10:11:12Z"));

        Assert.True(Detect(line, out var strategy, out var ts));

        Assert.Equal(Expected, ts);
        Assert.Equal(TimestampSource.Field, strategy.Source);
    }

    [Fact]
    public void Field_WithNonDateValue_FallsThroughToNextCandidate()
    {
        var line = Fields("raw", ("time", "not a date"), ("ts", "1762510272"));

        Assert.True(Detect(line, out var strategy, out _));

        Assert.Equal("ts", strategy.FieldName);
    }

    [Fact]
    public void Field_Failure_FallsThroughToPrefix()
    {
        var line = Fields("2026-11-07T10:11:12Z hello", ("time", "garbage"));

        Assert.True(Detect(line, out var strategy, out var ts));

        Assert.Equal(TimestampSource.IsoPrefix, strategy.Source);
        Assert.Equal(Expected, ts);
    }

    [Theory]
    [InlineData("2026-11-07T10:11:12Z msg", "2026-11-07T10:11:12Z")]
    [InlineData("2026-11-07 10:11:12,345 INFO x", "2026-11-07T10:11:12.345Z")]
    [InlineData("2026-11-07 10:11:12.5 INFO x", "2026-11-07T10:11:12.5Z")]
    [InlineData("[2026-11-07T10:11:12.345+01:00] x", "2026-11-07T09:11:12.345Z")]
    [InlineData("2026-11-07T10:11:12-0500 x", "2026-11-07T15:11:12Z")]
    [InlineData("2026-11-07 10:11:12 UTC trailing", "2026-11-07T10:11:12Z")]
    [InlineData("2026-11-07 10:11:12", "2026-11-07T10:11:12Z")]
    [InlineData("2024-02-29T23:59:59Z leap", "2024-02-29T23:59:59Z")]
    public void IsoPrefix_IsRecognised(string raw, string expected)
    {
        Assert.True(Detect(new Line(raw), out var strategy, out var ts));

        Assert.Equal(TimestampSource.IsoPrefix, strategy.Source);
        Assert.Null(strategy.FieldName);
        Assert.Equal(DateTimeOffset.Parse(expected), ts);
    }

    [Fact]
    public void IsoPrefix_NaiveValue_IsReadInTheNaiveZone()
    {
        var plusFive = TimeZoneInfo.CreateCustomTimeZone("p5", TimeSpan.FromHours(5), "p5", "p5");

        Assert.True(
            Detect(new Line("2026-11-07 10:11:12 hello"), out _, out var ts, zone: plusFive)
        );

        Assert.Equal(DateTimeOffset.Parse("2026-11-07T05:11:12Z"), ts.ToUniversalTime());
    }

    // US-Eastern-like rules built in code, so the DST tests never depend on the OS zone database.
    private static TimeZoneInfo EasternLike()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date,
            DateTime.MaxValue.Date,
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                new DateTime(1, 1, 1, 2, 0, 0),
                month: 3,
                week: 2,
                dayOfWeek: DayOfWeek.Sunday
            ),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                new DateTime(1, 1, 1, 2, 0, 0),
                month: 11,
                week: 1,
                dayOfWeek: DayOfWeek.Sunday
            )
        );
        return TimeZoneInfo.CreateCustomTimeZone(
            "eastern-like",
            TimeSpan.FromHours(-5),
            "eastern-like",
            "EST",
            "EDT",
            [rule]
        );
    }

    [Fact]
    public void IsoPrefix_NaiveLocalTimeInDstGap_DoesNotThrowAndUsesStandardOffset()
    {
        // 2026-03-08 02:30 does not exist (clocks jump 02:00 -> 03:00).
        var zone = EasternLike();
        Assert.True(zone.IsInvalidTime(new DateTime(2026, 3, 8, 2, 30, 0)));

        Assert.True(Detect(new Line("2026-03-08 02:30:00 x"), out _, out var ts, zone: zone));

        Assert.Equal(DateTimeOffset.Parse("2026-03-08T07:30:00Z"), ts.ToUniversalTime());
    }

    [Fact]
    public void IsoPrefix_NaiveLocalTimeInDstOverlap_PicksTheStandardInterpretation()
    {
        // 2026-11-01 01:30 happens twice (EDT then EST); the standard offset (EST, 06:30Z) wins.
        var zone = EasternLike();
        Assert.True(zone.IsAmbiguousTime(new DateTime(2026, 11, 1, 1, 30, 0)));

        Assert.True(Detect(new Line("2026-11-01 01:30:00 x"), out _, out var ts, zone: zone));

        Assert.Equal(DateTimeOffset.Parse("2026-11-01T06:30:00Z"), ts.ToUniversalTime());
        Assert.Equal(TimeSpan.FromHours(-5), ts.Offset);
    }

    [Theory]
    [InlineData("2026-11-07 10:11:12 +12 files changed")]
    [InlineData("2026-11-07 10:11:12 -05 retries")]
    [InlineData("2026-11-07 10:11:12 +12")]
    public void IsoPrefix_BareHourOffsetAfterSpace_IsNotAnOffset(string raw)
    {
        Assert.True(Detect(new Line(raw), out _, out var ts));

        Assert.Equal(Expected, ts);
        Assert.Equal(TimeSpan.Zero, ts.Offset);
    }

    [Fact]
    public void IsoPrefix_BareHourOffsetAfterSpace_UsesTheNaiveZone()
    {
        var plusFive = TimeZoneInfo.CreateCustomTimeZone("p5", TimeSpan.FromHours(5), "p5", "p5");

        Assert.True(
            Detect(
                new Line("2026-11-07 10:11:12 +12 files changed"),
                out _,
                out var ts,
                zone: plusFive
            )
        );

        Assert.Equal(DateTimeOffset.Parse("2026-11-07T05:11:12Z"), ts.ToUniversalTime());
    }

    [Theory]
    [InlineData("2026-11-07T22:11:12+12 x", "2026-11-07T10:11:12Z")]
    [InlineData("2026-11-07 22:11:12+12 x", "2026-11-07T10:11:12Z")]
    [InlineData("2026-11-07 22:11:12 +12:00 x", "2026-11-07T10:11:12Z")]
    [InlineData("2026-11-07 22:11:12 +1200 x", "2026-11-07T10:11:12Z")]
    [InlineData("2026-11-07 05:11:12 -05:00 x", "2026-11-07T10:11:12Z")]
    public void IsoPrefix_OffsetGluedOrWithMinutes_IsStillAnOffset(string raw, string expected)
    {
        Assert.True(Detect(new Line(raw), out _, out var ts));

        Assert.Equal(DateTimeOffset.Parse(expected), ts.ToUniversalTime());
        Assert.NotEqual(TimeSpan.Zero, ts.Offset);
    }

    [Theory]
    [InlineData("127.0.0.1 - - [07/Nov/2026:10:11:12 +0000] \"GET /\" 200", "2026-11-07T10:11:12Z")]
    [InlineData("[07/Nov/2026:12:11:12 +0200] \"GET /\"", "2026-11-07T10:11:12Z")]
    [InlineData("h - u [07/Nov/2026:05:11:12 -0500] x", "2026-11-07T10:11:12Z")]
    public void CommonLog_BracketWithinFirst64Chars_IsRecognised(string raw, string expected)
    {
        Assert.True(Detect(new Line(raw), out var strategy, out var ts));

        Assert.Equal(TimestampSource.CommonLogPrefix, strategy.Source);
        Assert.Equal(DateTimeOffset.Parse(expected), ts.ToUniversalTime());
    }

    [Fact]
    public void CommonLog_BracketBeyond64Chars_IsIgnored()
    {
        var raw = new string('x', 70) + " [07/Nov/2026:10:11:12 +0000] x";

        Assert.False(Detect(new Line(raw), out _, out _));
    }

    [Fact]
    public void Syslog_UsesCurrentYearInTheNaiveZone()
    {
        Assert.True(
            Detect(new Line("Nov  7 10:11:12 host app[1]: x"), out var strategy, out var ts)
        );

        Assert.Equal(TimestampSource.SyslogPrefix, strategy.Source);
        Assert.Equal(Expected, ts.ToUniversalTime());
    }

    [Fact]
    public void Syslog_TwoDigitDay_IsRecognised()
    {
        var now = DateTimeOffset.Parse("2026-11-30T00:00:00Z");

        Assert.True(Detect(new Line("Nov 17 10:11:12 host x"), out _, out var ts, now: now));

        Assert.Equal(DateTimeOffset.Parse("2026-11-17T10:11:12Z"), ts.ToUniversalTime());
    }

    [Fact]
    public void Syslog_DateMoreThanADayInTheFuture_BelongsToLastYear()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:10Z");

        Assert.True(Detect(new Line("Dec 31 23:59:59 host x"), out _, out var ts, now: now));

        Assert.Equal(DateTimeOffset.Parse("2025-12-31T23:59:59Z"), ts.ToUniversalTime());
    }

    [Fact]
    public void Syslog_Feb29_InNonLeapYear_FallsBackToTheLastLeapYear()
    {
        Assert.True(Detect(new Line("Feb 29 10:00:00 host x"), out _, out var ts));

        Assert.Equal(DateTimeOffset.Parse("2024-02-29T10:00:00Z"), ts.ToUniversalTime());
    }

    [Fact]
    public void Syslog_ImpossibleDate_IsRejected() =>
        Assert.False(Detect(new Line("Feb 30 10:00:00 host x"), out _, out _));

    [Fact]
    public void Syslog_Feb29_InLeapYear_IsAccepted()
    {
        var now = DateTimeOffset.Parse("2028-03-01T00:00:00Z");

        Assert.True(Detect(new Line("Feb 29 10:00:00 host x"), out _, out var ts, now: now));

        Assert.Equal(DateTimeOffset.Parse("2028-02-29T10:00:00Z"), ts.ToUniversalTime());
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("12:30 meeting")]
    [InlineData("2026-13-40 99:99:99")]
    [InlineData("2026-11-07")]
    [InlineData("")]
    [InlineData("Nov 7 99:99:99 x")]
    [InlineData("Novel 7 10:11:12 x")]
    public void Rejects(string raw) => Assert.False(Detect(new Line(raw), out _, out _));

    [Fact]
    public void Rejects_ImplausibleNumericField()
    {
        Assert.False(Detect(Fields("raw", ("ts", "42")), out _, out _));
        Assert.False(Detect(Fields("raw", ("ts", "-1762510272")), out _, out _));
        Assert.False(Detect(Fields("raw", ("ts", "99999999999999999999999")), out _, out _));
    }

    [Fact]
    public void HugeLineWithoutTimestamp_IsRejectedQuickly()
    {
        var raw = new string('a', 10_000);
        Detect(new Line(raw), out _, out _);

        var watch = Stopwatch.StartNew();
        var found = Detect(new Line(raw), out _, out _);
        watch.Stop();

        Assert.False(found);
        Assert.True(watch.ElapsedMilliseconds < 50, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void HugeLineWithLeadingTimestamp_StillParses()
    {
        var raw = "2026-11-07T10:11:12Z " + new string('a', 100_000);

        Assert.True(Detect(new Line(raw), out _, out var ts));

        Assert.Equal(Expected, ts);
    }

    [Fact]
    public void TryExtract_UsesOnlyTheGivenStrategy()
    {
        var line = new Line("2026-11-07T10:11:12Z x");
        var strategy = new TimestampStrategy(TimestampSource.Field, "time");

        Assert.False(TimestampDetector.TryExtract(line, strategy, TimeZoneInfo.Utc, Now, out _));
    }

    [Fact]
    public void TryExtract_FieldStrategy_ReadsThatField()
    {
        var line = Fields("raw", ("time", "2026-11-07T10:11:12Z"), ("ts", "1"));
        var strategy = new TimestampStrategy(TimestampSource.Field, "time");

        Assert.True(
            TimestampDetector.TryExtract(line, strategy, TimeZoneInfo.Utc, Now, out var ts)
        );

        Assert.Equal(Expected, ts);
    }

    [Fact]
    public void TryExtract_PrefixStrategy_IgnoresFields()
    {
        var line = Fields("2026-11-07T10:11:12Z x", ("time", "2000-01-01T00:00:00Z"));
        var strategy = new TimestampStrategy(TimestampSource.IsoPrefix, null);

        Assert.True(
            TimestampDetector.TryExtract(line, strategy, TimeZoneInfo.Utc, Now, out var ts)
        );

        Assert.Equal(Expected, ts);
    }

    [Fact]
    public void ParsedFields_TakePrecedenceOverPrefix()
    {
        var line = Fields("2000-01-01T00:00:00Z x", ("@timestamp", "2026-11-07T10:11:12Z"));

        Assert.True(Detect(line, out var strategy, out var ts));

        Assert.Equal(TimestampSource.Field, strategy.Source);
        Assert.Equal(Expected, ts);
    }
}
