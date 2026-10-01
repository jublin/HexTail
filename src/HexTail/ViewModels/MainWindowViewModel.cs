using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using HexTail;
using HexTail.Application;
using HexTail.Domain;
using HexTail.Elastic;
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
    private readonly AppState _state;
    private readonly string[] _startupPaths;
    private readonly IScheduler _scheduler;
    private readonly bool _startPolling;
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
    private bool _restoring;
    private int _syncQueued;
    private DateTimeOffset? _elasticFromDate;
    private DateTimeOffset? _elasticToDate;
    private TimeSpan? _elasticFromTime;
    private TimeSpan? _elasticToTime;
    private bool _elasticToNow = true;
    private bool _syncingRange;
    private bool _elasticRangeDirty;
    private int _elasticPresetIndex;
    private AppTimeZoneMode _elasticInputZone = AppTimeZoneMode.Utc;

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
        SelectFileCommand = ReactiveCommand.Create<FileTabViewModel>(SelectFile, _scheduler);
        ApplyElasticTimeRangeCommand = ReactiveCommand.Create(ApplyElasticTimeRange, _scheduler);
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
    public ObservableCollection<ElasticSourceOptionViewModel> ElasticSources { get; } = [];
    public bool HasElasticSources => ElasticSources.Count > 0;
    public bool HasElasticWarning => _state.HasElasticWarning;
    public string ElasticSourceIcon => HasElasticWarning ? "mdi-cloud-alert" : "mdi-cloud-check";
    public SettingsViewModel Settings { get; }
    public Interaction<Unit, IReadOnlyList<string>> PickFiles { get; }
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<IEnumerable<string>, Unit> OpenPathsCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleSettingsCommand { get; }
    public ReactiveCommand<FileTabViewModel, Unit> SelectFileCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyElasticTimeRangeCommand { get; }
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
            {
                _syncingRange = true;
                _elasticInputZone = value.Model.ElasticInputZone;
                this.RaisePropertyChanged(nameof(ElasticInputZone));
                var now = DateTimeOffset.UtcNow;
                SetPickerValues(
                    ElasticTailer.ParseTime(value.Model.ElasticFrom, now),
                    ElasticTailer.ParseTime(value.Model.ElasticTo, now)
                );
                ElasticToNow = value.Model.ElasticTo == "now";
                _elasticPresetIndex = value.Model.ElasticFrom switch
                {
                    "now-5m" => 1,
                    "now-15m" => 2,
                    "now-1h" => 3,
                    "now-24h" => 4,
                    _ => 0,
                };
                this.RaisePropertyChanged(nameof(ElasticPresetIndex));
                _syncingRange = false;
                ElasticRangeDirty = false;
            }
            value?.RaiseSelectionChanged();
        }
    }

    public bool HasFile => SelectedFile is not null;
    public bool IsElasticSelected => SelectedFile?.Model.Source.Kind == LogSourceKind.Elastic;
    public DateTimeOffset? ElasticFromDate
    {
        get => _elasticFromDate;
        set
        {
            if (_elasticFromDate == value)
                return;
            this.RaiseAndSetIfChanged(ref _elasticFromDate, value);
            if (!_syncingRange)
            {
                _elasticPresetIndex = 0;
                this.RaisePropertyChanged(nameof(ElasticPresetIndex));
                ElasticRangeDirty = true;
            }
        }
    }
    public TimeSpan? ElasticFromTime
    {
        get => _elasticFromTime;
        set
        {
            if (_elasticFromTime == value)
                return;
            this.RaiseAndSetIfChanged(ref _elasticFromTime, value);
            if (!_syncingRange)
            {
                _elasticPresetIndex = 0;
                this.RaisePropertyChanged(nameof(ElasticPresetIndex));
                ElasticRangeDirty = true;
            }
        }
    }
    public DateTimeOffset? ElasticToDate
    {
        get => _elasticToDate;
        set
        {
            if (_elasticToDate == value)
                return;
            this.RaiseAndSetIfChanged(ref _elasticToDate, value);
            if (!_syncingRange)
            {
                _elasticPresetIndex = 0;
                this.RaisePropertyChanged(nameof(ElasticPresetIndex));
                ElasticRangeDirty = true;
            }
        }
    }
    public TimeSpan? ElasticToTime
    {
        get => _elasticToTime;
        set
        {
            if (_elasticToTime == value)
                return;
            this.RaiseAndSetIfChanged(ref _elasticToTime, value);
            if (!_syncingRange)
            {
                _elasticPresetIndex = 0;
                this.RaisePropertyChanged(nameof(ElasticPresetIndex));
                ElasticRangeDirty = true;
            }
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
            if (!_syncingRange)
            {
                _elasticPresetIndex = 0;
                this.RaisePropertyChanged(nameof(ElasticPresetIndex));
                ElasticRangeDirty = true;
            }
        }
    }
    public IReadOnlyList<string> ElasticPresets { get; } =
    ["Custom interval", "Last 5 minutes", "Last 15 minutes", "Last hour", "Last 24 hours"];
    public int ElasticPresetIndex
    {
        get => _elasticPresetIndex;
        set
        {
            if (value < 0 || value >= ElasticPresets.Count || value == _elasticPresetIndex)
                return;
            this.RaiseAndSetIfChanged(ref _elasticPresetIndex, value);
            if (value > 0)
            {
                var now = DateTimeOffset.UtcNow;
                _syncingRange = true;
                SetPickerValues(ElasticTailer.ParseTime(PresetExpression(value), now), now);
                ElasticToNow = true;
                _syncingRange = false;
            }
            ElasticRangeDirty = true;
        }
    }
    public IReadOnlyList<AppTimeZoneMode> ElasticInputZones { get; } =
    [AppTimeZoneMode.Utc, AppTimeZoneMode.Local];
    public AppTimeZoneMode ElasticInputZone
    {
        get => _elasticInputZone;
        set
        {
            if (value == _elasticInputZone)
                return;
            DateTimeOffset? from = null,
                to = null;
            try
            {
                from = DateTimeOffset.Parse(PickerTimestamp(ElasticFromDate, ElasticFromTime));
                to = DateTimeOffset.Parse(PickerTimestamp(ElasticToDate, ElasticToTime));
            }
            catch (ArgumentException) { }
            this.RaiseAndSetIfChanged(ref _elasticInputZone, value);
            if (SelectedFile is not null)
                SelectedFile.Model.ElasticInputZone = value;
            if (from is not null && to is not null)
            {
                _syncingRange = true;
                SetPickerValues(from.Value, to.Value);
                _syncingRange = false;
            }
            this.RaisePropertyChanged(nameof(ElasticAppliedRange));
        }
    }
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
        ElasticRangeDirty ? "Unapplied changes" : "Matches applied range";
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
                        ElasticInputZone == AppTimeZoneMode.Utc
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

    private static string PresetExpression(int index) =>
        index switch
        {
            1 => "now-5m",
            2 => "now-15m",
            3 => "now-1h",
            4 => "now-24h",
            _ => throw new ArgumentException("Select a valid preset."),
        };

    private void SetPickerValues(DateTimeOffset from, DateTimeOffset to)
    {
        from =
            ElasticInputZone == AppTimeZoneMode.Utc ? from.ToUniversalTime() : from.ToLocalTime();
        to = ElasticInputZone == AppTimeZoneMode.Utc ? to.ToUniversalTime() : to.ToLocalTime();
        ElasticFromDate = from;
        ElasticFromTime = from.TimeOfDay;
        ElasticToDate = to;
        ElasticToTime = to.TimeOfDay;
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

    public async Task InitializeAsync()
    {
        if (_started)
            return;

        _started = true;
        _restoring = true;
        try
        {
            await _state.RestoreAsync();
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

        SelectedFile = _state.SelectedFile is null
            ? null
            : Files.FirstOrDefault(item => ReferenceEquals(item.Model, _state.SelectedFile));
        Settings.Sync(_state.Settings);
        var configured = _state
            .Settings.ElasticConnections.SelectMany(connection =>
                connection.Views.SelectMany(view =>
                    view.Sources.Select(source =>
                        LogSourceDescriptor.Elastic(connection, view, source)
                    )
                )
            )
            .ToArray();
        for (var index = ElasticSources.Count - 1; index >= 0; index--)
            if (!configured.Any(item => item.Id == ElasticSources[index].SourceId))
                ElasticSources.RemoveAt(index);
        foreach (var item in configured)
            if (!ElasticSources.Any(option => option.SourceId == item.Id))
                ElasticSources.Add(
                    new ElasticSourceOptionViewModel(this, item.Id, item.DisplayName, item.ToolTip)
                );
        foreach (var option in ElasticSources)
        {
            option.SyncIdentity(configured.First(source => source.Id == option.SourceId));
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
        this.RaisePropertyChanged(nameof(ElasticAppliedRange));
        this.RaisePropertyChanged(nameof(ElasticRangeStatus));
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
        try
        {
            var from =
                ElasticPresetIndex > 0
                    ? PresetExpression(ElasticPresetIndex)
                    : PickerTimestamp(ElasticFromDate, ElasticFromTime);
            var to = ElasticToNow ? "now" : PickerTimestamp(ElasticToDate, ElasticToTime);
            _state.SetElasticTimeRange(SelectedFile.Model, from, to);
            ElasticRangeDirty = false;
            SetFileError(null);
        }
        catch (ArgumentException exception)
        {
            SetFileError(exception.Message);
        }
    }

    private string PickerTimestamp(DateTimeOffset? date, TimeSpan? time)
    {
        if (date is null || time is null || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
            throw new ArgumentException("Select both a date and a time.");
        var wallTime = DateTime.SpecifyKind(date.Value.Date + time.Value, DateTimeKind.Unspecified);
        if (
            ElasticInputZone == AppTimeZoneMode.Local
            && (
                TimeZoneInfo.Local.IsInvalidTime(wallTime)
                || TimeZoneInfo.Local.IsAmbiguousTime(wallTime)
            )
        )
            throw new ArgumentException(
                "This local time is missing or ambiguous due to daylight saving. Select UTC."
            );
        var offset =
            ElasticInputZone == AppTimeZoneMode.Utc
                ? TimeSpan.Zero
                : TimeZoneInfo.Local.GetUtcOffset(wallTime);
        return new DateTimeOffset(wallTime, offset).ToString("O");
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
