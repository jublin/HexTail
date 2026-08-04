using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using AtomUI.Controls;
using AtomUI.Desktop.Controls;
using AtomUI.Theme;
using Avalonia;
using Avalonia.Media;
using HexTailSharp.Application;
using HexTailSharp.Domain;
using HexTailSharp.Persistence;
using ReactiveUI;

namespace HexTailSharp.ViewModels;

internal sealed class MainWindowViewModel : ReactiveObject, IAsyncDisposable
{
    private readonly AppState _state;
    private readonly string[] _startupPaths;
    private readonly IScheduler _scheduler;
    private readonly CompositeDisposable _subscriptions = new();
    private FileTabViewModel? _selectedFile;
    private string _query = string.Empty;
    private MatchMode _matchMode = MatchMode.Literal;
    private bool _caseSensitive;
    private Color _searchColor = Color.Parse("#F59E0B");
    private bool _settingsOpen;
    private int _selectedViewIndex;
    private string? _fileError;
    private string? _searchError;
    private bool _started;
    private bool _closed;

    public MainWindowViewModel(
        AppState state,
        IEnumerable<string>? startupPaths = null,
        IScheduler? scheduler = null
    )
    {
        _state = state;
        _startupPaths = startupPaths?.ToArray() ?? [];
        _scheduler = scheduler ?? RxSchedulers.MainThreadScheduler;
        Settings = new SettingsViewModel(this);
        PickFiles = new Interaction<Unit, IReadOnlyList<string>>();

        OpenCommand = ReactiveCommand.CreateFromTask(OpenFilesAsync);
        OpenPathsCommand = ReactiveCommand.CreateFromTask<IEnumerable<string>>(OpenPathsAsync);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);
        ToggleSettingsCommand = ReactiveCommand.Create(() =>
        {
            SettingsOpen = !SettingsOpen;
        });
        SelectFileCommand = ReactiveCommand.Create<FileTabViewModel>(SelectFile);
        CloseFileCommand = ReactiveCommand.CreateFromTask<FileTabViewModel>(CloseFileAsync);

        var canAddSearch = this.WhenAnyValue(
            viewModel => viewModel.Query,
            viewModel => viewModel.SelectedFile,
            (query, file) => !string.IsNullOrWhiteSpace(query) && file is not null
        );
        AddSearchCommand = ReactiveCommand.CreateFromTask(AddSearchAsync, canAddSearch);

        _subscriptions.Add(
            Observable
                .Create<Unit>(observer =>
                {
                    void Changed() => observer.OnNext(Unit.Default);
                    _state.Changed += Changed;
                    return Disposable.Create(() => _state.Changed -= Changed);
                })
                .ObserveOn(_scheduler)
                .Subscribe(_ => SyncFromState())
        );

        _subscriptions.Add(OpenCommand.ThrownExceptions.Subscribe(ex => SetFileError(ex.Message)));
        _subscriptions.Add(
            OpenPathsCommand.ThrownExceptions.Subscribe(ex => SetFileError(ex.Message))
        );
        _subscriptions.Add(SaveCommand.ThrownExceptions.Subscribe(ex => SetFileError(ex.Message)));
    }

    public AppState State => _state;
    public ObservableCollection<FileTabViewModel> Files { get; } = [];
    public SettingsViewModel Settings { get; }
    public Interaction<Unit, IReadOnlyList<string>> PickFiles { get; }
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<IEnumerable<string>, Unit> OpenPathsCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleSettingsCommand { get; }
    public ReactiveCommand<FileTabViewModel, Unit> SelectFileCommand { get; }
    public ReactiveCommand<FileTabViewModel, Unit> CloseFileCommand { get; }
    public ReactiveCommand<Unit, Unit> AddSearchCommand { get; }

    public FileTabViewModel? SelectedFile
    {
        get => _selectedFile;
        private set => this.RaiseAndSetIfChanged(ref _selectedFile, value);
    }

    public bool HasFile => SelectedFile is not null;
    public bool ShowEmpty => !HasFile;
    public int FileCount => Files.Count;
    public int LineCount => SelectedFile?.Model.Buffer.Count ?? 0;

    public string? FileError
    {
        get => _fileError ?? SelectedFile?.Model.Error;
        private set
        {
            this.RaiseAndSetIfChanged(ref _fileError, value);
            this.RaisePropertyChanged(nameof(HasFileError));
        }
    }

    public bool HasFileError => !string.IsNullOrWhiteSpace(FileError);

    public string? SearchError
    {
        get => _searchError;
        private set
        {
            this.RaiseAndSetIfChanged(ref _searchError, value);
            this.RaisePropertyChanged(nameof(HasSearchError));
        }
    }

    public bool HasSearchError => !string.IsNullOrWhiteSpace(SearchError);

    public string Query
    {
        get => _query;
        set => this.RaiseAndSetIfChanged(ref _query, value);
    }

    public MatchMode MatchMode
    {
        get => _matchMode;
        set => this.RaiseAndSetIfChanged(ref _matchMode, value);
    }

    public IReadOnlyList<MatchMode> MatchModes { get; } = [MatchMode.Literal, MatchMode.Regex];

    public bool CaseSensitive
    {
        get => _caseSensitive;
        set => this.RaiseAndSetIfChanged(ref _caseSensitive, value);
    }

    public Color SearchColor
    {
        get => _searchColor;
        set => this.RaiseAndSetIfChanged(ref _searchColor, value);
    }

    public bool SettingsOpen
    {
        get => _settingsOpen;
        set => this.RaiseAndSetIfChanged(ref _settingsOpen, value);
    }

    public DrawerPlacement SettingsPlacement =>
        _state.Settings.SettingsMenuAlignment == SettingsMenuAlignment.Left
            ? DrawerPlacement.Left
            : DrawerPlacement.Right;

    public int SelectedViewIndex
    {
        get => _selectedViewIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedViewIndex, Math.Max(0, value));
    }

    public async Task InitializeAsync()
    {
        if (_started)
            return;

        _started = true;
        try
        {
            await _state.RestoreAsync();
            await ApplyThemeAsync(_state.Settings.Theme);
            foreach (var path in _startupPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
                await TryOpenPathAsync(path);

            SyncFromState();
            _subscriptions.Add(
                Observable
                    .Interval(TimeSpan.FromMilliseconds(100), _scheduler)
                    .Subscribe(_ => DrainTailer())
            );
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetFileError(ex.Message);
            SyncFromState();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed)
            return;

        _closed = true;
        _subscriptions.Dispose();
        await _state.DisposeAsync();
    }

    internal async Task UpdateSettingsAsync(AppSettings settings)
    {
        try
        {
            await _state.UpdateSettingsAsync(settings);
            if (Avalonia.Application.Current is not null)
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    ApplyThemeAsync(settings.Theme)
                );
        }
        catch (Exception ex)
        {
            SetFileError(ex.Message);
        }
    }

    internal void SelectLine(FileTabViewModel file, Line line) =>
        _state.SelectLine(file.Model, line);

    internal void ToggleExpanded(FileTabViewModel file, Line line) =>
        _state.ToggleExpanded(file.Model, line);

    internal async Task SetFollowAllAsync(FileTabViewModel file, bool value)
    {
        try
        {
            await _state.SetFollowAllAsync(file.Model, value);
        }
        catch (Exception ex)
        {
            SetFileError(ex.Message);
        }
    }

    internal async Task SetShowContextAsync(FileTabViewModel file, bool value)
    {
        try
        {
            await _state.SetShowContextAsync(file.Model, value);
        }
        catch (Exception ex)
        {
            SetFileError(ex.Message);
        }
    }

    internal async Task SetSearchFollowAsync(FileTabViewModel file, Search search, bool value)
    {
        try
        {
            await _state.SetSearchFollowAsync(file.Model, search, value);
        }
        catch (Exception ex)
        {
            SetFileError(ex.Message);
        }
    }

    internal void SetFileError(string? message)
    {
        FileError = string.IsNullOrWhiteSpace(message) ? null : message;
    }

    internal void ClearSearchError() => SearchError = null;

    private async Task OpenFilesAsync()
    {
        var paths = await PickFiles.Handle(Unit.Default);
        await OpenPathsAsync(paths);
    }

    private async Task OpenPathsAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
            await TryOpenPathAsync(path);
        SyncFromState();
    }

    private async Task TryOpenPathAsync(string path)
    {
        try
        {
            await _state.OpenFileAsync(path);
            FileError = null;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetFileError($"Could not open {path}: {ex.Message}");
        }
    }

    internal async Task CloseFileAsync(FileTabViewModel file)
    {
        try
        {
            await _state.CloseFileAsync(file.Model);
            SyncFromState();
        }
        catch (Exception ex)
        {
            SetFileError(ex.Message);
        }
    }

    private async Task SaveAsync()
    {
        await _state.SaveAsync();
        SetFileError(null);
    }

    private async Task AddSearchAsync()
    {
        if (SelectedFile is null || string.IsNullOrWhiteSpace(Query))
            return;

        try
        {
            _state.AddSearch(
                SelectedFile.Model,
                Query,
                MatchMode,
                CaseSensitive,
                ColorToHex(SearchColor)
            );
            Query = string.Empty;
            SearchError = null;
            await _state.SaveAsync();
            SyncFromState();
        }
        catch (ArgumentException ex)
        {
            SearchError = ex.Message;
        }
    }

    internal void SelectFile(FileTabViewModel file)
    {
        _state.SelectFile(file.Model);
        SelectedViewIndex = 0;
        SyncFromState();
    }

    private void DrainTailer()
    {
        if (_closed || !_state.DrainTailerEvents())
            return;

        SyncFromState();
        SelectedFile?.SyncViews();
    }

    private void SyncFromState()
    {
        if (_closed)
            return;

        for (var index = Files.Count - 1; index >= 0; index--)
        {
            if (_state.Files.Contains(Files[index].Model))
                continue;
            Files.RemoveAt(index);
        }

        foreach (var file in _state.Files)
        {
            var viewModel = Files.FirstOrDefault(item => ReferenceEquals(item.Model, file));
            if (viewModel is null)
            {
                viewModel = new FileTabViewModel(this, file);
                Files.Add(viewModel);
            }
            viewModel.SyncViews();
        }

        SelectedFile = _state.SelectedFile is null
            ? null
            : Files.FirstOrDefault(item => ReferenceEquals(item.Model, _state.SelectedFile));
        Settings.Sync(_state.Settings);
        this.RaisePropertyChanged(nameof(FileCount));
        this.RaisePropertyChanged(nameof(HasFile));
        this.RaisePropertyChanged(nameof(ShowEmpty));
        this.RaisePropertyChanged(nameof(LineCount));
        this.RaisePropertyChanged(nameof(FileError));
        this.RaisePropertyChanged(nameof(HasFileError));
        this.RaisePropertyChanged(nameof(HasSearchError));
        this.RaisePropertyChanged(nameof(SettingsPlacement));
    }

    private async Task ApplyThemeAsync(string theme)
    {
        if (Avalonia.Application.Current is not { } application)
            return;

        var manager = application.GetThemeManager();
        if (manager is not null)
        {
            var result = await manager.ApplyThemeAsync(
                new ThemeRequest(
                    IThemeManager.DEFAULT_THEME_ID,
                    null,
                    ThemeTransitionReason.UserRequest
                )
            );
            if (result.Status == ThemeTransitionStatus.Failed)
                throw new InvalidOperationException("AtomUI could not apply the selected theme.");
        }

        application.RequestedThemeVariant = theme switch
        {
            "light" => Avalonia.Styling.ThemeVariant.Light,
            "dark" => Avalonia.Styling.ThemeVariant.Dark,
            _ => null,
        };
        var light = application.ActualThemeVariant != Avalonia.Styling.ThemeVariant.Dark;
        application.Resources["SurfaceBrush"] = new SolidColorBrush(
            Color.Parse(light ? "#F8FAFC" : "#111827")
        );
        application.Resources["RaisedSurfaceBrush"] = new SolidColorBrush(
            Color.Parse(light ? "#F1F5F9" : "#172033")
        );
        application.Resources["ToolbarBrush"] = new SolidColorBrush(
            Color.Parse(light ? "#FFFFFF" : "#1F2937")
        );
        application.Resources["BorderBrush"] = new SolidColorBrush(
            Color.Parse(light ? "#CBD5E1" : "#334155")
        );
        application.Resources["TextBrush"] = new SolidColorBrush(
            Color.Parse(light ? "#1E293B" : "#E2E8F0")
        );
        application.Resources["MutedTextBrush"] = new SolidColorBrush(
            Color.Parse(light ? "#475569" : "#94A3B8")
        );
    }

    private static string ColorToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}

internal sealed class FileTabViewModel : ReactiveObject
{
    private readonly MainWindowViewModel _owner;
    private int _selectedViewIndex;
    private int _searchCount;

    internal FileTabViewModel(MainWindowViewModel owner, FileTabState model)
    {
        _owner = owner;
        Model = model;
        SelectCommand = ReactiveCommand.Create(() => _owner.SelectFile(this));
        CloseCommand = ReactiveCommand.CreateFromTask(() => _owner.CloseFileAsync(this));
        SyncViews();
    }

    public MainWindowViewModel Workspace => _owner;
    public FileTabState Model { get; }
    public string DisplayName => Model.DisplayName;
    public string Path => Model.Path;
    public string? Error => Model.Error;
    public ObservableCollection<LogViewViewModel> Views { get; } = [];
    public ReactiveCommand<Unit, Unit> SelectCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

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

    internal Task SetSearchFollowAsync(Search search, bool value) =>
        _owner.SetSearchFollowAsync(this, search, value);
}

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
    public ObservableCollection<Line> Lines { get; } = [];
    public ObservableCollection<Line> ContextLines { get; } = [];
    public ReactiveCommand<Line, Unit> SelectLineCommand { get; }
    public ReactiveCommand<Line, Unit> ToggleExpandedCommand { get; }
    public bool ShowContext => _file.Model.ShowContext;
    public bool ContextEmpty => ContextLines.Count == 0;
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
        SyncCollection(Lines, lines, resetItems);

        var context = _file.Model.ShowContext ? ContextLinesFor() : [];
        SyncCollection(ContextLines, context, resetItems);

        this.RaisePropertyChanged(nameof(Header));
        this.RaisePropertyChanged(nameof(MatchSummary));
        this.RaisePropertyChanged(nameof(ShowContext));
        this.RaisePropertyChanged(nameof(ContextEmpty));
        this.RaisePropertyChanged(nameof(IsFollowing));
    }

    public static void SyncCollection(
        ObservableCollection<Line> current,
        IReadOnlyList<Line> desired,
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

internal sealed class SettingsViewModel : ReactiveObject
{
    private readonly MainWindowViewModel _owner;
    private string _theme = "dark";
    private UiDensity _density;
    private LogFontSize _fontSize;
    private SettingsMenuAlignment _menuAlignment;
    private string _newLabelText = string.Empty;
    private Color _newLabelColor = Color.Parse("#F59E0B");
    private string _newExclusionText = string.Empty;
    private string _section = "labels";
    private int _sectionIndex;
    private bool _syncing;

    internal SettingsViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
        AddLabelCommand = ReactiveCommand.Create(AddLabel);
        RemoveLabelCommand = ReactiveCommand.Create<LabelSettingViewModel>(RemoveLabel);
        AddExclusionCommand = ReactiveCommand.Create(AddExclusion);
        RemoveExclusionCommand = ReactiveCommand.Create<ExclusionSettingViewModel>(RemoveExclusion);
    }

    public ObservableCollection<LabelSettingViewModel> Labels { get; } = [];
    public ObservableCollection<ExclusionSettingViewModel> Exclusions { get; } = [];
    public IReadOnlyList<string> ThemeOptions { get; } = ThemeCatalog.Names;
    public IReadOnlyList<UiDensity> DensityOptions { get; } =
    [UiDensity.Comfortable, UiDensity.Cozy, UiDensity.Compact];
    public IReadOnlyList<LogFontSize> FontSizeOptions { get; } =
    [LogFontSize.Small, LogFontSize.Medium, LogFontSize.Large, LogFontSize.ExtraLarge];
    public IReadOnlyList<SettingsMenuAlignment> MenuAlignmentOptions { get; } =
    [SettingsMenuAlignment.Left, SettingsMenuAlignment.Right];
    public ReactiveCommand<Unit, Unit> AddLabelCommand { get; }
    public ReactiveCommand<LabelSettingViewModel, Unit> RemoveLabelCommand { get; }
    public ReactiveCommand<Unit, Unit> AddExclusionCommand { get; }
    public ReactiveCommand<ExclusionSettingViewModel, Unit> RemoveExclusionCommand { get; }

    public string Section
    {
        get => _section;
        set => this.RaiseAndSetIfChanged(ref _section, value);
    }

    public int SectionIndex
    {
        get => _sectionIndex;
        set
        {
            var index = Math.Clamp(value, 0, 2);
            if (_sectionIndex == index)
                return;
            this.RaiseAndSetIfChanged(ref _sectionIndex, index);
            _section = index switch
            {
                1 => "exclusions",
                2 => "appearance",
                _ => "labels",
            };
        }
    }

    public string Theme
    {
        get => _theme;
        set
        {
            if (string.Equals(_theme, value, StringComparison.Ordinal))
                return;
            this.RaiseAndSetIfChanged(ref _theme, value);
            if (!_syncing && ThemeCatalog.Contains(value))
                _ = CommitAsync(_owner.State.Settings with { Theme = value });
        }
    }

    public UiDensity Density
    {
        get => _density;
        set
        {
            if (_density == value)
                return;
            this.RaiseAndSetIfChanged(ref _density, value);
            if (!_syncing)
                _ = CommitAsync(_owner.State.Settings with { Density = value });
        }
    }

    public LogFontSize FontSize
    {
        get => _fontSize;
        set
        {
            if (_fontSize == value)
                return;
            this.RaiseAndSetIfChanged(ref _fontSize, value);
            if (!_syncing)
                _ = CommitAsync(_owner.State.Settings with { LogFontSize = value });
        }
    }

    public SettingsMenuAlignment MenuAlignment
    {
        get => _menuAlignment;
        set
        {
            if (_menuAlignment == value)
                return;
            this.RaiseAndSetIfChanged(ref _menuAlignment, value);
            if (!_syncing)
                _ = CommitAsync(_owner.State.Settings with { SettingsMenuAlignment = value });
        }
    }

    public string NewLabelText
    {
        get => _newLabelText;
        set => this.RaiseAndSetIfChanged(ref _newLabelText, value);
    }

    public Color NewLabelColor
    {
        get => _newLabelColor;
        set => this.RaiseAndSetIfChanged(ref _newLabelColor, value);
    }

    public string NewExclusionText
    {
        get => _newExclusionText;
        set => this.RaiseAndSetIfChanged(ref _newExclusionText, value);
    }

    internal void Sync(AppSettings settings)
    {
        _syncing = true;
        try
        {
            Theme = settings.Theme;
            Density = settings.Density;
            FontSize = settings.LogFontSize;
            MenuAlignment = settings.SettingsMenuAlignment;
        }
        finally
        {
            _syncing = false;
        }

        while (Labels.Count > settings.GlobalLabels.Count)
            Labels.RemoveAt(Labels.Count - 1);
        for (var index = 0; index < settings.GlobalLabels.Count; index++)
        {
            if (index == Labels.Count)
                Labels.Add(new LabelSettingViewModel(this, index));
            Labels[index].Sync(settings.GlobalLabels[index]);
        }

        while (Exclusions.Count > settings.GlobalExcludeLabels.Count)
            Exclusions.RemoveAt(Exclusions.Count - 1);
        for (var index = 0; index < settings.GlobalExcludeLabels.Count; index++)
        {
            if (index == Exclusions.Count)
                Exclusions.Add(new ExclusionSettingViewModel(this, index));
            Exclusions[index].Sync(settings.GlobalExcludeLabels[index]);
        }
    }

    internal Task CommitLabelAsync(int index, string text, Color color)
    {
        var labels = _owner.State.Settings.GlobalLabels.ToList();
        if (index < 0 || index >= labels.Count)
            return Task.CompletedTask;
        labels[index] = new GlobalLabel { Text = text, Color = ColorToHex(color) };
        return CommitAsync(_owner.State.Settings with { GlobalLabels = labels });
    }

    internal Task CommitExclusionAsync(int index, string text)
    {
        var exclusions = _owner.State.Settings.GlobalExcludeLabels.ToList();
        if (index < 0 || index >= exclusions.Count)
            return Task.CompletedTask;
        exclusions[index] = text;
        return CommitAsync(_owner.State.Settings with { GlobalExcludeLabels = exclusions });
    }

    private void AddLabel()
    {
        if (string.IsNullOrWhiteSpace(NewLabelText))
            return;
        _ = CommitAsync(
            _owner.State.Settings with
            {
                GlobalLabels =
                [
                    .. _owner.State.Settings.GlobalLabels,
                    new GlobalLabel { Text = NewLabelText, Color = ColorToHex(NewLabelColor) },
                ],
            }
        );
        NewLabelText = string.Empty;
    }

    private void RemoveLabel(LabelSettingViewModel item)
    {
        var labels = _owner
            .State.Settings.GlobalLabels.Where((_, index) => index != item.Index)
            .ToList();
        _ = CommitAsync(_owner.State.Settings with { GlobalLabels = labels });
    }

    private void AddExclusion()
    {
        if (string.IsNullOrWhiteSpace(NewExclusionText))
            return;
        _ = CommitAsync(
            _owner.State.Settings with
            {
                GlobalExcludeLabels =
                [
                    .. _owner.State.Settings.GlobalExcludeLabels,
                    NewExclusionText,
                ],
            }
        );
        NewExclusionText = string.Empty;
    }

    private void RemoveExclusion(ExclusionSettingViewModel item)
    {
        var exclusions = _owner
            .State.Settings.GlobalExcludeLabels.Where((_, index) => index != item.Index)
            .ToList();
        _ = CommitAsync(_owner.State.Settings with { GlobalExcludeLabels = exclusions });
    }

    private Task CommitAsync(AppSettings settings) => _owner.UpdateSettingsAsync(settings);

    private static string ColorToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}

internal sealed class LabelSettingViewModel : ReactiveObject
{
    private readonly SettingsViewModel _owner;
    private string _text = string.Empty;
    private Color _color = Color.Parse("#F59E0B");
    private bool _syncing;

    internal LabelSettingViewModel(SettingsViewModel owner, int index)
    {
        _owner = owner;
        Index = index;
    }

    public int Index { get; }

    public string Text
    {
        get => _text;
        set
        {
            if (string.Equals(_text, value, StringComparison.Ordinal))
                return;
            this.RaiseAndSetIfChanged(ref _text, value);
            if (!_syncing)
                _ = _owner.CommitLabelAsync(Index, value, Color);
        }
    }

    public Color Color
    {
        get => _color;
        set
        {
            if (_color == value)
                return;
            this.RaiseAndSetIfChanged(ref _color, value);
            if (!_syncing)
                _ = _owner.CommitLabelAsync(Index, Text, value);
        }
    }

    internal void Sync(GlobalLabel label)
    {
        _syncing = true;
        try
        {
            Text = label.Text;
            Color = Avalonia.Media.Color.Parse(label.Color);
        }
        finally
        {
            _syncing = false;
        }
    }
}

internal sealed class ExclusionSettingViewModel : ReactiveObject
{
    private readonly SettingsViewModel _owner;
    private string _text = string.Empty;
    private bool _syncing;

    internal ExclusionSettingViewModel(SettingsViewModel owner, int index)
    {
        _owner = owner;
        Index = index;
    }

    public int Index { get; }

    public string Text
    {
        get => _text;
        set
        {
            if (string.Equals(_text, value, StringComparison.Ordinal))
                return;
            this.RaiseAndSetIfChanged(ref _text, value);
            if (!_syncing)
                _ = _owner.CommitExclusionAsync(Index, value);
        }
    }

    internal void Sync(string text)
    {
        _syncing = true;
        try
        {
            Text = text;
        }
        finally
        {
            _syncing = false;
        }
    }
}
