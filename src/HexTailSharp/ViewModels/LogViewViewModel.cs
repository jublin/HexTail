using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using HexTailSharp.Application;
using HexTailSharp.Domain;
using HexTailSharp.Persistence;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTailSharp.ViewModels;

internal sealed class LogViewViewModel : ReactiveObject
{
    private readonly MainWindowViewModel _owner;
    private readonly FileTabViewModel _file;

    internal LogViewViewModel(MainWindowViewModel owner, FileTabViewModel file, Search? search)
    {
        _owner = owner;
        _file = file;
        Search = search;
        SelectLineCommand = ReactiveCommand.Create<Line>(line => _file.SelectLine(line));
        ToggleExpandedCommand = ReactiveCommand.Create<Line>(line => _file.ToggleExpanded(line));
    }

    public Search? Search { get; }
    public FileTabState File => _file.Model;
    public AppSettings Settings => _owner.State.Settings;
    public bool IsAllView => Search is null;
    public string Header => Search is null ? "All" : Truncate(Search.Query.Query);
    public string MatchSummary =>
        Search is null ? string.Empty : $"{Search.Results.Count:N0} matches";

    public override string ToString() => Header;

    public ObservableCollection<LogLineViewModel> Lines { get; } = [];
    public ObservableCollection<LogLineViewModel> ContextLines { get; } = [];
    public ReactiveCommand<Line, Unit> SelectLineCommand { get; }
    public ReactiveCommand<Line, Unit> ToggleExpandedCommand { get; }
    public bool ShowContext => _file.Model.ShowContext;
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
            if (Search is null)
                _ = _owner.SetFollowAllAsync(_file, value);
            else
                _ = _file.SetSearchFollowAsync(Search, value);
            this.RaisePropertyChanged(nameof(IsFollowing));
        }
    }

    public void Sync(bool resetItems = false)
    {
        var lines = LinesFor();
        SyncRows(Lines, lines, isContext: false, resetItems);

        var context = _file.Model.ShowContext ? ContextLinesFor() : [];
        SyncRows(ContextLines, context, isContext: true, resetItems);

        this.RaisePropertyChanged(nameof(Header));
        this.RaisePropertyChanged(nameof(MatchSummary));
        this.RaisePropertyChanged(nameof(ShowContext));
        this.RaisePropertyChanged(nameof(ContextVisible));
        this.RaisePropertyChanged(nameof(ContextRowHeight));
        this.RaisePropertyChanged(nameof(ContextSplitterHeight));
        this.RaisePropertyChanged(nameof(ContextEmpty));
        this.RaisePropertyChanged(nameof(ContextEmptyVisible));
        this.RaisePropertyChanged(nameof(IsFollowing));
    }

    private void SyncRows(
        ObservableCollection<LogLineViewModel> current,
        IReadOnlyList<Line> desired,
        bool isContext,
        bool resetItems
    )
    {
        var rows = desired
            .Select(line =>
            {
                var row = current.FirstOrDefault(candidate =>
                    ReferenceEquals(candidate.Line, line)
                );
                if (row is null)
                    row = new LogLineViewModel(this, _file, line, isContext);
                else
                    row.Refresh();
                return row;
            })
            .ToList();
        SyncCollection(current, rows, resetItems);
    }

    public static void SyncCollection<T>(
        ObservableCollection<T> current,
        IReadOnlyList<T> desired,
        bool resetItems = false
    )
    {
        if (resetItems)
        {
            current.Clear();
            foreach (var line in desired)
                current.Add(line);
            return;
        }

        if (
            current.Count == desired.Count
            && (current.Count == 0 || ReferenceEquals(current[^1], desired[^1]))
        )
            return;

        if (
            current.Count < desired.Count
            && (current.Count == 0 || ReferenceEquals(current[^1], desired[current.Count - 1]))
        )
        {
            for (var index = current.Count; index < desired.Count; index++)
                current.Add(desired[index]);
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
                // ponytail: ObservableCollection has no RemoveRange; use a range-aware
                // collection only if unusually large rollover batches become measurable.
                for (var index = 0; index < retainedStart; index++)
                    current.RemoveAt(0);
                for (var index = retainedCount; index < desired.Count; index++)
                    current.Add(desired[index]);
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

        for (var index = common; index < desired.Count; index++)
            current.Add(desired[index]);
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
        return _owner.State.Settings.GlobalExcludeLabels.Count == 0
            ? lines as IReadOnlyList<Line> ?? lines.ToList()
            : lines.Where(line => !_owner.State.Settings.Excludes(line.Raw)).ToList();
    }

    private IReadOnlyList<Line> ContextLinesFor()
    {
        var lines = _file.Model.ContextLines;
        return _owner.State.Settings.GlobalExcludeLabels.Count == 0
            ? lines
            : lines.Where(line => !_owner.State.Settings.Excludes(line.Raw)).ToList();
    }

    private static string Truncate(string value) => value.Length > 24 ? $"{value[..21]}..." : value;
}
