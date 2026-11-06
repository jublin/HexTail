using System.Collections.ObjectModel;
using System.Reactive.Concurrency;
using Avalonia;
using Avalonia.Threading;
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
    private decimal? _maxLinesValue;
    private decimal? _contextAboveValue;
    private decimal? _contextBelowValue;
    private decimal? _elasticInitialLinesValue;
    private ThemeOption _selectedTheme;
    private AppTimeZoneMode _timeZoneMode;
    private readonly HashSet<string> _syncedElasticConnectionIds = new(StringComparer.Ordinal);

    internal SettingsViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
        _selectedTheme = ThemeOptions[0];
    }

    public ObservableCollection<ElasticConnectionEditorViewModel> ElasticConnections { get; } = [];

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
                1 => "limits",
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

    public decimal? MaxLinesValue
    {
        get => _maxLinesValue;
        set =>
            SetLimit(
                v => _maxLinesValue = v,
                value,
                nameof(MaxLinesValue),
                _owner.State.Settings.MaxLines,
                1_000,
                1_000_000,
                (settings, limit) => settings with { MaxLines = limit }
            );
    }

    public decimal? ContextAboveValue
    {
        get => _contextAboveValue;
        set =>
            SetLimit(
                v => _contextAboveValue = v,
                value,
                nameof(ContextAboveValue),
                _owner.State.Settings.ContextAbove,
                0,
                50,
                (settings, limit) => settings with { ContextAbove = limit }
            );
    }

    public decimal? ContextBelowValue
    {
        get => _contextBelowValue;
        set =>
            SetLimit(
                v => _contextBelowValue = v,
                value,
                nameof(ContextBelowValue),
                _owner.State.Settings.ContextBelow,
                0,
                50,
                (settings, limit) => settings with { ContextBelow = limit }
            );
    }

    public decimal? ElasticInitialLinesValue
    {
        get => _elasticInitialLinesValue;
        set =>
            SetLimit(
                v => _elasticInitialLinesValue = v,
                value,
                nameof(ElasticInitialLinesValue),
                _owner.State.Settings.ElasticInitialLines,
                100,
                100_000,
                (settings, limit) => settings with { ElasticInitialLines = limit }
            );
    }

    /// <summary>
    /// Shared setter for the numeric limits. A cleared field restores the current setting without
    /// committing; anything else is clamped, re-synced into the field and committed.
    /// </summary>
    private void SetLimit(
        Action<decimal?> store,
        decimal? value,
        string property,
        int current,
        int minimum,
        int maximum,
        Func<AppSettings, int, AppSettings> apply
    )
    {
        if (_syncing)
        {
            store(value);
            this.RaisePropertyChanged(property);
            return;
        }

        var restored = value is null;
        var limit = restored
            ? current
            : (int)Math.Clamp(Math.Floor(value!.Value), minimum, maximum);
        store(limit);
        this.RaisePropertyChanged(property);
        if (value != limit)
        {
            // A binding ignores notifications raised while it is writing the value back, so a
            // control still showing the rejected value (cleared, or out of range) is refreshed
            // once that settles. The intermediate null makes the refresh a real change.
            Dispatcher.UIThread.Post(() =>
            {
                store(null);
                this.RaisePropertyChanged(property);
                store(limit);
                this.RaisePropertyChanged(property);
            });
        }
        if (!restored && limit != current)
            _ = CommitAsync(apply(_owner.State.Settings, limit));
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
            MaxLinesValue = settings.MaxLines;
            ContextAboveValue = settings.ContextAbove;
            ContextBelowValue = settings.ContextBelow;
            ElasticInitialLinesValue = settings.ElasticInitialLines;
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

    internal ElasticConnectionSettings? SavedConnection(string connectionId) =>
        _owner.State.Settings.ElasticConnections.FirstOrDefault(connection =>
            connection.Id == connectionId
        );

    internal List<ElasticViewSettings> SavedViews(string connectionId) =>
        SavedConnection(connectionId)?.Views.ToList() ?? [];

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

    internal IScheduler Scheduler => _owner.Scheduler;

    private const int PreviewSize = 5;
    private static readonly TimeSpan CountWindow = TimeSpan.FromMinutes(15);

    internal Task<long> CountAsync(
        ElasticConnectionSettings connection,
        string title,
        string timeField,
        string field,
        string value,
        string? secret = null
    )
    {
        var to = DateTimeOffset.UtcNow;
        return _owner.State.CountAsync(
            connection,
            title,
            timeField,
            field,
            value,
            to - CountWindow,
            to,
            secret
        );
    }

    internal Task<IReadOnlyList<ElasticHit>> PreviewAsync(
        ElasticConnectionSettings connection,
        string title,
        string timeField,
        string? field,
        string? value,
        IReadOnlyList<string> outputFields,
        string? secret = null
    ) =>
        _owner.State.PreviewAsync(
            connection,
            title,
            timeField,
            field,
            value,
            outputFields,
            PreviewSize,
            secret
        );

    internal ElasticConnectionEditorViewModel AddElasticConnection()
    {
        var editor = new ElasticConnectionEditorViewModel(this, Guid.NewGuid().ToString("N"));
        ElasticConnections.Add(editor);
        return editor;
    }

    internal ElasticConnectionEditorViewModel? EditorFor(string connectionId) =>
        ElasticConnections.FirstOrDefault(editor => editor.Id == connectionId);

    /// <summary>Drops an editor that was never saved (the setup flow was closed before its first save).</summary>
    internal void DiscardUnsavedElasticConnection(ElasticConnectionEditorViewModel editor)
    {
        if (!editor.IsSaved)
            ElasticConnections.Remove(editor);
    }

    /// <summary>
    /// Replaces an editor holding abandoned edits (fields, views, a typed secret) with a fresh one
    /// built from the persisted settings. Does nothing while the editor is saving or when the
    /// connection was never saved.
    /// </summary>
    internal void RevertElasticConnection(ElasticConnectionEditorViewModel editor)
    {
        if (editor.IsSaving)
            return;
        var index = ElasticConnections.IndexOf(editor);
        if (index < 0 || SavedConnection(editor.Id) is not { } saved)
            return;
        var fresh = new ElasticConnectionEditorViewModel(this, editor.Id);
        fresh.Sync(saved);
        ElasticConnections[index] = fresh;
    }
}
