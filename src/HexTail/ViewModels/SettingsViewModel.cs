using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Concurrency;
using Avalonia;
using Avalonia.Media;
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
    private string _newLabelText = string.Empty;
    private Color _newLabelColor = Color.Parse("#F59E0B");
    private string _newExclusionText = string.Empty;
    private string _section = "labels";
    private string? _saveError;
    private int _sectionIndex;
    private bool _isSaving;
    private bool _syncing;
    private ThemeOption _selectedTheme;
    private AppTimeZoneMode _timeZoneMode;
    private ElasticConnectionEditorViewModel? _selectedElasticConnection;
    private readonly HashSet<string> _syncedElasticConnectionIds = new(StringComparer.Ordinal);

    internal SettingsViewModel(MainWindowViewModel owner, IScheduler scheduler)
    {
        _owner = owner;
        AddLabelCommand = ReactiveCommand.Create(AddLabel, scheduler);
        RemoveLabelCommand = ReactiveCommand.Create<LabelSettingViewModel>(RemoveLabel, scheduler);
        AddExclusionCommand = ReactiveCommand.Create(AddExclusion, scheduler);
        RemoveExclusionCommand = ReactiveCommand.Create<ExclusionSettingViewModel>(
            RemoveExclusion,
            scheduler
        );
        AddElasticConnectionCommand = ReactiveCommand.Create(AddElasticConnection, scheduler);
        RemoveElasticConnectionCommand =
            ReactiveCommand.CreateFromTask<ElasticConnectionEditorViewModel>(
                RemoveElasticConnectionAsync,
                scheduler
            );
        _selectedTheme = ThemeOptions[0];
    }

    public ObservableCollection<LabelSettingViewModel> Labels { get; } = [];
    public ObservableCollection<ExclusionSettingViewModel> Exclusions { get; } = [];
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
    public ReactiveCommand<Unit, Unit> AddLabelCommand { get; }
    public ReactiveCommand<LabelSettingViewModel, Unit> RemoveLabelCommand { get; }
    public ReactiveCommand<Unit, Unit> AddExclusionCommand { get; }
    public ReactiveCommand<ExclusionSettingViewModel, Unit> RemoveExclusionCommand { get; }
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
            var index = Math.Clamp(value, 0, 2);
            if (_sectionIndex == index)
                return;
            this.RaiseAndSetIfChanged(ref _sectionIndex, index);
            _section = index switch
            {
                1 => "appearance",
                2 => "elastic",
                _ => "labels",
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
            this.RaisePropertyChanged(nameof(SearchInputPadding));
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

    public Thickness SearchInputPadding =>
        new(
            SecondaryPadding.Left,
            SecondaryPadding.Top,
            SecondaryCloseSize + 5,
            SecondaryPadding.Bottom
        );

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

    internal Task CommitLabelAsync(int index, string text, Color color, bool showInOpenFile)
    {
        var labels = _owner.State.Settings.GlobalLabels.ToList();
        if (index < 0 || index >= labels.Count)
            return Task.CompletedTask;
        labels[index] = new GlobalLabel
        {
            Text = text,
            Color = ColorToHex(color),
            ShowInOpenFile = showInOpenFile,
        };
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
                    new GlobalLabel
                    {
                        Text = NewLabelText,
                        Color = ColorToHex(NewLabelColor),
                        ShowInOpenFile = true,
                    },
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

    private async Task RemoveElasticConnectionAsync(ElasticConnectionEditorViewModel editor)
    {
        await _owner.State.RemoveElasticConnectionAsync(editor.Id);
        ElasticConnections.Remove(editor);
        SelectedElasticConnection = ElasticConnections.FirstOrDefault();
    }

    private static string ColorToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}

internal sealed class LabelSettingViewModel : ReactiveObject
{
    private readonly SettingsViewModel _owner;
    private string _text = string.Empty;
    private Color _color = Color.Parse("#F59E0B");
    private bool _showInOpenFile = true;
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
                _ = _owner.CommitLabelAsync(Index, value, Color, ShowInOpenFile);
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
                _ = _owner.CommitLabelAsync(Index, Text, value, ShowInOpenFile);
        }
    }

    public bool ShowInOpenFile
    {
        get => _showInOpenFile;
        set
        {
            if (!this.RaiseAndSetIfChanged(ref _showInOpenFile, value) || _syncing)
                return;
            _ = _owner.CommitLabelAsync(Index, Text, Color, value);
        }
    }

    internal void Sync(GlobalLabel label)
    {
        _syncing = true;
        try
        {
            Text = label.Text;
            Color = Avalonia.Media.Color.Parse(label.Color);
            ShowInOpenFile = label.ShowInOpenFile;
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
