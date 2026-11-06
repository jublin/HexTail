# UX Phase 2B — Filter Bar, Level Control, Saved Filters Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the rule engine from part A its UI: a chip row that shows every active rule (global, source, pinned tab), one input with Enter / Shift+Enter / Alt+Enter, a chip popover for editing, a T/D/I/W/E level control, saved filters in the sidebar, `j`/`k`/`n`/`N` keys, and remove Settings → Labels & Exclusions.

**Architecture:** A `FilterBarViewModel` (owned by `MainWindowViewModel.FilterBar`) projects `AppSettings.Rules` (Global), the selected source's `FileTabState.Rules` (Source) and its non-global `Search` objects (pinned tabs) into `RuleChipViewModel`s, rebuilt from `SyncFromState`. Rule edits go through new `AppState.AddRuleAsync / ReplaceRuleAsync / RemoveRuleAsync`, which pick the Global or Source list from `rule.Scope` and reuse `UpdateSettingsAsync` / `SetSourceRulesAsync`. The filter bar view sits in each view tab's content template, between the view toolbar and the log list, and replaces `SearchBar`. Saved filters are a new top-level `AppConfig.SavedFilters` list (additive, config version stays 2).

**Tech Stack:** .NET 10, Avalonia 12.1.1 (`Flyout`, `Rectangle` dashed stroke), ReactiveUI, xUnit v3 (`[Fact]`, `[AvaloniaFact]`), System.Text.Json.

This is part B of phase 2 of `docs/ux/2026-11-04-ux-proposals.md` (items 2, 4, 8). Part A (`docs/plans/2026-11-06-ux-phase-2a-rule-engine.md`) is merged. Part C (row inspector, Settings shrinks to Appearance / Limits / Advanced) is a separate plan.

## Global Constraints

- Branch `audit-ui-phase-2b`, cut from `origin/main` after PR #11. Never work on `main`. **Never `git push` unless the user explicitly says so.**
- Conventional Commits, one commit per task. End each commit message with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- A CSharpier pre-commit hook reformats staged C# on commit. `csharpier check` already fails on `src/HexTail/HexTail.csproj`; leave that file alone.
- Prefix shell commands with `rtk`. Explore with `sb` (`sb show <file> <Symbol>`, `sb callers <Symbol>`) before opening whole files.
- Build/test commands (run from the repo root):
  - `rtk dotnet build src/HexTail.Tests`
  - `rtk dotnet test src/HexTail.Tests --no-build --filter "FullyQualifiedName~<Name>"`
  - full suite: `rtk dotnet test src/HexTail.Tests --no-build`. Baseline at the start of this part: **442 passing, 53 build warnings** (do not add warnings).
- Tests must never touch the OS credential vault: every `new AppState(...)` in a test passes `credentials: new InMemoryCredentialVault()` (`TestWindow.Create*` already does).
- UI tests that append to a temp file use `TestWindow.CreatePolling(out var viewModel)`; waits go through `UiWait.Until(...)`; never assert immediately after an append.
- ReactiveUI commands keep the positional scheduler pattern (`ReactiveCommand.Create*(func, scheduler)`).
- Reflection bindings are used in XAML (compiled bindings are off). Every interactive control gets an `AutomationProperties.Name`. Any control that changes focus must keep the Settings focus rules in `MainWindow.axaml.cs` working (do not touch them).
- Old sessions must still load: no change to the v2 rule or tab JSON shapes; `savedFilters` is optional and tolerant (Task 6).
- Known flaky test: `AccessibilityTests.SettingsFocus…` (fixed in #11; if it flakes again, report it, do not paper over it).

### Decisions taken where the proposal left room (veto points)

1. **No rule `Id`.** A chip holds the stored `ViewRule` instance; edit/remove find the first rule with *value equality* in the list that `rule.Scope` selects. Identical duplicates are indistinguishable by design, which is harmless because they behave identically.
2. **`RuleScope.View` stays unstored.** Per-view pinned searches remain `Search` objects (view tabs, `PersistedSearch`). They appear as chips marked "tab" with select + remove only; they have no popover. Global rules with `PinnedAsTab` still own a tab and show as ordinary Global chips (their `Search` has `IsGlobalRule` and is skipped when building chips).
3. **Source-pinned rules stay unsupported** (decided in 2A). "Pin as tab" is an option on the input: with it on, a plain Enter creates a `Search` tab exactly as today. Shift+Enter and Alt+Enter ignore it. The popover offers "Show as a tab in every file" only for Global, non-Hide rules.
4. **New rules default to Source scope**, with an automatic color from the palette. The popover has an "All sources" switch that moves a rule between Source and Global (remove from one list, add to the other).
5. **Chip display:** global chips use a dashed outline, source chips a solid one; a disabled chip is dimmed with strikethrough text; Hide chips have no color swatch. Avalonia's `Border` cannot dash, so the outline is a `Rectangle` behind the content.
6. **Level control** toggles one flag per click on the selected source (`SetLevelsAsync`) and refuses to clear the last flag. Lines with no detectable level stay visible (2A decision 5).
7. **Saved filters** store Source-scope rules plus a level mask. *Apply* merges the rules into the selected source (value-deduped, rescoped to Source) and replaces the level mask only when the saved mask is not All. Names are unique case-insensitively; saving an existing name overwrites it. Global rules are not captured (they already apply everywhere).
8. **Settings sections become Appearance (0) and Elastic (1).** Part C reshapes Settings further; part B only deletes the Labels & Exclusions section.
9. **Global rules are only visible while a source is open** (they are chips on the bar). With no source open they are inert anyway. This is accepted; the empty state is unchanged.
10. **Keys** `j`/`k` move the selected row, `n`/`N` jump to the next / previous row that has at least one highlight in the current view. They only fire when no text input has focus (same rule as `f` and `g`).

### Not in part B (deferred again, still in `.superpowers/sdd/progress.md`)

Level cache as a lazily stored value on `Line` (replacing the `ConditionalWeakTable`), `IReadOnlyList<ViewRule>` on the persisted types, the 2A coverage-gap minors, a v3 migration-chain structure, rules for merged streams, row inspector and Settings shrink (part C).

## File Structure

| Path | Status | Responsibility |
| --- | --- | --- |
| `src/HexTail/Domain/RuleValidation.cs` | create | `RuleValidation.Validate(pattern, mode)` (moved out of `SettingsViewModel`) |
| `src/HexTail/Domain/Search.cs` | modify | rename `IsGlobalLabel` → `IsGlobalRule` |
| `src/HexTail/Application/RuleEditor.cs` | create | pure list edits: `Add`, `Replace`, `Remove` |
| `src/HexTail/Application/AppState.cs` | modify | `AddRuleAsync`, `ReplaceRuleAsync`, `RemoveRuleAsync`, saved filters |
| `src/HexTail/Persistence/AppConfig.cs` | modify | `SavedFilter`, `AppConfig.SavedFilters`, tolerant sanitize |
| `src/HexTail/ViewModels/RuleChipViewModel.cs` | create | one chip (rule or pinned search) |
| `src/HexTail/ViewModels/RuleDraftViewModel.cs` | create | popover draft: validate, build the replacement rule |
| `src/HexTail/ViewModels/FilterBarViewModel.cs` | create | chips, input, submit, level toggles, save-filter flyout |
| `src/HexTail/ViewModels/LevelToggleViewModel.cs` | create | one T/D/I/W/E toggle |
| `src/HexTail/ViewModels/SavedFilterViewModel.cs` | create | sidebar entry |
| `src/HexTail/Views/FilterBar.axaml(.cs)` | create | chip row, input, level control, save flyout (replaces `SearchBar`) |
| `src/HexTail/Views/SearchBar.axaml(.cs)` | delete | replaced |
| `src/HexTail/Views/LogWorkspace.axaml` | modify | host `FilterBar` in the view content template; drop the stray `SearchError` text |
| `src/HexTail/Views/SourceSidebar.axaml` | modify | SAVED FILTERS section |
| `src/HexTail/Views/SettingsPanel.axaml` | modify | remove the Labels & Exclusions tab |
| `src/HexTail/ViewModels/SettingsViewModel.cs` | modify | remove label/exclusion editing; two sections |
| `src/HexTail/ViewModels/MainWindowViewModel.cs` | modify | `FilterBar`, `SavedFilters`, palette items, remove old search-input members |
| `src/HexTail/ViewModels/LogViewViewModel.cs` | modify | `MoveSelection`, `MoveToMatch` |
| `src/HexTail/Input/ShellShortcuts.cs` | modify | `NextRow`, `PreviousRow`, `NextMatch`, `PreviousMatch` |
| `src/HexTail/MainWindow.axaml.cs` | modify | dispatch the new actions; focus the filter input |
| `src/HexTail/Styles/CyberTail.axaml` | modify | chip, level-segment and swatch styles |
| `src/HexTail.Tests/Support/TestRules.cs` | modify | `Source(pattern, action)` builder |
| Tests | create/modify | see each task; ports for every test that used the old search input or Settings labels |

---

### Task 1: Rule edit primitives, validation, and the `IsGlobalRule` rename

**Files:**
- Create: `src/HexTail/Domain/RuleValidation.cs`, `src/HexTail/Application/RuleEditor.cs`
- Modify: `src/HexTail/Domain/Search.cs`, `src/HexTail/Application/FileTabState.cs`, `src/HexTail/Application/AppState.cs`, `src/HexTail/ViewModels/SettingsViewModel.cs`, every other usage found by `sb callers`
- Test: `src/HexTail.Tests/Application/RuleEditorTests.cs`, `src/HexTail.Tests/Application/RuleCommandsTests.cs`, `src/HexTail.Tests/Support/TestRules.cs`

**Interfaces:**
- Consumes: `ViewRule`, `RuleScope`, `AppState.UpdateSettingsAsync(AppSettings, CancellationToken)`, `AppState.SetSourceRulesAsync(FileTabState, IEnumerable<ViewRule>, CancellationToken)`.
- Produces:
  - `RuleValidation.Validate(string pattern, MatchMode mode) → string?` (null when valid).
  - `RuleEditor.Add/Replace/Remove(IEnumerable<ViewRule>, …) → List<ViewRule>` (pure; `Replace`/`Remove` act on the first value-equal rule and return an unchanged copy when absent).
  - `AppState.AddRuleAsync(FileTabState? tab, ViewRule rule, ct)`, `ReplaceRuleAsync(FileTabState? tab, ViewRule old, ViewRule updated, ct)`, `RemoveRuleAsync(FileTabState? tab, ViewRule rule, ct)`. `Global` scope uses settings; `Source` scope requires `tab` (otherwise the call is a no-op). `Replace` across scopes removes from the old scope's list and adds to the new one.
  - `Search.IsGlobalRule` (renamed from `IsGlobalLabel`).

- [ ] **Step 1: Write the failing tests**

Add to `TestRules`:

```csharp
public static ViewRule Source(string pattern, RuleAction action = RuleAction.Highlight) =>
    new()
    {
        Pattern = pattern,
        Mode = CompiledQuery.DetectMode(pattern),
        Action = action,
        Scope = RuleScope.Source,
    };
```

`RuleEditorTests.cs`:

```csharp
using HexTail.Application;
using HexTail.Domain;
using HexTail.Tests.Support;

namespace HexTail.Tests.Application;

public sealed class RuleEditorTests
{
    [Fact]
    public void Add_AppendsWithoutTouchingTheInput()
    {
        var rules = new List<ViewRule> { TestRules.Source("a") };
        var result = RuleEditor.Add(rules, TestRules.Source("b"));
        Assert.Equal(["a", "b"], result.Select(rule => rule.Pattern));
        Assert.Single(rules);
    }

    [Fact]
    public void Replace_ActsOnTheFirstValueEqualRuleOnly()
    {
        var twin = TestRules.Source("x");
        var result = RuleEditor.Replace(
            [twin, TestRules.Source("y"), twin],
            twin,
            twin with { Enabled = false }
        );
        Assert.Equal([false, true, true], result.Select(rule => rule.Enabled));
    }

    [Fact]
    public void Remove_DropsOneMatchAndIgnoresAbsentRules()
    {
        var rule = TestRules.Source("x");
        Assert.Equal(["x"], RuleEditor.Remove([rule, rule], rule).Select(r => r.Pattern));
        Assert.Single(RuleEditor.Remove([rule], TestRules.Source("other")));
    }
}
```

`RuleCommandsTests.cs` (uses `AppState` directly; mirror the construction in `SourceRulesTests.cs`, including `credentials: new InMemoryCredentialVault()`; open a temp file with `await state.OpenFileAsync(path)` or whichever method `SourceRulesTests` uses — copy it):

```csharp
[Fact]
public async Task GlobalRulesRoundTripThroughAddReplaceRemove()
{
    await using var state = NewState();
    var rule = TestRules.Highlight("boom");
    await state.AddRuleAsync(null, rule);
    Assert.Equal(["boom"], state.Settings.Rules.Select(r => r.Pattern));
    var stored = state.Settings.Rules[0];
    await state.ReplaceRuleAsync(null, stored, stored with { Enabled = false });
    Assert.False(state.Settings.Rules[0].Enabled);
    await state.RemoveRuleAsync(null, state.Settings.Rules[0]);
    Assert.Empty(state.Settings.Rules);
}

[Fact]
public async Task SourceRulesNeedATabAndMoveBetweenScopes()
{
    await using var state = NewState();
    var tab = await OpenTempFileAsync(state);
    await state.AddRuleAsync(null, TestRules.Source("ignored"));
    Assert.Empty(tab.Rules);

    await state.AddRuleAsync(tab, TestRules.Source("noise", RuleAction.Hide));
    var stored = tab.Rules[0];
    await state.ReplaceRuleAsync(tab, stored, stored with { Scope = RuleScope.Global });
    Assert.Empty(tab.Rules);
    Assert.Equal(["noise"], state.Settings.Rules.Select(r => r.Pattern));
    Assert.Equal(RuleAction.Hide, state.Settings.Rules[0].Action);
}

[Fact]
public void Validate_ReportsBadRegexOnly()
{
    Assert.Null(RuleValidation.Validate("[", MatchMode.Literal));
    Assert.NotNull(RuleValidation.Validate("[", MatchMode.Regex));
    Assert.Null(RuleValidation.Validate("a|b", MatchMode.Regex));
}
```

Define `NewState()` / `OpenTempFileAsync(state)` as private helpers in the test class (copy the pattern from `SourceRulesTests.cs`).

- [ ] **Step 2: Run to verify they fail**

Run: `rtk dotnet build src/HexTail.Tests`
Expected: FAIL — `RuleEditor`, `RuleValidation`, `AddRuleAsync` do not exist.

- [ ] **Step 3: Implement**

`Domain/RuleValidation.cs`:

```csharp
namespace HexTail.Domain;

public static class RuleValidation
{
    /// <summary>Returns the reason a pattern cannot be compiled, or null when it is usable.</summary>
    public static string? Validate(string pattern, MatchMode mode)
    {
        if (mode is not MatchMode.Regex)
            return null;
        try
        {
            _ = new CompiledQuery(pattern, mode, caseSensitive: false);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }
}
```

Make `SettingsViewModel.ValidateRule` call it (`=> RuleValidation.Validate(text, mode)`); Task 7 deletes it.

`Application/RuleEditor.cs`:

```csharp
using HexTail.Domain;

namespace HexTail.Application;

internal static class RuleEditor
{
    public static List<ViewRule> Add(IEnumerable<ViewRule> rules, ViewRule rule) => [.. rules, rule];

    public static List<ViewRule> Replace(
        IEnumerable<ViewRule> rules,
        ViewRule old,
        ViewRule updated
    )
    {
        var list = rules.ToList();
        var index = list.IndexOf(old);
        if (index >= 0)
            list[index] = updated;
        return list;
    }

    public static List<ViewRule> Remove(IEnumerable<ViewRule> rules, ViewRule rule)
    {
        var list = rules.ToList();
        var index = list.IndexOf(rule);
        if (index >= 0)
            list.RemoveAt(index);
        return list;
    }
}
```

`AppState` additions (next to `SetSourceRulesAsync`). Keep the same lock/NotifyChanged/Save shape as the neighbours; `ListFor` reads the current list for a scope:

```csharp
public async ValueTask AddRuleAsync(
    FileTabState? tab,
    ViewRule rule,
    CancellationToken cancellationToken = default
)
{
    ArgumentNullException.ThrowIfNull(rule);
    await EditRulesAsync(
        tab,
        rule.Scope,
        rules => RuleEditor.Add(rules, rule),
        cancellationToken
    );
}

public async ValueTask RemoveRuleAsync(
    FileTabState? tab,
    ViewRule rule,
    CancellationToken cancellationToken = default
)
{
    ArgumentNullException.ThrowIfNull(rule);
    await EditRulesAsync(
        tab,
        rule.Scope,
        rules => RuleEditor.Remove(rules, rule),
        cancellationToken
    );
}

public async ValueTask ReplaceRuleAsync(
    FileTabState? tab,
    ViewRule old,
    ViewRule updated,
    CancellationToken cancellationToken = default
)
{
    ArgumentNullException.ThrowIfNull(old);
    ArgumentNullException.ThrowIfNull(updated);
    if (old.Scope == updated.Scope)
    {
        await EditRulesAsync(
            tab,
            old.Scope,
            rules => RuleEditor.Replace(rules, old, updated),
            cancellationToken
        );
        return;
    }
    // Adding first means a missing source tab cannot silently drop the rule.
    await EditRulesAsync(tab, updated.Scope, rules => RuleEditor.Add(rules, updated), cancellationToken);
    await EditRulesAsync(tab, old.Scope, rules => RuleEditor.Remove(rules, old), cancellationToken);
}

private async ValueTask EditRulesAsync(
    FileTabState? tab,
    RuleScope scope,
    Func<IReadOnlyList<ViewRule>, List<ViewRule>> edit,
    CancellationToken cancellationToken
)
{
    if (scope == RuleScope.Global)
    {
        await UpdateSettingsAsync(
            Settings with { Rules = edit(Settings.Rules) },
            cancellationToken
        );
        return;
    }
    if (scope == RuleScope.Source && tab is not null)
        await SetSourceRulesAsync(tab, edit(tab.Rules), cancellationToken);
}
```

`Scope == View` is intentionally ignored (not stored). Rename `Search.IsGlobalLabel` → `IsGlobalRule` (ctor parameter `isGlobalRule`) and update every usage (`FileTabState`, `AppState`, `LogViewViewModel`, tests) — find them with `sb callers` / `grep -rn IsGlobalLabel src`.

- [ ] **Step 4: Run to verify they pass**

Run: `rtk dotnet build src/HexTail.Tests` then `rtk dotnet test src/HexTail.Tests --no-build --filter "FullyQualifiedName~RuleEditorTests|FullyQualifiedName~RuleCommandsTests"` then the full suite.
Expected: PASS; 442 + 6 tests, warnings 53.

- [ ] **Step 5: Commit**

```bash
git add -A src
git commit -m "feat(rules): add rule edit operations and validation helper"
```

---

### Task 2: Filter bar view model and the chip row

**Files:**
- Create: `src/HexTail/ViewModels/RuleChipViewModel.cs`, `src/HexTail/ViewModels/FilterBarViewModel.cs`, `src/HexTail/Views/FilterBar.axaml`, `src/HexTail/Views/FilterBar.axaml.cs`
- Modify: `src/HexTail/ViewModels/MainWindowViewModel.cs`, `src/HexTail/Views/LogWorkspace.axaml`, `src/HexTail/Styles/CyberTail.axaml`
- Test: `src/HexTail.Tests/ViewModels/FilterBarChipTests.cs`, `src/HexTail.Tests/Ui/FilterBarTests.cs`

**Interfaces:**
- Consumes: Task 1 (`AddRuleAsync`/`ReplaceRuleAsync`/`RemoveRuleAsync`, `Search.IsGlobalRule`), `MainWindowViewModel.State`, `.SelectedFile`, `FileTabViewModel.Views / SelectedViewIndex / Model`.
- Produces:
  - `RuleChipViewModel`: `ViewRule? Rule`, `Search? PinnedSearch`, `string Pattern`, `string Text` (pattern truncated to 28 chars with `…`), `bool IsGlobal`, `IsDisabled`, `IsHide`, `IsShowOnly`, `IsHighlight`, `IsPinnedTab`, `IBrush SwatchBrush`, `bool HasSwatch` (false for Hide), `string ToolTip`, `string AutomationName`, commands `ToggleEnabledCommand`, `RemoveCommand`, `ActivateCommand` (pinned-search chips select their tab; rule chips do nothing until Task 4).
  - `FilterBarViewModel`: `ObservableCollection<RuleChipViewModel> Chips`, `bool HasChips`, `void Sync()`. Reachable as `MainWindowViewModel.FilterBar`; `SyncFromState` calls `FilterBar.Sync()` right after `Settings.Sync(_state.Settings)`.
- Chip order: Global rules (in list order), then Source rules, then pinned-search chips (non-global `Search`es in tab order).

- [ ] **Step 1: Write the failing tests**

`ViewModels/FilterBarChipTests.cs` (these are `[AvaloniaFact]` because `MainWindowViewModel` needs the UI thread helpers; follow `GlobalRuleTests` setup):

```csharp
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HexTail.Domain;
using HexTail.Persistence;
using HexTail.Tests.Support;

namespace HexTail.Tests.ViewModels;

public sealed class FilterBarChipTests
{
    [AvaloniaFact]
    public async Task ChipsListGlobalThenSourceThenPinnedTabsAndSkipPinnedGlobalSearches()
    {
        var path = Path.GetTempFileName();
        try
        {
            var persistence = new TestPersistence();
            await persistence.SaveAsync(
                new AppConfig
                {
                    Settings = new AppSettings
                    {
                        Rules =
                        [
                            TestRules.Highlight("global-hit", pinned: true),
                            TestRules.Hide("noise"),
                        ],
                    },
                }
            );
            var window = TestWindow.Create(persistence, out var owner);
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            await owner.State.SetSourceRulesAsync(file.Model, [TestRules.Source("src-hit")]);
            owner.State.AddSearch(file.Model, "tab-hit", MatchMode.Literal, false, "#22D3EE");
            Dispatcher.UIThread.RunJobs();
            owner.FilterBar.Sync();

            Assert.Equal(
                ["global-hit", "noise", "src-hit", "tab-hit"],
                owner.FilterBar.Chips.Select(chip => chip.Pattern)
            );
            Assert.Equal(
                [true, true, false, false],
                owner.FilterBar.Chips.Select(chip => chip.IsGlobal)
            );
            Assert.True(owner.FilterBar.Chips[3].IsPinnedTab);
            Assert.False(owner.FilterBar.Chips[1].HasSwatch);
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task TogglingAndRemovingChipsEditsTheOwningRuleList()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var owner);
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            await owner.State.SetSourceRulesAsync(file.Model, [TestRules.Source("a")]);
            owner.FilterBar.Sync();

            await owner.FilterBar.Chips[0].ToggleEnabledCommand.Execute();
            Assert.False(file.Model.Rules[0].Enabled);
            owner.FilterBar.Sync();
            Assert.True(owner.FilterBar.Chips[0].IsDisabled);

            await owner.FilterBar.Chips[0].RemoveCommand.Execute();
            Assert.Empty(file.Model.Rules);
            owner.FilterBar.Sync();
            Assert.Empty(owner.FilterBar.Chips);
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ActivatingAPinnedTabChipSelectsItsView()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var owner);
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            owner.State.AddSearch(file.Model, "tab", MatchMode.Literal, false, "#22D3EE");
            file.SyncViews();
            owner.FilterBar.Sync();
            Assert.Equal(0, file.SelectedViewIndex);
            await owner.FilterBar.Chips[0].ActivateCommand.Execute();
            Assert.Equal(1, file.SelectedViewIndex);
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
```

`Ui/FilterBarTests.cs`:

```csharp
[AvaloniaFact]
public async Task ChipRowRendersEveryChipWithAnAccessibleName()
{
    var path = Path.GetTempFileName();
    try
    {
        var window = TestWindow.Create(out var owner);
        window.Show();
        await owner.InitializeAsync();
        await owner.OpenPathsCommand.Execute([path]);
        await owner.State.SetSourceRulesAsync(
            owner.SelectedFile!.Model,
            [TestRules.Source("boom"), TestRules.Source("noise", RuleAction.Hide)]
        );
        Dispatcher.UIThread.RunJobs();
        owner.FilterBar.Sync();
        await UiWait.Until(() => window.GetVisualDescendants().OfType<FilterBar>().Any());
        var names = window
            .GetVisualDescendants()
            .OfType<Control>()
            .Where(control => control.Name == "RuleChip")
            .Select(control => AutomationProperties.GetName(control))
            .ToList();
        Assert.Contains(names, name => name.Contains("boom") && name.Contains("Highlight"));
        Assert.Contains(names, name => name.Contains("noise") && name.Contains("Hide"));
        window.Close();
        await owner.DisposeAsync();
    }
    finally
    {
        File.Delete(path);
    }
}
```

- [ ] **Step 2: Run to verify they fail** — `FilterBar`, `RuleChipViewModel` do not exist (build fails).

- [ ] **Step 3: Implement**

`RuleChipViewModel.cs`:

```csharp
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
            outputScheduler: owner.Scheduler
        );
        RemoveCommand = ReactiveCommand.CreateFromTask(
            () => _owner.RemoveAsync(this),
            outputScheduler: owner.Scheduler
        );
        ActivateCommand = ReactiveCommand.Create(() => _owner.Activate(this), owner.Scheduler);
    }

    internal RuleChipViewModel(FilterBarViewModel owner, Search search)
        : this(owner, new ViewRule { Pattern = search.Query.Query }) =>
        PinnedSearch = search;

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
            Color.TryParse(PinnedSearch?.Color ?? Rule?.Color, out var color)
                ? color
                : Colors.Gray
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

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ToggleEnabledCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RemoveCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ActivateCommand { get; }
}
```

Match the exact `ReactiveCommand.Create*` overloads and `Unit` using-directive used in `FileTabViewModel`/`MainWindowViewModel` (they use `Unit` from `System.Reactive`; the positional-scheduler form is `ReactiveCommand.CreateFromTask(func, scheduler)`). Expose `FilterBarViewModel.Scheduler` as `_owner.Scheduler` (already `internal` on `MainWindowViewModel`).

`FilterBarViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using HexTail.Domain;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal sealed class FilterBarViewModel : ReactiveObject
{
    private readonly MainWindowViewModel _owner;
    private IReadOnlyList<ChipKey> _lastKeys = [];

    internal FilterBarViewModel(MainWindowViewModel owner) => _owner = owner;

    internal System.Reactive.Concurrency.IScheduler Scheduler => _owner.Scheduler;
    public ObservableCollection<RuleChipViewModel> Chips { get; } = [];
    public bool HasChips => Chips.Count > 0;

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
            ? _owner.State.ReplaceRuleAsync(
                _owner.SelectedFile?.Model,
                rule,
                rule with { Enabled = !rule.Enabled }
            ).AsTask()
            : Task.CompletedTask;

    internal Task RemoveAsync(RuleChipViewModel chip)
    {
        var file = _owner.SelectedFile?.Model;
        if (chip.PinnedSearch is { } search && file is not null)
            return _owner.State.RemoveSearchAsync(file, search).AsTask();
        return chip.Rule is { } rule
            ? _owner.State.RemoveRuleAsync(file, rule).AsTask()
            : Task.CompletedTask;
    }

    internal void Activate(RuleChipViewModel chip)
    {
        if (chip.PinnedSearch is null || _owner.SelectedFile is not { } file)
            return;
        var index = file.Views.ToList().FindIndex(view => ReferenceEquals(view.Search, chip.PinnedSearch));
        if (index >= 0)
            file.SelectedViewIndex = index;
    }
}
```

`ChipKey` equality uses `ViewRule` value equality and `Search` reference equality, which is what makes `Sync` cheap and stable. Wire it in `MainWindowViewModel`: `public FilterBarViewModel FilterBar { get; }` created in the constructor, and `FilterBar.Sync();` in `SyncFromState` after `Settings.Sync(_state.Settings);`. A chip toggle/remove ends in `NotifyChanged`, which already schedules `SyncFromState`.

`Views/FilterBar.axaml` (chip row only in this task; the input arrives in Task 3). `x:DataType="vm:FilterBarViewModel"`; the host sets `DataContext`:

```xml
<UserControl
  xmlns="https://github.com/avaloniaui"
  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
  xmlns:vm="using:HexTail.ViewModels"
  xmlns:ic="https://github.com/projektanker/icons.avalonia"
  x:Class="HexTail.Views.FilterBar"
  x:DataType="vm:FilterBarViewModel"
  AutomationProperties.Name="Filters"
>
  <StackPanel Margin="8,2,8,4" Spacing="4">
    <ItemsControl x:Name="ChipList" ItemsSource="{Binding Chips}" IsVisible="{Binding HasChips}">
      <ItemsControl.ItemsPanel>
        <ItemsPanelTemplate>
          <WrapPanel />
        </ItemsPanelTemplate>
      </ItemsControl.ItemsPanel>
      <ItemsControl.ItemTemplate>
        <DataTemplate x:DataType="vm:RuleChipViewModel">
          <Grid
            x:Name="RuleChip"
            Name="RuleChip"
            Margin="0,0,6,4"
            Classes="rule-chip"
            Classes.disabled="{Binding IsDisabled}"
            ToolTip.Tip="{Binding ToolTip}"
            AutomationProperties.Name="{Binding AutomationName}"
          >
            <Rectangle
              Classes="chip-outline"
              Classes.global="{Binding IsGlobal}"
              RadiusX="10"
              RadiusY="10"
            />
            <StackPanel Orientation="Horizontal" Spacing="4" Margin="8,2,4,2">
              <Ellipse
                Width="8"
                Height="8"
                Fill="{Binding SwatchBrush}"
                IsVisible="{Binding HasSwatch}"
                VerticalAlignment="Center"
              />
              <ic:Icon Value="mdi-eye-off-outline" IsVisible="{Binding IsHide}" />
              <ic:Icon Value="mdi-filter-outline" IsVisible="{Binding IsShowOnly}" />
              <ic:Icon Value="mdi-bookmark-outline" IsVisible="{Binding IsPinnedTab}" />
              <Button
                Classes="chip-body"
                Command="{Binding ActivateCommand}"
                AutomationProperties.Name="{Binding Text, StringFormat='Edit {0}'}"
              >
                <TextBlock Classes="chip-text" Text="{Binding Text}" />
              </Button>
              <Button
                Classes="icon tab-close"
                Width="18"
                Height="18"
                Command="{Binding RemoveCommand}"
                ToolTip.Tip="Remove"
                AutomationProperties.Name="{Binding Pattern, StringFormat='Remove {0}'}"
              >
                <ic:Icon Value="mdi-close" FontSize="12" />
              </Button>
            </StackPanel>
          </Grid>
        </DataTemplate>
      </ItemsControl.ItemTemplate>
    </ItemsControl>
  </StackPanel>
</UserControl>
```

Code-behind `FilterBar.axaml.cs`: `public partial class FilterBar : UserControl { public FilterBar() => InitializeComponent(); }`. Middle-click disables a chip: in this task add to the `RuleChip` `Grid` a `PointerReleased` handler in code-behind that, when `e.InitialPressMouseButton == MouseButton.Middle` and `(sender as Control)?.DataContext is RuleChipViewModel chip`, runs `chip.ToggleEnabledCommand.Execute().Subscribe()`; wire it via `AddHandler(PointerReleasedEvent, OnChipPointerReleased, RoutingStrategies.Bubble)` on the `ChipList` after `InitializeComponent`.

Styles in `CyberTail.axaml` (inside the existing `<Styles>`; use only brush keys that already exist: `MutedBrush`, `TextBrush`, `RaisedAltBrush`, `AccentBrush`):

```xml
<Style Selector="Rectangle.chip-outline">
  <Setter Property="Stroke" Value="{DynamicResource MutedBrush}" />
  <Setter Property="StrokeThickness" Value="1" />
  <Setter Property="Fill" Value="{DynamicResource RaisedAltBrush}" />
</Style>
<Style Selector="Rectangle.chip-outline.global">
  <Setter Property="StrokeDashArray" Value="3,2" />
</Style>
<Style Selector="Grid.rule-chip.disabled">
  <Setter Property="Opacity" Value="0.5" />
</Style>
<Style Selector="Grid.rule-chip.disabled TextBlock.chip-text">
  <Setter Property="TextDecorations" Value="Strikethrough" />
</Style>
<Style Selector="Button.chip-body">
  <Setter Property="Background" Value="Transparent" />
  <Setter Property="BorderThickness" Value="0" />
  <Setter Property="Padding" Value="2,0" />
  <Setter Property="MinHeight" Value="0" />
</Style>
```

Host it: in `LogWorkspace.axaml` change the per-view content template `Grid RowDefinitions="Auto,*"` to `RowDefinitions="Auto,Auto,*"`, add `<views:FilterBar Grid.Row="1" DataContext="{Binding Workspace.FilterBar}" />`, and set `<views:LogView Grid.Row="2" />`. `SearchBar` stays in place until Task 3 (both exist for one commit; the chips row is additive).

- [ ] **Step 4: Run to verify** — the three view-model tests and the UI test pass; full suite green, warnings 53.

- [ ] **Step 5: Commit**

```bash
git add -A src
git commit -m "feat(ui): show every active rule as a chip under the view tabs"
```

---

### Task 3: One input — Enter / Shift+Enter / Alt+Enter, pin as tab, automatic color

**Files:**
- Modify: `src/HexTail/ViewModels/FilterBarViewModel.cs`, `src/HexTail/ViewModels/MainWindowViewModel.cs`, `src/HexTail/Views/FilterBar.axaml(.cs)`, `src/HexTail/Views/LogWorkspace.axaml`, `src/HexTail/Views/MainWindow` hosting (`src/HexTail/MainWindow.axaml`, `MainWindow.axaml.cs`), `src/HexTail/Styles/CyberTail.axaml`
- Delete: `src/HexTail/Views/SearchBar.axaml`, `SearchBar.axaml.cs`
- Test: `src/HexTail.Tests/ViewModels/FilterBarSubmitTests.cs`, ports of every existing test that used the old input (see Step 1)

**Interfaces:**
- Consumes: Task 1 and 2.
- Produces on `FilterBarViewModel`: `string Query`, `MatchMode MatchMode`, `bool CaseSensitive`, `bool PinAsTab`, `string? Error`, `bool HasError`, `IReadOnlyList<MatchMode> MatchModes`, `Task SubmitAsync(RuleAction action)`, `event EventHandler? FocusRequested`, `void RequestFocus()`.
- Removes from `MainWindowViewModel`: `Query`, `MatchMode`, `MatchModes`, `CaseSensitive`, `SearchColor`, `SearchError`, `HasSearchError`, `AddSearchCommand`, `AddSearchOnKeyCommand`, `AddSearchAsync`, and `FocusSearchRequested` (replaced by `FilterBar.FocusRequested`). `NextSearchColor` and the `SearchColors` array stay (made reachable via `internal static IReadOnlyList<string> Swatches => SearchColors[..5]`).
- Behavior: Enter → Highlight, Shift+Enter → ShowOnly, Alt+Enter → Hide, all as Source-scope rules with an automatic color; with `PinAsTab` on and a plain Enter, a pinned `Search` tab is created (today's behavior). An invalid regex sets `Error` and creates nothing. Success clears `Query` and `Error`. No selected source or a blank query does nothing.

- [ ] **Step 1: Write the failing tests, then find every test to port**

Run: `rtk grep -rln "AddSearchCommand\|AddSearchOnKeyCommand\|\.Query =\|owner.Query\|viewModel.Query\|SearchColor\|SearchBar\|QueryBox\|AddSearchButton\|SearchError\|HasSearchError\|FocusSearchRequested\|CaseToggle\|SearchModePicker" src/HexTail.Tests src/HexTail`
Expected: a list of production and test files. Every hit must end up either ported to the new surface or deleted because the behavior no longer exists. Record the list in the report.

`ViewModels/FilterBarSubmitTests.cs` (shared helper `OpenAsync` opens one temp file as in Task 2):

```csharp
[AvaloniaFact]
public async Task SubmitCreatesSourceRulesForEachAction()
{
    var path = Path.GetTempFileName();
    try
    {
        var window = TestWindow.Create(out var owner);
        await owner.InitializeAsync();
        await owner.OpenPathsCommand.Execute([path]);
        var model = owner.SelectedFile!.Model;
        var bar = owner.FilterBar;

        bar.Query = "boom";
        await bar.SubmitAsync(RuleAction.Highlight);
        bar.Query = "slow";
        await bar.SubmitAsync(RuleAction.ShowOnly);
        bar.Query = "health";
        await bar.SubmitAsync(RuleAction.Hide);

        Assert.Equal(
            [
                ("boom", RuleAction.Highlight),
                ("slow", RuleAction.ShowOnly),
                ("health", RuleAction.Hide),
            ],
            model.Rules.Select(rule => (rule.Pattern, rule.Action))
        );
        Assert.All(model.Rules, rule => Assert.Equal(RuleScope.Source, rule.Scope));
        Assert.Equal(model.Rules.Count, model.Rules.Select(rule => rule.Color).Distinct().Count());
        Assert.Equal(string.Empty, bar.Query);
        window.Close();
        await owner.DisposeAsync();
    }
    finally
    {
        File.Delete(path);
    }
}

[AvaloniaFact]
public async Task PinAsTabMakesAPlainEnterCreateATabSearchButNotShiftEnter()
{
    var path = Path.GetTempFileName();
    try
    {
        var window = TestWindow.Create(out var owner);
        await owner.InitializeAsync();
        await owner.OpenPathsCommand.Execute([path]);
        var model = owner.SelectedFile!.Model;
        owner.FilterBar.PinAsTab = true;
        owner.FilterBar.Query = "tabbed";
        await owner.FilterBar.SubmitAsync(RuleAction.Highlight);
        Assert.Equal(["tabbed"], model.Searches.Select(search => search.Query.Query));
        Assert.Empty(model.Rules);
        owner.FilterBar.Query = "narrow";
        await owner.FilterBar.SubmitAsync(RuleAction.ShowOnly);
        Assert.Equal(["narrow"], model.Rules.Select(rule => rule.Pattern));
        Assert.Single(model.Searches);
        window.Close();
        await owner.DisposeAsync();
    }
    finally
    {
        File.Delete(path);
    }
}

[AvaloniaFact]
public async Task InvalidRegexSetsAnErrorAndCreatesNothing()
{
    var path = Path.GetTempFileName();
    try
    {
        var window = TestWindow.Create(out var owner);
        await owner.InitializeAsync();
        await owner.OpenPathsCommand.Execute([path]);
        owner.FilterBar.MatchMode = MatchMode.Regex;
        owner.FilterBar.Query = "[";
        await owner.FilterBar.SubmitAsync(RuleAction.Highlight);
        Assert.True(owner.FilterBar.HasError);
        Assert.Empty(owner.SelectedFile!.Model.Rules);
        Assert.Equal("[", owner.FilterBar.Query);
        window.Close();
        await owner.DisposeAsync();
    }
    finally
    {
        File.Delete(path);
    }
}

[AvaloniaFact]
public async Task ShiftEnterAndAltEnterInTheInputPickTheAction()
{
    var path = Path.GetTempFileName();
    try
    {
        var window = TestWindow.Create(out var owner);
        window.Show();
        await owner.InitializeAsync();
        await owner.OpenPathsCommand.Execute([path]);
        Dispatcher.UIThread.RunJobs();
        var box = window.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "QueryBox");
        box.Focus();
        owner.FilterBar.Query = "one";
        box.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.None, null);
        owner.FilterBar.Query = "two";
        box.KeyPress(Key.Enter, RawInputModifiers.Alt, PhysicalKey.None, null);
        owner.FilterBar.Query = "three";
        box.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
        await UiWait.Until(() => owner.SelectedFile!.Model.Rules.Count == 3);
        Assert.Equal(
            [RuleAction.ShowOnly, RuleAction.Hide, RuleAction.Highlight],
            owner.SelectedFile!.Model.Rules.Select(rule => rule.Action)
        );
        window.Close();
        await owner.DisposeAsync();
    }
    finally
    {
        File.Delete(path);
    }
}
```

(`KeyPress` is the `KeyboardDevice`/headless extension used in `AccessibilityTests`; copy its using directives.)

- [ ] **Step 2: Run to verify they fail** — `FilterBar.SubmitAsync` and friends do not exist.

- [ ] **Step 3: Implement**

Add to `FilterBarViewModel` (fields `_query`, `_matchMode = MatchMode.Literal`, `_caseSensitive`, `_pinAsTab`, `_error`, standard `RaiseAndSetIfChanged` properties; `HasError => !string.IsNullOrWhiteSpace(Error)`; setting `Query` or `MatchMode` clears `Error`):

```csharp
public event EventHandler? FocusRequested;
internal void RequestFocus() => FocusRequested?.Invoke(this, EventArgs.Empty);

public IReadOnlyList<MatchMode> MatchModes { get; } = [MatchMode.Literal, MatchMode.Regex];

internal async Task SubmitAsync(RuleAction action)
{
    var file = _owner.SelectedFile?.Model;
    var pattern = Query.Trim();
    if (file is null || pattern.Length == 0)
        return;
    if (RuleValidation.Validate(pattern, MatchMode) is { } error)
    {
        Error = error;
        return;
    }
    var color = MainWindowViewModel.NextSearchColor(
        file.Searches.Select(search => search.Color)
            .Concat(file.Rules.Where(rule => rule.Action != RuleAction.Hide).Select(rule => rule.Color)),
        _owner.State.Settings.Rules.Where(rule => rule.Action != RuleAction.Hide).Select(rule => rule.Color)
    );
    if (PinAsTab && action == RuleAction.Highlight)
        _owner.State.AddSearch(file, pattern, MatchMode, CaseSensitive, color);
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
    Query = string.Empty;
    Error = null;
    await _owner.State.SaveAsync();
}
```

(`AddRuleAsync` already saves; `SaveAsync` after `AddSearch` is what the old path did — keep it for the pin branch only: move the `SaveAsync` call inside the `if (PinAsTab …)` branch.) Wrap the whole body in `try { … } catch (ArgumentException ex) { Error = ex.Message; }` to keep the old defensive behavior.

`MainWindowViewModel`: delete the members listed under *Removes*; `FocusSearch` paths (`ShellAction.FocusSearch` in `MainWindow.axaml.cs`, the palette "Focus search" item, `FocusSearchRequested` handler in the `MainWindow` constructor) now call `ViewModel.FilterBar.RequestFocus()`; the `MainWindow` constructor subscribes `ViewModel.FilterBar.FocusRequested += (_, _) => Dispatcher.UIThread.Post(() => this.GetVisualDescendants().OfType<FilterBar>().FirstOrDefault()?.FocusQuery())`. Remove `HasSearchError` from `MainSnapshot` and its raise in `SyncFromState`; delete the `SearchError` `TextBlock` from `LogWorkspace.axaml`.

`FilterBar.axaml`: above the `ChipList` add the input row:

```xml
<Grid ColumnDefinitions="*,Auto,Auto,Auto" ColumnSpacing="4">
  <TextBox
    x:Name="QueryBox"
    Name="QueryBox"
    VerticalContentAlignment="Center"
    Text="{Binding Query, Mode=TwoWay}"
    PlaceholderText="Filter this source — Enter highlight · Shift+Enter show only · Alt+Enter hide"
    AutomationProperties.Name="Filter text"
    UseFloatingPlaceholder="False"
  />
  <ComboBox
    x:Name="SearchModePicker"
    Grid.Column="1"
    Width="94"
    ItemsSource="{Binding MatchModes}"
    SelectedItem="{Binding MatchMode, Mode=TwoWay}"
    AutomationProperties.Name="Match mode"
    ToolTip.Tip="Literal matches exact text; Regex interprets a regular expression."
  />
  <ToggleButton
    x:Name="CaseToggle"
    Grid.Column="2"
    Content="Aa"
    IsChecked="{Binding CaseSensitive, Mode=TwoWay}"
    ToolTip.Tip="Match case"
    AutomationProperties.Name="Match case"
  />
  <ToggleButton
    x:Name="PinTabToggle"
    Grid.Column="3"
    IsChecked="{Binding PinAsTab, Mode=TwoWay}"
    ToolTip.Tip="Enter creates a view tab instead of a highlight rule"
    AutomationProperties.Name="Pin as tab"
  >
    <ic:Icon Value="mdi-bookmark-outline" />
  </ToggleButton>
</Grid>
<TextBlock Classes="error" Text="{Binding Error}" IsVisible="{Binding HasError}" />
```

Keep the density sizing the old `SearchBar` applied (`Settings.SecondaryControlHeight` etc.) by binding through `$parent[UserControl].DataContext`-free means: since `FilterBar.DataContext` is `FilterBarViewModel`, expose `public AppSettings Settings => _owner.State.Settings` on it and bind `Height="{Binding Settings.SecondaryControlHeight}"` as the old file did. Code-behind:

```csharp
public FilterBar()
{
    InitializeComponent();
    var box = this.FindControl<TextBox>("QueryBox")!;
    box.KeyDown += OnQueryKeyDown;
    ChipList.AddHandler(PointerReleasedEvent, OnChipPointerReleased, RoutingStrategies.Bubble);
}

internal void FocusQuery() => this.FindControl<TextBox>("QueryBox")?.Focus();

private void OnQueryKeyDown(object? sender, KeyEventArgs e)
{
    if (e.Key != Key.Enter || DataContext is not FilterBarViewModel bar)
        return;
    var action = e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? RuleAction.Hide
        : e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? RuleAction.ShowOnly
        : RuleAction.Highlight;
    e.Handled = true;
    _ = bar.SubmitAsync(action);
}
```

Delete `SearchBar.axaml(.cs)` and its row (`Grid.Row="0"`) in `MainWindow.axaml`: the grid becomes `RowDefinitions="Auto,*"` with `WorkspaceError` row 0 and `LogWorkspace` row 1. Port the tests found in Step 1: tests that typed in the old box now set `owner.FilterBar.Query` and call `SubmitAsync(RuleAction.Highlight)` with `PinAsTab = true` when they asserted tab creation. Add `Swatches` to `MainWindowViewModel`. Update `docs/user-guide.md` wording in Task 9, not here.

- [ ] **Step 4: Run to verify** — new tests pass, every ported test passes, full suite green, warnings 53.

- [ ] **Step 5: Commit**

```bash
git add -A src
git commit -m "feat(ui): one filter input with highlight, show-only and hide actions"
```

---

### Task 4: Chip popover — edit pattern, mode, action, scope, color, tab

**Files:**
- Create: `src/HexTail/ViewModels/RuleDraftViewModel.cs`
- Modify: `src/HexTail/ViewModels/RuleChipViewModel.cs`, `src/HexTail/ViewModels/FilterBarViewModel.cs`, `src/HexTail/Views/FilterBar.axaml`, `src/HexTail/Styles/CyberTail.axaml`
- Test: `src/HexTail.Tests/ViewModels/RuleDraftTests.cs`, `src/HexTail.Tests/Ui/FilterBarTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3, `MainWindowViewModel.Swatches`.
- Produces: `RuleDraftViewModel(ViewRule original)` with `Pattern`, `MatchMode`, `CaseSensitive`, `RuleAction Action`, `bool AllSources`, `string Color`, `bool PinnedAsTab`, `bool CanPin` (`AllSources && Action != Hide`), `bool CanSave`, `string? Error`, `IReadOnlyList<string> Swatches`, `ViewRule ToRule()`, `void PickSwatch(string color)`. `RuleChipViewModel.Draft` (null for pinned-search chips), `bool IsEditorOpen`, `ReactiveCommand ApplyCommand`, `CancelCommand`. `FilterBarViewModel.ApplyAsync(RuleChipViewModel)`.

- [ ] **Step 1: Write the failing tests**

`RuleDraftTests.cs` (plain `[Fact]`, no UI):

```csharp
[Fact]
public void ToRuleCarriesEditsAndScope()
{
    var draft = new RuleDraftViewModel(TestRules.Source("old"))
    {
        Pattern = "  new  ",
        Action = RuleAction.ShowOnly,
        AllSources = true,
        CaseSensitive = true,
    };
    var rule = draft.ToRule();
    Assert.Equal("new", rule.Pattern);
    Assert.Equal(RuleAction.ShowOnly, rule.Action);
    Assert.Equal(RuleScope.Global, rule.Scope);
    Assert.True(rule.CaseSensitive);
}

[Fact]
public void PinningIsOnlyOfferedToGlobalNonHideRules()
{
    var draft = new RuleDraftViewModel(TestRules.Source("x"));
    Assert.False(draft.CanPin);
    draft.AllSources = true;
    Assert.True(draft.CanPin);
    draft.PinnedAsTab = true;
    draft.Action = RuleAction.Hide;
    Assert.False(draft.CanPin);
    Assert.False(draft.ToRule().PinnedAsTab);
}

[Fact]
public void InvalidRegexBlocksSaving()
{
    var draft = new RuleDraftViewModel(TestRules.Source("x")) { MatchMode = MatchMode.Regex, Pattern = "[" };
    Assert.False(draft.CanSave);
    Assert.NotNull(draft.Error);
    draft.Pattern = "a|b";
    Assert.True(draft.CanSave);
    Assert.Null(draft.Error);
}

[Fact]
public void BlankPatternBlocksSaving()
{
    Assert.False(new RuleDraftViewModel(TestRules.Source("x")) { Pattern = "  " }.CanSave);
}
```

Add to `FilterBarChipTests` (or a new `FilterBarEditTests`) an `[AvaloniaFact]` that applies a draft: open a file, `SetSourceRulesAsync(file, [Source("a")])`, `Sync()`, set `chip.Draft!.Pattern = "b"`, `Draft.AllSources = true`, `await chip.ApplyCommand.Execute()`, then assert `file.Model.Rules` is empty and `State.Settings.Rules.Single().Pattern == "b"`. A second test: `CancelCommand` leaves rules untouched and closes the editor (`IsEditorOpen == false`).

- [ ] **Step 2: Run to verify they fail** — `RuleDraftViewModel` does not exist.

- [ ] **Step 3: Implement**

```csharp
using HexTail.Domain;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal sealed class RuleDraftViewModel : ReactiveObject
{
    private string _pattern;
    private MatchMode _matchMode;
    private bool _caseSensitive;
    private RuleAction _action;
    private bool _allSources;
    private string _color;
    private bool _pinnedAsTab;
    private readonly ViewRule _original;

    internal RuleDraftViewModel(ViewRule original)
    {
        _original = original;
        _pattern = original.Pattern;
        _matchMode = original.Mode;
        _caseSensitive = original.CaseSensitive;
        _action = original.Action;
        _allSources = original.Scope == RuleScope.Global;
        _color = original.Color;
        _pinnedAsTab = original.PinnedAsTab;
    }

    public IReadOnlyList<string> Swatches => MainWindowViewModel.Swatches;
    public IReadOnlyList<MatchMode> MatchModes { get; } = [MatchMode.Literal, MatchMode.Regex];
    public IReadOnlyList<RuleAction> Actions { get; } =
        [RuleAction.Highlight, RuleAction.ShowOnly, RuleAction.Hide];

    public string Pattern
    {
        get => _pattern;
        set { this.RaiseAndSetIfChanged(ref _pattern, value); RaiseValidity(); }
    }
    public MatchMode MatchMode
    {
        get => _matchMode;
        set { this.RaiseAndSetIfChanged(ref _matchMode, value); RaiseValidity(); }
    }
    public bool CaseSensitive
    {
        get => _caseSensitive;
        set => this.RaiseAndSetIfChanged(ref _caseSensitive, value);
    }
    public RuleAction Action
    {
        get => _action;
        set { this.RaiseAndSetIfChanged(ref _action, value); this.RaisePropertyChanged(nameof(CanPin)); this.RaisePropertyChanged(nameof(HasColor)); }
    }
    public bool AllSources
    {
        get => _allSources;
        set { this.RaiseAndSetIfChanged(ref _allSources, value); this.RaisePropertyChanged(nameof(CanPin)); }
    }
    public string Color
    {
        get => _color;
        set => this.RaiseAndSetIfChanged(ref _color, value);
    }
    public bool PinnedAsTab
    {
        get => _pinnedAsTab;
        set => this.RaiseAndSetIfChanged(ref _pinnedAsTab, value);
    }

    public bool HasColor => Action != RuleAction.Hide;
    public bool CanPin => AllSources && Action != RuleAction.Hide;
    public string? Error => RuleValidation.Validate(Pattern.Trim(), MatchMode);
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
        this.RaisePropertyChanged(nameof(CanSave));
    }
}
```

`RuleChipViewModel`: for non-pinned chips create `Draft = new RuleDraftViewModel(rule)` lazily on first access and recreate it each time the editor opens (`OpenEditor()` sets `Draft = new(...)`, `IsEditorOpen = true`). `ApplyCommand` is `CreateFromTask(() => _owner.ApplyAsync(this), canExecute: this.WhenAnyValue(c => c.Draft!.CanSave) …)` — guard inside `ApplyAsync` rather than with a reactive `canExecute` on a nullable path: return early when `Draft?.CanSave != true`. `CancelCommand` sets `IsEditorOpen = false`. `ActivateCommand` now: pinned-search chip → select tab (Task 2); rule chip → `OpenEditor()`.

```csharp
// FilterBarViewModel
internal async Task ApplyAsync(RuleChipViewModel chip)
{
    if (chip.Rule is not { } rule || chip.PinnedSearch is not null || chip.Draft is not { CanSave: true } draft)
        return;
    await _owner.State.ReplaceRuleAsync(_owner.SelectedFile?.Model, rule, draft.ToRule());
    chip.IsEditorOpen = false;
}
```

If the chip is rebuilt by `Sync` the flyout closes with it; that is acceptable (the edit has been committed).

`FilterBar.axaml`: inside the chip's `StackPanel`, attach a `Flyout` to the `chip-body` button: `Button.Flyout` → `Flyout` whose `IsOpen` is not bound (Avalonia `Flyout` is a popup); open it from code: in `FilterBar.axaml.cs`'s `OnChipClicked` for `Classes="chip-body"` buttons (`AddHandler(Button.ClickEvent, …)`), when `DataContext is RuleChipViewModel { PinnedSearch: null } chip` call `chip.OpenEditor()` and `FlyoutBase.ShowAttachedFlyout(button)`. Flyout content (a `StackPanel` bound to `Draft`, `x:DataType="vm:RuleDraftViewModel"`; give every control a name): `TextBox` (`DraftPatternBox`, Enter applies via `KeyDown`), `ComboBox` for `MatchMode`, `ToggleButton` "Aa" for `CaseSensitive`, a `ComboBox` for `Action` (`DraftActionPicker`), a `ToggleSwitch` "All sources" (`DraftScopeSwitch`), a `CheckBox` "Show as a tab in every file" bound to `PinnedAsTab` with `IsVisible="{Binding CanPin}"`, a swatch row (`ItemsControl` over `Swatches`, each a `Button` with `Command` → `$parent[ItemsControl].DataContext.PickSwatchCommand`; expose `ReactiveCommand<string, Unit> PickSwatchCommand` on the draft instead of the method if bindings need a command) plus a "Custom…" `ColorPicker` button, an error `TextBlock`, and `Apply` / `Cancel` / `Remove` buttons (`Remove` reuses `RemoveCommand`). Swatch buttons use style `Button.swatch` (16×16, `CornerRadius=8`, `Background` bound to the swatch hex through `Color.Parse` converter-less: bind `Background="{Binding}"` — Avalonia converts a hex string to a brush).

- [ ] **Step 4: Run to verify** — draft tests, apply/cancel tests and a UI test that clicks `chip-body` and finds `DraftPatternBox` in the visual tree after `Dispatcher.UIThread.RunJobs()` all pass; full suite green.

- [ ] **Step 5: Commit**

```bash
git add -A src
git commit -m "feat(ui): edit a rule chip in a popover"
```

---

### Task 5: Level control (T / D / I / W / E)

**Files:**
- Create: `src/HexTail/ViewModels/LevelToggleViewModel.cs`
- Modify: `src/HexTail/ViewModels/FilterBarViewModel.cs`, `src/HexTail/Views/FilterBar.axaml`, `src/HexTail/Styles/CyberTail.axaml`
- Test: `src/HexTail.Tests/ViewModels/LevelToggleTests.cs`

**Interfaces:**
- Consumes: `AppState.SetLevelsAsync`, `FileTabState.Levels`, `LogLevels`.
- Produces: `LevelToggleViewModel` (`LogLevels Level`, `string Label`, `string Name`, `bool IsOn`, `ToggleCommand`); `FilterBarViewModel.LevelToggles` (5 items, order Trace, Debug, Info, Warn, Error with labels `T D I W E`), `bool HasLevelFilter` (mask ≠ All).

- [ ] **Step 1: Write the failing tests**

```csharp
[AvaloniaFact]
public async Task TogglesFollowTheSelectedSourcesMask()
{
    var path = Path.GetTempFileName();
    try
    {
        var window = TestWindow.Create(out var owner);
        await owner.InitializeAsync();
        await owner.OpenPathsCommand.Execute([path]);
        var model = owner.SelectedFile!.Model;
        owner.FilterBar.Sync();
        Assert.All(owner.FilterBar.LevelToggles, toggle => Assert.True(toggle.IsOn));

        await owner.FilterBar.LevelToggles.Single(t => t.Level == LogLevels.Debug).ToggleCommand.Execute();
        Assert.Equal(LogLevels.All & ~LogLevels.Debug, model.Levels);
        owner.FilterBar.Sync();
        Assert.False(owner.FilterBar.LevelToggles.Single(t => t.Level == LogLevels.Debug).IsOn);
        Assert.True(owner.FilterBar.HasLevelFilter);
        window.Close();
        await owner.DisposeAsync();
    }
    finally
    {
        File.Delete(path);
    }
}

[AvaloniaFact]
public async Task TheLastEnabledLevelCannotBeCleared()
{
    var path = Path.GetTempFileName();
    try
    {
        var window = TestWindow.Create(out var owner);
        await owner.InitializeAsync();
        await owner.OpenPathsCommand.Execute([path]);
        var model = owner.SelectedFile!.Model;
        await owner.State.SetLevelsAsync(model, LogLevels.Error);
        owner.FilterBar.Sync();
        await owner.FilterBar.LevelToggles.Single(t => t.Level == LogLevels.Error).ToggleCommand.Execute();
        Assert.Equal(LogLevels.Error, model.Levels);
        window.Close();
        await owner.DisposeAsync();
    }
    finally
    {
        File.Delete(path);
    }
}

[AvaloniaFact]
public async Task TogglingALevelHidesThoseRowsInTheView()
{
    var path = Path.GetTempFileName();
    try
    {
        var window = TestWindow.Create(out var owner);
        await owner.InitializeAsync();
        await owner.OpenPathsCommand.Execute([path]);
        var file = owner.SelectedFile!;
        file.Model.Buffer.Append(new Line("INFO started"));
        file.Model.Buffer.Append(new Line("ERROR failed"));
        file.Model.Buffer.Append(new Line("continuation without a level"));
        file.SelectedView.Sync();
        Assert.Equal(3, file.SelectedView.Lines.Count);

        await owner.State.SetLevelsAsync(file.Model, LogLevels.Error);
        file.SelectedView.Sync();
        Assert.Equal(
            ["ERROR failed", "continuation without a level"],
            file.SelectedView.Lines.Select(row => row.Line.Raw)
        );
        window.Close();
        await owner.DisposeAsync();
    }
    finally
    {
        File.Delete(path);
    }
}
```

(Use whatever buffer append helper `LogViewTests` uses; the snippet above matches `file.Model.Buffer.Append(new Line(...))` from `LogViewTests`.) If the third test fails because `Sync()` does not recompute lines after a level change, fix `LogViewViewModel.Sync` so a changed `File.Levels` forces a recompute (compare against a remembered `_lastLevels`) — this is the one place where this task touches `LogViewViewModel`.

- [ ] **Step 2: Run to verify they fail** — `LevelToggles` does not exist.

- [ ] **Step 3: Implement**

```csharp
internal sealed class LevelToggleViewModel : ReactiveObject
{
    private readonly FilterBarViewModel _owner;

    internal LevelToggleViewModel(FilterBarViewModel owner, LogLevels level, string label, string name)
    {
        _owner = owner;
        Level = level;
        Label = label;
        Name = name;
        ToggleCommand = ReactiveCommand.CreateFromTask(() => _owner.ToggleLevelAsync(this), owner.Scheduler);
    }

    public LogLevels Level { get; }
    public string Label { get; }
    public string Name { get; }
    public bool IsOn => _owner.IsLevelOn(Level);
    public string AutomationName => $"{Name} {(IsOn ? "shown" : "hidden")}";
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ToggleCommand { get; }
    internal void Refresh()
    {
        this.RaisePropertyChanged(nameof(IsOn));
        this.RaisePropertyChanged(nameof(AutomationName));
    }
}
```

`FilterBarViewModel`: create the five toggles in the constructor (`T`/Trace, `D`/Debug, `I`/Info, `W`/Warn, `E`/Error); `IsLevelOn(level) => _owner.SelectedFile is { } f && (f.Model.Levels & level) != 0`; `HasLevelFilter => file.Model.Levels != LogLevels.All`; `ToggleLevelAsync`:

```csharp
internal async Task ToggleLevelAsync(LevelToggleViewModel toggle)
{
    var file = _owner.SelectedFile?.Model;
    if (file is null)
        return;
    var next = file.Levels ^ toggle.Level;
    if ((next & LogLevels.All) == LogLevels.None)
        return;
    await _owner.State.SetLevelsAsync(file, next);
}
```

`Sync()` calls `toggle.Refresh()` for all toggles and raises `HasLevelFilter` on every call (before the early return on unchanged chips — restructure so the chip comparison is skipped but level refresh always runs).

XAML (right-aligned in the input row, new column): a `Border Classes="level-segment"` containing an `ItemsControl` of `ToggleButton`s: `IsChecked="{Binding IsOn, Mode=OneWay}"`, `Command="{Binding ToggleCommand}"`, `Content="{Binding Label}"`, `ToolTip.Tip="{Binding Name}"`, `AutomationProperties.Name="{Binding AutomationName}"`, name `LevelToggle`. Style `Border.level-segment` (rounded, 1px `MutedBrush`) and `ToggleButton` in it with `MinWidth=24`, `Padding=4,0`, no inner border, `:checked` background `AccentBrush` at 25% opacity. Set `IsChecked` one-way so the `Command` owns the state (a refused clear snaps back on the next `Sync`).

- [ ] **Step 4: Run to verify** — three new tests pass; full suite green.

- [ ] **Step 5: Commit**

```bash
git add -A src
git commit -m "feat(ui): add a per-source log level control"
```

---

### Task 6: Saved filters (model, persistence, sidebar, palette)

**Files:**
- Create: `src/HexTail/ViewModels/SavedFilterViewModel.cs`
- Modify: `src/HexTail/Persistence/AppConfig.cs`, `src/HexTail/Application/AppState.cs`, `src/HexTail/ViewModels/MainWindowViewModel.cs`, `src/HexTail/ViewModels/FilterBarViewModel.cs`, `src/HexTail/Views/SourceSidebar.axaml`, `src/HexTail/Views/FilterBar.axaml`
- Test: `src/HexTail.Tests/Application/SavedFilterTests.cs`, `src/HexTail.Tests/Persistence/SessionToleranceTests.cs` (add cases), `src/HexTail.Tests/Ui/SidebarTests.cs` (add one test)

**Interfaces:**
- Consumes: Tasks 1–5.
- Produces:
  - `public sealed record SavedFilter { string Name; List<ViewRule> Rules; LogLevels Levels = All }` and `AppConfig.SavedFilters` (`List<SavedFilter>`).
  - `AppState.SavedFilters` (`IReadOnlyList<SavedFilter>`), `SaveFilterAsync(string name, IEnumerable<ViewRule> rules, LogLevels levels, ct)`, `DeleteSavedFilterAsync(string name, ct)`, `ApplySavedFilterAsync(FileTabState tab, string name, ct)`.
  - `SavedFilterViewModel` (`Name`, `Summary` like `2 rules · levels W E`, `ApplyCommand`, `DeleteCommand`), `MainWindowViewModel.SavedFilters` (`ObservableCollection`), `bool HasSavedFilters`.
  - `FilterBarViewModel`: `bool CanSaveFilter` (selected source has Source rules or `HasLevelFilter`), `string NewFilterName`, `ReactiveCommand SaveFilterCommand`, `bool SaveFlyoutOpen`.
  - Palette: one item per saved filter, title `Apply filter: {name}`, category `Filters`, enabled only when a source is selected.

- [ ] **Step 1: Write the failing tests**

`SavedFilterTests.cs` (`NewState()` / `OpenTempFileAsync` helpers as in Task 1):

```csharp
[Fact]
public async Task SavingCapturesSourceRulesAndLevelsAndOverwritesBySameName()
{
    await using var state = NewState();
    await state.SaveFilterAsync("Errors", [TestRules.Source("boom")], LogLevels.Error | LogLevels.Warn);
    await state.SaveFilterAsync("errors ", [TestRules.Source("other")], LogLevels.All);
    var filter = Assert.Single(state.SavedFilters);
    Assert.Equal("errors", filter.Name);
    Assert.Equal(["other"], filter.Rules.Select(rule => rule.Pattern));
    Assert.All(filter.Rules, rule => Assert.Equal(RuleScope.Source, rule.Scope));
}

[Fact]
public async Task ApplyMergesRulesWithoutDuplicatesAndSetsLevelsOnlyWhenNotAll()
{
    await using var state = NewState();
    var tab = await OpenTempFileAsync(state);
    await state.SetSourceRulesAsync(tab, [TestRules.Source("keep")]);
    await state.SaveFilterAsync(
        "mix",
        [TestRules.Source("keep"), TestRules.Source("noise", RuleAction.Hide)],
        LogLevels.Error
    );
    await state.ApplySavedFilterAsync(tab, "mix");
    Assert.Equal(["keep", "noise"], tab.Rules.Select(rule => rule.Pattern));
    Assert.Equal(LogLevels.Error, tab.Levels);

    await state.SaveFilterAsync("rules-only", [TestRules.Source("x")], LogLevels.All);
    await state.ApplySavedFilterAsync(tab, "rules-only");
    Assert.Equal(LogLevels.Error, tab.Levels);
}

[Fact]
public async Task DeleteAndUnknownNamesAreSafe()
{
    await using var state = NewState();
    var tab = await OpenTempFileAsync(state);
    await state.SaveFilterAsync("a", [TestRules.Source("x")], LogLevels.All);
    await state.ApplySavedFilterAsync(tab, "missing");
    Assert.Empty(tab.Rules);
    await state.DeleteSavedFilterAsync("A");
    Assert.Empty(state.SavedFilters);
}

[Fact]
public async Task SavedFiltersSurviveARestart()
{
    var persistence = new TestPersistence();
    await using (var first = NewState(persistence))
        await first.SaveFilterAsync("keep", [TestRules.Source("boom")], LogLevels.Warn);
    await using var second = NewState(persistence);
    await second.RestoreAsync();
    var filter = Assert.Single(second.SavedFilters);
    Assert.Equal(LogLevels.Warn, filter.Levels);
}
```

(Make `NewState(IAppPersistence?)` accept a shared persistence; the restore entry point is `AppState.RestoreAsync`.)

`SessionToleranceTests` additions: a session JSON whose `savedFilters` is `[{"name":"ok","rules":[{"pattern":"a"}],"levels":"Error"},{"rules":[]},"junk",{"name":"bad","levels":"Nope","rules":[{"pattern":"b","action":"Bogus"}]}]` loads with `ok` intact; the nameless entry and the string are dropped; `bad` keeps its name with `Levels == All` and its invalid rule dropped (the same tolerance `SanitizeRules`/`levels` give tabs). Plus: a session without `savedFilters` loads with an empty list.

- [ ] **Step 2: Run to verify they fail** — types do not exist.

- [ ] **Step 3: Implement**

`AppConfig.cs`:

```csharp
public sealed record SavedFilter
{
    public string Name { get; init; } = string.Empty;
    public List<ViewRule> Rules { get; init; } = [];
    public LogLevels Levels { get; init; } = LogLevels.All;
}
```

Add `public List<SavedFilter> SavedFilters { get; init; } = [];` to `AppConfig`. In `AppConfigJson.Sanitize`, after the settings/tabs handling, tolerantly sanitize `root["savedFilters"]`: if it is not a `JsonArray`, remove it; drop entries that are not objects or lack a non-blank string `name`; for each kept entry call the existing `SanitizeRules(entry)` and drop an unparseable `levels` exactly as the tab loop does (`Parses<LogLevels>`). Reuse the same helper methods; do not duplicate their logic.

`AppState`:
- Private `List<SavedFilter> _savedFilters = []` guarded by `_gate`; `public IReadOnlyList<SavedFilter> SavedFilters` returns a snapshot.
- Restore from `AppConfig.SavedFilters` where the config is applied (next to where `Rules` are restored); normalize each: trimmed non-blank name, unique case-insensitively (later wins), rules via `NormalizeRules(rules, RuleScope.Source)`, `Levels = NormalizeLevels(levels)`.
- Include `SavedFilters = [.. _savedFilters]` in the `AppConfig` that `SaveAsync` builds.
- `SaveFilterAsync`: normalize as above; replace an existing name case-insensitively; `NotifyChanged(); await SaveAsync(ct)`.
- `DeleteSavedFilterAsync`: remove case-insensitively; notify and save.
- `ApplySavedFilterAsync(tab, name)`: find the filter (no-op if absent or tab not open); `rules = tab.Rules` plus any filter rules not already value-present; `await SetSourceRulesAsync(tab, rules)`; if `filter.Levels != LogLevels.All` then `await SetLevelsAsync(tab, filter.Levels)`.

`MainWindowViewModel`: `SavedFilters` collection rebuilt in `SyncFromState` (compare names + summaries before clearing to avoid churn); `SavedFilterViewModel` commands call `_state.ApplySavedFilterAsync(SelectedFile?.Model, name)` / `DeleteSavedFilterAsync(name)`; `Summary`: `$"{n} rule{(n==1?"":"s")}"` and, when `Levels != All`, ` · levels ` + the letters of the set flags joined by spaces (`T D I W E`). Palette: in `BuildPaletteItems` add `foreach (var filter in _state.SavedFilters) yield return new($"Apply filter: {filter.Name}", "Filters", null, async () => await _state.ApplySavedFilterAsync(SelectedFile?.Model, filter.Name))`, guarded by `SelectedFile is not null` (match the existing guards for file-dependent items).

`FilterBarViewModel`: `CanSaveFilter`, `NewFilterName`, `SaveFlyoutOpen`, and

```csharp
internal async Task SaveFilterAsync()
{
    var file = _owner.SelectedFile?.Model;
    var name = NewFilterName.Trim();
    if (file is null || name.Length == 0 || !CanSaveFilter)
        return;
    await _owner.State.SaveFilterAsync(name, file.Rules, file.Levels);
    NewFilterName = string.Empty;
    SaveFlyoutOpen = false;
}
```

UI: in `FilterBar.axaml` a "Save filter" `Button` (icon `mdi-content-save-outline`, `AutomationProperties.Name="Save filter"`, `IsVisible="{Binding CanSaveFilter}"`) with a `Flyout` containing a name `TextBox` (`SaveFilterNameBox`, Enter saves) and a `Save` button bound to `SaveFilterCommand`. `SourceSidebar.axaml`: below the Elastic section add a `SAVED FILTERS` heading (`Classes="sidebar-heading"`), a hint `No saved filters` (`IsVisible="{Binding !HasSavedFilters}"`), and an `ItemsControl x:Name="SavedFilterList"` of rows (`Classes="source-row"`): a `Button Classes="source-select"` with name and summary (`Command="{Binding ApplyCommand}"`, `AutomationProperties.Name="{Binding Name, StringFormat='Apply filter {0}'}"`, tooltip "Merge into the selected source") and a ✕ button (`DeleteCommand`, `AutomationProperties.Name` "Delete saved filter {Name}").

- [ ] **Step 4: Run to verify** — saved-filter tests, the tolerance cases and a sidebar UI test (save one filter via `FilterBar.SaveFilterCommand`, assert `SavedFilterList` shows one row and clicking apply adds the rules) pass; full suite green.

- [ ] **Step 5: Commit**

```bash
git add -A src
git commit -m "feat(rules): save named filters and apply them from the sidebar"
```

---

### Task 7: Remove Settings → Labels & Exclusions

**Files:**
- Modify: `src/HexTail/Views/SettingsPanel.axaml`, `src/HexTail/ViewModels/SettingsViewModel.cs`, `src/HexTail/ViewModels/MainWindowViewModel.cs`
- Test: delete or port every test found in Step 1

**Interfaces:**
- Consumes: Tasks 2–4 (global rules are edited as chips with "All sources").
- Produces: `SettingsViewModel.SectionIndex` clamped to 0..1 (0 Appearance, 1 Elastic, default 0, `Section` default `"appearance"`); `SetUpElasticCommand` sets `SectionIndex = 1`. Removed: `Labels`, `Exclusions`, all `New*`/`CanAdd*`/`Has*Error` members, `AddLabelCommand`, `RemoveLabelCommand`, `AddExclusionCommand`, `RemoveExclusionCommand`, `CommitLabelAsync`, `CommitExclusionAsync`, `LabelSettingViewModel`, `ExclusionSettingViewModel`, `ValidateRule`, `IsLabelRule`/`IsExclusionRule`/`AbsoluteIndex` helpers, and their parts of `Sync`.

- [ ] **Step 1: Find the tests that must change**

Run: `rtk grep -rln "\.Labels\b\|\.Exclusions\b\|AddLabelCommand\|AddExclusionCommand\|NewLabel\|NewExclusion\|CommitLabel\|CommitExclusion\|LabelSettingViewModel\|ExclusionSettingViewModel\|SectionIndex\|AddLabelButton\|AddExclusionButton\|LabelsSettingsScrollViewer" src/HexTail.Tests`
Expected (from the survey): `GlobalRuleTests`, `SettingsReflowTests`, `SidebarTests`, `OnboardingTests`, `ElasticSetupTests`, `MainWindowInteractionTests`, `ElasticSettingsViewModelTests`. For each hit decide: **delete** if it only exercised the removed editor; **port** if it protects behavior that still exists (global rules hide/highlight rows — rewrite with `owner.State.AddRuleAsync(null, TestRules.Highlight(...))`; Elastic section index 2 → 1; reflow tests that measured the Labels tab → measure the Appearance tab). Write a failing replacement for each ported behavior first (these fail to compile or assert until the section is removed). Add one new test: `SettingsHasNoLabelsOrExclusionsSection` — open Settings and assert no control named `AddLabelButton`/`AddExclusionButton` exists and the `TabControl` has two items.

- [ ] **Step 2: Run to verify** — the new/ported tests fail or do not compile.

- [ ] **Step 3: Implement** — delete the first `TabItem` (the `Labels & Exclusions` block, lines ~50–240 of `SettingsPanel.axaml`) and make the remaining items indexes 0 and 1; delete the members listed above from `SettingsViewModel` and the two nested view-model classes; change `SectionIndex` clamp/`Section` mapping (`1 => "elastic"`, default `"appearance"`); update `SetUpElasticCommand`. Re-grep `Settings.Rules` consumers to confirm nothing else wrote to them via the removed commit methods. Check the settings commit path still preserves `Rules`: every `CommitAsync(... with { ... })` starts from `_owner.State.Settings`, so `Rules` is carried; add one test that changing the theme leaves a global rule in place.

- [ ] **Step 4: Run to verify** — full suite green, warnings 53 (removing code should not add any); `rtk grep -rn "Labels & Exclusions" src docs README.md` shows only historical plan files.

- [ ] **Step 5: Commit**

```bash
git add -A src
git commit -m "refactor(settings): remove the labels and exclusions section"
```

---

### Task 8: Row keys — `j` / `k`, `n` / `N`

**Files:**
- Modify: `src/HexTail/Input/ShellShortcuts.cs`, `src/HexTail/MainWindow.axaml.cs`, `src/HexTail/ViewModels/LogViewViewModel.cs`, `src/HexTail/ViewModels/MainWindowViewModel.cs` (palette), `src/HexTail/Views/LogView.axaml.cs`
- Test: `src/HexTail.Tests/Input/ShellShortcutsTests.cs` (extend the existing mapper test file if present, else create), `src/HexTail.Tests/Ui/RowKeyTests.cs`

**Interfaces:**
- Consumes: `FileTabState.SelectedLine`, `MainWindowViewModel.SelectLine`, `HighlightSpanCache.Get(line, searches, RuleSet)`, `FileTabState.EffectiveRules(settings)`.
- Produces: `ShellAction.NextRow`, `PreviousRow`, `NextMatch`, `PreviousMatch`; `LogViewViewModel.MoveSelection(int delta)` and `LogViewViewModel.MoveToMatch(int direction)`.
- Mapping (non-primary, no text input focused): `J` → NextRow, `K` → PreviousRow, `N` → NextMatch, `Shift+N` → PreviousMatch. With Ctrl/Meta the existing mappings are unchanged (`Ctrl+K` stays the palette).

- [ ] **Step 1: Write the failing tests**

Mapper tests (copy the style of the existing `ShellShortcuts` tests; if none exist create `Input/ShellShortcutsTests.cs`):

```csharp
[Theory]
[InlineData(Key.J, KeyModifiers.None, ShellAction.NextRow)]
[InlineData(Key.K, KeyModifiers.None, ShellAction.PreviousRow)]
[InlineData(Key.N, KeyModifiers.None, ShellAction.NextMatch)]
[InlineData(Key.N, KeyModifiers.Shift, ShellAction.PreviousMatch)]
public void RowKeysResolveOutsideTextInput(Key key, KeyModifiers modifiers, ShellAction expected) =>
    Assert.Equal(expected, ShellShortcuts.Resolve(key, modifiers, textInputFocused: false).Action);

[Theory]
[InlineData(Key.J)]
[InlineData(Key.K)]
[InlineData(Key.N)]
public void RowKeysAreIgnoredWhileTyping(Key key) =>
    Assert.Equal(ShellAction.None, ShellShortcuts.Resolve(key, KeyModifiers.None, textInputFocused: true).Action);

[Fact]
public void CtrlKStillOpensThePalette() =>
    Assert.Equal(ShellAction.CommandPalette, ShellShortcuts.Resolve(Key.K, KeyModifiers.Control, false).Action);
```

`Ui/RowKeyTests.cs` — view-model level plus one dispatch test:

```csharp
[AvaloniaFact]
public async Task MoveSelectionWalksVisibleRowsAndStopsAtTheEnds()
{
    var path = Path.GetTempFileName();
    try
    {
        var window = TestWindow.Create(out var owner);
        await owner.InitializeAsync();
        await owner.OpenPathsCommand.Execute([path]);
        var file = owner.SelectedFile!;
        foreach (var text in new[] { "a", "b", "c" })
            file.Model.Buffer.Append(new Line(text));
        var view = file.SelectedView;
        view.Sync();
        view.MoveSelection(1);
        Assert.Equal(0, file.Model.SelectedLine);
        view.MoveSelection(1);
        view.MoveSelection(1);
        view.MoveSelection(1);
        Assert.Equal(2, file.Model.SelectedLine);
        view.MoveSelection(-5);
        Assert.Equal(0, file.Model.SelectedLine);
        window.Close();
        await owner.DisposeAsync();
    }
    finally
    {
        File.Delete(path);
    }
}

[AvaloniaFact]
public async Task MoveToMatchJumpsBetweenRowsThatHaveHighlights()
{
    var path = Path.GetTempFileName();
    try
    {
        var window = TestWindow.Create(out var owner);
        await owner.InitializeAsync();
        await owner.OpenPathsCommand.Execute([path]);
        var file = owner.SelectedFile!;
        await owner.State.SetSourceRulesAsync(file.Model, [TestRules.Source("hit")]);
        foreach (var text in new[] { "x", "hit one", "y", "z", "hit two", "w" })
            file.Model.Buffer.Append(new Line(text));
        var view = file.SelectedView;
        view.Sync();
        view.MoveToMatch(1);
        Assert.Equal(1, file.Model.SelectedLine);
        view.MoveToMatch(1);
        Assert.Equal(4, file.Model.SelectedLine);
        view.MoveToMatch(1);
        Assert.Equal(4, file.Model.SelectedLine);
        view.MoveToMatch(-1);
        Assert.Equal(1, file.Model.SelectedLine);
        window.Close();
        await owner.DisposeAsync();
    }
    finally
    {
        File.Delete(path);
    }
}
```

(`FileTabState.SelectedLine` is a buffer index: `AppState.SelectLine` stores `FindLineIndex(tab, line)`, so the expectations above hold while no row is hidden.)

- [ ] **Step 2: Run to verify they fail** — enum members and view-model methods do not exist.

- [ ] **Step 3: Implement**

`ShellShortcuts.Resolve`, in the non-primary `switch` (after `Key.G`):

```csharp
Key.J => new(ShellAction.NextRow),
Key.K => new(ShellAction.PreviousRow),
Key.N => new(shift ? ShellAction.PreviousMatch : ShellAction.NextMatch),
```

Add the four enum members. The existing `Key.F when !shift` / `Key.G` arms show the style; `shift` is already computed. `MainWindow.Execute`:

```csharp
case ShellAction.NextRow when file is not null:
    file.SelectedView.MoveSelection(1);
    break;
case ShellAction.PreviousRow when file is not null:
    file.SelectedView.MoveSelection(-1);
    break;
case ShellAction.NextMatch when file is not null:
    file.SelectedView.MoveToMatch(1);
    break;
case ShellAction.PreviousMatch when file is not null:
    file.SelectedView.MoveToMatch(-1);
    break;
```

`LogViewViewModel`: both methods work on `Lines` (the visible rows of this view) and select through the existing selection path so the context pane and persistence behave as for a click:

```csharp
internal void MoveSelection(int delta)
{
    if (Lines.Count == 0)
        return;
    var current = CurrentRowIndex();
    var next = current < 0
        ? (delta > 0 ? 0 : Lines.Count - 1)
        : Math.Clamp(current + delta, 0, Lines.Count - 1);
    _file.SelectLine(Lines[next].Line);
}

internal void MoveToMatch(int direction)
{
    var rules = File.EffectiveRules(Settings);
    var start = CurrentRowIndex();
    for (var index = start + direction; index >= 0 && index < Lines.Count; index += direction)
    {
        if (_file.HighlightSpans.Get(Lines[index].Line, File.Searches, rules).Count > 0)
        {
            _file.SelectLine(Lines[index].Line);
            return;
        }
    }
}

private int CurrentRowIndex()
{
    var selected = File.SelectedLine;
    if (selected is null || selected < 0 || selected >= File.Buffer.Count)
        return -1;
    var line = File.Buffer[selected.Value];
    for (var index = 0; index < Lines.Count; index++)
        if (ReferenceEquals(Lines[index].Line, line))
            return index;
    return -1;
}
```

`File.Buffer[index]` is the `FileBuffer` indexer. `HighlightSpanCache.Get(line, searches, rules)` is the exact call `LogLineViewModel.Render` makes; reusing it keeps "has a highlight" identical to what is drawn (pinned searches and rules both count).

`LogView.axaml.cs`: when the selected line changes through the view model (not through a click), scroll the new row into view. Subscribe to the view model's property changes for the selected line (or, simplest, in `OnLogSelectionChanged` after the list item is selected call `LogList.ScrollIntoView(LogList.SelectedItem!)`). Verify manually in a headless test that the selected item is the expected `LogLineViewModel` (`LogList.SelectedItem`) — real scrolling is not asserted headlessly; say so in the report.

Palette: no new items (the keys are not commands the palette needs); add the four keys to the Settings → none. Update `docs/user-guide.md` in Task 9.

- [ ] **Step 4: Run to verify** — mapper tests, view-model tests pass; full suite green, warnings 53.

- [ ] **Step 5: Commit**

```bash
git add -A src
git commit -m "feat(keys): move between rows and matches with j, k, n and N"
```

---

### Task 9: Docs and closing checks

**Files:**
- Modify: `README.md`, `docs/user-guide.md`, `docs/architecture.md` (only if it names `SearchBar`, `Labels & Exclusions` or `IsGlobalLabel`)
- Verify only: the full suite, the headless warnings, and a real-session run

- [ ] **Step 1: Update the docs**

In `docs/user-guide.md` replace the Search / Labels / Exclusions sections with one **Filters** section: the chip row (global chips dashed, source chips solid, tab chips marked), the input (`Enter` highlight, `Shift+Enter` show only, `Alt+Enter` hide, `Pin as tab`, `Aa`, Literal/Regex), chip actions (click to edit, middle-click to disable, ✕ to remove, "All sources" makes a rule global), the level control (T D I W E, lines without a level stay visible), saved filters (save from the bar, apply from the sidebar or `Ctrl+K`, apply merges), and the new keys (`j`/`k` rows, `n`/`N` matches) in the shortcut table. Remove the Settings → Labels & Exclusions description; Settings now lists Appearance and Elastic. In `README.md` update the feature list the same way (`rtk grep -n "Label\|Exclusion\|Search" README.md` first).

- [ ] **Step 2: Verify against real data**

Run the full suite three times: `rtk dotnet test src/HexTail.Tests --no-build` (expected: all green each time; if `AccessibilityTests.SettingsFocus…` flakes, report it). Then copy the user's real `session.json` to a scratch directory (do **not** touch the original), load it through `JsonFileAppPersistence` the way `SessionToleranceTests` does, and confirm the 7 migrated global rules appear in `AppSettings.Rules` and an unchanged round-trip keeps them. Record both results in the report. Note in the report which UI behaviors are only covered headlessly: chip dashed outline, popover layout, sidebar section, scroll-into-view for `j`/`k`/`n`/`N`.

- [ ] **Step 3: Update the ledger and commit**

Append to `.superpowers/sdd/progress.md` (git-ignored) a `2B: complete` line with test count, warnings and the open manual checks. Then:

```bash
git add -A README.md docs
git commit -m "docs: describe the filter bar, level control, saved filters and row keys"
```

---

## Self-review notes

- **Spec coverage (proposal item 2):** chips with dashed globals — T2; click to edit, middle-click disable, ✕ remove — T2/T4; one input with Enter / Shift+Enter / Alt+Enter and "Pin as tab" — T3; automatic color, five swatches plus custom, inline `ColorPicker` removed — T3/T4; level filter T/D/I/W/E — T5; saved filters in the sidebar applied to any source — T6; Settings Labels & Exclusions removed — T7. **Item 4 (UI half):** level control and level-driven filtering — T5 (columns and inspector are part C). **Item 8 keys deferred from phase 1:** `n`/`N`, `j`/`k` — T8 (`/` shipped in phase 1).
- **Deliberate deviations to confirm:** no `ViewRule.Id` (decision 1); source-pinned rules still unsupported (decision 3); saved filters merge instead of replacing (decision 7); `Pin as tab` is an input toggle instead of a separate gesture (decision 3).
- **Type consistency:** `FilterBarViewModel` members used across tasks — `Chips`, `Sync`, `SubmitAsync(RuleAction)`, `ToggleAsync`, `RemoveAsync`, `Activate`, `ApplyAsync`, `LevelToggles`, `ToggleLevelAsync`, `IsLevelOn`, `CanSaveFilter`, `SaveFilterAsync`, `Scheduler`, `FocusRequested` — are introduced in the task that first needs them and keep the same names afterwards. `AppState` rule methods are `AddRuleAsync/ReplaceRuleAsync/RemoveRuleAsync(FileTabState?, …)` everywhere.
- **Known gaps accepted:** global rules can only be edited while a source is open (decision 9); persisting chip order is not needed (order is list order); the chip popover and sidebar are covered by headless tests only, so a manual pass in the running app is still owed.
