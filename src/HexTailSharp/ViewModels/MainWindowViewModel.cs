using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using HexTailSharp;
using HexTailSharp.Application;
using HexTailSharp.Domain;
using HexTailSharp.Persistence;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTailSharp.ViewModels;

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
                .ObserveOn(_scheduler)
                .Subscribe(_ => SyncFromState())
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
    public bool HasElasticWarning =>
        _state
            .Settings.ElasticConnections.SelectMany(connection => connection.Sources)
            .Any(source => !_state.IsElasticSourceOpen(source.Id));
    public SettingsViewModel Settings { get; }
    public Interaction<Unit, IReadOnlyList<string>> PickFiles { get; }
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<IEnumerable<string>, Unit> OpenPathsCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleSettingsCommand { get; }
    public ReactiveCommand<FileTabViewModel, Unit> SelectFileCommand { get; }
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
            previous?.RaiseSelectionChanged();
            value?.RaiseSelectionChanged();
        }
    }

    public bool HasFile => SelectedFile is not null;
    public bool ShowEmpty => !HasFile;
    public int FileCount => Files.Count;
    public int LineCount => SelectedFile?.Model.Buffer.Count ?? 0;

    public string? FileError
    {
        get =>
            _fileError
            ?? (SelectedFile?.Model.Error is { } error ? $"{SelectedFile.Path}: {error}" : null);
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
        SyncFromState();
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
            SyncFromState();
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
        SyncFromState();
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
            _state.AddSearch(
                file.Model,
                Query,
                CompiledQuery.DetectMode(Query),
                CaseSensitive,
                ColorToHex(SearchColor)
            );
            SearchColor = Color.Parse(
                NextSearchColor(
                    file.Model.Searches.Select(search => search.Color),
                    State.Settings.GlobalLabels.Select(label => label.Color)
                )
            );
            Query = string.Empty;
            SearchError = null;
            SyncFromState();
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
        SyncFromState();
    }

    private void DrainTailer()
    {
        if (_closed || !_state.DrainTailerEvents())
            return;
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
        var configured = _state
            .Settings.ElasticConnections.SelectMany(connection =>
                connection.Sources.Select(source =>
                    (
                        source.Id,
                        source.DisplayName,
                        ToolTip: $"{connection.Name}: {source.DisplayName}"
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
        this.RaisePropertyChanged(nameof(FileCount));
        this.RaisePropertyChanged(nameof(HasFile));
        this.RaisePropertyChanged(nameof(ShowEmpty));
        this.RaisePropertyChanged(nameof(LineCount));
        this.RaisePropertyChanged(nameof(FileError));
        this.RaisePropertyChanged(nameof(HasFileError));
        this.RaisePropertyChanged(nameof(HasSearchError));
        this.RaisePropertyChanged(nameof(HasElasticSources));
        this.RaisePropertyChanged(nameof(HasElasticWarning));
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
}
