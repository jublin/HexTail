using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Media;
using HexTail.Application;
using HexTail.Domain;
using HexTail.Persistence;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal sealed class LogViewViewModel : ReactiveObject
{
    private readonly MainWindowViewModel _owner;
    private readonly FileTabViewModel _file;
    private AppSettings? _lastSettings;
    private int? _lastExpandedLine;
    private int _lastViewsVersion;
    private int _lastRulesVersion;
    private ViewSnapshot? _lastSnapshot;
    internal const int UnseenScanLimit = 10_000;
    private Line? _seenAnchor;
    private bool _rowRefreshPending;
    private int _unseenCount;

    internal LogViewViewModel(MainWindowViewModel owner, FileTabViewModel file, Search? search)
    {
        _owner = owner;
        _file = file;
        Search = search;
        SelectLineCommand = ReactiveCommand.Create<Line>(line => _file.SelectLine(line));
        ToggleExpandedCommand = ReactiveCommand.Create<Line>(line => _file.ToggleExpanded(line));
        JumpToLatestCommand = ReactiveCommand.Create(JumpToLatest, owner.Scheduler);
    }

    public Search? Search { get; }
    public MainWindowViewModel Workspace => _owner;
    public FileTabState File => _file.Model;
    public AppSettings Settings => _owner.State.Settings;
    public bool IsAllView => Search is null;
    public string Header => Search is null ? "All" : Truncate(Search.Query.Query);
    public string SearchModeLabel => Search?.Query.Mode.ToString() ?? string.Empty;
    public string MatchSummary => Search is null ? string.Empty : $"({Search.Results.Count:N0})";
    public string ResultCount =>
        $"{Lines.Count:N0} visible · {File.Buffer.Count:N0} loaded from source";
    public string ResultMessage =>
        File.Error is { } error ? error
        : File.ElasticLoading ? "Loading logs for the applied interval…"
        : Lines.Count > 0 ? string.Empty
        : File.Buffer.Count == 0
            ? File.Source.Kind == LogSourceKind.Elastic
                    ? "No logs returned for the applied interval."
                : "No logs in this source yet."
        : Search is not null && Search.Results.Count == 0 ? "No logs match this search."
        : "All matching logs are hidden by filter rules.";
    public bool HasResultMessage => ResultMessage.Length > 0;
    public string InitialLimitMessage =>
        File.ElasticInitialLimitReached is { } limit
            ? $"Initial limit reached ({limit:N0} newest logs). The applied interval may contain more logs; narrow the interval to inspect earlier history."
            : string.Empty;
    public bool HasInitialLimitMessage => InitialLimitMessage.Length > 0;

    public override string ToString() => Header;

    public AvaloniaList<LogLineViewModel> Lines { get; } = [];
    public AvaloniaList<LogLineViewModel> ContextLines { get; } = [];
    public ReactiveCommand<Line, Unit> SelectLineCommand { get; }
    public ReactiveCommand<Line, Unit> ToggleExpandedCommand { get; }
    public ReactiveCommand<Unit, Unit> JumpToLatestCommand { get; }

    public int UnseenCount => _unseenCount;
    public bool HasUnseen => _unseenCount > 0;
    public string UnseenLabel =>
        _unseenCount >= UnseenScanLimit ? $"↓ {UnseenScanLimit:N0}+ new lines"
        : _unseenCount == 1 ? "↓ 1 new line"
        : $"↓ {_unseenCount:N0} new lines";
    public bool ShowContext
    {
        get => _file.Model.ShowContext;
        set
        {
            if (_file.Model.ShowContext == value)
                return;
            _file.ShowContext = value;
            Sync();
        }
    }

    public bool IsSearchView => Search is not null;
    public IBrush? HighlightBrush =>
        Search is null
            ? new SolidColorBrush(Colors.WhiteSmoke)
            : new SolidColorBrush(Color.Parse(Search.Color));
    public bool ContextVisible => ShowContext && _file.Model.SelectedLine is not null;
    public GridLength ContextRowHeight => ContextVisible ? new(1, GridUnitType.Star) : new(0);
    public GridLength ContextSplitterHeight => ContextVisible ? new(4) : new(0);
    public bool ContextEmpty => ContextLines.Count == 0;
    public bool ContextEmptyVisible => ContextVisible && ContextEmpty;
    public bool IsFollowing
    {
        get => Search is null ? _file.Model.FollowAll : IsSearchFollow();
        set
        {
            if (IsFollowing == value)
                return;
            if (_lastSnapshot is { } snapshot)
                _lastSnapshot = snapshot with { IsFollowing = value };
            if (Search is null)
                _ = _owner.SetFollowAllAsync(_file, value);
            else
                _ = _file.SetSearchFollowAsync(Search, value);
            if (!value)
                _seenAnchor = DisplayedTail;
            this.RaisePropertyChanged(nameof(IsFollowing));
        }
    }

    private void JumpToLatest()
    {
        _owner.IsPaused = false;
        IsFollowing = true;
        Sync();
    }

    private Line? DisplayedTail => Lines.Count > 0 ? Lines[^1].Line : null;

    private void UpdateUnseen(IReadOnlyList<Line> lines, bool frozen)
    {
        var count = 0;
        if (!frozen && IsFollowing)
            _seenAnchor = lines.Count > 0 ? lines[^1] : null;
        else
        {
            _seenAnchor ??= DisplayedTail;
            var anchorSeen = _seenAnchor is null;
            var index = lines.Count - 1;
            for (; index >= 0 && count < UnseenScanLimit; index--)
            {
                if (ReferenceEquals(lines[index], _seenAnchor))
                {
                    anchorSeen = true;
                    break;
                }
                count++;
            }
            // The anchor left the list (exclusion change, eviction, elastic re-apply): restart from the tail.
            // Trade-off: lines that arrived after the anchor was removed are not counted; the count restarts from the current tail.
            if (!anchorSeen && index < 0)
            {
                _seenAnchor = lines.Count > 0 ? lines[^1] : null;
                count = 0;
            }
        }
        if (count == _unseenCount)
            return;
        _unseenCount = count;
        this.RaisePropertyChanged(nameof(UnseenCount));
        this.RaisePropertyChanged(nameof(HasUnseen));
        this.RaisePropertyChanged(nameof(UnseenLabel));
    }

    public void Sync(bool resetItems = false)
    {
        var settings = Settings;
        var settingsChanged = !ReferenceEquals(_lastSettings, settings);
        var expandedChanged = _lastExpandedLine != _file.Model.ExpandedLine;
        var viewsChanged = _lastViewsVersion != _file.ViewsVersion;
        var rulesChanged = _lastRulesVersion != _file.Model.RulesVersion;
        _lastRulesVersion = _file.Model.RulesVersion;
        _lastSettings = settings;
        _lastExpandedLine = _file.Model.ExpandedLine;
        _lastViewsVersion = _file.ViewsVersion;
        if (expandedChanged)
        {
            SyncExpansion(Lines);
            SyncExpansion(ContextLines);
        }
        if (viewsChanged || rulesChanged)
        {
            InvalidateRows(Lines);
            InvalidateRows(ContextLines);
        }
        var lines = LinesFor();
        var frozen = _owner.IsPaused && Lines.Count > 0;
        if (!frozen)
        {
            // A settings change seen while frozen still has to reach the retained rows once they sync.
            SyncRows(
                Lines,
                lines,
                isContext: false,
                resetItems,
                settingsChanged || _rowRefreshPending
            );
            _rowRefreshPending = false;
        }
        else
            _rowRefreshPending |= settingsChanged;
        UpdateUnseen(lines, frozen);

        var context = _file.Model.ShowContext ? ContextLinesFor() : [];
        SyncRows(ContextLines, context, isContext: true, resetItems, settingsChanged);

        var snapshot = new ViewSnapshot(
            MatchSummary,
            ShowContext,
            ContextVisible,
            ContextRowHeight,
            ContextSplitterHeight,
            ContextEmpty,
            ContextEmptyVisible,
            IsFollowing,
            ResultCount,
            ResultMessage,
            InitialLimitMessage
        );
        var previous = _lastSnapshot;
        _lastSnapshot = snapshot;
        if (previous is null || previous.Value.MatchSummary != snapshot.MatchSummary)
            this.RaisePropertyChanged(nameof(MatchSummary));
        if (previous is null || previous.Value.ShowContext != snapshot.ShowContext)
            this.RaisePropertyChanged(nameof(ShowContext));
        if (previous is null || previous.Value.ContextVisible != snapshot.ContextVisible)
            this.RaisePropertyChanged(nameof(ContextVisible));
        if (previous is null || previous.Value.ContextRowHeight != snapshot.ContextRowHeight)
            this.RaisePropertyChanged(nameof(ContextRowHeight));
        if (
            previous is null
            || previous.Value.ContextSplitterHeight != snapshot.ContextSplitterHeight
        )
            this.RaisePropertyChanged(nameof(ContextSplitterHeight));
        if (previous is null || previous.Value.ContextEmpty != snapshot.ContextEmpty)
            this.RaisePropertyChanged(nameof(ContextEmpty));
        if (previous is null || previous.Value.ContextEmptyVisible != snapshot.ContextEmptyVisible)
            this.RaisePropertyChanged(nameof(ContextEmptyVisible));
        if (previous is null || previous.Value.IsFollowing != snapshot.IsFollowing)
            this.RaisePropertyChanged(nameof(IsFollowing));
        if (previous is null || previous.Value.ResultCount != snapshot.ResultCount)
            this.RaisePropertyChanged(nameof(ResultCount));
        if (previous is null || previous.Value.ResultMessage != snapshot.ResultMessage)
        {
            this.RaisePropertyChanged(nameof(ResultMessage));
            this.RaisePropertyChanged(nameof(HasResultMessage));
        }
        if (previous is null || previous.Value.InitialLimitMessage != snapshot.InitialLimitMessage)
        {
            this.RaisePropertyChanged(nameof(InitialLimitMessage));
            this.RaisePropertyChanged(nameof(HasInitialLimitMessage));
        }
    }

    private static void InvalidateRows(AvaloniaList<LogLineViewModel> rows)
    {
        foreach (var row in rows)
            row.InvalidateRender();
    }

    private static void SyncExpansion(AvaloniaList<LogLineViewModel> rows)
    {
        foreach (var row in rows)
            row.SyncExpansion();
    }

    private void SyncRows(
        AvaloniaList<LogLineViewModel> current,
        IReadOnlyList<Line> desired,
        bool isContext,
        bool resetItems,
        bool refreshRows
    )
    {
        if (
            !resetItems
            && !refreshRows
            && current.Count == desired.Count
            && (
                current.Count == 0
                || ReferenceEquals(current[current.Count - 1].Line, desired[desired.Count - 1])
            )
        )
            return;

        if (!resetItems && current.Count > 0 && desired.Count > current.Count)
        {
            var lastLine = current[^1].Line;
            var lastIndex = desired.Count - 1;
            while (lastIndex >= 0 && !ReferenceEquals(desired[lastIndex], lastLine))
                lastIndex--;

            if (lastIndex >= 0 && lastIndex < desired.Count - 1)
            {
                if (refreshRows)
                    foreach (var row in current)
                        row.Refresh();
                current.AddRange(
                    desired
                        .Skip(lastIndex + 1)
                        .Select(line => new LogLineViewModel(this, _file, line, isContext))
                );
                return;
            }
        }

        if (!resetItems && current.Count > 0 && desired.Count > 0)
        {
            var retainedStart = 0;
            while (
                retainedStart < current.Count
                && !ReferenceEquals(current[retainedStart].Line, desired[0])
            )
                retainedStart++;

            var retainedCount = current.Count - retainedStart;
            var isHeadRollover =
                retainedStart > 0 && retainedCount > 0 && retainedCount <= desired.Count;
            for (var index = 0; isHeadRollover && index < retainedCount; index++)
                isHeadRollover = ReferenceEquals(
                    current[retainedStart + index].Line,
                    desired[index]
                );
            if (isHeadRollover)
            {
                current.RemoveRange(0, retainedStart);
                if (refreshRows)
                    foreach (var row in current)
                        row.Refresh();
                current.AddRange(
                    desired
                        .Skip(retainedCount)
                        .Select(line => new LogLineViewModel(this, _file, line, isContext))
                );
                return;
            }
        }

        Dictionary<Line, LogLineViewModel>? existing =
            current.Count == 0
                ? null
                : new Dictionary<Line, LogLineViewModel>(ReferenceEqualityComparer.Instance);
        if (existing is not null)
            foreach (var row in current)
                existing[row.Line] = row;
        var rows = desired
            .Select(line =>
            {
                var row =
                    existing is not null && existing.TryGetValue(line, out var retained)
                        ? retained
                        : null;
                if (row is null)
                    row = new LogLineViewModel(this, _file, line, isContext);
                else if (refreshRows)
                    row.Refresh();
                return row;
            })
            .ToList();
        SyncCollection(current, rows, resetItems);
    }

    public static void SyncCollection<T>(
        AvaloniaList<T> current,
        IReadOnlyList<T> desired,
        bool resetItems = false
    )
    {
        if (resetItems)
        {
            current.Clear();
            current.AddRange(desired);
            return;
        }

        if (
            current.Count == desired.Count
            && (
                current.Count == 0
                || ReferenceEquals(current[current.Count - 1], desired[desired.Count - 1])
            )
        )
            return;

        if (
            current.Count < desired.Count
            && (
                current.Count == 0
                || ReferenceEquals(current[current.Count - 1], desired[current.Count - 1])
            )
        )
        {
            current.AddRange(desired.Skip(current.Count));
            return;
        }

        if (current.Count > 0 && desired.Count > 0)
        {
            var retainedStart = 0;
            while (
                retainedStart < current.Count
                && !ReferenceEquals(current[retainedStart], desired[0])
            )
                retainedStart++;

            var retainedCount = current.Count - retainedStart;
            var isHeadRollover =
                retainedStart > 0 && retainedCount > 0 && retainedCount <= desired.Count;
            for (var index = 0; isHeadRollover && index < retainedCount; index++)
                isHeadRollover = ReferenceEquals(current[retainedStart + index], desired[index]);
            if (isHeadRollover)
            {
                current.RemoveRange(0, retainedStart);
                current.AddRange(desired.Skip(retainedCount));
                return;
            }
        }

        var common = 0;
        while (
            common < current.Count
            && common < desired.Count
            && ReferenceEquals(current[common], desired[common])
        )
            common++;

        if (common < current.Count / 2)
        {
            current.Clear();
            common = 0;
        }
        else
        {
            while (current.Count > common)
                current.RemoveAt(current.Count - 1);
        }

        current.AddRange(desired.Skip(common));
    }

    private bool IsSearchFollow()
    {
        var index = _file.Model.Searches.IndexOf(Search!);
        return index >= 0
            && index < _file.Model.FollowSearches.Count
            && _file.Model.FollowSearches[index];
    }

    private IReadOnlyList<Line> LinesFor()
    {
        IEnumerable<Line> lines = Search is null
            ? _file.Model.Buffer.Lines
            : Search
                .Results.Where(index => index >= 0 && index < _file.Model.Buffer.Count)
                .Select(index => _file.Model.Buffer[index]);
        return ApplyFilters(lines);
    }

    private IReadOnlyList<Line> ContextLinesFor() => ApplyFilters(_file.Model.Buffer.Lines);

    private IReadOnlyList<Line> ApplyFilters(IEnumerable<Line> lines)
    {
        var rules = _file.Model.EffectiveRules(_owner.State.Settings);
        var levels = _file.Model.Levels;
        var filterLevels = levels != LogLevels.All;
        if (!rules.HasVisibilityRules && !filterLevels)
            return lines as IReadOnlyList<Line> ?? lines.ToList();
        var result = lines;
        if (rules.HasVisibilityRules)
            result = result.Where(rules.IsVisible);
        if (filterLevels)
            result = result.Where(line => LevelDetector.Allows(levels, line));
        return result.ToList();
    }

    private static string Truncate(string value) => value.Length > 24 ? $"{value[..21]}..." : value;

    private readonly record struct ViewSnapshot(
        string MatchSummary,
        bool ShowContext,
        bool ContextVisible,
        GridLength ContextRowHeight,
        GridLength ContextSplitterHeight,
        bool ContextEmpty,
        bool ContextEmptyVisible,
        bool IsFollowing,
        string ResultCount,
        string ResultMessage,
        string InitialLimitMessage
    );
}
