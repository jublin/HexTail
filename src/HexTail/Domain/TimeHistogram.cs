namespace HexTail.Domain;

/// <summary>One time bucket of a <see cref="TimeHistogram"/>: row counts by level and the first row in it (-1 when empty).</summary>
public sealed record HistogramBucket(
    DateTimeOffset Start,
    DateTimeOffset End,
    int Error,
    int Warn,
    int Info,
    int Debug,
    int Other,
    int FirstRow
)
{
    public int Total => Error + Warn + Info + Debug + Other;
}

/// <summary>
/// Row counts per time bucket for the rows a view displays. Buckets use a "nice" interval, the smallest
/// from <see cref="Intervals"/> that fits in the bucket budget (whole weeks past seven days), with
/// boundaries aligned in the display zone. Rows without a timestamp are not counted.
/// </summary>
public sealed class TimeHistogram
{
    public static IReadOnlyList<TimeSpan> Intervals { get; } =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(3),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(12),
        TimeSpan.FromDays(1),
        TimeSpan.FromDays(2),
        TimeSpan.FromDays(7),
    ];

    private TimeHistogram(DateTimeOffset start, TimeSpan interval, HistogramBucket[] buckets)
    {
        Start = start;
        Interval = interval;
        Buckets = buckets;
        End = start + interval * buckets.Length;
        MaxTotal = buckets.Max(bucket => bucket.Total);
    }

    public DateTimeOffset Start { get; }
    public DateTimeOffset End { get; }
    public TimeSpan Interval { get; }
    public IReadOnlyList<HistogramBucket> Buckets { get; }
    public int MaxTotal { get; }

    /// <summary>The bucket under a horizontal position (0 = left edge, 1 = right edge), clamped.</summary>
    public int BucketIndexAt(double fraction) =>
        Math.Clamp((int)Math.Floor(fraction * Buckets.Count), 0, Buckets.Count - 1);

    /// <param name="zoneOffset">The display zone's UTC offset at a given instant.</param>
    public static TimeHistogram? Build(
        IReadOnlyList<Line> rows,
        int maxBuckets,
        Func<DateTimeOffset, TimeSpan> zoneOffset
    )
    {
        maxBuckets = Math.Max(1, maxBuckets);
        DateTimeOffset? min = null;
        DateTimeOffset? max = null;
        foreach (var row in rows)
        {
            if (row.Timestamp is not { } time)
                continue;
            if (min is null || time < min)
                min = time;
            if (max is null || time > max)
                max = time;
        }
        if (min is not { } first || max is not { } last)
            return null;

        var offset = zoneOffset(first);
        var interval = ChooseInterval(first, last, offset, maxBuckets);
        var start = AlignDown(first, interval, offset);
        var count = (int)((last - start).Ticks / interval.Ticks) + 1;
        var error = new int[count];
        var warn = new int[count];
        var info = new int[count];
        var debug = new int[count];
        var other = new int[count];
        var firstRow = new int[count];
        Array.Fill(firstRow, -1);
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index].Timestamp is not { } time)
                continue;
            var bucket = (int)((time - start).Ticks / interval.Ticks);
            if (firstRow[bucket] < 0)
                firstRow[bucket] = index;
            switch (LevelDetector.Detect(rows[index]))
            {
                case LogLevels.Error:
                    error[bucket]++;
                    break;
                case LogLevels.Warn:
                    warn[bucket]++;
                    break;
                case LogLevels.Info:
                    info[bucket]++;
                    break;
                case LogLevels.Debug or LogLevels.Trace:
                    debug[bucket]++;
                    break;
                default:
                    other[bucket]++;
                    break;
            }
        }
        var buckets = new HistogramBucket[count];
        for (var bucket = 0; bucket < count; bucket++)
            buckets[bucket] = new HistogramBucket(
                start + interval * bucket,
                start + interval * (bucket + 1),
                error[bucket],
                warn[bucket],
                info[bucket],
                debug[bucket],
                other[bucket],
                firstRow[bucket]
            );
        return new TimeHistogram(start, interval, buckets);
    }

    private static TimeSpan ChooseInterval(
        DateTimeOffset first,
        DateTimeOffset last,
        TimeSpan offset,
        int maxBuckets
    )
    {
        foreach (var interval in Intervals)
            if (Fits(interval))
                return interval;
        var week = TimeSpan.FromDays(7);
        for (var weeks = 2L; ; weeks++)
            if (Fits(week * weeks))
                return week * weeks;

        bool Fits(TimeSpan interval)
        {
            var start = AlignDown(first, interval, offset);
            return (last - start).Ticks / interval.Ticks + 1 <= maxBuckets;
        }
    }

    // Floors the instant to a whole interval on the display-zone clock.
    private static DateTimeOffset AlignDown(DateTimeOffset time, TimeSpan interval, TimeSpan offset)
    {
        var local = time.UtcTicks + offset.Ticks;
        var floored = local - local % interval.Ticks;
        return new DateTimeOffset(floored - offset.Ticks, TimeSpan.Zero);
    }
}
