using System.Globalization;
using System.Text;
using HexTail.Application;
using HexTail.Domain;
using HexTail.Persistence;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

/// <summary>
/// What the timeline histogram and the overview ruler show for one log view: the histogram buckets,
/// the ruler markers, bucket descriptions, and jumps/range changes back into the owner. The controls
/// watch <see cref="Version"/> and rebuild (coalesced) when it changes.
/// </summary>
internal sealed class LogOverviewViewModel : ReactiveObject
{
    private readonly MainWindowViewModel _owner;
    private readonly LogViewViewModel _view;
    private int _version;
    private bool _hasTimeline;
    private RuleSet? _cachedRules;
    private Dictionary<Line, string?> _ruleColors = new(ReferenceEqualityComparer.Instance);

    internal LogOverviewViewModel(MainWindowViewModel owner, LogViewViewModel view)
    {
        _owner = owner;
        _view = view;
    }

    /// <summary>Raised on <see cref="Invalidate"/>; the controls rebuild when it changes.</summary>
    public int Version => _version;

    /// <summary>The view has at least one row (the ruler is hidden otherwise).</summary>
    public bool HasRows => _view.Lines.Count > 0;

    /// <summary>The last <see cref="BuildHistogram"/> found rows with timestamps.</summary>
    public bool HasTimeline
    {
        get => _hasTimeline;
        private set => this.RaiseAndSetIfChanged(ref _hasTimeline, value);
    }

    internal void Invalidate()
    {
        _version++;
        this.RaisePropertyChanged(nameof(Version));
        this.RaisePropertyChanged(nameof(HasRows));
    }

    private AppTimeZoneMode Zone => _view.Settings.TimeZoneMode;

    private TimeSpan OffsetAt(DateTimeOffset time) =>
        Zone == AppTimeZoneMode.Utc ? TimeSpan.Zero : TimeZoneInfo.Local.GetUtcOffset(time);

    internal TimeHistogram? BuildHistogram(int maxBuckets)
    {
        var rows = _view.Lines;
        var lines = new Line[rows.Count];
        for (var index = 0; index < lines.Length; index++)
            lines[index] = rows[index].Line;
        var histogram = TimeHistogram.Build(lines, maxBuckets, OffsetAt);
        HasTimeline = histogram is not null;
        return histogram;
    }

    internal IReadOnlyList<ScrollMarker> BuildMarkers()
    {
        var rows = _view.Lines;
        var lines = new Line[rows.Count];
        for (var index = 0; index < lines.Length; index++)
            lines[index] = rows[index].Line;

        var searchColors = new Dictionary<Line, string>(ReferenceEqualityComparer.Instance);
        var buffer = _view.File.Buffer;
        foreach (var search in _view.File.Searches)
        {
            if (ReferenceEquals(search, _view.Search))
                continue;
            foreach (var bufferIndex in search.Results)
                if (bufferIndex >= 0 && bufferIndex < buffer.Count)
                    searchColors.TryAdd(buffer[bufferIndex], search.Color);
        }

        var rules = _view.File.EffectiveRules(_view.Settings);
        if (!ReferenceEquals(rules, _cachedRules))
        {
            _cachedRules = rules;
            _ruleColors = new Dictionary<Line, string?>(ReferenceEqualityComparer.Instance);
        }
        // A fresh dictionary per build holds only the current rows, so the cache never outgrows the view.
        var current = new Dictionary<Line, string?>(ReferenceEqualityComparer.Instance);
        var markers = ScrollMarkers.Build(
            lines,
            line =>
            {
                if (searchColors.TryGetValue(line, out var searchColor))
                    return searchColor;
                if (!_ruleColors.TryGetValue(line, out var ruleColor))
                    ruleColor = rules.FirstHighlightColor(line);
                current[line] = ruleColor;
                return ruleColor;
            }
        );
        _ruleColors = current;
        return markers;
    }

    internal string DescribeBucket(TimeHistogram histogram, int index)
    {
        var bucket = histogram.Buckets[index];
        var format =
            histogram.End - histogram.Start < TimeSpan.FromHours(24) ? "HH:mm:ss" : "MM-dd HH:mm";
        string Format(DateTimeOffset time) =>
            time.ToOffset(OffsetAt(time)).ToString(format, CultureInfo.InvariantCulture);

        var text = new StringBuilder()
            .Append(Format(bucket.Start))
            .Append(" – ")
            .Append(Format(bucket.End))
            .Append(" · ")
            .Append(bucket.Total.ToString("N0", CultureInfo.InvariantCulture))
            .Append(" rows");
        if (bucket.Total == 0)
            return text.ToString();
        var parts = new List<string>();
        void Add(int count, string name)
        {
            if (count > 0)
                parts.Add($"{count.ToString("N0", CultureInfo.InvariantCulture)} {name}");
        }
        Add(bucket.Error, "error");
        Add(bucket.Warn, "warn");
        Add(bucket.Info, "info");
        Add(bucket.Debug, "debug");
        Add(bucket.Other, "other");
        return parts.Count == 0
            ? text.ToString()
            : text.Append(" (").AppendJoin(", ", parts).Append(')').ToString();
    }

    internal void JumpToBucket(TimeHistogram histogram, int index)
    {
        if (index < 0 || index >= histogram.Buckets.Count)
            return;
        var row = histogram.Buckets[index].FirstRow;
        if (row >= 0)
            _view.SelectRowAt(row);
    }

    internal void JumpToFraction(double fraction)
    {
        var count = _view.Lines.Count;
        if (count == 0)
            return;
        _view.SelectRowAt(Math.Clamp((int)Math.Floor(fraction * count), 0, count - 1));
    }

    internal void ApplyRange(DateTimeOffset from, DateTimeOffset to) =>
        _owner.ApplyHistogramRange(from, to);

    internal void ResetRange() => _owner.ResetHistogramRange();
}
