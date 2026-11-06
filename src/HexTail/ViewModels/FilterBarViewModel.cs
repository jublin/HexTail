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
    private string _query = string.Empty;
    private MatchMode _matchMode = MatchMode.Literal;
    private bool _caseSensitive;
    private bool _pinAsTab;
    private string? _error;
    private bool _submitting;

    internal FilterBarViewModel(MainWindowViewModel owner) => _owner = owner;

    internal IScheduler Scheduler => _owner.Scheduler;
    public ObservableCollection<RuleChipViewModel> Chips { get; } = [];
    public bool HasChips => Chips.Count > 0;
    public SettingsViewModel Settings => _owner.Settings;
    public IReadOnlyList<MatchMode> MatchModes { get; } = [MatchMode.Literal, MatchMode.Regex];

    public event EventHandler? FocusRequested;

    internal void RequestFocus() => FocusRequested?.Invoke(this, EventArgs.Empty);

    public string Query
    {
        get => _query;
        set
        {
            this.RaiseAndSetIfChanged(ref _query, value);
            Error = null;
        }
    }

    public MatchMode MatchMode
    {
        get => _matchMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _matchMode, value);
            Error = null;
        }
    }

    public bool CaseSensitive
    {
        get => _caseSensitive;
        set => this.RaiseAndSetIfChanged(ref _caseSensitive, value);
    }

    public bool PinAsTab
    {
        get => _pinAsTab;
        set => this.RaiseAndSetIfChanged(ref _pinAsTab, value);
    }

    public string? Error
    {
        get => _error;
        internal set
        {
            this.RaiseAndSetIfChanged(ref _error, value);
            this.RaisePropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

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

    internal async Task SubmitAsync(RuleAction action)
    {
        var file = _owner.SelectedFile?.Model;
        var pattern = Query.Trim();
        if (_submitting || file is null || pattern.Length == 0)
            return;
        if (RuleValidation.Validate(pattern, MatchMode) is { } error)
        {
            Error = error;
            return;
        }
        _submitting = true;
        var submitted = Query;
        Query = string.Empty;
        try
        {
            var color = MainWindowViewModel.NextSearchColor(
                file.Searches.Select(search => search.Color)
                    .Concat(
                        file.Rules.Where(rule => rule.Action != RuleAction.Hide)
                            .Select(rule => rule.Color)
                    ),
                _owner
                    .State.Settings.Rules.Where(rule => rule.Action != RuleAction.Hide)
                    .Select(rule => rule.Color)
            );
            if (PinAsTab && action == RuleAction.Highlight)
            {
                _owner.State.AddSearch(file, pattern, MatchMode, CaseSensitive, color);
                await _owner.State.SaveAsync();
            }
            else
                await _owner.State.AddRuleAsync(
                    file,
                    new ViewRule
                    {
                        Pattern = pattern,
                        Mode = MatchMode,
                        CaseSensitive = CaseSensitive,
                        Color = color,
                        Action = action,
                        Scope = RuleScope.Source,
                    }
                );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (Query.Length == 0)
                Query = submitted;
            Error = ex.Message;
        }
        finally
        {
            _submitting = false;
        }
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
