namespace HexTail.Domain;

/// <summary>
/// Decorates a parser with timestamp detection. The first line that carries a timestamp fixes the
/// strategy for the source; later lines are read with that strategy only. A line without one
/// inherits the previous timestamp (<see cref="Line.TimestampInherited"/>), which keeps stack
/// traces and wrapped messages attached to their entry. After <see cref="MissLimit"/> consecutive
/// misses the strategy is forgotten so a changed format can be re-detected.
/// </summary>
public sealed class TimestampingParser : ILogParser
{
    public const int MissLimit = 200;

    private readonly ILogParser _inner;
    private readonly Func<TimeZoneInfo> _naiveZone;
    private readonly Func<DateTimeOffset>? _now;
    private readonly object _gate = new();
    private TimestampStrategy? _strategy;
    private DateTimeOffset? _last;
    private int _misses;

    public TimestampingParser(
        ILogParser inner,
        Func<TimeZoneInfo> naiveZone,
        Func<DateTimeOffset>? now = null
    )
    {
        _inner = inner;
        _naiveZone = naiveZone;
        _now = now;
    }

    public ILogParser Inner => _inner;

    /// <summary>The chosen strategy, for diagnostics and tests.</summary>
    public TimestampStrategy? Strategy
    {
        get
        {
            lock (_gate)
                return _strategy;
        }
    }

    public Line Parse(string rawText)
    {
        var line = _inner.Parse(rawText);
        var zone = ReadZone();
        var now = _now?.Invoke() ?? DateTimeOffset.UtcNow;

        lock (_gate)
        {
            if (_strategy is { } strategy)
            {
                if (TimestampDetector.TryExtract(line, strategy, zone, now, out var found))
                {
                    _misses = 0;
                    _last = found;
                    return line with { Timestamp = found };
                }

                if (++_misses >= MissLimit)
                {
                    _strategy = null;
                    _misses = 0;
                }
            }
            else if (TimestampDetector.TryDetect(line, zone, now, out var chosen, out var first))
            {
                _strategy = chosen;
                _misses = 0;
                _last = first;
                return line with { Timestamp = first };
            }

            return _last is { } previous
                ? line with
                {
                    Timestamp = previous,
                    TimestampInherited = true,
                }
                : line;
        }
    }

    private TimeZoneInfo ReadZone()
    {
        try
        {
            return _naiveZone() ?? TimeZoneInfo.Utc;
        }
        catch (Exception)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
