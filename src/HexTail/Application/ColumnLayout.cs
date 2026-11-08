using HexTail.Domain;
using HexTail.Persistence;

namespace HexTail.Application;

/// <summary>One column of the row layout: a well-known key (<c>@time</c>, <c>@level</c>, <c>@message</c>) or a parsed field name.</summary>
public sealed record ColumnSpec(string Key, double Width);

/// <summary>
/// How a source's rows are laid out: which columns, how wide, whether continuation lines are composed into
/// their parent row and how timestamps are shown. Immutable; every change returns a new layout.
/// </summary>
public sealed record ColumnLayout(
    IReadOnlyList<ColumnSpec> Columns,
    bool ComposedRows,
    TimeDisplay TimeDisplay
)
{
    public const string Time = "@time";
    public const string Level = "@level";
    public const string Message = "@message";
    public const double MinWidth = 40;
    public const double MaxWidth = 1200;

    /// <summary>The log font size (Medium) the fixed default widths were designed for.</summary>
    public const double DefaultFontSize = 13;

    // A monospace glyph is about 0.6 em wide; the widest common log fonts reach 0.605 em.
    private const double MonospaceEm = 0.605;
    private const int TimeTextChars = 18; // "MM-dd HH:mm:ss.fff"
    private const double CellGutter = 8;

    /// <summary>
    /// Files get time, level and message; Elastic sources get time, level and then the view's output
    /// fields in order (message first when the view lists no fields).
    /// </summary>
    public static ColumnLayout DefaultFor(
        LogSourceDescriptor source,
        IReadOnlyList<string>? elasticOutputFields,
        double fontSize = DefaultFontSize
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        List<string> keys = [Time, Level];
        if (source.Kind == LogSourceKind.Elastic)
        {
            foreach (var field in elasticOutputFields ?? [])
                if (!string.IsNullOrWhiteSpace(field) && !keys.Contains(field))
                    keys.Add(field);
            if (keys.Count == 2)
                keys.Add(Message);
        }
        else
            keys.Add(Message);
        return new ColumnLayout(
            [.. keys.Select(key => new ColumnSpec(key, DefaultWidth(key, fontSize)))],
            ComposedRows: false,
            TimeDisplay.Absolute
        );
    }

    /// <summary>
    /// The width a column starts with, gutter included. The time column grows with the log font size
    /// so its <c>MM-dd HH:mm:ss.fff</c> text never gets cut off (150 px at the default size).
    /// </summary>
    public static double DefaultWidth(string key, double fontSize = DefaultFontSize) =>
        key switch
        {
            Time => Math.Ceiling(TimeTextChars * MonospaceEm * Math.Max(1, fontSize)) + CellGutter,
            Level => 48,
            Message => 400,
            _ => 140,
        };

    /// <summary>Appends a column with its default width; a key that is already present changes nothing.</summary>
    public ColumnLayout WithColumn(string key, double fontSize = DefaultFontSize)
    {
        if (string.IsNullOrWhiteSpace(key) || Columns.Any(column => column.Key == key))
            return this;
        return this with
        {
            Columns = [.. Columns, new ColumnSpec(key, DefaultWidth(key, fontSize))],
        };
    }

    /// <summary>Removes a column, but never the last one.</summary>
    public ColumnLayout WithoutColumn(string key)
    {
        if (Columns.Count <= 1 || Columns.All(column => column.Key != key))
            return this;
        return this with { Columns = [.. Columns.Where(column => column.Key != key)] };
    }

    public ColumnLayout WithWidth(string key, double width) =>
        this with
        {
            Columns =
            [
                .. Columns.Select(column =>
                    column.Key == key ? column with { Width = ClampWidth(width) } : column
                ),
            ],
        };

    public static double ClampWidth(double width) =>
        double.IsNaN(width) ? MinWidth : Math.Clamp(width, MinWidth, MaxWidth);

    /// <summary>
    /// Rebuilds a layout from saved values: blank and duplicate keys are dropped and widths are clamped.
    /// Returns null (use the defaults) when nothing usable remains.
    /// </summary>
    public static ColumnLayout? FromPersisted(
        IEnumerable<PersistedColumn?>? columns,
        bool composedRows,
        TimeDisplay timeDisplay
    )
    {
        List<ColumnSpec> specs = [];
        foreach (var column in columns ?? [])
            if (
                column is { Key: { } key }
                && !string.IsNullOrWhiteSpace(key)
                && specs.All(spec => spec.Key != key)
            )
                specs.Add(new ColumnSpec(key, ClampWidth(column.Width)));
        return specs.Count == 0
            ? null
            : new ColumnLayout(
                specs,
                composedRows,
                Enum.IsDefined(timeDisplay) ? timeDisplay : TimeDisplay.Absolute
            );
    }

    public List<PersistedColumn> ToPersisted() =>
        [
            .. Columns.Select(column => new PersistedColumn
            {
                Key = column.Key,
                Width = column.Width,
            }),
        ];
}

/// <summary>Picks the field that holds a line's human message.</summary>
public static class MessageField
{
    public static readonly string[] Candidates = ["message", "msg", "log", "event", "text", "body"];

    /// <summary>The value of the first candidate present (case-insensitive), or null when none is.</summary>
    public static string? Pick(IReadOnlyDictionary<string, string>? fields)
    {
        if (fields is not { Count: > 0 })
            return null;
        foreach (var candidate in Candidates)
        {
            if (fields.TryGetValue(candidate, out var direct))
                return direct;
            foreach (var pair in fields)
                if (string.Equals(pair.Key, candidate, StringComparison.OrdinalIgnoreCase))
                    return pair.Value;
        }
        return null;
    }
}
