using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Concurrency;
using HexTail.Domain;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

/// <summary>Projects every active rule (global, source, pinned tab) into chips for the filter bar.</summary>
internal sealed class FilterBarViewModel : ReactiveObject
{
    private readonly MainWindowViewModel _owner;
    private List<(ChipKey Key, RuleChipViewModel Chip)> _entries = [];
    private string _query = string.Empty;
    private MatchMode _matchMode = MatchMode.Literal;
    private bool _caseSensitive;
    private bool _pinAsTab;
    private string? _error;
    private bool _submitting;
    private string _newFilterName = string.Empty;
    private string? _saveError;
    private bool _saveFlyoutOpen;
    private bool _savingFilter;

    internal FilterBarViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
        LevelToggles =
        [
            new LevelToggleViewModel(this, LogLevels.Trace, "T", "Trace"),
            new LevelToggleViewModel(this, LogLevels.Debug, "D", "Debug"),
            new LevelToggleViewModel(this, LogLevels.Info, "I", "Info"),
            new LevelToggleViewModel(this, LogLevels.Warn, "W", "Warn"),
            new LevelToggleViewModel(this, LogLevels.Error, "E", "Error"),
        ];
        SaveFilterCommand = ReactiveCommand.CreateFromTask(SaveFilterAsync, owner.Scheduler);
        CancelSaveFilterCommand = ReactiveCommand.Create(
            () =>
            {
                SaveFlyoutOpen = false;
            },
            owner.Scheduler
        );
    }

    internal IScheduler Scheduler => _owner.Scheduler;
    public ObservableCollection<RuleChipViewModel> Chips { get; } = [];
    public bool HasChips => Chips.Count > 0;
    public IReadOnlyList<LevelToggleViewModel> LevelToggles { get; }
    public bool HasLevelFilter =>
        _owner.SelectedFile?.Model.Levels is { } levels && levels != LogLevels.All;
    public bool CanSaveFilter =>
        _owner.SelectedFile?.Model is { } file && (file.Rules.Count > 0 || HasLevelFilter);
    public ReactiveCommand<Unit, Unit> SaveFilterCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelSaveFilterCommand { get; }
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

    public string NewFilterName
    {
        get => _newFilterName;
        set
        {
            this.RaiseAndSetIfChanged(ref _newFilterName, value);
            this.RaisePropertyChanged(nameof(HasFilterName));
            SaveError = null;
        }
    }

    public bool HasFilterName => !string.IsNullOrWhiteSpace(NewFilterName);

    /// <summary>Shown inside the save flyout, which covers the filter bar's own error line.</summary>
    public string? SaveError
    {
        get => _saveError;
        internal set
        {
            this.RaiseAndSetIfChanged(ref _saveError, value);
            this.RaisePropertyChanged(nameof(HasSaveError));
        }
    }

    public bool HasSaveError => !string.IsNullOrWhiteSpace(SaveError);

    public bool SaveFlyoutOpen
    {
        get => _saveFlyoutOpen;
        internal set => this.RaiseAndSetIfChanged(ref _saveFlyoutOpen, value);
    }

    // ViewRule compares by value, Search by reference: that keeps Sync cheap and stable.
    private readonly record struct ChipKey(ViewRule? Rule, Search? Search);

    internal bool IsLevelOn(LogLevels level) =>
        _owner.SelectedFile is { } file && (file.Model.Levels & level) != 0;

    internal async Task ToggleLevelAsync(LevelToggleViewModel toggle)
    {
        var file = _owner.SelectedFile?.Model;
        if (file is null)
            return;
        var next = file.Levels ^ toggle.Level;
        if ((next & LogLevels.All) == LogLevels.None)
        {
            // The button already flipped itself locally; no state change means no Sync, so re-assert.
            toggle.Refresh();
            return;
        }
        await _owner.State.SetLevelsAsync(file, next);
    }

    internal void Sync()
    {
        // The level control follows the selected source on every sync, chips changed or not.
        foreach (var toggle in LevelToggles)
            toggle.Refresh();
        this.RaisePropertyChanged(nameof(HasLevelFilter));
        this.RaisePropertyChanged(nameof(CanSaveFilter));
        SyncChips();
    }

    private void SyncChips()
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
        if (keys.SequenceEqual(_entries.Select(entry => entry.Key)))
            return;

        // Reuse the chip of every key that survives, so an open editor (and its focus) is not
        // torn down by an unrelated change. Duplicate keys are consumed one chip at a time.
        var available = _entries.ToList();
        var next = new List<(ChipKey Key, RuleChipViewModel Chip)>(keys.Count);
        foreach (var key in keys)
        {
            var index = available.FindIndex(entry => entry.Key == key);
            if (index >= 0)
            {
                next.Add(available[index]);
                available.RemoveAt(index);
            }
            else
                next.Add(
                    (
                        key,
                        key.Search is { } search
                            ? new RuleChipViewModel(this, search)
                            : new RuleChipViewModel(this, key.Rule!)
                    )
                );
        }
        foreach (var gone in available)
            Chips.Remove(gone.Chip);
        for (var i = 0; i < next.Count; i++)
        {
            var chip = next[i].Chip;
            if (i < Chips.Count && ReferenceEquals(Chips[i], chip))
                continue;
            var current = Chips.IndexOf(chip);
            if (current >= 0)
                Chips.Move(current, i);
            else
                Chips.Insert(i, chip);
        }
        _entries = next;
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

    internal async Task SaveFilterAsync()
    {
        var file = _owner.SelectedFile?.Model;
        var name = NewFilterName.Trim();
        if (_savingFilter || file is null || name.Length == 0 || !CanSaveFilter)
            return;
        _savingFilter = true;
        SaveError = null;
        try
        {
            await _owner.State.SaveFilterAsync(name, file.Rules, file.Levels);
            NewFilterName = string.Empty;
            SaveFlyoutOpen = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SaveError = ex.Message;
        }
        finally
        {
            _savingFilter = false;
        }
    }

    internal Task ToggleAsync(RuleChipViewModel chip) =>
        chip.IsRuleChip
            ? _owner
                .State.ReplaceRuleAsync(
                    _owner.SelectedFile?.Model,
                    chip.Rule,
                    chip.Rule with
                    {
                        Enabled = !chip.Rule.Enabled,
                    }
                )
                .AsTask()
            : Task.CompletedTask;

    internal async Task RemoveAsync(RuleChipViewModel chip)
    {
        var file = _owner.SelectedFile?.Model;
        if (chip.PinnedSearch is { } search)
        {
            if (file is not null)
                await _owner.State.RemoveSearchAsync(file, search);
            return;
        }
        await _owner.State.RemoveRuleAsync(file, chip.Rule);
        // Removing from inside the editor must also dismiss its flyout.
        chip.IsEditorOpen = false;
    }

    internal async Task ApplyAsync(RuleChipViewModel chip)
    {
        if (!chip.IsRuleChip || chip.Draft is not { CanSave: true } draft)
            return;
        draft.SaveError = null;
        Error = null;
        try
        {
            await _owner.State.ReplaceRuleAsync(
                _owner.SelectedFile?.Model,
                chip.Rule,
                draft.ToRule()
            );
            chip.IsEditorOpen = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The flyout covers the filter bar's own error line, so report inside the editor.
            draft.SaveError = ex.Message;
        }
    }

    internal void Activate(RuleChipViewModel chip)
    {
        if (chip.IsRuleChip)
        {
            chip.OpenEditor();
            return;
        }
        if (_owner.SelectedFile is not { } file)
            return;
        var index = file
            .Views.ToList()
            .FindIndex(view => ReferenceEquals(view.Search, chip.PinnedSearch));
        if (index >= 0)
            file.SelectedViewIndex = index;
    }
}
