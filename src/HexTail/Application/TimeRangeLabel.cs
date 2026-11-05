using System.Text.RegularExpressions;
using HexTail.Persistence;

namespace HexTail.Application;

public static partial class TimeRangeLabel
{
    [GeneratedRegex(
        @"^now-(\d+(?:\.\d+)?)([smhd])$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex LastPattern();

    public static string Describe(string from, string to, AppTimeZoneMode zone)
    {
        if (
            to.Trim().Equals("now", StringComparison.OrdinalIgnoreCase)
            && TryDescribeLast(from, out var last)
        )
            return last;
        return $"{TimeExpression.FormatForInput(from, zone)} → {TimeExpression.FormatForInput(to, zone)}";
    }

    private static bool TryDescribeLast(string from, out string label)
    {
        label = string.Empty;
        var match = LastPattern().Match(from.Trim());
        if (!match.Success)
            return false;
        var amount = match.Groups[1].Value;
        var one = amount == "1";
        label = char.ToLowerInvariant(match.Groups[2].Value[0]) switch
        {
            's' => one ? "Last second" : $"Last {amount} sec",
            'm' => one ? "Last minute" : $"Last {amount} min",
            'h' => one ? "Last hour" : $"Last {amount} hours",
            'd' => one ? "Last day" : $"Last {amount} days",
            _ => string.Empty,
        };
        return label.Length > 0;
    }
}
