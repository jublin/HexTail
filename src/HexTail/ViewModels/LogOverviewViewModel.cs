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
    private bool _linesChanged = true;
    private BuildInputs? _lastInputs;
    private TimeHistogram? _builtHistogram;
    private Line?[] _builtFirstLines = [];

    /// <summary>Everything besides the displayed rows that a histogram or marker build depends on.</summary>
    private readonly record struct BuildInputs(
        RuleSet Rules,
        int SearchHash,
        AppTimeZoneMode Zone,
        HistogramPrior? Prior,
        TimeWindow? Window,
        bool ElasticLoading
    );

    internal LogOverviewViewModel(MainWindowViewModel owner, LogViewViewModel view)
    {
        _owner = owner;
        _view = view;
        view.Lines.CollectionChanged += (_, _) => _linesChanged = true;
    }

    /// <summary>Raised on <see cref="Invalidate"/>; the controls rebuild when it changes.</summary>
    public int Version => _version;

    /// <summary>The view has at least one row (the ruler is hidden otherwise).</summary>
    public bool HasRows => _view.Lines.Count > 0;

    /// <summary>Some displayed row has a timestamp (checked on every sync and by <see cref="BuildHistogram"/>).</summary>
    public bool HasTimeline
    {
        get => _hasTimeline;
        private set => this.RaiseAndSetIfChanged(ref _hasTimeline, value);
    }

    /// <summary>
    /// Called on every view sync: bumps <see cref="Version"/> only when something a build depends on
    /// changed (the displayed rows, the rules, the searches, the zone, the range state), so syncs that
    /// change nothing (a paused view, another tab's traffic) do not make the controls rebuild.
    /// </summary>
    internal void Refresh()
    {
        var inputs = CurrentInputs();
        if (!_linesChanged && _lastInputs is { } last && last.Equals(inputs))
            return;
        _linesChanged = false;
        _lastInputs = inputs;
        Invalidate();
    }

    private BuildInputs CurrentInputs()
    {
        var file = _view.File;
        var hash = new HashCode();
        foreach (var search in file.Searches)
        {
            hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(search));
            hash.Add(search.Results.Count);
            hash.Add(search.Color);
        }
        return new BuildInputs(
            file.EffectiveRules(_view.Settings),
            hash.ToHashCode(),
            Zone,
            file.HistogramPrior,
            file.TimeWindow,
            file.ElasticLoading
        );
    }

    internal void Invalidate()
    {
        _version++;
        HasTimeline = AnyTimestamp() || KeepsPanel;
        this.RaisePropertyChanged(nameof(Version));
        this.RaisePropertyChanged(nameof(HasRows));
    }

    // The panel stays up while a dragged range is in place (so a double-click can reset it) and while an
    // Elastic source reloads, even when no displayed row has a time.
    private bool KeepsPanel =>
        _view.File.HistogramPrior is not null
        || (_view.File.Source.Kind == LogSourceKind.Elastic && _view.File.ElasticLoading);

    private bool AnyTimestamp()
    {
        var rows = _view.Lines;
        for (var index = 0; index < rows.Count; index++)
            if (rows[index].Line.Timestamp is not null)
                return true;
        return false;
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
        HasTimeline = histogram is not null || KeepsPanel;
        // A click is resolved against the rows this build saw, not whatever the view shows by then.
        var first = new Line?[histogram?.Buckets.Count ?? 0];
        for (var bucket = 0; bucket < first.Length; bucket++)
            if (histogram!.Buckets[bucket].FirstRow is var row and >= 0)
                first[bucket] = lines[row];
        _builtHistogram = histogram;
        _builtFirstLines = first;
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

    /// <summary>A histogram instant in the display zone: HH:mm:ss under 24 h, else MM-dd HH:mm.</summary>
    internal string FormatTime(TimeHistogram histogram, DateTimeOffset time)
    {
        var format =
            histogram.End - histogram.Start < TimeSpan.FromHours(24) ? "HH:mm:ss" : "MM-dd HH:mm";
        return time.ToOffset(OffsetAt(time)).ToString(format, CultureInfo.InvariantCulture);
    }

    internal string DescribeBucket(TimeHistogram histogram, int index)
    {
        var bucket = histogram.Buckets[index];
        string Format(DateTimeOffset time) => FormatTime(histogram, time);

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
        if (ReferenceEquals(histogram, _builtHistogram))
        {
            // The row may have scrolled away or been filtered out since the build: then there is nothing to select.
            if (_builtFirstLines[index] is { } line)
                _view.SelectRowOf(line);
            return;
        }
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
