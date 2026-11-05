using System.Globalization;
using System.Text.RegularExpressions;
using HexTail.Elastic;
using HexTail.Persistence;

namespace HexTail.Application;

public readonly record struct ParsedTime(string Expression, DateTimeOffset Resolved);

/// <summary>
/// Parses the time text users type into the expressions <see cref="ElasticTailer"/> accepts:
/// <c>now</c>, <c>now-&lt;n&gt;[smhd]</c>, or a round-trip ("O") timestamp.
/// </summary>
public static partial class TimeExpression
{
    private static readonly string[] WallFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-dd",
    ];

    private static readonly string[] ClockFormats = ["HH:mm:ss", "HH:mm", "H:mm:ss", "H:mm"];

    [GeneratedRegex(
        @"^(?:now)?-(\d+(?:\.\d+)?)([smhdw])$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex RelativePattern();

    [GeneratedRegex(
        @"(?:Z|[+-]\d{2}:?\d{2})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex OffsetSuffix();

    public static bool TryParse(
        string? input,
        DateTimeOffset now,
        AppTimeZoneMode zone,
        out ParsedTime result,
        out string? error
    )
    {
        result = default;
        error = null;
        var value = input?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            error = "Enter a time.";
            return false;
        }

        if (value.Equals("now", StringComparison.OrdinalIgnoreCase))
        {
            result = new("now", now);
            return true;
        }

        var relative = RelativePattern().Match(value);
        if (relative.Success)
        {
            try
            {
                var amount = decimal.Parse(
                    relative.Groups[1].Value,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture
                );
                var unit = char.ToLowerInvariant(relative.Groups[2].Value[0]);
                if (unit == 'w')
                {
                    amount *= 7;
                    unit = 'd';
                }
                var expression =
                    $"now-{amount.ToString("0.###", CultureInfo.InvariantCulture)}{unit}";
                result = new(expression, ElasticTailer.ParseTime(expression, now));
                return true;
            }
            catch (Exception ex)
                when (ex is ArgumentException or FormatException or OverflowException)
            {
                error = $"“{value}” is out of range.";
                return false;
            }
        }

        if (
            OffsetSuffix().IsMatch(value)
            && DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var absolute
            )
        )
        {
            result = new(absolute.ToString("O", CultureInfo.InvariantCulture), absolute);
            return true;
        }

        var today = ToZone(now, zone).Date;
        DateTime? wall = null;
        if (
            DateTime.TryParseExact(
                value,
                WallFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dated
            )
        )
            wall = dated;
        else if (TryParseDayWord(value, today, out var day))
            wall = day;
        else if (TryParseClock(value, out var clock))
            wall = today + clock;

        if (wall is null)
        {
            error =
                $"“{value}” is not a recognized time. Try now-15m, -2h, 14:00, yesterday 09:00, or 2026-11-05 14:00.";
            return false;
        }

        return TryResolveWallTime(wall.Value, zone, out result, out error);
    }

    public static string FormatForInput(string expression, AppTimeZoneMode zone)
    {
        var value = expression.Trim();
        if (
            value.Equals("now", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("now-", StringComparison.OrdinalIgnoreCase)
        )
            return value.ToLowerInvariant();
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var time
        )
            ? ToZone(time, zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : value;
    }

    internal static DateTimeOffset ToZone(DateTimeOffset value, AppTimeZoneMode zone) =>
        zone == AppTimeZoneMode.Utc ? value.ToUniversalTime() : value.ToLocalTime();

    private static bool TryParseDayWord(string value, DateTime today, out DateTime wall)
    {
        wall = default;
        var parts = value.Split(
            ' ',
            2,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        DateTime day;
        if (parts[0].Equals("today", StringComparison.OrdinalIgnoreCase))
            day = today;
        else if (parts[0].Equals("yesterday", StringComparison.OrdinalIgnoreCase))
            day = today.AddDays(-1);
        else
            return false;

        if (parts.Length == 1)
        {
            wall = day;
            return true;
        }
        if (!TryParseClock(parts[1], out var clock))
            return false;
        wall = day + clock;
        return true;
    }

    private static bool TryParseClock(string value, out TimeSpan clock)
    {
        clock = default;
        if (
            !DateTime.TryParseExact(
                value,
                ClockFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.NoCurrentDateDefault,
                out var parsed
            )
        )
            return false;
        clock = parsed.TimeOfDay;
        return true;
    }

    private static bool TryResolveWallTime(
        DateTime wall,
        AppTimeZoneMode zone,
        out ParsedTime result,
        out string? error
    )
    {
        result = default;
        error = null;
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (
            zone == AppTimeZoneMode.Local
            && (TimeZoneInfo.Local.IsInvalidTime(wall) || TimeZoneInfo.Local.IsAmbiguousTime(wall))
        )
        {
            error =
                "This local time is missing or ambiguous due to daylight saving. Switch to UTC.";
            return false;
        }
        var offset =
            zone == AppTimeZoneMode.Utc ? TimeSpan.Zero : TimeZoneInfo.Local.GetUtcOffset(wall);
        var resolved = new DateTimeOffset(wall, offset);
        result = new(resolved.ToString("O", CultureInfo.InvariantCulture), resolved);
        return true;
    }
}
