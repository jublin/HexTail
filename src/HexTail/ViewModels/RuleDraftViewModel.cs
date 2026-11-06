using System.Reactive;
using System.Reactive.Concurrency;
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
        PickSwatchCommand = ReactiveCommand.Create<string>(
            PickSwatch,
            scheduler ?? ImmediateScheduler.Instance
        );
    }

    public IReadOnlyList<string> Swatches => MainWindowViewModel.Swatches;
    public IReadOnlyList<MatchMode> MatchModes { get; } = [MatchMode.Literal, MatchMode.Regex];
    public IReadOnlyList<RuleAction> Actions { get; } =
    [RuleAction.Highlight, RuleAction.ShowOnly, RuleAction.Hide];

    public ReactiveCommand<string, Unit> PickSwatchCommand { get; }

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
        set => this.RaiseAndSetIfChanged(ref _caseSensitive, value);
    }

    public RuleAction Action
    {
        get => _action;
        set
        {
            this.RaiseAndSetIfChanged(ref _action, value);
            this.RaisePropertyChanged(nameof(CanPin));
            this.RaisePropertyChanged(nameof(HasColor));
        }
    }

    public bool AllSources
    {
        get => _allSources;
        set
        {
            this.RaiseAndSetIfChanged(ref _allSources, value);
            this.RaisePropertyChanged(nameof(CanPin));
        }
    }

    public string Color
    {
        get => _color;
        set
        {
            this.RaiseAndSetIfChanged(ref _color, value);
            this.RaisePropertyChanged(nameof(PickerColor));
        }
    }

    /// <summary>The color as an Avalonia value, for the custom ColorPicker.</summary>
    public Avalonia.Media.Color PickerColor
    {
        get => Avalonia.Media.Color.TryParse(_color, out var color) ? color : Colors.Gray;
        set => Color = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
    }

    public bool PinnedAsTab
    {
        get => _pinnedAsTab;
        set => this.RaiseAndSetIfChanged(ref _pinnedAsTab, value);
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
            PinnedAsTab = CanPin && PinnedAsTab,
        };

    private void RaiseValidity()
    {
        this.RaisePropertyChanged(nameof(Error));
        this.RaisePropertyChanged(nameof(HasError));
        this.RaisePropertyChanged(nameof(CanSave));
    }
}
