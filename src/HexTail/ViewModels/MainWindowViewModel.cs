using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Controls;
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
    private string _sourceSummary = "No sources open";
    private bool _settingsOpen;
    private int _selectedViewIndex;
    private string? _fileError;
    private bool _started;
    private bool _closed;
    private bool _restoring;
    private int _syncQueued;
    private SampleLogWriter? _sample;
    private Task _sampleStopping = Task.CompletedTask;
    private string _recentKey = string.Empty;
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
        ElasticSetup = new ElasticSetupViewModel(this);
        FilterBar = new FilterBarViewModel(this);
        Inspector = new InspectorViewModel(this);
        PickFiles = new Interaction<Unit, IReadOnlyList<string>>(_scheduler);
        Palette = new CommandPaletteViewModel(BuildPaletteItems, SetFileError);
        OpenPaletteCommand = ReactiveCommand.Create(Palette.Open, _scheduler);

        OpenCommand = ReactiveCommand.CreateFromTask(OpenFilesAsync, _scheduler);
        OpenPathsCommand = ReactiveCommand.CreateFromTask<IEnumerable<string>>(
            OpenPathsAsync,
            _scheduler
        );
        OpenRecentCommand = ReactiveCommand.CreateFromTask<string>(OpenRecentAsync, _scheduler);
        OpenSampleCommand = ReactiveCommand.CreateFromTask(OpenSampleAsync, _scheduler);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, _scheduler);
        ToggleSettingsCommand = ReactiveCommand.Create(
            () =>
            {
                SettingsOpen = !SettingsOpen;
            },
            _scheduler
        );
        SetUpElasticCommand = ReactiveCommand.Create(
            () => ElasticSetup.Run(ElasticSetup.StartNew),
            outputScheduler: _scheduler
        );
        ToggleSidebarCommand = ReactiveCommand.Create(
            () =>
            {
                SidebarPaneOpen = !SidebarPaneOpen;
            },
            _scheduler
        );
        ToggleInspectorCommand = ReactiveCommand.Create(() => Inspector.Toggle(), _scheduler);
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
        SavedFilters.CollectionChanged += (_, _) =>
            this.RaisePropertyChanged(nameof(HasSavedFilters));

        _subscriptions.Add(
            Observable
                .Merge(
                    OpenCommand.ThrownExceptions,
                    OpenPathsCommand.ThrownExceptions,
                    OpenRecentCommand.ThrownExceptions,
                    OpenSampleCommand.ThrownExceptions,
                    SaveCommand.ThrownExceptions,
                    CloseFileCommand.ThrownExceptions,
                    RemoveSearchCommand.ThrownExceptions
                )
                .Subscribe(ex => SetFileError(ex.Message))
        );
    }

    public AppState State => _state;
    public ObservableCollection<RecentFileViewModel> RecentFiles { get; } = [];
    public ObservableCollection<ElasticSourceOptionViewModel> RecentElasticSources { get; } = [];
    public bool HasRecents => RecentFiles.Count > 0 || RecentElasticSources.Count > 0;
    public ReactiveCommand<string, Unit> OpenRecentCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenSampleCommand { get; }
    public ObservableCollection<FileTabViewModel> Files { get; } = [];
    public ObservableCollection<FileTabViewModel> FileTabs { get; } = [];
    public bool HasFileTabs => FileTabs.Count > 0;
    public ObservableCollection<SavedFilterViewModel> SavedFilters { get; } = [];
    public bool HasSavedFilters => SavedFilters.Count > 0;
    public ObservableCollection<ElasticSourceOptionViewModel> ElasticSources { get; } = [];
    public ObservableCollection<ElasticConnectionGroupViewModel> ElasticConnectionGroups { get; } =
    [];
    internal IScheduler Scheduler => _scheduler;
    public bool HasElasticSources => ElasticSources.Count > 0;
    public bool HasElasticWarning => _state.HasElasticWarning;
    public string ElasticSourceIcon => HasElasticWarning ? "mdi-cloud-alert" : "mdi-cloud-check";
    public SettingsViewModel Settings { get; }
    public ElasticSetupViewModel ElasticSetup { get; }
    public FilterBarViewModel FilterBar { get; }
    public InspectorViewModel Inspector { get; }
    public Interaction<Unit, IReadOnlyList<string>> PickFiles { get; }
    public CommandPaletteViewModel Palette { get; }
    public ReactiveCommand<Unit, Unit> OpenPaletteCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<IEnumerable<string>, Unit> OpenPathsCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleSidebarCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleInspectorCommand { get; }
    public ReactiveCommand<Unit, Unit> TogglePauseCommand { get; }
    public ReactiveCommand<Unit, Unit> SetUpElasticCommand { get; }
    public ReactiveCommand<FileTabViewModel, Unit> SelectFileCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyElasticTimeRangeCommand { get; }
    public ReactiveCommand<string, Unit> ApplyRangePresetCommand { get; }
    public ReactiveCommand<FileTabViewModel, Unit> CloseFileCommand { get; }
    public ReactiveCommand<LogViewViewModel, Unit> RemoveSearchCommand { get; }

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
            Inspector.IsOpen = _state.Window.InspectorOpen;
            Inspector.Width =
                _state.Window.InspectorWidth > 0
                    ? _state.Window.InspectorWidth
                    : InspectorViewModel.DefaultWidth;
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
        // The setup flow's debounce timer must not fire a connection test into a closed window.
        if (Dispatcher.UIThread.CheckAccess())
            ElasticSetup.CancelAutoTest();
        else
            Dispatcher.UIThread.Post(ElasticSetup.CancelAutoTest);
        try
        {
            _subscriptions.Dispose();
        }
        finally
        {
            try
            {
                // Close the tabs (and the tailer's handle on the sample file) first, so the writer's
                // cleanup can delete the file on Windows.
                await _state.DisposeAsync();
            }
            finally
            {
                try
                {
                    if (_sample is not null)
                        await _sample.DisposeAsync();
                }
                finally
                {
                    await _sampleStopping.ConfigureAwait(false);
                }
            }
        }
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

    private async Task OpenFilesAsync()
    {
        var paths = await PickFiles.Handle(Unit.Default);
        await OpenPathsAsync(paths);
    }

    private async Task OpenRecentAsync(string path)
    {
        if (!File.Exists(path))
        {
            SetFileError($"Could not open {path}: the file no longer exists.");
            return;
        }
        await OpenPathsAsync([path]);
    }

    private async Task OpenSampleAsync()
    {
        await _sampleStopping.ConfigureAwait(true);
        var sample = _sample ?? SampleLogWriter.Start(TimeSpan.FromMilliseconds(500));
        try
        {
            await _state.OpenFileAsync(sample.Path, remember: false, ephemeral: true);
            // Publish the writer only once its tab exists, so a concurrent sync cannot mistake
            // the not-yet-open sample for a closed one and stop it.
            _sample = sample;
        }
        catch
        {
            if (!ReferenceEquals(sample, _sample))
                await sample.DisposeAsync();
            throw;
        }
    }

    private void SyncRecents()
    {
        var files = _state.RecentFiles;
        var sources = _state.RecentElasticSourceIds;
        var resolved = sources
            .Select(id => ElasticSources.FirstOrDefault(option => option.SourceId == id))
            .OfType<ElasticSourceOptionViewModel>()
            .ToArray();
        var key =
            $"{string.Join('\n', files)}\0{string.Join('\n', resolved.Select(option => option.SourceId))}";
        if (key == _recentKey)
            return;
        _recentKey = key;
        RecentFiles.Clear();
        foreach (var path in files)
            RecentFiles.Add(new RecentFileViewModel(path, File.Exists(path)));
        RecentElasticSources.Clear();
        foreach (var option in resolved)
            RecentElasticSources.Add(option);
        this.RaisePropertyChanged(nameof(HasRecents));
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

    // The connection of the open Elastic tab, else the first configured one.
    private string? AddSourceConnectionId()
    {
        var sourceId = SelectedFile?.Model.Source.ElasticSourceId;
        var current = sourceId is null
            ? null
            : ElasticSources.FirstOrDefault(option => option.SourceId == sourceId)?.ConnectionId;
        return !string.IsNullOrEmpty(current)
            ? current
            : _state.Settings.ElasticConnections.FirstOrDefault()?.Id;
    }

    internal IEnumerable<PaletteItem> BuildPaletteItems()
    {
        yield return new("Open files…", "File", "Ctrl+O", async () => await OpenCommand.Execute());
        yield return new(
            "Set up Elastic",
            "Elastic",
            null,
            async () => await SetUpElasticCommand.Execute()
        );
        if (AddSourceConnectionId() is { } connectionId)
            yield return new(
                "Add Elastic source",
                "Elastic",
                null,
                () =>
                {
                    ElasticSetup.Run(() => ElasticSetup.AddSource(connectionId));
                    return Task.CompletedTask;
                }
            );
        yield return new(
            "Settings",
            "App",
            null,
            async () => await ToggleSettingsCommand.Execute()
        );
        yield return new(
            "Toggle sidebar",
            "View",
            "Ctrl+B",
            async () => await ToggleSidebarCommand.Execute()
        );
        yield return new(
            "Toggle inspector",
            "View",
            "Ctrl+I",
            async () => await ToggleInspectorCommand.Execute()
        );
        yield return new(
            IsPaused ? "Resume view updates" : "Pause view updates",
            "View",
            "Space",
            async () => await TogglePauseCommand.Execute()
        );
        if (SelectedFile is { } file)
        {
            yield return new(
                "Toggle bookmark on selected row",
                "View",
                "M",
                () =>
                {
                    Inspector.ToggleBookmarkOnSelection();
                    return Task.CompletedTask;
                }
            );
            yield return new(
                "Focus search",
                "Search",
                "/",
                () =>
                {
                    FilterBar.RequestFocus();
                    return Task.CompletedTask;
                }
            );
            yield return new(
                "Follow newest lines",
                "View",
                "F",
                () =>
                {
                    file.SelectedView.IsFollowing = true;
                    return Task.CompletedTask;
                }
            );
            foreach (var saved in _state.SavedFilters)
                yield return new(
                    $"Apply filter: {saved.Name}",
                    "Filters",
                    null,
                    async () => await _state.ApplySavedFilterAsync(file.Model, saved.Name)
                );
            foreach (var view in file.Views)
                yield return new(
                    $"View: {view.Header}",
                    "View",
                    null,
                    () =>
                    {
                        file.SelectedViewIndex = file.Views.IndexOf(view);
                        return Task.CompletedTask;
                    }
                );
        }
        if (IsElasticSelected)
            foreach (
                var (label, expression) in new[]
                {
                    ("last 15 min", "now-15m"),
                    ("last hour", "now-1h"),
                    ("last 24 hours", "now-24h"),
                }
            )
                yield return new(
                    $"Time range: {label}",
                    "Elastic",
                    null,
                    async () => await ApplyRangePresetCommand.Execute(expression)
                );
        foreach (var tab in Files)
            yield return new(
                tab.DisplayName,
                "Open source",
                null,
                () =>
                {
                    SelectFile(tab);
                    return Task.CompletedTask;
                }
            );
        foreach (var option in ElasticSources.Where(option => !option.IsOpen))
            yield return new(
                option.DisplayName,
                "Elastic source",
                null,
                async () => await option.ActivateCommand.Execute()
            );
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
        FilterBar.Sync();
        Inspector.Sync();
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
        SyncRecents();
        SyncSavedFilters();
        if (
            _sample is { } sample
            && !_state.Files.Any(file =>
                string.Equals(file.Path, sample.Path, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            _sample = null;
            _sampleStopping = Task.WhenAll(_sampleStopping, sample.DisposeAsync().AsTask());
        }
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
        if (previous.HasElasticSources != current.HasElasticSources)
            this.RaisePropertyChanged(nameof(HasElasticSources));
        if (previous.HasElasticWarning != current.HasElasticWarning)
            this.RaisePropertyChanged(nameof(HasElasticWarning));
        if (previous.ElasticSourceIcon != current.ElasticSourceIcon)
            this.RaisePropertyChanged(nameof(ElasticSourceIcon));
    }

    private void SyncSavedFilters()
    {
        var filters = _state.SavedFilters;
        // Rows hold focus and a pending click: leave them alone unless a name or summary changed.
        if (
            filters
                .Select(filter => (filter.Name, SavedFilterViewModel.Summarize(filter)))
                .SequenceEqual(SavedFilters.Select(row => (row.Name, row.Summary)))
        )
            return;
        var retired = SavedFilters.ToList();
        SavedFilters.Clear();
        foreach (var row in retired)
            row.Dispose();
        foreach (var filter in filters)
            SavedFilters.Add(new SavedFilterViewModel(this, filter));
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

    internal static IReadOnlyList<string> Swatches => SearchColors[..5];

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
        bool HasElasticSources,
        bool HasElasticWarning,
        string ElasticSourceIcon
    );
}
