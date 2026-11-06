using System.Reactive;
using Avalonia.Media;
using HexTail.Domain;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal sealed class RuleChipViewModel : ReactiveObject
{
    private readonly FilterBarViewModel _owner;

    internal RuleChipViewModel(FilterBarViewModel owner, ViewRule rule)
    {
        _owner = owner;
        Rule = rule;
        Pattern = rule.Pattern;
        ToggleEnabledCommand = ReactiveCommand.CreateFromTask(
            () => _owner.ToggleAsync(this),
            owner.Scheduler
        );
        RemoveCommand = ReactiveCommand.CreateFromTask(
            () => _owner.RemoveAsync(this),
            owner.Scheduler
        );
        ActivateCommand = ReactiveCommand.Create(() => _owner.Activate(this), owner.Scheduler);
    }

    internal RuleChipViewModel(FilterBarViewModel owner, Search search)
        : this(
            owner,
            new ViewRule
            {
                Pattern = search.Query.Query,
                Mode = search.Query.Mode,
                CaseSensitive = search.Query.CaseSensitive,
                Color = search.Color,
            }
        ) => PinnedSearch = search;

    public ViewRule? Rule { get; }
    public Search? PinnedSearch { get; }
    public string Pattern { get; }
    public string Text => Pattern.Length <= 28 ? Pattern : Pattern[..27] + "…";
    public bool IsGlobal => PinnedSearch is null && Rule?.Scope == RuleScope.Global;
    public bool IsDisabled => PinnedSearch is null && Rule is { Enabled: false };
    public bool IsHide => PinnedSearch is null && Rule?.Action == RuleAction.Hide;
    public bool IsShowOnly => PinnedSearch is null && Rule?.Action == RuleAction.ShowOnly;
    public bool IsHighlight => !IsHide && !IsShowOnly;
    public bool IsPinnedTab => PinnedSearch is not null || Rule is { PinnedAsTab: true };
    public bool HasSwatch => !IsHide;
    public IBrush SwatchBrush =>
        new SolidColorBrush(
            Color.TryParse(PinnedSearch?.Color ?? Rule?.Color, out var color) ? color : Colors.Gray
        );
    public string ActionLabel =>
        PinnedSearch is not null ? "Tab"
        : IsHide ? "Hide"
        : IsShowOnly ? "Show only"
        : "Highlight";
    public string ScopeLabel =>
        PinnedSearch is not null ? "this source tab"
        : IsGlobal ? "all sources"
        : "this source";
    public string ToolTip =>
        $"{ActionLabel} · {ScopeLabel} · {(Rule?.Mode ?? PinnedSearch!.Query.Mode).ToString().ToLowerInvariant()}"
        + (IsDisabled ? " · disabled" : string.Empty);
    public string AutomationName =>
        $"{ActionLabel} {Pattern}, {ScopeLabel}{(IsDisabled ? ", disabled" : string.Empty)}";

    public ReactiveCommand<Unit, Unit> ToggleEnabledCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
    public ReactiveCommand<Unit, Unit> ActivateCommand { get; }
}
