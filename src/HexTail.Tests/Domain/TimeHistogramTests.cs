using HexTail.Domain;

namespace HexTail.Tests.Domain;

public sealed class TimeHistogramTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly Func<DateTimeOffset, TimeSpan> Utc = _ => TimeSpan.Zero;

    private static Line At(DateTimeOffset? time, string raw = "INFO x") =>
        new(raw, null) { Timestamp = time };

    [Fact]
    public void NoTimestamps_GivesNull()
    {
        Assert.Null(TimeHistogram.Build([At(null), At(null)], 100, Utc));
        Assert.Null(TimeHistogram.Build([], 100, Utc));
    }

    [Fact]
    public void PicksTheSmallestNiceIntervalThatFits()
    {
        // 10 minutes of data into at most 20 buckets: 30 s (21 buckets) is too many, 1 m fits (11).
        var rows = new[] { At(T0), At(T0.AddMinutes(10)) };
        var histogram = TimeHistogram.Build(rows, 20, Utc)!;
        Assert.Equal(TimeSpan.FromMinutes(1), histogram.Interval);
        Assert.Equal(T0, histogram.Start);
        Assert.Equal(11, histogram.Buckets.Count);
        Assert.Equal(T0.AddMinutes(11), histogram.End);
    }

    [Fact]
    public void BoundariesAlignInTheDisplayZone()
    {
        // 10:07:30 UTC is 11:07:30 at +01:00; 5-minute buckets start at 11:05 local = 10:05 UTC.
        var rows = new[] { At(T0.AddMinutes(7.5)), At(T0.AddMinutes(30)) };
        var histogram = TimeHistogram.Build(rows, 6, _ => TimeSpan.FromHours(1))!;
        Assert.Equal(TimeSpan.FromMinutes(5), histogram.Interval);
        Assert.Equal(T0.AddMinutes(5), histogram.Start);
    }

    [Fact]
    public void CountsByLevel_AndRemembersTheFirstRowOfEachBucket()
    {
        var rows = new[]
        {
            At(T0.AddSeconds(1), "INFO a"),
            At(T0.AddSeconds(2), "ERROR b"),
            At(null, "untimed"),
            At(T0.AddSeconds(3), "WARN c"),
            At(T0.AddSeconds(4), "DEBUG d"),
            At(T0.AddSeconds(5), "TRACE e"),
            At(T0.AddSeconds(61), "plain f"),
        };
        var histogram = TimeHistogram.Build(rows, 2, Utc)!;
        Assert.Equal(TimeSpan.FromMinutes(1), histogram.Interval);
        var first = histogram.Buckets[0];
        Assert.Equal(
            (1, 1, 1, 2, 0),
            (first.Error, first.Warn, first.Info, first.Debug, first.Other)
        );
        Assert.Equal(5, first.Total);
        Assert.Equal(0, first.FirstRow);
        var second = histogram.Buckets[1];
        Assert.Equal(1, second.Other);
        Assert.Equal(6, second.FirstRow);
        Assert.Equal(5, histogram.MaxTotal);
    }

    [Fact]
    public void EmptyBucketsHaveNoFirstRow()
    {
        var histogram = TimeHistogram.Build([At(T0), At(T0.AddMinutes(2))], 3, Utc)!;
        Assert.Equal(-1, histogram.Buckets[1].FirstRow);
        Assert.Equal(0, histogram.Buckets[1].Total);
    }

    [Fact]
    public void BeyondSevenDays_UsesWholeWeeks()
    {
        var histogram = TimeHistogram.Build([At(T0), At(T0.AddDays(40))], 3, Utc)!;
        Assert.Equal(TimeSpan.FromDays(21), histogram.Interval);
    }

    [Fact]
    public void SingleInstant_GivesOneOneSecondBucket()
    {
        var histogram = TimeHistogram.Build([At(T0), At(T0)], 50, Utc)!;
        Assert.Equal(TimeSpan.FromSeconds(1), histogram.Interval);
        Assert.Single(histogram.Buckets);
        Assert.Equal(2, histogram.Buckets[0].Total);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.49, 0)]
    [InlineData(0.5, 1)]
    [InlineData(1.0, 1)]
    [InlineData(-3.0, 0)]
    [InlineData(7.0, 1)]
    public void BucketIndexAt_ClampsToTheBuckets(double fraction, int expected)
    {
        var histogram = TimeHistogram.Build([At(T0), At(T0.AddMinutes(1))], 2, Utc)!;
        Assert.Equal(expected, histogram.BucketIndexAt(fraction));
    }
}
