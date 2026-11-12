using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Concurrency;
using HexTail.Application;
using HexTail.Domain;
using HexTail.Persistence;
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
        ClearTimeWindowCommand = ReactiveCommand.Create(
            () =>
            {
                if (_owner.SelectedFile?.Model is not { } tab)
                    return;
                try
                {
                    _owner.State.SetFileTimeWindow(tab, null);
                    tab.HistogramPrior = null;
                }
                catch (ArgumentException) { }
            },
            owner.Scheduler
        );
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

    /// <summary>One toggle per member of the selected stream tab; empty on every other tab.</summary>
    public ObservableCollection<SourceChipViewModel> SourceChips { get; } = [];

    /// <summary>The selected file tab's histogram time window, shown as a chip before the source chips.</summary>
    public bool HasTimeWindow => _owner.SelectedFile?.Model.TimeWindow is not null;

    public string TimeWindowLabel =>
        _owner.SelectedFile?.Model.TimeWindow is { } window
            ? $"Time {TimeRangeLabel.Describe(window.From.ToString("O", CultureInfo.InvariantCulture), window.To.ToString("O", CultureInfo.InvariantCulture), _owner.State.Settings.TimeZoneMode)}"
            : string.Empty;

    public ReactiveCommand<Unit, Unit> ClearTimeWindowCommand { get; }
    public bool HasSourceChips => SourceChips.Count > 0;
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

    // ViewRule compares by value, Search and FileTabState by reference: that keeps Sync cheap and
    // stable, and rebuilds every chip when the selected source changes so a chip never acts on
    // a file it was not created for.
    private readonly record struct ChipKey(ViewRule? Rule, Search? Search, FileTabState File);

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
        Error = null;
        try
        {
            await _owner.State.SetLevelsAsync(file, next);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The mask is already changed in memory; only the save failed. Report it on the bar
            // instead of letting the command's unobserved exception reach the UI thread.
            Error = ex.Message;
            toggle.Refresh();
        }
    }

    internal bool IsSourceShown(string tabId) =>
        _owner.SelectedFile?.Model.Stream is { } stream && !stream.HiddenSources.Contains(tabId);

    internal async Task ToggleSourceAsync(SourceChipViewModel chip)
    {
        var file = _owner.SelectedFile?.Model;
        if (file?.Stream is not { } stream || chip.TabId is not { } id)
        {
            chip.Refresh();
            return;
        }
        Error = null;
        try
        {
            var hidden = new HashSet<string>(stream.HiddenSources, StringComparer.Ordinal);
            if (!hidden.Remove(id))
                hidden.Add(id);
            await _owner.State.SetHiddenSourcesAsync(file, hidden);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex.Message;
        }
        finally
        {
            // The button flipped itself; put it back in step with the stream's real state.
            foreach (var other in SourceChips)
                other.Refresh();
        }
    }

    private void SyncSourceChips()
    {
        var stream = _owner.SelectedFile?.Model.Stream;
        if (stream is null)
        {
            if (SourceChips.Count > 0)
            {
                SourceChips.Clear();
                this.RaisePropertyChanged(nameof(HasSourceChips));
            }
            return;
        }
        var members = stream.Definition.Members;
        var open = stream.MemberIds;
        var wanted = new List<(string Key, string? TabId, string Name, string Color)>(
            members.Count
        );
        foreach (var member in members)
        {
            string? tabId = null;
            foreach (var id in open)
                if (stream.KeyOf(id) is { } key && StreamMember.KeyComparer.Equals(key, member.Key))
                {
                    tabId = id;
                    break;
                }
            wanted.Add(
                (
                    member.Key,
                    tabId,
                    tabId is null ? MissingName(member.Key) : stream.DisplayNameFor(tabId),
                    member.Color
                )
            );
        }
        var same =
            SourceChips.Count == wanted.Count
            && wanted
                .Select(
                    (item, index) =>
                        SourceChips[index].Key == item.Key
                        && SourceChips[index].TabId == item.TabId
                        && SourceChips[index].Name == item.Name
                        && SourceChips[index].Color == item.Color
                )
                .All(match => match);
        if (!same)
        {
            SourceChips.Clear();
            foreach (var item in wanted)
                SourceChips.Add(
                    new SourceChipViewModel(this, item.Key, item.TabId, item.Name, item.Color)
                );
            this.RaisePropertyChanged(nameof(HasSourceChips));
        }
        foreach (var chip in SourceChips)
            chip.RefreshIfChanged();
    }

    /// <summary>A readable name for a member that is not open: the file name, or the Elastic source id.</summary>
    private static string MissingName(string key)
    {
        var colon = key.IndexOf(':');
        var tail = colon >= 0 ? key[(colon + 1)..] : key;
        return key.StartsWith("file:", StringComparison.Ordinal)
            ? Path.GetFileName(tail) is { Length: > 0 } name
                ? name
                : tail
            : tail;
    }

    internal void Sync()
    {
        // The level control follows the selected source on every sync, chips changed or not.
        foreach (var toggle in LevelToggles)
            toggle.Refresh();
        this.RaisePropertyChanged(nameof(HasLevelFilter));
        this.RaisePropertyChanged(nameof(CanSaveFilter));
        this.RaisePropertyChanged(nameof(HasTimeWindow));
        this.RaisePropertyChanged(nameof(TimeWindowLabel));
        SyncChips();
        SyncSourceChips();
    }

    private void SyncChips()
    {
        var file = _owner.SelectedFile;
        var keys = new List<ChipKey>();
        if (file is not null)
        {
            var model = file.Model;
            keys.AddRange(
                _owner.State.Settings.Rules.Select(rule => new ChipKey(rule, null, model))
            );
            keys.AddRange(model.Rules.Select(rule => new ChipKey(rule, null, model)));
            keys.AddRange(
                model
                    .Searches.Where(search => !search.IsGlobalRule)
                    .Select(search => new ChipKey(null, search, model))
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
                            ? new RuleChipViewModel(this, key.File, search)
                            : new RuleChipViewModel(this, key.File, key.Rule!)
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

    internal ViewRule NewSourceRule(
        FileTabState file,
        string pattern,
        MatchMode mode,
        bool caseSensitive,
        RuleAction action
    )
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
        return new ViewRule
        {
            Pattern = pattern,
            Mode = mode,
            CaseSensitive = caseSensitive,
            Color = color,
            Action = action,
            Scope = RuleScope.Source,
        };
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
            var rule = NewSourceRule(file, pattern, MatchMode, CaseSensitive, action);
            if (PinAsTab && action == RuleAction.Highlight)
            {
                _owner.State.AddSearch(file, pattern, MatchMode, CaseSensitive, rule.Color);
                await _owner.State.SaveAsync();
            }
            else
                await _owner.State.AddRuleAsync(file, rule);
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

    internal async Task ToggleAsync(RuleChipViewModel chip)
    {
        if (!chip.IsRuleChip)
            return;
        Error = null;
        try
        {
            await _owner.State.ReplaceRuleAsync(
                chip.File,
                chip.Rule,
                chip.Rule with
                {
                    Enabled = !chip.Rule.Enabled,
                }
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex.Message;
        }
    }

    internal async Task RemoveAsync(RuleChipViewModel chip)
    {
        Error = null;
        try
        {
            if (chip.PinnedSearch is { } search)
            {
                await _owner.State.RemoveSearchAsync(chip.File, search);
                return;
            }
            await _owner.State.RemoveRuleAsync(chip.File, chip.Rule);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The rule is already gone in memory; only the save failed.
            Error = ex.Message;
        }
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
            await _owner.State.ReplaceRuleAsync(chip.File, chip.Rule, draft.ToRule());
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
