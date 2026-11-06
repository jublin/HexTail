using HexTail.Application;
using HexTail.Elastic;
using HexTail.Persistence;

namespace HexTail.Tests.Application;

public sealed class TimeExpressionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 14, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("now", "now")]
    [InlineData("NOW", "now")]
    [InlineData("now-15m", "now-15m")]
    [InlineData("-15m", "now-15m")]
    [InlineData(" -2h ", "now-2h")]
    [InlineData("-30s", "now-30s")]
    [InlineData("now-1w", "now-7d")]
    [InlineData("-01h", "now-1h")]
    [InlineData("-1.0h", "now-1h")]
    [InlineData("now-0.1w", "now-0.7d")]
    public void RelativeInputNormalizesToTailerExpression(string input, string expected)
    {
        Assert.True(
            TimeExpression.TryParse(input, Now, AppTimeZoneMode.Utc, out var parsed, out var error)
        );
        Assert.Null(error);
        Assert.Equal(expected, parsed.Expression);
        Assert.Equal(ElasticTailer.ParseTime(expected, Now), parsed.Resolved);
    }

    [Theory]
    [InlineData("2026-08-20T10:15:30Z", "2026-08-20T10:15:30.0000000+00:00")]
    [InlineData("2026-08-20T10:15:30+02:00", "2026-08-20T10:15:30.0000000+02:00")]
    [InlineData("2026-08-20 10:15:30", "2026-08-20T10:15:30.0000000+00:00")]
    [InlineData("2026-08-20 10:15", "2026-08-20T10:15:00.0000000+00:00")]
    [InlineData("2026-08-20", "2026-08-20T00:00:00.0000000+00:00")]
    [InlineData("09:05", "2026-11-05T09:05:00.0000000+00:00")]
    [InlineData("09:05:10", "2026-11-05T09:05:10.0000000+00:00")]
    [InlineData("today", "2026-11-05T00:00:00.0000000+00:00")]
    [InlineData("yesterday 22:10", "2026-11-04T22:10:00.0000000+00:00")]
    public void AbsoluteInputUsesUtcWallTime(string input, string expected)
    {
        Assert.True(
            TimeExpression.TryParse(input, Now, AppTimeZoneMode.Utc, out var parsed, out var error)
        );
        Assert.Null(error);
        Assert.Equal(expected, parsed.Expression);
        Assert.Equal(DateTimeOffset.Parse(expected), parsed.Resolved);
    }

    [Fact]
    public void LocalWallTimeUsesThisComputersOffset()
    {
        Assert.True(
            TimeExpression.TryParse(
                "2026-08-20 10:00",
                Now,
                AppTimeZoneMode.Local,
                out var parsed,
                out _
            )
        );
        var wall = new DateTime(2026, 8, 20, 10, 0, 0);
        Assert.Equal(
            new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall)),
            parsed.Resolved
        );
    }

    [Theory]
    [InlineData("", "Enter a time")]
    [InlineData("   ", "Enter a time")]
    [InlineData("now-15x", "not a recognized time")]
    [InlineData("-1000000d", "out of range")]
    [InlineData("-99999999999w", "out of range")]
    [InlineData("2026-13-40", "not a recognized time")]
    [InlineData("25:00", "not a recognized time")]
    [InlineData("yesterday 99:00", "not a recognized time")]
    public void InvalidInputReportsAnError(string input, string fragment)
    {
        Assert.False(
            TimeExpression.TryParse(input, Now, AppTimeZoneMode.Utc, out _, out var error)
        );
        Assert.Contains(fragment, error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("now", "now")]
    [InlineData("now-15m", "now-15m")]
    [InlineData("2026-08-20T12:15:30.0000000+02:00", "2026-08-20 10:15:30")]
    [InlineData("not a time", "not a time")]
    public void FormatForInputRendersAbsoluteTimesInZone(string expression, string expected) =>
        Assert.Equal(expected, TimeExpression.FormatForInput(expression, AppTimeZoneMode.Utc));

    [Theory]
    [InlineData("now-15m", "now", "Last 15 min")]
    [InlineData("now-1m", "now", "Last minute")]
    [InlineData("now-1h", "now", "Last hour")]
    [InlineData("now-4h", "now", "Last 4 hours")]
    [InlineData("now-24h", "now", "Last 24 hours")]
    [InlineData("now-7d", "now", "Last 7 days")]
    [InlineData("now-15m", "now-5m", "now-15m → now-5m")]
    [InlineData(
        "2026-08-20T10:15:30.0000000+00:00",
        "2026-08-20T10:16:45.0000000+00:00",
        "2026-08-20 10:15:30 → 2026-08-20 10:16:45"
    )]
    [InlineData("2026-08-20T10:15:30.0000000+00:00", "now", "2026-08-20 10:15:30 → now")]
    public void RangeLabelIsShortAndReadable(string from, string to, string expected) =>
        Assert.Equal(expected, TimeRangeLabel.Describe(from, to, AppTimeZoneMode.Utc));
}
