using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using HexTail;
using HexTail.Application;
using HexTail.Domain;
using HexTail.Persistence;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal sealed class MainWindowViewModel : ReactiveObject, IAsyncDisposable
{
    private static readonly string[] SearchColors =
    [
        "#F59E0B",
        "#22D3EE",
        "#A78BFA",
        "#34D399",
        "#FB7185",
        "#60A5FA",
        "#F97316",
        "#E879F9",
    ];
    internal const double NarrowShellWidth = 1000;
    internal const double MinSidebarWidth = 160;
    internal const double MaxSidebarWidth = 480;
    private readonly AppState _state;
    private readonly string[] _startupPaths;
    private readonly IScheduler _scheduler;
    private bool _isPaused;
    private readonly bool _startPolling;
    private readonly CompositeDisposable _subscriptions = new();
    private FileTabViewModel? _selectedFile;
    private string _query = string.Empty;
    private string _sourceSummary = "No sources open";
    private MatchMode _matchMode = MatchMode.Literal;
    private bool _caseSensitive;
    private Color _searchColor = Color.Parse("#F59E0B");
    private bool _settingsOpen;
    private int _selectedViewIndex;
    private string? _fileError;
    private string? _searchError;
    private bool _started;
    private bool _closed;
    private bool _restoring;
    private int _syncQueued;
    private bool _elasticToNow = true;
    private bool _syncingRange;
    private bool _elasticRangeDirty;
    private string _rangeFromText = "now-5m";
    private string _rangeToText = "now";
    private string? _rangeError;
    private bool _sidebarOpen = true;
    private bool _narrowShell;
    private bool _narrowPaneOpen;
    private double _sidebarWidth = 240;

    public MainWindowViewModel(
        AppState state,
        IEnumerable<string>? startupPaths = null,
        IScheduler? scheduler = null,
        bool startPolling = true
    )
    {
        _state = state;
        _startupPaths = startupPaths?.ToArray() ?? [];
        _scheduler = scheduler ?? RxSchedulers.MainThreadScheduler;
        _startPolling = startPolling;
        Settings = new SettingsViewModel(this, _scheduler);
        PickFiles = new Interaction<Unit, IReadOnlyList<string>>(_scheduler);

        OpenCommand = ReactiveCommand.CreateFromTask(OpenFilesAsync, _scheduler);
        OpenPathsCommand = ReactiveCommand.CreateFromTask<IEnumerable<string>>(
            OpenPathsAsync,
            _scheduler
        );
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, _scheduler);
        ToggleSettingsCommand = ReactiveCommand.Create(
            () =>
            {
                SettingsOpen = !SettingsOpen;
            },
            _scheduler
        );
        SetUpElasticCommand = ReactiveCommand.Create(
            () =>
            {
                Settings.SectionIndex = 2;
                SettingsOpen = true;
            },
            _scheduler
        );
        ToggleSidebarCommand = ReactiveCommand.Create(
            () =>
            {
                SidebarPaneOpen = !SidebarPaneOpen;
            },
            _scheduler
        );
        TogglePauseCommand = ReactiveCommand.Create(
            () =>
            {
                IsPaused = !IsPaused;
            },
            _scheduler
        );
        SelectFileCommand = ReactiveCommand.Create<FileTabViewModel>(SelectFile, _scheduler);
        ApplyElasticTimeRangeCommand = ReactiveCommand.Create(ApplyElasticTimeRange, _scheduler);
        ApplyRangePresetCommand = ReactiveCommand.Create<string>(ApplyRangePreset, _scheduler);
        CloseFileCommand = ReactiveCommand.CreateFromTask<FileTabViewModel>(
            CloseFileAsync,
            _scheduler
        );
        RemoveSearchCommand = ReactiveCommand.CreateFromTask<LogViewViewModel>(
            RemoveSearchAsync,
            _scheduler
        );

        var canAddSearch = this.WhenAnyValue(
            viewModel => viewModel.Query,
            viewModel => viewModel.SelectedFile,
            (query, file) => !string.IsNullOrWhiteSpace(query) && file is not null
        );
        AddSearchCommand = ReactiveCommand.CreateFromTask(AddSearchAsync, canAddSearch, _scheduler);
        AddSearchOnKeyCommand = ReactiveCommand.Create<KeyEventArgs>(
            args =>
            {
                if (args.Key == Key.Enter)
                    AddSearchCommand.Execute().Subscribe();
            },
            _scheduler
        );

        _subscriptions.Add(
            Observable
                .Create<Unit>(observer =>
                {
                    void Changed() => observer.OnNext(Unit.Default);
                    _state.Changed += Changed;
                    return Disposable.Create(() => _state.Changed -= Changed);
                })
                .Subscribe(_ => QueueSyncFromState())
        );
        FileTabs.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(HasFileTabs));

        _subscriptions.Add(
            Observable
                .Merge(
                    OpenCommand.ThrownExceptions,
                    OpenPathsCommand.ThrownExceptions,
                    SaveCommand.ThrownExceptions,
                    CloseFileCommand.ThrownExceptions,
                    RemoveSearchCommand.ThrownExceptions,
                    AddSearchCommand.ThrownExceptions
                )
                .Subscribe(ex => SetFileError(ex.Message))
        );
    }

    public AppState State => _state;
    public ObservableCollection<FileTabViewModel> Files { get; } = [];
    public ObservableCollection<FileTabViewModel> FileTabs { get; } = [];
    public bool HasFileTabs => FileTabs.Count > 0;
    public ObservableCollection<ElasticSourceOptionViewModel> ElasticSources { get; } = [];
    public ObservableCollection<ElasticConnectionGroupViewModel> ElasticConnectionGroups { get; } =
    [];
    internal IScheduler Scheduler => _scheduler;
    public bool HasElasticSources => ElasticSources.Count > 0;
    public bool HasElasticWarning => _state.HasElasticWarning;
    public string ElasticSourceIcon => HasElasticWarning ? "mdi-cloud-alert" : "mdi-cloud-check";
    public SettingsViewModel Settings { get; }
    public Interaction<Unit, IReadOnlyList<string>> PickFiles { get; }
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<IEnumerable<string>, Unit> OpenPathsCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleSidebarCommand { get; }
    public ReactiveCommand<Unit, Unit> TogglePauseCommand { get; }
    public ReactiveCommand<Unit, Unit> SetUpElasticCommand { get; }
    public ReactiveCommand<FileTabViewModel, Unit> SelectFileCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyElasticTimeRangeCommand { get; }
    public ReactiveCommand<string, Unit> ApplyRangePresetCommand { get; }
    public ReactiveCommand<FileTabViewModel, Unit> CloseFileCommand { get; }
    public ReactiveCommand<LogViewViewModel, Unit> RemoveSearchCommand { get; }
    public ReactiveCommand<Unit, Unit> AddSearchCommand { get; }
    public ReactiveCommand<KeyEventArgs, Unit> AddSearchOnKeyCommand { get; }

    public FileTabViewModel? SelectedFile
    {
        get => _selectedFile;
        private set
        {
            if (ReferenceEquals(_selectedFile, value))
                return;

            var previous = _selectedFile;
            this.RaiseAndSetIfChanged(ref _selectedFile, value);
            this.RaisePropertyChanged(nameof(IsElasticSelected));
            previous?.RaiseSelectionChanged();
            if (value is not null)
                LoadRangeInputs(value.Model);
            value?.RaiseSelectionChanged();
        }
    }

    public bool HasFile => SelectedFile is not null;

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (_isPaused == value)
                return;
            this.RaiseAndSetIfChanged(ref _isPaused, value);
            if (!value)
                SelectedFile?.SelectedView.Sync();
        }
    }

    public string SourceSummary
    {
        get => _sourceSummary;
        private set => this.RaiseAndSetIfChanged(ref _sourceSummary, value);
    }

    internal static string DescribeSources(IReadOnlyCollection<FileTabState> files)
    {
        if (files.Count == 0)
            return "No sources open";
        var text = files.Count == 1 ? "1 source open" : $"{files.Count} sources open";
        var failing = files.Count(file => file.Error is not null);
        return failing == 0 ? text : $"{text} · {failing} with errors";
    }

    public bool IsElasticSelected => SelectedFile?.Model.Source.Kind == LogSourceKind.Elastic;
    public string RangeFromText
    {
        get => _rangeFromText;
        set
        {
            if (_rangeFromText == value)
                return;
            this.RaiseAndSetIfChanged(ref _rangeFromText, value);
            this.RaisePropertyChanged(nameof(RangeFromPreview));
            if (!_syncingRange)
                ElasticRangeDirty = true;
        }
    }

    public string RangeToText
    {
        get => _rangeToText;
        set
        {
            if (_rangeToText == value)
                return;
            this.RaiseAndSetIfChanged(ref _rangeToText, value);
            this.RaisePropertyChanged(nameof(RangeToPreview));
            if (!_syncingRange)
                ElasticRangeDirty = true;
        }
    }

    public bool ElasticToNow
    {
        get => _elasticToNow;
        set
        {
            if (_elasticToNow == value)
                return;
            this.RaiseAndSetIfChanged(ref _elasticToNow, value);
            this.RaisePropertyChanged(nameof(RangeToPreview));
            if (!_syncingRange)
                ElasticRangeDirty = true;
        }
    }

    public string RangeFromPreview => Preview(RangeFromText);
    public string RangeToPreview =>
        ElasticToNow ? "Live — new logs keep appending" : Preview(RangeToText);
    public string RangeZoneHint =>
        $"Times without an offset are read as {ZoneLabel}. Try now-15m, -2h, 14:00, yesterday 09:00.";

    public string? RangeError
    {
        get => _rangeError;
        private set => this.RaiseAndSetIfChanged(ref _rangeError, value);
    }

    private AppTimeZoneMode DisplayZone => _state.Settings.TimeZoneMode;
    private string ZoneLabel => DisplayZone == AppTimeZoneMode.Utc ? "UTC" : "local";

    public bool ElasticRangeDirty
    {
        get => _elasticRangeDirty;
        private set
        {
            this.RaiseAndSetIfChanged(ref _elasticRangeDirty, value);
            this.RaisePropertyChanged(nameof(ElasticDraftStatus));
        }
    }
    public string ElasticDraftStatus =>
        ElasticRangeDirty ? "Unapplied — press Enter or Apply" : string.Empty;

    public string ElasticRangePill =>
        SelectedFile?.Model is not { } tab || !IsElasticSelected
            ? string.Empty
            : $"{TimeRangeLabel.Describe(tab.ElasticFrom, tab.ElasticTo, DisplayZone)} · {RangeState(tab)}";
    public string ElasticAppliedRange
    {
        get
        {
            var tab = SelectedFile?.Model;
            if (tab is null)
                return string.Empty;
            string Display(DateTimeOffset? time, string expression) =>
                time is null
                    ? expression
                    : (
                        DisplayZone == AppTimeZoneMode.Utc
                            ? time.Value.ToUniversalTime()
                            : time.Value.ToLocalTime()
                    ).ToString("yyyy-MM-dd HH:mm:ss zzz");
            return $"Applied: {Display(tab.ElasticResolvedFrom, tab.ElasticFrom)} → {Display(tab.ElasticResolvedTo, tab.ElasticTo)}";
        }
    }
    public string ElasticRangeStatus =>
        SelectedFile?.Model is not { } tab ? string.Empty
        : tab.Error is not null ? $"Failed: {tab.Error}"
        : tab.ElasticLoading ? "Loading logs…"
        : tab.ElasticTo == "now" ? "Live — fetching new logs"
        : "Historical — loaded; Apply to refresh";

    private static string RangeState(FileTabState tab) =>
        tab.Error is not null ? "failed"
        : tab.ElasticLoading ? "loading…"
        : tab.ElasticTo == "now" ? "live"
        : "historical";

    private string Preview(string text) =>
        TimeExpression.TryParse(
            text,
            DateTimeOffset.UtcNow,
            DisplayZone,
            out var parsed,
            out var error
        )
            ? $"= {TimeExpression.FormatForInput(parsed.Resolved.ToString("O", CultureInfo.InvariantCulture), DisplayZone)} {ZoneLabel}"
            : error!;

    private void LoadRangeInputs(FileTabState tab)
    {
        _syncingRange = true;
        RangeFromText = TimeExpression.FormatForInput(tab.ElasticFrom, DisplayZone);
        ElasticToNow = string.Equals(tab.ElasticTo, "now", StringComparison.OrdinalIgnoreCase);
        RangeToText = ElasticToNow
            ? "now"
            : TimeExpression.FormatForInput(tab.ElasticTo, DisplayZone);
        _syncingRange = false;
        ElasticRangeDirty = false;
        RangeError = null;
    }

    private void ApplyRangePreset(string expression)
    {
        _syncingRange = true;
        RangeFromText = expression;
        ElasticToNow = true;
        RangeToText = "now";
        _syncingRange = false;
        ApplyElasticTimeRange();
    }

    public bool ShowEmpty => !HasFile;
    public int FileCount => Files.Count;
    public int LineCount => SelectedFile?.Model.Buffer.Count ?? 0;

    public string? FileError
    {
        get =>
            _fileError
            ?? (
                SelectedFile?.Model.Error is { } error
                    ? $"{(SelectedFile.Model.Source.Kind == LogSourceKind.File ? SelectedFile.Path : SelectedFile.Model.Source.ToolTip)}: {error}"
                    : null
            );
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
        set
        {
            if (!value && _settingsOpen && !Settings.TryClose())
            {
                this.RaisePropertyChanged(nameof(SettingsOpen));
                return;
            }
            this.RaiseAndSetIfChanged(ref _settingsOpen, value);
        }
    }

    internal void CloseSettingsAfterDraftChoice() =>
        this.RaiseAndSetIfChanged(ref _settingsOpen, false, nameof(SettingsOpen));

    public int SelectedViewIndex
    {
        get => _selectedViewIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedViewIndex, Math.Max(0, value));
    }

    /// <summary>Persisted preference for wide windows.</summary>
    public bool SidebarOpen
    {
        get => _sidebarOpen;
        set
        {
            if (_sidebarOpen == value)
                return;
            this.RaiseAndSetIfChanged(ref _sidebarOpen, value);
            if (!_narrowShell)
                this.RaisePropertyChanged(nameof(SidebarPaneOpen));
        }
    }

    /// <summary>Effective pane state: the preference when wide, a transient overlay when narrow.</summary>
    public bool SidebarPaneOpen
    {
        get => _narrowShell ? _narrowPaneOpen : _sidebarOpen;
        set
        {
            if (_narrowShell)
            {
                if (_narrowPaneOpen == value)
                    return;
                _narrowPaneOpen = value;
                this.RaisePropertyChanged();
            }
            else
                SidebarOpen = value;
        }
    }

    public double SidebarWidth
    {
        get => _sidebarWidth;
        set =>
            this.RaiseAndSetIfChanged(
                ref _sidebarWidth,
                Math.Clamp(value, MinSidebarWidth, MaxSidebarWidth)
            );
    }

    public bool IsNarrowShell => _narrowShell;
    public SplitViewDisplayMode SidebarDisplayMode =>
        _narrowShell ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;

    internal void SetShellWidth(double width)
    {
        var narrow = width < NarrowShellWidth;
        if (narrow == _narrowShell)
            return;
        _narrowShell = narrow;
        _narrowPaneOpen = false;
        this.RaisePropertyChanged(nameof(IsNarrowShell));
        this.RaisePropertyChanged(nameof(SidebarDisplayMode));
        this.RaisePropertyChanged(nameof(SidebarPaneOpen));
    }

    internal void DismissNarrowSidebar()
    {
        if (_narrowShell)
            SidebarPaneOpen = false;
    }

    public async Task InitializeAsync()
    {
        if (_started)
            return;

        _started = true;
        _restoring = true;
        try
        {
            await _state.RestoreAsync();
            SidebarOpen = !_state.Window.SidebarCollapsed;
            if (_state.Window.SidebarWidth > 0)
                SidebarWidth = _state.Window.SidebarWidth;
            if (Dispatcher.UIThread.CheckAccess())
                ThemeManager.Apply(_state.Settings.Theme);
            else
                await Dispatcher.UIThread.InvokeAsync(() =>
                    ThemeManager.Apply(_state.Settings.Theme)
                );
            foreach (var path in _startupPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
                await TryOpenPathAsync(path);

            SyncFromState();
            if (_startPolling)
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
        finally
        {
            _restoring = false;
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
        await _state.UpdateSettingsAsync(settings);
        if (Dispatcher.UIThread.CheckAccess())
            ThemeManager.Apply(settings.Theme);
        else
            await Dispatcher.UIThread.InvokeAsync(() => ThemeManager.Apply(settings.Theme));
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
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetFileError(message));
            return;
        }

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
    }

    private async Task TryOpenPathAsync(string path)
    {
        try
        {
            await _state.OpenFileAsync(path);
            SetFileError(null);
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
        }
        catch (Exception ex)
        {
            SetFileError(ex.Message);
        }
    }

    private async Task RemoveSearchAsync(LogViewViewModel view)
    {
        if (view.Search is null)
            return;

        await _state.RemoveSearchAsync(view.File, view.Search);
    }

    private async Task SaveAsync()
    {
        await _state.SaveAsync();
        SetFileError(null);
    }

    private async Task AddSearchAsync()
    {
        var file = SelectedFile;
        if (file is null || string.IsNullOrWhiteSpace(Query))
            return;

        try
        {
            _state.AddSearch(file.Model, Query, MatchMode, CaseSensitive, ColorToHex(SearchColor));
            SearchColor = Color.Parse(
                NextSearchColor(
                    file.Model.Searches.Select(search => search.Color),
                    State.Settings.GlobalLabels.Select(label => label.Color)
                )
            );
            Query = string.Empty;
            SearchError = null;
            await _state.SaveAsync();
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
        DismissNarrowSidebar();
    }

    private void DrainTailer()
    {
        if (_closed || !_state.DrainTailerEvents())
            return;
    }

    private void QueueSyncFromState()
    {
        if (_closed || _restoring || Interlocked.Exchange(ref _syncQueued, 1) != 0)
            return;

        _scheduler.Schedule(() =>
        {
            Interlocked.Exchange(ref _syncQueued, 0);
            if (!_closed && !_restoring)
                SyncFromState();
        });
    }

    private void SyncFromState()
    {
        if (_closed)
            return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(SyncFromState);
            return;
        }

        var previous = Snapshot();
        var files = _state.Files;
        SourceSummary = DescribeSources(files);

        for (var index = Files.Count - 1; index >= 0; index--)
        {
            if (files.Contains(Files[index].Model))
                continue;
            Files.RemoveAt(index);
        }

        foreach (var file in files)
        {
            var viewModel = Files.FirstOrDefault(item => ReferenceEquals(item.Model, file));
            var loadRows = ReferenceEquals(file, _state.SelectedFile);
            if (viewModel is null)
            {
                viewModel = new FileTabViewModel(this, file, loadRows);
                Files.Add(viewModel);
            }
            else
                viewModel.SyncViews(loadRows);
        }

        for (var index = FileTabs.Count - 1; index >= 0; index--)
            if (!Files.Contains(FileTabs[index]))
                FileTabs.RemoveAt(index);
        foreach (var file in Files)
            if (file.Model.Source.Kind == LogSourceKind.File && !FileTabs.Contains(file))
                FileTabs.Add(file);

        SelectedFile = _state.SelectedFile is null
            ? null
            : Files.FirstOrDefault(item => ReferenceEquals(item.Model, _state.SelectedFile));
        Settings.Sync(_state.Settings);
        var configured = _state
            .Settings.ElasticConnections.SelectMany(connection =>
                connection.Views.SelectMany(view =>
                    view.Sources.Select(source =>
                        (
                            ConnectionId: connection.Id,
                            Descriptor: LogSourceDescriptor.Elastic(connection, view, source),
                            ShortName: $"{(string.IsNullOrWhiteSpace(view.Name) ? view.DataViewTitle : view.Name)} · {source.ServerValue}"
                        )
                    )
                )
            )
            .ToArray();
        for (var index = ElasticSources.Count - 1; index >= 0; index--)
            if (!configured.Any(item => item.Descriptor.Id == ElasticSources[index].SourceId))
                ElasticSources.RemoveAt(index);
        foreach (var item in configured)
            if (!ElasticSources.Any(option => option.SourceId == item.Descriptor.Id))
                ElasticSources.Add(
                    new ElasticSourceOptionViewModel(
                        this,
                        item.Descriptor.Id,
                        item.Descriptor.DisplayName,
                        item.Descriptor.ToolTip
                    )
                );
        var selectedSourceId = SelectedFile?.Model.Source.ElasticSourceId;
        foreach (var option in ElasticSources)
        {
            var item = configured.First(source => source.Descriptor.Id == option.SourceId);
            option.SyncIdentity(item.Descriptor);
            option.SyncConnection(item.ConnectionId, item.ShortName);
            option.SyncSelection(option.SourceId == selectedSourceId);
            var status =
                _state.ElasticSourceStatuses.GetValueOrDefault(option.SourceId)?.Status.ToString()
                ?? "Checking";
            var health = _state.ElasticSourceStatuses.GetValueOrDefault(option.SourceId);
            var tab = _state.Files.FirstOrDefault(tab => tab.Id == option.SourceId);
            var load =
                tab is null ? "Logs: closed"
                : tab.Error is not null ? $"Logs: {tab.Error}"
                : tab.ElasticLoading ? "Logs: loading…"
                : tab.ElasticTo == "now" ? "Logs: live"
                : "Logs: historical";
            option.Sync(tab is not null, status, health?.Message, load);
        }
        SyncConnectionGroups();
        this.RaisePropertyChanged(nameof(ElasticAppliedRange));
        this.RaisePropertyChanged(nameof(ElasticRangeStatus));
        if (SelectedFile is not null && !ElasticRangeDirty)
            LoadRangeInputs(SelectedFile.Model);
        this.RaisePropertyChanged(nameof(ElasticRangePill));
        this.RaisePropertyChanged(nameof(RangeFromPreview));
        this.RaisePropertyChanged(nameof(RangeToPreview));
        this.RaisePropertyChanged(nameof(RangeZoneHint));
        var current = Snapshot();
        if (previous.FileCount != current.FileCount)
            this.RaisePropertyChanged(nameof(FileCount));
        if (previous.HasFile != current.HasFile)
            this.RaisePropertyChanged(nameof(HasFile));
        if (previous.ShowEmpty != current.ShowEmpty)
            this.RaisePropertyChanged(nameof(ShowEmpty));
        if (previous.LineCount != current.LineCount)
            this.RaisePropertyChanged(nameof(LineCount));
        if (previous.FileError != current.FileError)
            this.RaisePropertyChanged(nameof(FileError));
        if (previous.HasFileError != current.HasFileError)
            this.RaisePropertyChanged(nameof(HasFileError));
        if (previous.HasSearchError != current.HasSearchError)
            this.RaisePropertyChanged(nameof(HasSearchError));
        if (previous.HasElasticSources != current.HasElasticSources)
            this.RaisePropertyChanged(nameof(HasElasticSources));
        if (previous.HasElasticWarning != current.HasElasticWarning)
            this.RaisePropertyChanged(nameof(HasElasticWarning));
        if (previous.ElasticSourceIcon != current.ElasticSourceIcon)
            this.RaisePropertyChanged(nameof(ElasticSourceIcon));
    }

    private void SyncConnectionGroups()
    {
        var connections = _state.Settings.ElasticConnections;
        for (var index = ElasticConnectionGroups.Count - 1; index >= 0; index--)
            if (!connections.Any(connection => connection.Id == ElasticConnectionGroups[index].Id))
                ElasticConnectionGroups.RemoveAt(index);
        for (var index = 0; index < connections.Count; index++)
        {
            var connection = connections[index];
            var group = ElasticConnectionGroups.FirstOrDefault(item => item.Id == connection.Id);
            if (group is null)
            {
                group = new ElasticConnectionGroupViewModel(connection.Id);
                ElasticConnectionGroups.Insert(index, group);
            }
            else if (ElasticConnectionGroups.IndexOf(group) is var current && current != index)
                ElasticConnectionGroups.Move(current, index);
            group.Sync(
                connection.Name,
                ElasticSources.Where(option => option.ConnectionId == connection.Id).ToArray()
            );
        }
    }

    private MainSnapshot Snapshot() =>
        new(
            FileCount,
            HasFile,
            ShowEmpty,
            LineCount,
            FileError,
            HasFileError,
            HasSearchError,
            HasElasticSources,
            HasElasticWarning,
            ElasticSourceIcon
        );

    private void ApplyElasticTimeRange()
    {
        if (!IsElasticSelected || SelectedFile is null)
            return;
        var now = DateTimeOffset.UtcNow;
        if (
            !TimeExpression.TryParse(
                RangeFromText,
                now,
                DisplayZone,
                out var from,
                out var fromError
            )
        )
        {
            RangeError = $"From: {fromError}";
            return;
        }
        var to = "now";
        if (!ElasticToNow)
        {
            if (
                !TimeExpression.TryParse(
                    RangeToText,
                    now,
                    DisplayZone,
                    out var parsedTo,
                    out var toError
                )
            )
            {
                RangeError = $"To: {toError}";
                return;
            }
            to = parsedTo.Expression;
        }
        try
        {
            _state.SetElasticTimeRange(SelectedFile.Model, from.Expression, to);
            SelectedFile.Model.ElasticInputZone = DisplayZone;
            ElasticRangeDirty = false;
            RangeError = null;
            SetFileError(null);
        }
        catch (ArgumentException exception)
        {
            RangeError = exception.Message;
        }
    }

    private static string ColorToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    internal static string NextSearchColor(
        IEnumerable<string> activeSearchColors,
        IEnumerable<string> globalLabelColors
    )
    {
        var used = activeSearchColors
            .Concat(globalLabelColors)
            .Select(color => color.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var next = SearchColors.FirstOrDefault(color => !used.Contains(color));
        if (next is not null)
            return next;

        for (var red = 32; red <= 224; red += 32)
        for (var green = 32; green <= 224; green += 32)
        for (var blue = 32; blue <= 224; blue += 32)
        {
            next = $"#{red:X2}{green:X2}{blue:X2}";
            if (!used.Contains(next))
                return next;
        }

        return "#FFFFFF";
    }

    private readonly record struct MainSnapshot(
        int FileCount,
        bool HasFile,
        bool ShowEmpty,
        int LineCount,
        string? FileError,
        bool HasFileError,
        bool HasSearchError,
        bool HasElasticSources,
        bool HasElasticWarning,
        string ElasticSourceIcon
    );
}
