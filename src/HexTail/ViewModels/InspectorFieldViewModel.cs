using System.Reactive;
using HexTail.Domain;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal sealed class InspectorFieldViewModel : ReactiveObject
{
    internal InspectorFieldViewModel(InspectorViewModel owner, string key, string value)
    {
        Owner = owner;
        Key = key;
        Value = value;
        ShowOnlyCommand = ReactiveCommand.CreateFromTask(
            () => owner.ApplyFieldRuleAsync(this, RuleAction.ShowOnly),
            owner.Owner.Scheduler
        );
        HideCommand = ReactiveCommand.CreateFromTask(
            () => owner.ApplyFieldRuleAsync(this, RuleAction.Hide),
            owner.Owner.Scheduler
        );
        HighlightCommand = ReactiveCommand.CreateFromTask(
            () => owner.ApplyFieldRuleAsync(this, RuleAction.Highlight),
            owner.Owner.Scheduler
        );
    }

    internal InspectorViewModel Owner { get; }
    public string Key { get; }
    public string Value { get; }
    public ReactiveCommand<Unit, Unit> ShowOnlyCommand { get; }
    public ReactiveCommand<Unit, Unit> HideCommand { get; }
    public ReactiveCommand<Unit, Unit> HighlightCommand { get; }
}
