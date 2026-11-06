using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace HexTail.Domain;

[Flags]
public enum LogLevels
{
    None = 0,
    Trace = 1,
    Debug = 2,
    Info = 4,
    Warn = 8,
    Error = 16,
    All = Trace | Debug | Info | Warn | Error,
}

/// <summary>
/// Finds a line's level from parsed fields first (<c>level</c>, <c>severity</c>, …), then from an
/// UPPERCASE token near the start of the raw text. Lowercase words are never treated as levels.
/// Results are cached per <see cref="Line"/> because views re-filter the whole buffer on every sync.
/// </summary>
public static partial class LevelDetector
{
    private static readonly string[] FieldNames =
    [
        "level",
        "lvl",
        "severity",
        "loglevel",
        "log.level",
        "levelname",
        "@l",
    ];

    private static readonly ConditionalWeakTable<Line, StrongBox<LogLevels>> Cache = new();

    public static LogLevels Detect(Line line) =>
        Cache.GetValue(line, static item => new StrongBox<LogLevels>(Compute(item))).Value;

    public static bool Allows(LogLevels mask, Line line)
    {
        if (mask == LogLevels.All)
            return true;
        var level = Detect(line);
        return level == LogLevels.None || mask.HasFlag(level);
    }

    private static LogLevels Compute(Line line)
    {
        if (line.ParsedFields is { Count: > 0 } fields)
            foreach (var name in FieldNames)
            foreach (var field in fields)
                if (string.Equals(field.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    var parsed = FromWord(field.Value);
                    if (parsed != LogLevels.None)
                        return parsed;
                }

        var raw = line.Raw;
        if (raw.Length == 0)
            return LogLevels.None;
        var head = raw.Length > 120 ? raw[..120] : raw;
        var match = Token().Match(head);
        return match.Success ? FromWord(match.Groups[1].Value) : LogLevels.None;
    }

    private static LogLevels FromWord(string word) =>
        word.Trim().ToLowerInvariant() switch
        {
            "trace" or "trc" or "verbose" or "finest" or "finer" => LogLevels.Trace,
            "debug" or "dbg" or "fine" => LogLevels.Debug,
            "info" or "information" or "inf" or "notice" => LogLevels.Info,
            "warn" or "warning" or "wrn" => LogLevels.Warn,
            "error"
            or "err"
            or "fatal"
            or "ftl"
            or "critical"
            or "crit"
            or "severe"
            or "panic"
            or "alert"
            or "emergency" => LogLevels.Error,
            _ => LogLevels.None,
        };

    [GeneratedRegex(
        @"(?:^|[\s\[\(=:|])(TRACE|TRC|DEBUG|DBG|INFO|INF|WARN|WARNING|WRN|ERROR|ERR|FATAL|FTL|CRITICAL|SEVERE)(?=$|[\s\]\):|,])",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex Token();
}
