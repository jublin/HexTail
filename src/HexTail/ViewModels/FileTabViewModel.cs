using System.Collections.ObjectModel;
using Avalonia.Automation;
using Avalonia.Media;
using HexTail.Application;
using HexTail.Domain;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal sealed class FileTabViewModel : ReactiveObject
{
    private readonly MainWindowViewModel _owner;
    private int _selectedViewIndex;
    private int _viewsVersion;
    private FileSnapshot? _lastSnapshot;

    internal FileTabViewModel(MainWindowViewModel owner, FileTabState model, bool loadRows = true)
    {
        _owner = owner;
        Model = model;
        SyncViews(loadRows);
    }

    public MainWindowViewModel Workspace => _owner;
    public FileTabState Model { get; }
    public string DisplayName => Model.DisplayName;
    public string Path => Model.Path;
    public string? Error => Model.Error;
    public bool IsSelected => ReferenceEquals(_owner.SelectedFile, this);

    public IBrush SelectedTabBrush =>
        IsSelected ? ThemeManager.Brush("SelectedTabBrush") : ThemeManager.Brush("MutedBrush");
    public ObservableCollection<LogViewViewModel> Views { get; } = [];
    public bool FollowAll
    {
        get => Model.FollowAll;
        set
        {
            if (Model.FollowAll == value)
                return;
            Model.FollowAll = value;
            if (_lastSnapshot is { } snapshot)
                _lastSnapshot = snapshot with { FollowAll = value };
            _ = _owner.SetFollowAllAsync(this, value);
            this.RaisePropertyChanged(nameof(FollowAll));
        }
    }

    public bool ShowContext
    {
        get => Model.ShowContext;
        set
        {
            if (Model.ShowContext == value)
                return;
            Model.ShowContext = value;
            if (_lastSnapshot is { } snapshot)
                _lastSnapshot = snapshot with { ShowContext = value };
            _ = _owner.SetShowContextAsync(this, value);
            this.RaisePropertyChanged(nameof(ShowContext));
        }
    }

    public int SelectedViewIndex
    {
        get => _selectedViewIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedViewIndex, Math.Max(0, value));
    }

    public LogViewViewModel SelectedView => Views[SelectedViewIndex];

    public string SearchCount => $"{Model.Searches.Count:N0} search(es)";
    internal int ViewsVersion => _viewsVersion;

    internal void SyncViews(bool loadRows = true)
    {
        var topologyChanged = !HasCurrentViews();

        if (topologyChanged)
        {
            if (Views.Count == 0)
                Views.Add(new LogViewViewModel(_owner, this, null));

            for (var index = Views.Count - 1; index >= 1; index--)
            {
                var search = Views[index].Search;
                if (search is null || !Model.Searches.Contains(search))
                    Views.RemoveAt(index);
            }

            for (var index = 0; index < Model.Searches.Count; index++)
            {
                var search = Model.Searches[index];
                var targetIndex = index + 1;
                var currentIndex = -1;
                for (var candidate = 1; candidate < Views.Count; candidate++)
                {
                    if (!ReferenceEquals(Views[candidate].Search, search))
                        continue;
                    currentIndex = candidate;
                    break;
                }

                if (currentIndex < 0)
                    Views.Insert(targetIndex, new LogViewViewModel(_owner, this, search));
                else if (currentIndex != targetIndex)
                    Views.Move(currentIndex, targetIndex);
            }

            SelectedViewIndex = Math.Clamp(SelectedViewIndex, 0, Math.Max(0, Views.Count - 1));
            _viewsVersion++;
        }

        if (loadRows && Views.Count > 0)
            Views[SelectedViewIndex].Sync();

        if (!loadRows)
            return;

        var snapshot = new FileSnapshot(
            FollowAll,
            ShowContext,
            DisplayName,
            Error,
            Model.Searches.Count
        );
        var previous = _lastSnapshot;
        _lastSnapshot = snapshot;
        if (previous is null || previous.Value.FollowAll != snapshot.FollowAll)
            this.RaisePropertyChanged(nameof(FollowAll));
        if (previous is null || previous.Value.ShowContext != snapshot.ShowContext)
            this.RaisePropertyChanged(nameof(ShowContext));
        if (previous is null || previous.Value.DisplayName != snapshot.DisplayName)
            this.RaisePropertyChanged(nameof(DisplayName));
        if (previous is null || previous.Value.Error != snapshot.Error)
            this.RaisePropertyChanged(nameof(Error));
        if (previous is null || previous.Value.SearchCount != snapshot.SearchCount)
            this.RaisePropertyChanged(nameof(SearchCount));
    }

    private bool HasCurrentViews()
    {
        if (Views.Count != Model.Searches.Count + 1 || Views.Count == 0)
            return false;
        if (Views[0].Search is not null)
            return false;
        for (var index = 0; index < Model.Searches.Count; index++)
            if (!ReferenceEquals(Views[index + 1].Search, Model.Searches[index]))
                return false;
        return true;
    }

    internal void SelectLine(Line line) => _owner.SelectLine(this, line);

    internal void ToggleExpanded(Line line) => _owner.ToggleExpanded(this, line);

    internal void RaiseSelectionChanged()
    {
        this.RaisePropertyChanged(nameof(IsSelected));
        this.RaisePropertyChanged(nameof(SelectedTabBrush));
    }

    internal Task SetSearchFollowAsync(Search search, bool value)
    {
        var index = Model.Searches.IndexOf(search);
        if (index >= 0 && index < Model.FollowSearches.Count)
            Model.FollowSearches[index] = value;
        return _owner.SetSearchFollowAsync(this, search, value);
    }

    private readonly record struct FileSnapshot(
        bool FollowAll,
        bool ShowContext,
        string DisplayName,
        string? Error,
        int SearchCount
    );
}
