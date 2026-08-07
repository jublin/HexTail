using System.Collections.ObjectModel;
using HexTailSharp.Application;
using HexTailSharp.Domain;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTailSharp.ViewModels;

internal sealed class FileTabViewModel : ReactiveObject
{
    private readonly MainWindowViewModel _owner;
    private int _selectedViewIndex;
    private int _searchCount;

    internal FileTabViewModel(MainWindowViewModel owner, FileTabState model)
    {
        _owner = owner;
        Model = model;
        SyncViews();
    }

    public MainWindowViewModel Workspace => _owner;
    public FileTabState Model { get; }
    public string DisplayName => Model.DisplayName;
    public string Path => Model.Path;
    public string? Error => Model.Error;
    public bool IsSelected => ReferenceEquals(_owner.SelectedFile, this);
    public ObservableCollection<LogViewViewModel> Views { get; } = [];
    public bool FollowAll
    {
        get => Model.FollowAll;
        set
        {
            if (Model.FollowAll == value)
                return;
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
            _ = _owner.SetShowContextAsync(this, value);
            this.RaisePropertyChanged(nameof(ShowContext));
        }
    }

    public int SelectedViewIndex
    {
        get => _selectedViewIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedViewIndex, Math.Max(0, value));
    }

    public string SearchCount => $"{Model.Searches.Count:N0} search(es)";

    internal void SyncViews()
    {
        var topologyChanged =
            _searchCount != Model.Searches.Count || Views.Count != Model.Searches.Count + 1;

        if (topologyChanged)
        {
            Views.Clear();
            Views.Add(new LogViewViewModel(_owner, this, null));
            foreach (var search in Model.Searches)
                Views.Add(new LogViewViewModel(_owner, this, search));
            _searchCount = Model.Searches.Count;
            SelectedViewIndex = Math.Clamp(SelectedViewIndex, 0, Math.Max(0, Views.Count - 1));
        }

        foreach (var view in Views)
            view.Sync(topologyChanged);

        this.RaisePropertyChanged(nameof(FollowAll));
        this.RaisePropertyChanged(nameof(ShowContext));
        this.RaisePropertyChanged(nameof(DisplayName));
        this.RaisePropertyChanged(nameof(Error));
        this.RaisePropertyChanged(nameof(SearchCount));
    }

    internal void SelectLine(Line line) => _owner.SelectLine(this, line);

    internal void ToggleExpanded(Line line) => _owner.ToggleExpanded(this, line);

    internal void RaiseSelectionChanged() => this.RaisePropertyChanged(nameof(IsSelected));

    internal Task SetSearchFollowAsync(Search search, bool value) =>
        _owner.SetSearchFollowAsync(this, search, value);
}
