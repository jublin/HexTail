using System.Reactive;
using System.Reactive.Subjects;
using HexTail.Domain;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal sealed class InspectorFieldViewModel : ReactiveObject
{
    private readonly BehaviorSubject<bool> _canAddColumn = new(false);
    private bool _isStructured;
    private bool _isColumn;

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
        AddColumnCommand = ReactiveCommand.CreateFromTask(
            () => owner.AddColumnAsync(this),
            _canAddColumn,
            owner.Owner.Scheduler
        );
        RefreshColumnState();
    }

    internal InspectorViewModel Owner { get; }
    public string Key { get; }
    public string Value { get; }
    public ReactiveCommand<Unit, Unit> ShowOnlyCommand { get; }
    public ReactiveCommand<Unit, Unit> HideCommand { get; }
    public ReactiveCommand<Unit, Unit> HighlightCommand { get; }
    public ReactiveCommand<Unit, Unit> AddColumnCommand { get; }

    /// <summary>True when the field is already a column of the source's layout.</summary>
    public bool IsColumn => _isColumn;

    public string AddColumnHeader => _isColumn ? "In columns" : "Add column";

    /// <summary>Contains the visible text, so a screen reader announces what the item shows.</summary>
    public string AddColumnName => $"{AddColumnHeader}: {Key}";

    public string? AddColumnTip =>
        _isStructured ? null : "Columns are available for structured sources";

    /// <summary>Re-reads whether the source is structured and whether this field is a column already.</summary>
    internal void RefreshColumnState()
    {
        var structured = Owner.IsStructuredSource;
        var isColumn = structured && Owner.HasColumn(Key);
        if (structured != _isStructured)
        {
            _isStructured = structured;
            this.RaisePropertyChanged(nameof(AddColumnTip));
        }
        if (isColumn != _isColumn)
        {
            _isColumn = isColumn;
            this.RaisePropertyChanged(nameof(IsColumn));
            this.RaisePropertyChanged(nameof(AddColumnHeader));
            this.RaisePropertyChanged(nameof(AddColumnName));
        }
        _canAddColumn.OnNext(structured && !isColumn);
    }
}
