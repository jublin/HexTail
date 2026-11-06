using System.Reactive;
using Avalonia.Media;
using HexTail.Domain;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal sealed class RuleChipViewModel : ReactiveObject
{
    private readonly FilterBarViewModel _owner;
    private RuleDraftViewModel? _draft;
    private bool _isEditorOpen;

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
        ApplyCommand = ReactiveCommand.CreateFromTask(
            () => _owner.ApplyAsync(this),
            owner.Scheduler
        );
        CancelCommand = ReactiveCommand.Create(
            () =>
            {
                IsEditorOpen = false;
            },
            owner.Scheduler
        );
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

    /// <summary>The stored rule. Synthetic (built from <see cref="PinnedSearch"/>) when a pinned tab is set: use <see cref="IsRuleChip"/> before editing it.</summary>
    public ViewRule Rule { get; }

    public Search? PinnedSearch { get; }

    /// <summary>True for a real stored rule; false for a pinned-tab chip whose <see cref="Rule"/> is a placeholder.</summary>
    public bool IsRuleChip => PinnedSearch is null;

    public string Pattern { get; }
    public string Text => Pattern.Length <= 28 ? Pattern : Pattern[..27] + "…";
    public bool IsGlobal => IsRuleChip && Rule.Scope == RuleScope.Global;
    public bool IsDisabled => IsRuleChip && !Rule.Enabled;
    public bool IsHide => IsRuleChip && Rule.Action == RuleAction.Hide;
    public bool IsShowOnly => IsRuleChip && Rule.Action == RuleAction.ShowOnly;
    public bool IsHighlight => !IsHide && !IsShowOnly;
    public bool IsPinnedTab => !IsRuleChip || Rule.PinnedAsTab;
    public bool HasSwatch => !IsHide;
    public IBrush SwatchBrush =>
        new SolidColorBrush(Color.TryParse(Rule.Color, out var color) ? color : Colors.Gray);
    public string ActionLabel =>
        !IsRuleChip ? "Tab"
        : IsHide ? "Hide"
        : IsShowOnly ? "Show only"
        : "Highlight";
    public string ScopeLabel =>
        !IsRuleChip ? "this source tab"
        : IsGlobal ? "all sources"
        : "this source";
    public string ToolTip =>
        $"{ActionLabel} · {ScopeLabel} · {Rule.Mode.ToString().ToLowerInvariant()}"
        + (IsDisabled ? " · disabled" : string.Empty);
    public string AutomationName =>
        $"{ActionLabel} {Pattern}, {ScopeLabel}{(IsDisabled ? ", disabled" : string.Empty)}";

    /// <summary>The body button edits a rule chip but only selects the tab of a pinned-tab chip.</summary>
    public string BodyAutomationName => IsRuleChip ? $"Edit {Text}" : $"Select tab {Text}";

    /// <summary>Editable copy of the rule; null for pinned-tab chips, which have no editor.</summary>
    public RuleDraftViewModel? Draft
    {
        get => _draft ??= IsRuleChip ? new RuleDraftViewModel(Rule, _owner.Scheduler) : null;
        private set => this.RaiseAndSetIfChanged(ref _draft, value);
    }

    public bool IsEditorOpen
    {
        get => _isEditorOpen;
        internal set => this.RaiseAndSetIfChanged(ref _isEditorOpen, value);
    }

    public ReactiveCommand<Unit, Unit> ToggleEnabledCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
    public ReactiveCommand<Unit, Unit> ActivateCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>Opens the editor on a fresh draft; a no-op when already open or for pinned-tab chips.</summary>
    internal void OpenEditor()
    {
        if (!IsRuleChip || IsEditorOpen)
            return;
        Draft = new RuleDraftViewModel(Rule, _owner.Scheduler);
        IsEditorOpen = true;
    }
}
