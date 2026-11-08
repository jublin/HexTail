using System.Globalization;
using System.Runtime.CompilerServices;

namespace HexTail.Domain;

public enum TimestampSource
{
    Field,
    IsoPrefix,
    SyslogPrefix,
    CommonLogPrefix,
}

/// <summary>How a source's timestamps are read: a parsed field (by canonical name) or a text prefix.</summary>
public readonly record struct TimestampStrategy(TimestampSource Source, string? FieldName);

/// <summary>
/// Finds a line's timestamp from well-known parsed fields or from a recognisable text prefix.
/// Runs on the tailer thread for every line, so it scans at most the first
/// <see cref="PrefixLimit"/> characters with hand-written, allocation-free, non-backtracking
/// scanners and never throws. Offset-less ("naive") times are read in the supplied zone.
/// </summary>
public static class TimestampDetector
{
    private const int PrefixLimit = 64;
    private const int MaxFieldLength = 64;

    // Order matters: the first candidate that yields a date wins.
    private static readonly string[] FieldNames =
    [
        "@timestamp",
        "timestamp",
        "time",
        "ts",
        "@t",
        "t",
        "date",
        "datetime",
        "eventTime",
    ];

    private static readonly string[] Months =
    [
        "jan",
        "feb",
        "mar",
        "apr",
        "may",
        "jun",
        "jul",
        "aug",
        "sep",
        "oct",
        "nov",
        "dec",
    ];

    [InlineArray(9)]
    private struct FieldSlots
    {
        private string? _first;
    }

    /// <summary>Tries every strategy in a fixed order: fields, ISO prefix, common-log, syslog.</summary>
    public static bool TryDetect(
        Line line,
        TimeZoneInfo naiveZone,
        DateTimeOffset now,
        out TimestampStrategy strategy,
        out DateTimeOffset timestamp
    )
    {
        strategy = default;
        timestamp = default;
        try
        {
            if (line.ParsedFields is { Count: > 0 } fields)
            {
                var slots = new FieldSlots();
                var any = false;
                foreach (var pair in fields)
                {
                    var index = CandidateIndex(pair.Key);
                    if (index >= 0 && slots[index] is null)
                    {
                        slots[index] = pair.Value;
                        any = true;
                    }
                }

                if (any)
                    for (var i = 0; i < FieldNames.Length; i++)
                        if (
                            slots[i] is { } value
                            && TryParseFieldValue(value, naiveZone, out timestamp)
                        )
                        {
                            strategy = new TimestampStrategy(TimestampSource.Field, FieldNames[i]);
                            return true;
                        }
            }

            foreach (
                var source in (ReadOnlySpan<TimestampSource>)
                    [
                        TimestampSource.IsoPrefix,
                        TimestampSource.CommonLogPrefix,
                        TimestampSource.SyslogPrefix,
                    ]
            )
                if (TryPrefix(line.Raw, source, naiveZone, now, out timestamp))
                {
                    strategy = new TimestampStrategy(source, null);
                    return true;
                }
        }
        catch (Exception)
        {
            // Detection is best effort; a malformed line simply has no timestamp.
        }

        timestamp = default;
        return false;
    }

    /// <summary>Reads a timestamp using only <paramref name="strategy"/>.</summary>
    public static bool TryExtract(
        Line line,
        TimestampStrategy strategy,
        TimeZoneInfo naiveZone,
        DateTimeOffset now,
        out DateTimeOffset timestamp
    )
    {
        timestamp = default;
        try
        {
            if (strategy.Source == TimestampSource.Field)
                return strategy.FieldName is { } name
                    && line.ParsedFields is { Count: > 0 } fields
                    && TryGetField(fields, name, out var value)
                    && TryParseFieldValue(value, naiveZone, out timestamp);
            return TryPrefix(line.Raw, strategy.Source, naiveZone, now, out timestamp);
        }
        catch (Exception)
        {
            timestamp = default;
            return false;
        }
    }

    private static int CandidateIndex(string key)
    {
        if (key.Length is 0 or > 10)
            return -1;
        for (var i = 0; i < FieldNames.Length; i++)
            if (string.Equals(key, FieldNames[i], StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static bool TryGetField(
        IReadOnlyDictionary<string, string> fields,
        string name,
        out string value
    )
    {
        if (fields.TryGetValue(name, out value!))
            return true;
        foreach (var pair in fields)
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        value = "";
        return false;
    }

    private static bool TryPrefix(
        string raw,
        TimestampSource source,
        TimeZoneInfo zone,
        DateTimeOffset now,
        out DateTimeOffset timestamp
    )
    {
        var head = raw.AsSpan(0, Math.Min(PrefixLimit, raw.Length));
        switch (source)
        {
            case TimestampSource.IsoPrefix:
                return TryParseIso(head, zone, requireEnd: false, out timestamp);
            case TimestampSource.CommonLogPrefix:
                return TryParseCommonLog(head, out timestamp);
            case TimestampSource.SyslogPrefix:
                return TryParseSyslog(head, zone, now, out timestamp);
            default:
                timestamp = default;
                return false;
        }
    }

    // ---- field values -------------------------------------------------------------------

    private static bool TryParseFieldValue(string value, TimeZoneInfo zone, out DateTimeOffset ts)
    {
        ts = default;
        var text = value.AsSpan().Trim();
        if (text.Length is 0 or > MaxFieldLength || !IsAsciiDigit(text[0]))
            return false;
        return TryParseEpoch(text, out ts) || TryParseIso(text, zone, requireEnd: true, out ts);
    }

    /// <summary>Unix seconds / ms / µs / ns chosen by magnitude (≈ years 1973–5138 per unit).</summary>
    private static bool TryParseEpoch(ReadOnlySpan<char> text, out DateTimeOffset ts)
    {
        ts = default;
        long whole = 0;
        var i = 0;
        for (; i < text.Length && IsAsciiDigit(text[i]); i++)
        {
            var digit = text[i] - '0';
            if (whole > (long.MaxValue - digit) / 10)
                return false;
            whole = whole * 10 + digit;
        }

        var hasFraction = false;
        long fractionTicks = 0;
        if (i < text.Length)
        {
            if (text[i] != '.' || i + 1 >= text.Length)
                return false;
            hasFraction = true;
            var scale = 1_000_000L; // first fractional digit is 10^6 ticks
            for (i++; i < text.Length; i++)
            {
                if (!IsAsciiDigit(text[i]))
                    return false;
                if (scale > 0)
                {
                    fractionTicks += (text[i] - '0') * scale;
                    scale /= 10;
                }
            }
        }

        long ticks;
        if (whole is >= 100_000_000L and < 100_000_000_000L)
            ticks = whole * TimeSpan.TicksPerSecond + fractionTicks;
        else if (hasFraction)
            return false;
        else if (whole is >= 100_000_000_000L and < 100_000_000_000_000L)
            ticks = whole * TimeSpan.TicksPerMillisecond;
        else if (whole is >= 100_000_000_000_000L and < 100_000_000_000_000_000L)
            ticks = whole * 10;
        else if (whole >= 100_000_000_000_000_000L)
            ticks = whole / 100;
        else
            return false;

        var max = DateTimeOffset.MaxValue.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        if (ticks < 0 || ticks > max)
            return false;
        ts = new DateTimeOffset(DateTimeOffset.UnixEpoch.UtcTicks + ticks, TimeSpan.Zero);
        return true;
    }

    // ---- ISO-8601 -----------------------------------------------------------------------

    /// <summary>
    /// <c>[</c>? yyyy-MM-dd (<c>T</c>|space) HH:mm:ss (<c>.</c>|<c>,</c> fraction)? (space? <c>Z</c>|<c>UTC</c>|±HH[:]?[mm])?
    /// </summary>
    private static bool TryParseIso(
        ReadOnlySpan<char> s,
        TimeZoneInfo zone,
        bool requireEnd,
        out DateTimeOffset ts
    )
    {
        ts = default;
        var p = 0;
        if (p < s.Length && s[p] == '[')
            p++;
        if (
            !TryDigits(s, ref p, 4, out var year)
            || !Take(s, ref p, '-')
            || !TryDigits(s, ref p, 2, out var month)
            || !Take(s, ref p, '-')
            || !TryDigits(s, ref p, 2, out var day)
            || p >= s.Length
            || (s[p] != 'T' && s[p] != 't' && s[p] != ' ')
        )
            return false;
        p++;
        if (
            !TryDigits(s, ref p, 2, out var hour)
            || !Take(s, ref p, ':')
            || !TryDigits(s, ref p, 2, out var minute)
            || !Take(s, ref p, ':')
            || !TryDigits(s, ref p, 2, out var second)
        )
            return false;

        long fractionTicks = 0;
        if (p + 1 < s.Length && (s[p] == '.' || s[p] == ',') && IsAsciiDigit(s[p + 1]))
        {
            p++;
            var scale = 1_000_000L;
            while (p < s.Length && IsAsciiDigit(s[p]))
            {
                if (scale > 0)
                {
                    fractionTicks += (s[p] - '0') * scale;
                    scale /= 10;
                }
                p++;
            }
        }

        if (!TryBuild(year, month, day, hour, minute, second, fractionTicks, out var local))
            return false;

        // Optional zone designator.
        var q = p;
        if (q < s.Length && s[q] == ' ')
            q++;
        TimeSpan? offset = null;
        if (q < s.Length && (s[q] == 'Z' || s[q] == 'z') && !IsLetter(s, q + 1))
        {
            offset = TimeSpan.Zero;
            p = q + 1;
        }
        else if (
            q + 2 < s.Length
            && (s[q] == 'U' || s[q] == 'u')
            && (s[q + 1] == 'T' || s[q + 1] == 't')
            && (s[q + 2] == 'C' || s[q + 2] == 'c')
            && !IsLetter(s, q + 3)
        )
        {
            offset = TimeSpan.Zero;
            p = q + 3;
        }
        else if (q < s.Length && (s[q] == '+' || s[q] == '-'))
        {
            var r = q + 1;
            if (TryDigits(s, ref r, 2, out var offsetHours))
            {
                var offsetMinutes = 0;
                var afterHours = r;
                if (r < s.Length && s[r] == ':')
                    r++;
                if (TryDigits(s, ref r, 2, out var parsedMinutes) && parsedMinutes < 60)
                    offsetMinutes = parsedMinutes;
                else
                    r = afterHours;
                if (
                    !(r < s.Length && IsAsciiDigit(s[r]))
                    && (offsetHours < 14 || (offsetHours == 14 && offsetMinutes == 0))
                )
                {
                    var span = new TimeSpan(offsetHours, offsetMinutes, 0);
                    offset = s[q] == '-' ? -span : span;
                    p = r;
                }
            }
        }

        if (requireEnd)
        {
            var rest = s[p..];
            if (p < s.Length && s[p] == ']')
                rest = s[(p + 1)..];
            if (!rest.IsWhiteSpace())
                return false;
        }

        ts = offset is { } known ? new DateTimeOffset(local, known) : ResolveNaive(local, zone);
        return true;
    }

    /// <summary>Invalid (DST gap) and ambiguous (overlap) times use the zone's standard offset.</summary>
    private static DateTimeOffset ResolveNaive(DateTime unspecified, TimeZoneInfo zone)
    {
        if (ReferenceEquals(zone, TimeZoneInfo.Utc))
            return new DateTimeOffset(unspecified, TimeSpan.Zero);
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }

    // ---- common log: [dd/Mmm/yyyy:HH:mm:ss ±zzzz] ---------------------------------------

    private static bool TryParseCommonLog(ReadOnlySpan<char> s, out DateTimeOffset ts)
    {
        ts = default;
        for (var start = 0; start < s.Length; start++)
        {
            if (s[start] != '[')
                continue;
            var p = start + 1;
            if (
                TryDigits(s, ref p, 2, out var day)
                && Take(s, ref p, '/')
                && TryMonth(s, ref p, out var month)
                && Take(s, ref p, '/')
                && TryDigits(s, ref p, 4, out var year)
                && Take(s, ref p, ':')
                && TryDigits(s, ref p, 2, out var hour)
                && Take(s, ref p, ':')
                && TryDigits(s, ref p, 2, out var minute)
                && Take(s, ref p, ':')
                && TryDigits(s, ref p, 2, out var second)
                && Take(s, ref p, ' ')
                && p < s.Length
                && (s[p] == '+' || s[p] == '-')
            )
            {
                var negative = s[p] == '-';
                p++;
                if (
                    TryDigits(s, ref p, 2, out var offsetHours)
                    && TryDigits(s, ref p, 2, out var offsetMinutes)
                    && offsetMinutes < 60
                    && (offsetHours < 14 || (offsetHours == 14 && offsetMinutes == 0))
                    && Take(s, ref p, ']')
                    && TryBuild(year, month, day, hour, minute, second, 0, out var local)
                )
                {
                    var span = new TimeSpan(offsetHours, offsetMinutes, 0);
                    ts = new DateTimeOffset(local, negative ? -span : span);
                    return true;
                }
            }
        }

        return false;
    }

    // ---- classic syslog: Mmm dd HH:mm:ss ------------------------------------------------

    private static bool TryParseSyslog(
        ReadOnlySpan<char> s,
        TimeZoneInfo zone,
        DateTimeOffset now,
        out DateTimeOffset ts
    )
    {
        ts = default;
        var p = 0;
        if (!TryMonth(s, ref p, out var month) || !Take(s, ref p, ' '))
            return false;
        if (p < s.Length && s[p] == ' ') // single-digit days are padded: "Nov  7"
            p++;
        var dayStart = p;
        if (!TryDigits(s, ref p, 1, out var day))
            return false;
        if (p < s.Length && IsAsciiDigit(s[p]))
        {
            p = dayStart;
            if (!TryDigits(s, ref p, 2, out day))
                return false;
        }

        if (
            !Take(s, ref p, ' ')
            || !TryDigits(s, ref p, 2, out var hour)
            || !Take(s, ref p, ':')
            || !TryDigits(s, ref p, 2, out var minute)
            || !Take(s, ref p, ':')
            || !TryDigits(s, ref p, 2, out var second)
            || (p < s.Length && IsAsciiDigit(s[p]))
        )
            return false;

        var nowInZone = TimeZoneInfo.ConvertTime(now, zone);
        var limit = now.AddDays(1);
        // Syslog omits the year: take the latest one (searching back across leap years for Feb 29)
        // that does not put the line more than a day into the future.
        for (var back = 0; back <= 8; back++)
        {
            var year = nowInZone.Year - back;
            if (year < 1)
                break;
            if (!TryBuild(year, month, day, hour, minute, second, 0, out var local))
                continue;
            var candidate = ResolveNaive(local, zone);
            if (candidate <= limit)
            {
                ts = candidate;
                return true;
            }
        }

        return false;
    }

    // ---- scanning helpers ---------------------------------------------------------------

    private static bool TryBuild(
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second,
        long fractionTicks,
        out DateTime value
    )
    {
        value = default;
        if (
            year < 1
            || month is < 1 or > 12
            || day < 1
            || day > DateTime.DaysInMonth(year, month)
            || hour > 23
            || minute > 59
            || second > 59
        )
            return false;
        value = new DateTime(
            year,
            month,
            day,
            hour,
            minute,
            second,
            DateTimeKind.Unspecified
        ).AddTicks(fractionTicks);
        return true;
    }

    private static bool TryMonth(ReadOnlySpan<char> s, ref int p, out int month)
    {
        month = 0;
        if (p + 3 > s.Length || (p + 3 < s.Length && IsLetter(s, p + 3)))
            return false;
        for (var i = 0; i < Months.Length; i++)
            if (s.Slice(p, 3).Equals(Months[i], StringComparison.OrdinalIgnoreCase))
            {
                month = i + 1;
                p += 3;
                return true;
            }

        return false;
    }

    private static bool TryDigits(ReadOnlySpan<char> s, ref int p, int count, out int value)
    {
        value = 0;
        if (p + count > s.Length)
            return false;
        var result = 0;
        for (var i = 0; i < count; i++)
        {
            var c = s[p + i];
            if (!IsAsciiDigit(c))
                return false;
            result = result * 10 + (c - '0');
        }

        p += count;
        value = result;
        return true;
    }

    private static bool Take(ReadOnlySpan<char> s, ref int p, char expected)
    {
        if (p < s.Length && s[p] == expected)
        {
            p++;
            return true;
        }

        return false;
    }

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';

    private static bool IsLetter(ReadOnlySpan<char> s, int index) =>
        index < s.Length && char.IsAsciiLetter(s[index]);
}
