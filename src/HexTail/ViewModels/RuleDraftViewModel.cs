using System.Reactive;
using System.Reactive.Concurrency;
using Avalonia.Data.Converters;
using Avalonia.Media;
using HexTail.Domain;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

/// <summary>Editable copy of a rule shown in a chip's popover; nothing is stored until it is applied.</summary>
internal sealed class RuleDraftViewModel : ReactiveObject
{
    private readonly ViewRule _original;
    private string _pattern;
    private MatchMode _matchMode;
    private bool _caseSensitive;
    private RuleAction _action;
    private bool _allSources;
    private string _color;
    private bool _pinnedAsTab;
    private bool _enabled;
    private string? _saveError;

    internal RuleDraftViewModel(ViewRule original, IScheduler? scheduler = null)
    {
        _original = original;
        _pattern = original.Pattern;
        _matchMode = original.Mode;
        _caseSensitive = original.CaseSensitive;
        _action = original.Action;
        _allSources = original.Scope == RuleScope.Global;
        _color = original.Color;
        _pinnedAsTab = original.PinnedAsTab;
        _enabled = original.Enabled;
        SwatchOptions = MainWindowViewModel
            .Swatches.Select(color => new SwatchOption(color))
            .ToList();
        UpdateSelectedSwatch();
        PickSwatchCommand = ReactiveCommand.Create<string>(
            PickSwatch,
            scheduler ?? ImmediateScheduler.Instance
        );
    }

    public IReadOnlyList<string> Swatches => MainWindowViewModel.Swatches;
    public IReadOnlyList<MatchMode> MatchModes { get; } = [MatchMode.Literal, MatchMode.Regex];
    public IReadOnlyList<RuleAction> Actions { get; } =
    [RuleAction.Highlight, RuleAction.ShowOnly, RuleAction.Hide];

    /// <summary>The swatches with a flag marking the one equal to the draft color.</summary>
    public IReadOnlyList<SwatchOption> SwatchOptions { get; }

    /// <summary>Display text for the action picker.</summary>
    public static FuncValueConverter<RuleAction, string> ActionText { get; } =
        new(action =>
            action switch
            {
                RuleAction.ShowOnly => "Show only",
                RuleAction.Hide => "Hide",
                _ => "Highlight",
            }
        );

    public ReactiveCommand<string, Unit> PickSwatchCommand { get; }

    /// <summary>Why the last apply failed; shown inside the editor and cleared by the next edit.</summary>
    public string? SaveError
    {
        get => _saveError;
        set
        {
            this.RaiseAndSetIfChanged(ref _saveError, value);
            this.RaisePropertyChanged(nameof(HasSaveError));
        }
    }

    public bool HasSaveError => !string.IsNullOrWhiteSpace(SaveError);

    public string Pattern
    {
        get => _pattern;
        set
        {
            this.RaiseAndSetIfChanged(ref _pattern, value);
            RaiseValidity();
        }
    }

    public MatchMode MatchMode
    {
        get => _matchMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _matchMode, value);
            RaiseValidity();
        }
    }

    public bool CaseSensitive
    {
        get => _caseSensitive;
        set
        {
            this.RaiseAndSetIfChanged(ref _caseSensitive, value);
            SaveError = null;
        }
    }

    public RuleAction Action
    {
        get => _action;
        set
        {
            this.RaiseAndSetIfChanged(ref _action, value);
            this.RaisePropertyChanged(nameof(CanPin));
            this.RaisePropertyChanged(nameof(HasColor));
            SaveError = null;
        }
    }

    public bool AllSources
    {
        get => _allSources;
        set
        {
            this.RaiseAndSetIfChanged(ref _allSources, value);
            this.RaisePropertyChanged(nameof(CanPin));
            SaveError = null;
        }
    }

    public string Color
    {
        get => _color;
        set
        {
            this.RaiseAndSetIfChanged(ref _color, value);
            this.RaisePropertyChanged(nameof(PickerColor));
            UpdateSelectedSwatch();
            SaveError = null;
        }
    }

    /// <summary>The color as an Avalonia value, for the custom ColorPicker.</summary>
    public Avalonia.Media.Color PickerColor
    {
        get => Avalonia.Media.Color.TryParse(_color, out var color) ? color : Colors.Gray;
        set => Color = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
    }

    /// <summary>A disabled rule is kept but has no effect; the keyboard way to what a middle-click on the chip does.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _enabled, value);
            SaveError = null;
        }
    }

    public bool PinnedAsTab
    {
        get => _pinnedAsTab;
        set
        {
            this.RaiseAndSetIfChanged(ref _pinnedAsTab, value);
            SaveError = null;
        }
    }

    public bool HasColor => Action != RuleAction.Hide;
    public bool CanPin => AllSources && Action != RuleAction.Hide;
    public string? Error => RuleValidation.Validate(Pattern.Trim(), MatchMode);
    public bool HasError => Error is not null;
    public bool CanSave => Pattern.Trim().Length > 0 && Error is null;

    public void PickSwatch(string color) => Color = color;

    public ViewRule ToRule() =>
        _original with
        {
            Pattern = Pattern.Trim(),
            Mode = MatchMode,
            CaseSensitive = CaseSensitive,
            Action = Action,
            Scope = AllSources ? RuleScope.Global : RuleScope.Source,
            Color = Color,
            Enabled = Enabled,
            PinnedAsTab = CanPin && PinnedAsTab,
        };

    private void UpdateSelectedSwatch()
    {
        foreach (var option in SwatchOptions)
            option.IsSelected = string.Equals(
                option.Color,
                _color,
                StringComparison.OrdinalIgnoreCase
            );
    }

    private void RaiseValidity()
    {
        SaveError = null;
        this.RaisePropertyChanged(nameof(Error));
        this.RaisePropertyChanged(nameof(HasError));
        this.RaisePropertyChanged(nameof(CanSave));
    }
}

/// <summary>One color swatch in the editor; <see cref="IsSelected"/> marks the draft's current color.</summary>
internal sealed class SwatchOption(string color) : ReactiveObject
{
    private bool _isSelected;

    public string Color { get; } = color;

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}
