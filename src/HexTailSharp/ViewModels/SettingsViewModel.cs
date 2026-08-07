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
