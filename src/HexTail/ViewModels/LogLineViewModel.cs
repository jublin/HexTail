using System.Globalization;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Media;
using HexTail;
using HexTail.Application;
using HexTail.Domain;
using HexTail.Persistence;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal sealed class LogLineViewModel : ReactiveObject
{
    private readonly LogViewViewModel _owner;
    private readonly FileTabViewModel _file;
    private bool _isVisible;
    private bool _renderDirty = true;

    internal LogLineViewModel(LogViewViewModel owner, FileTabViewModel file, Line line)
    {
        _owner = owner;
        _file = file;
        Line = line;
        Refresh(notify: false);
    }

    public Line Line { get; }
    public bool IsBookmarked { get; private set; }
    public IReadOnlyList<HighlightSpan> Spans { get; private set; } = [];
    public string ParsedFieldsText { get; private set; } = string.Empty;
    public bool HasParsedFields => Line.ParsedFields is { Count: > 0 };
    public double FontSize { get; private set; }
    public Thickness RowPadding { get; private set; }
    public IBrush Foreground { get; private set; } = Brushes.White;
    public IBrush Background { get; private set; } = Brushes.Transparent;

    /// <summary>True when the row shows one cell per column (a structured source that is not set to composed rows).</summary>
    public bool UsesColumns { get; private set; }
    public IReadOnlyList<LogCellViewModel> Cells { get; private set; } = [];

    /// <summary>True on a stream tab's rows, which carry a left stripe in the member colour.</summary>
    public bool IsStreamRow { get; private set; }

    /// <summary>The member colour of a stream row; null on every other row.</summary>
    public IBrush? StripeBrush { get; private set; }

    /// <summary>The member's display name on a stream row; empty elsewhere.</summary>
    public string SourceName { get; private set; } = string.Empty;

    /// <summary>True when a stream released this row out of order.</summary>
    public bool IsLate { get; private set; }

    /// <summary>"Arrived late from {name}" on a late row; null otherwise.</summary>
    public string? LateToolTip { get; private set; }

    /// <summary>True when the line's level is Error; the row gets a faint tint in both modes.</summary>
    public bool IsErrorRow { get; private set; }

    /// <summary>
    /// Timestamp of the row shown just before this one, used for relative time and the tooltip delta.
    /// Set by <see cref="LogViewViewModel"/> when it creates or re-links rows.
    /// </summary>
    internal DateTimeOffset? PreviousTimestamp
    {
        get => _previousTimestamp;
        set
        {
            if (_previousTimestamp == value)
                return;
            _previousTimestamp = value;
            if (UsesColumns)
                InvalidateRender();
        }
    }

    private DateTimeOffset? _previousTimestamp;
    private const int LongCellText = 80;

    /// <summary>The log text size in pixels for a font-size setting.</summary>
    internal static double FontSizeFor(LogFontSize size) =>
        size switch
        {
            LogFontSize.Small => 11,
            LogFontSize.Large => 14,
            LogFontSize.ExtraLarge => 17,
            _ => ColumnLayout.DefaultFontSize,
        };

    internal void Refresh() => Refresh(notify: true);

    private void Refresh(bool notify)
    {
        var settings = _owner.Settings;
        FontSize = FontSizeFor(settings.LogFontSize);
        RowPadding = settings.Density switch
        {
            UiDensity.Compact => new Thickness(12, 0.6),
            UiDensity.Cozy => new Thickness(12, 2),
            _ => new Thickness(12, 3),
        };
        Foreground = ThemeManager.Brush("TextBrush");
        Background = ThemeManager.Brush("SurfaceBrush");
        ParsedFieldsText = string.Empty;
        IsBookmarked = _file.Model.IsBookmarked(Line);

        _renderDirty = true;
        if (_isVisible)
            Render();

        if (notify)
        {
            this.RaisePropertyChanged(nameof(ParsedFieldsText));
            this.RaisePropertyChanged(nameof(HasParsedFields));
            this.RaisePropertyChanged(nameof(FontSize));
            this.RaisePropertyChanged(nameof(RowPadding));
            this.RaisePropertyChanged(nameof(Foreground));
            this.RaisePropertyChanged(nameof(Background));
            this.RaisePropertyChanged(nameof(IsBookmarked));
        }
    }

    /// <summary>Re-reads the bookmark flag after the tab's bookmarks changed.</summary>
    internal void SyncBookmark()
    {
        var bookmarked = _file.Model.IsBookmarked(Line);
        if (bookmarked == IsBookmarked)
            return;
        IsBookmarked = bookmarked;
        this.RaisePropertyChanged(nameof(IsBookmarked));
    }

    internal void ToggleBookmark() => _owner.Workspace.State.ToggleBookmark(_file.Model, Line);

    internal void SetVisible(bool visible)
    {
        _isVisible = visible;
        if (visible)
            Render();
    }

    internal void InvalidateRender()
    {
        _renderDirty = true;
        if (_isVisible)
            Render();
    }

    internal void Select() => _owner.SelectLineCommand.Execute(Line).Subscribe();

    internal void Inspect()
    {
        Select();
        _owner.Workspace.Inspector.Open();
    }

    private void Render()
    {
        if (!_renderDirty)
            return;

        var settings = _owner.Settings;
        var rules = _file.Model.EffectiveRules(settings);
        var layout = _owner.Layout;
        var usesColumns = _file.Model.IsStructured && !layout.ComposedRows;
        ParsedFieldsText = Line.ParsedFields is { Count: > 0 }
            ? string.Join("  ", Line.ParsedFields.Select(field => $"{field.Key}={field.Value}"))
            : string.Empty;
        var level = LevelDetector.Detect(Line);
        if (usesColumns)
        {
            Spans = [];
            Cells = BuildCells(layout, settings, rules, level);
        }
        else
        {
            Spans = _file.HighlightSpans.Get(Line, _file.Model.Searches, rules);
            Cells = [];
        }
        UsesColumns = usesColumns;
        RenderStream();
        IsErrorRow = level == LogLevels.Error;
        Background = ThemeManager.Brush(IsErrorRow ? "ErrorRowBrush" : "SurfaceBrush");
        this.RaisePropertyChanged(nameof(Spans));
        this.RaisePropertyChanged(nameof(Cells));
        this.RaisePropertyChanged(nameof(UsesColumns));
        this.RaisePropertyChanged(nameof(IsErrorRow));
        this.RaisePropertyChanged(nameof(Background));
        this.RaisePropertyChanged(nameof(ParsedFieldsText));
        _renderDirty = false;
    }

    /// <summary>
    /// Resolves the member colour, name and late flag of a stream row. The late lookup takes AppState's
    /// lock, so it runs once per row render (never per cell) and only for realized rows.
    /// </summary>
    private void RenderStream()
    {
        var stream = _file.Model.Stream;
        if (stream is null)
        {
            if (!IsStreamRow)
                return;
            IsStreamRow = false;
            StripeBrush = null;
            SourceName = string.Empty;
            IsLate = false;
            LateToolTip = null;
        }
        else
        {
            IsStreamRow = true;
            StripeBrush = StreamBrushes.For(stream.ColorFor(Line.SourceId));
            SourceName = stream.DisplayNameFor(Line.SourceId);
            IsLate = stream.IsLate(Line);
            LateToolTip = IsLate ? $"Arrived late from {SourceName}" : null;
        }
        this.RaisePropertyChanged(nameof(IsStreamRow));
        this.RaisePropertyChanged(nameof(StripeBrush));
        this.RaisePropertyChanged(nameof(SourceName));
        this.RaisePropertyChanged(nameof(IsLate));
        this.RaisePropertyChanged(nameof(LateToolTip));
    }

    private LogCellViewModel[] BuildCells(
        ColumnLayout layout,
        AppSettings settings,
        RuleSet rules,
        LogLevels level
    )
    {
        var columns = layout.Columns;
        var searches = _file.Model.Searches;
        var zone =
            settings.TimeZoneMode == AppTimeZoneMode.Utc
                ? TimeZoneInfo.Utc
                : _owner.Workspace.LocalZone;
        var cells = new LogCellViewModel[columns.Count];
        // MessageField.Pick scans the candidate names per field, so run it at most once per render.
        string? message = null;
        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            string? toolTip = null;
            var dimmed = false;
            string? levelClass = null;
            string text;
            switch (column.Key)
            {
                case ColumnLayout.Time:
                    dimmed = Line.TimestampInherited;
                    text = TimeText(layout, zone, out toolTip);
                    break;
                case ColumnLayout.Source:
                    text = _file.Model.Stream?.DisplayNameFor(Line.SourceId) ?? string.Empty;
                    break;
                case ColumnLayout.Level:
                    text = LevelText(level, out levelClass);
                    break;
                case ColumnLayout.Message:
                    text = message ??= MessageField.Pick(Line.ParsedFields) ?? Line.Raw;
                    break;
                default:
                    text =
                        Line.ParsedFields is not null
                        && Line.ParsedFields.TryGetValue(column.Key, out var value)
                            ? value
                            : string.Empty;
                    break;
            }
            if (toolTip is null && text.Length > LongCellText)
                toolTip = text;
            var spans =
                text.Length == 0
                    ? (IReadOnlyList<HighlightSpan>)[]
                    : HighlightSpanCache.ComputeForText(text, searches, rules);
            cells[index] = new LogCellViewModel(
                column.Key,
                text,
                spans,
                column.Width,
                index == columns.Count - 1,
                toolTip,
                dimmed,
                levelClass
            );
        }
        return cells;
    }

    private string TimeText(ColumnLayout layout, TimeZoneInfo zone, out string? toolTip)
    {
        toolTip = null;
        if (Line.Timestamp is not { } timestamp)
            return string.Empty;
        var converted = TimeZoneInfo.ConvertTime(timestamp, zone);
        var iso = converted.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
        iso +=
            converted.Offset == TimeSpan.Zero
                ? "Z"
                : converted.ToString("zzz", CultureInfo.InvariantCulture);
        string? delta = _previousTimestamp is { } previous
            ? FormatDelta(timestamp - previous)
            : null;
        toolTip = delta is null ? iso : $"{iso}\nsince previous row: {delta}";
        return layout.TimeDisplay == TimeDisplay.Relative && delta is not null
            ? delta
            : FormatTime(timestamp, zone, DateTimeOffset.UtcNow);
    }

    private static string LevelText(LogLevels level, out string? levelClass)
    {
        (var text, levelClass) = level switch
        {
            LogLevels.Trace => ("TRC", "trc"),
            LogLevels.Debug => ("DBG", "dbg"),
            LogLevels.Info => ("INF", "inf"),
            LogLevels.Warn => ("WRN", "wrn"),
            LogLevels.Error => ("ERR", "err"),
            _ => (string.Empty, (string?)null),
        };
        return text;
    }

    /// <summary><c>HH:mm:ss.fff</c> in <paramref name="zone"/>, prefixed <c>MM-dd </c> when the date is not today there.</summary>
    internal static string FormatTime(DateTimeOffset value, TimeZoneInfo zone, DateTimeOffset now)
    {
        var converted = TimeZoneInfo.ConvertTime(value, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone);
        var time = converted.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        return converted.Date == today.Date
            ? time
            : converted.ToString("MM-dd ", CultureInfo.InvariantCulture) + time;
    }

    /// <summary>A signed duration: <c>+12 ms</c>, <c>+1.204 s</c>, <c>+3 min 4 s</c>, <c>+1 h 2 min</c>; negative for out-of-order lines.</summary>
    internal static string FormatDelta(TimeSpan delta)
    {
        var sign = delta < TimeSpan.Zero ? "-" : "+";
        var milliseconds = (long)Math.Round(Math.Abs(delta.TotalMilliseconds));
        if (milliseconds < 1_000)
            return $"{sign}{milliseconds} ms";
        if (milliseconds < 60_000)
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{sign}{milliseconds / 1000d:0.000} s"
            );
        var seconds = milliseconds / 1_000;
        if (seconds < 3_600)
            return $"{sign}{seconds / 60} min {seconds % 60} s";
        return $"{sign}{seconds / 3_600} h {seconds % 3_600 / 60} min";
    }
}
