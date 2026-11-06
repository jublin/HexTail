using System.Collections.ObjectModel;
using System.Reactive.Concurrency;
using HexTail.Domain;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

/// <summary>Projects every active rule (global, source, pinned tab) into chips for the filter bar.</summary>
internal sealed class FilterBarViewModel : ReactiveObject
{
    private readonly MainWindowViewModel _owner;
    private IReadOnlyList<ChipKey> _lastKeys = [];

    internal FilterBarViewModel(MainWindowViewModel owner) => _owner = owner;

    internal IScheduler Scheduler => _owner.Scheduler;
    public ObservableCollection<RuleChipViewModel> Chips { get; } = [];
    public bool HasChips => Chips.Count > 0;

    // ViewRule compares by value, Search by reference: that keeps Sync cheap and stable.
    private readonly record struct ChipKey(ViewRule? Rule, Search? Search);

    internal void Sync()
    {
        var file = _owner.SelectedFile;
        var keys = new List<ChipKey>();
        if (file is not null)
        {
            keys.AddRange(_owner.State.Settings.Rules.Select(rule => new ChipKey(rule, null)));
            keys.AddRange(file.Model.Rules.Select(rule => new ChipKey(rule, null)));
            keys.AddRange(
                file.Model.Searches.Where(search => !search.IsGlobalRule)
                    .Select(search => new ChipKey(null, search))
            );
        }
        if (keys.SequenceEqual(_lastKeys))
            return;
        _lastKeys = keys;
        Chips.Clear();
        foreach (var key in keys)
            Chips.Add(
                key.Search is { } search
                    ? new RuleChipViewModel(this, search)
                    : new RuleChipViewModel(this, key.Rule!)
            );
        this.RaisePropertyChanged(nameof(HasChips));
    }

    internal Task ToggleAsync(RuleChipViewModel chip) =>
        chip.Rule is { } rule && chip.PinnedSearch is null
            ? _owner
                .State.ReplaceRuleAsync(
                    _owner.SelectedFile?.Model,
                    rule,
                    rule with
                    {
                        Enabled = !rule.Enabled,
                    }
                )
                .AsTask()
            : Task.CompletedTask;

    internal Task RemoveAsync(RuleChipViewModel chip)
    {
        var file = _owner.SelectedFile?.Model;
        if (chip.PinnedSearch is { } search)
            return file is null
                ? Task.CompletedTask
                : _owner.State.RemoveSearchAsync(file, search).AsTask();
        return chip.Rule is { } rule
            ? _owner.State.RemoveRuleAsync(file, rule).AsTask()
            : Task.CompletedTask;
    }

    internal void Activate(RuleChipViewModel chip)
    {
        if (chip.PinnedSearch is null || _owner.SelectedFile is not { } file)
            return;
        var index = file
            .Views.ToList()
            .FindIndex(view => ReferenceEquals(view.Search, chip.PinnedSearch));
        if (index >= 0)
            file.SelectedViewIndex = index;
    }
}
