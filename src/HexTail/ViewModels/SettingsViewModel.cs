using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Concurrency;
using Avalonia;
using HexTail.Elastic;
using HexTail.Persistence;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal sealed class SettingsViewModel : ReactiveObject
{
    private readonly MainWindowViewModel _owner;
    private UiDensity _density;
    private LogFontSize _fontSize;
    private string _section = "appearance";
    private string? _saveError;
    private int _sectionIndex;
    private bool _isSaving;
    private bool _syncing;
    private ThemeOption _selectedTheme;
    private AppTimeZoneMode _timeZoneMode;
    private ElasticConnectionEditorViewModel? _selectedElasticConnection;
    private readonly HashSet<string> _syncedElasticConnectionIds = new(StringComparer.Ordinal);
    private bool _closeConfirmationVisible;
    private ElasticConnectionEditorViewModel? _pendingDelete;

    internal SettingsViewModel(MainWindowViewModel owner, IScheduler scheduler)
    {
        _owner = owner;
        AddElasticConnectionCommand = ReactiveCommand.Create(AddElasticConnection, scheduler);
        RemoveElasticConnectionCommand =
            ReactiveCommand.CreateFromTask<ElasticConnectionEditorViewModel>(
                RemoveElasticConnectionAsync,
                scheduler
            );
        SaveDraftsAndCloseCommand = ReactiveCommand.CreateFromTask(
            SaveDraftsAndCloseAsync,
            scheduler
        );
        DiscardDraftsAndCloseCommand = ReactiveCommand.Create(DiscardDraftsAndClose, scheduler);
        KeepEditingCommand = ReactiveCommand.Create(
            () =>
            {
                CloseConfirmationVisible = false;
            },
            scheduler
        );
        ConfirmDeleteCommand = ReactiveCommand.CreateFromTask(ConfirmDeleteAsync, scheduler);
        CancelDeleteCommand = ReactiveCommand.Create(CancelDelete, scheduler);
        ElasticConnections.CollectionChanged += (_, _) => NotifyElasticDraftsChanged();
        _selectedTheme = ThemeOptions[0];
    }

    public ObservableCollection<ElasticConnectionEditorViewModel> ElasticConnections { get; } = [];
    public ReactiveCommand<Unit, Unit> SaveDraftsAndCloseCommand { get; }
    public ReactiveCommand<Unit, Unit> DiscardDraftsAndCloseCommand { get; }
    public ReactiveCommand<Unit, Unit> KeepEditingCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmDeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelDeleteCommand { get; }
    public bool HasElasticDrafts => ElasticConnections.Any(editor => editor.IsDirty);
    public bool CanDiscardDrafts => ElasticConnections.All(editor => !editor.IsSaving);
    public bool CanSaveDrafts =>
        CanDiscardDrafts && ElasticConnections.All(editor => !editor.IsDirty || editor.CanSave);
    public bool CloseConfirmationVisible
    {
        get => _closeConfirmationVisible;
        private set => this.RaiseAndSetIfChanged(ref _closeConfirmationVisible, value);
    }
    public bool DeleteConfirmationVisible => _pendingDelete is not null;
    public string DeleteConfirmationMessage
    {
        get
        {
            if (_pendingDelete is not { } editor)
                return string.Empty;
            var saved = _owner.State.Settings.ElasticConnections.FirstOrDefault(connection =>
                connection.Id == editor.Id
            );
            var sources = saved?.Views.SelectMany(view =>
                view.Sources.Select(source =>
                    $"{view.Name}: {source.ServerValue}{(string.IsNullOrWhiteSpace(source.NamespaceValue) ? string.Empty : $" / {source.NamespaceValue}")}"
                )
            );
            return $"Delete {saved?.Name ?? editor.Name}? Its source tabs will close. Sources: {string.Join(", ", sources ?? [])}";
        }
    }

    internal void NotifyElasticDraftsChanged()
    {
        this.RaisePropertyChanged(nameof(HasElasticDrafts));
        this.RaisePropertyChanged(nameof(CanSaveDrafts));
        this.RaisePropertyChanged(nameof(CanDiscardDrafts));
    }

    internal bool TryClose()
    {
        if (!HasElasticDrafts && CanDiscardDrafts)
            return true;
        CancelDelete();
        CloseConfirmationVisible = true;
        return false;
    }

    private async Task SaveDraftsAndCloseAsync()
    {
        if (!CanSaveDrafts)
            return;
        foreach (var editor in ElasticConnections.Where(editor => editor.IsDirty).ToArray())
            if (!await editor.SaveDraftAsync())
                return;
        if (HasElasticDrafts)
            return;
        CloseConfirmationVisible = false;
        _owner.CloseSettingsAfterDraftChoice();
    }

    private void DiscardDraftsAndClose()
    {
        if (!CanDiscardDrafts)
            return;
        ElasticConnections.Clear();
        _syncedElasticConnectionIds.Clear();
        SelectedElasticConnection = null;
        Sync(_owner.State.Settings);
        CloseConfirmationVisible = false;
        _owner.CloseSettingsAfterDraftChoice();
    }

    public IReadOnlyList<UiDensity> DensityOptions { get; } =
    [UiDensity.Comfortable, UiDensity.Cozy, UiDensity.Compact];
    public IReadOnlyList<LogFontSize> FontSizeOptions { get; } =
    [LogFontSize.Small, LogFontSize.Medium, LogFontSize.Large, LogFontSize.ExtraLarge];
    public IReadOnlyList<AppTimeZoneMode> TimeZoneModes { get; } =
        Enum.GetValues<AppTimeZoneMode>();
    public IReadOnlyList<ThemeOption> ThemeOptions { get; } =
        ThemeCatalog
            .Names.Select(id => new ThemeOption(id, ThemeCatalog.DisplayNames[id]))
            .ToArray();
    public ReactiveCommand<Unit, Unit> AddElasticConnectionCommand { get; }
    public ReactiveCommand<
        ElasticConnectionEditorViewModel,
        Unit
    > RemoveElasticConnectionCommand { get; }

    public ElasticConnectionEditorViewModel? SelectedElasticConnection
    {
        get => _selectedElasticConnection;
        set => this.RaiseAndSetIfChanged(ref _selectedElasticConnection, value);
    }

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
            var index = Math.Clamp(value, 0, 1);
            if (_sectionIndex == index)
                return;
            this.RaiseAndSetIfChanged(ref _sectionIndex, index);
            _section = index switch
            {
                1 => "elastic",
                _ => "appearance",
            };
        }
    }

    public string? SaveError
    {
        get => _saveError;
        private set
        {
            this.RaiseAndSetIfChanged(ref _saveError, value);
            this.RaisePropertyChanged(nameof(HasSaveError));
        }
    }

    public bool HasSaveError => !string.IsNullOrWhiteSpace(SaveError);

    public bool IsSaving
    {
        get => _isSaving;
        private set => this.RaiseAndSetIfChanged(ref _isSaving, value);
    }

    public UiDensity Density
    {
        get => _density;
        set
        {
            if (_density == value)
                return;
            this.RaiseAndSetIfChanged(ref _density, value);
            this.RaisePropertyChanged(nameof(TabPadding));
            this.RaisePropertyChanged(nameof(TabCloseSize));
            this.RaisePropertyChanged(nameof(TabHeight));
            this.RaisePropertyChanged(nameof(SecondaryPadding));
            this.RaisePropertyChanged(nameof(SecondaryControlHeight));
            this.RaisePropertyChanged(nameof(SecondaryCloseSize));
            this.RaisePropertyChanged(nameof(SecondaryFontSize));
            this.RaisePropertyChanged(nameof(SecondaryTabHeight));
            if (!_syncing)
                _ = CommitAsync(_owner.State.Settings with { Density = value });
        }
    }

    public Thickness TabPadding =>
        Density switch
        {
            UiDensity.Compact => new(4, 2),
            UiDensity.Cozy => new(6, 4),
            _ => new(10, 6),
        };

    public double TabCloseSize =>
        Density switch
        {
            UiDensity.Compact => 20,
            UiDensity.Cozy => 24,
            _ => 28,
        };

    public double TabHeight =>
        Density switch
        {
            UiDensity.Compact => 22,
            UiDensity.Cozy => 28,
            _ => 30,
        };

    public double FileStripHeight =>
        Density switch
        {
            UiDensity.Compact => 30,
            UiDensity.Cozy => 34,
            _ => 38,
        };

    public double FileStripFontSize =>
        Density switch
        {
            UiDensity.Compact => 14,
            UiDensity.Cozy => 16,
            _ => 18,
        };

    public double SearchTabHeight =>
        Density switch
        {
            UiDensity.Compact => 26,
            UiDensity.Cozy => 30,
            _ => 34,
        };

    public Thickness SecondaryPadding => new(TabPadding.Left * 0.6, TabPadding.Top * 0.6);

    public double SecondaryControlHeight =>
        Density switch
        {
            UiDensity.Compact => 22,
            UiDensity.Cozy => 28,
            _ => 32,
        };

    public double SecondaryCloseSize => TabCloseSize * 0.8;

    public double SecondaryCloseButtonSize => TabHeight * 0.6;

    public double SecondaryFontSize =>
        Density switch
        {
            UiDensity.Compact => 12,
            UiDensity.Cozy => 14,
            _ => 16,
        };

    public double SecondaryTabHeight => TabHeight * 0.8;

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

    public AppTimeZoneMode TimeZoneMode
    {
        get => _timeZoneMode;
        set
        {
            if (_timeZoneMode == value)
                return;
            this.RaiseAndSetIfChanged(ref _timeZoneMode, value);
            if (!_syncing)
                _ = CommitAsync(_owner.State.Settings with { TimeZoneMode = value });
        }
    }

    public ThemeOption SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (_selectedTheme == value)
                return;
            this.RaiseAndSetIfChanged(ref _selectedTheme, value);
            if (!_syncing)
                _ = CommitAsync(_owner.State.Settings with { Theme = value.Id });
        }
    }

    internal void Sync(AppSettings settings)
    {
        _syncing = true;
        try
        {
            Density = settings.Density;
            FontSize = settings.LogFontSize;
            TimeZoneMode = settings.TimeZoneMode;
            SelectedTheme = ThemeOptions.First(option =>
                option.Id == ThemeCatalog.Normalize(settings.Theme)
            );
        }
        finally
        {
            _syncing = false;
        }

        var persistedConnectionIds = settings
            .ElasticConnections.Select(connection => connection.Id)
            .ToHashSet(StringComparer.Ordinal);
        for (var index = ElasticConnections.Count - 1; index >= 0; index--)
            if (
                _syncedElasticConnectionIds.Contains(ElasticConnections[index].Id)
                && !persistedConnectionIds.Contains(ElasticConnections[index].Id)
            )
                ElasticConnections.RemoveAt(index);
        foreach (var connection in settings.ElasticConnections)
        {
            if (ElasticConnections.Any(editor => editor.Id == connection.Id))
                continue;
            var editor = new ElasticConnectionEditorViewModel(this, connection.Id);
            ElasticConnections.Add(editor);
            editor.Sync(connection);
        }
        _syncedElasticConnectionIds.Clear();
        foreach (var id in persistedConnectionIds)
            _syncedElasticConnectionIds.Add(id);
        if (
            SelectedElasticConnection is null
            || !ElasticConnections.Contains(SelectedElasticConnection)
        )
            SelectedElasticConnection = ElasticConnections.FirstOrDefault();
    }

    internal async Task CommitAsync(AppSettings settings)
    {
        IsSaving = true;
        SaveError = null;
        try
        {
            await _owner.UpdateSettingsAsync(settings);
        }
        catch (Exception ex)
        {
            SaveError = ex.Message;
        }
        finally
        {
            IsSaving = false;
        }
    }

    internal AppSettings ConnectionSettingsWith(ElasticConnectionSettings connection, string secret)
    {
        _ = secret;
        return _owner.State.Settings with
        {
            ElasticConnections = _owner
                .State.Settings.ElasticConnections.Where(item => item.Id != connection.Id)
                .Append(connection)
                .ToList(),
        };
    }

    internal Task SaveElasticConnectionAsync(ElasticConnectionSettings connection, string secret) =>
        _owner.State.SaveElasticConnectionAsync(connection, secret).AsTask();

    internal Task<IReadOnlyList<ElasticDataViewSummary>> GetDataViewsAsync(
        ElasticConnectionSettings connection,
        string? secret = null
    ) => _owner.State.GetDataViewsAsync(connection, secret);

    internal Task CheckElasticsearchAsync(
        ElasticConnectionSettings connection,
        string? secret = null
    ) => _owner.State.CheckElasticsearchAsync(connection, secret);

    internal Task<ElasticDataView> GetDataViewAsync(
        ElasticConnectionSettings connection,
        string dataViewId,
        string? secret = null
    ) => _owner.State.GetDataViewAsync(connection, dataViewId, secret);

    private void AddElasticConnection()
    {
        var editor = new ElasticConnectionEditorViewModel(this, Guid.NewGuid().ToString("N"));
        ElasticConnections.Add(editor);
        SelectedElasticConnection = editor;
    }

    private Task RemoveElasticConnectionAsync(ElasticConnectionEditorViewModel editor)
    {
        CloseConfirmationVisible = false;
        _pendingDelete = editor;
        this.RaisePropertyChanged(nameof(DeleteConfirmationVisible));
        this.RaisePropertyChanged(nameof(DeleteConfirmationMessage));
        return Task.CompletedTask;
    }

    private void CancelDelete()
    {
        _pendingDelete = null;
        this.RaisePropertyChanged(nameof(DeleteConfirmationVisible));
        this.RaisePropertyChanged(nameof(DeleteConfirmationMessage));
    }

    private async Task ConfirmDeleteAsync()
    {
        if (_pendingDelete is not { } editor || editor.IsSaving)
            return;
        try
        {
            await _owner.State.RemoveElasticConnectionAsync(editor.Id);
            ElasticConnections.Remove(editor);
            SelectedElasticConnection = ElasticConnections.FirstOrDefault();
            SaveError = null;
            CancelDelete();
        }
        catch (Exception exception)
        {
            SaveError = exception.Message;
        }
    }
}
