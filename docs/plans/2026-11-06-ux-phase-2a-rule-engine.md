# UX Phase 2A — Rule Engine and Level Model Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the three unrelated filter concepts (per-file searches, global labels, global exclusions) with one rule model, add log-level detection with a per-source level mask, and migrate saved sessions, with no visible UI change except wording.

**Architecture:** A pure-domain `ViewRule` record plus an immutable, compiled `RuleSet` evaluate hide / show-only / highlight for a line. `AppSettings.Rules` (scope Global) and `FileTabState.Rules` (scope Source) feed one effective `RuleSet` per tab. Pinned rules keep using the existing `Search` machinery, so view tabs, follow flags and `PersistedSearch` do not change. Old `session.json` files are migrated at the JSON level (v1 → v2) and the original is kept as a backup.

**Tech Stack:** .NET 10, Avalonia 12.1.1, ReactiveUI, xUnit v3 (`[Fact]`, `[AvaloniaFact]`), System.Text.Json (`JsonNode` for the migration).

This is part A of phase 2 of `docs/ux/2026-11-04-ux-proposals.md` (items 2, 4, 10). Part B (filter bar, chips, level control, saved filters, removing Settings → Labels & Exclusions) and part C (row inspector, Settings becomes small) get their own plans once A has landed, because their code depends on the final shapes defined here.

## Global Constraints

- Branch `audit-ui-phase-2`, stacked on `audit-ui` (open PR jublin/HexTail#10). Never work on `main`. **Never `git push` unless the user explicitly says so.**
- Conventional Commits, one commit per task. End each commit message with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- A CSharpier pre-commit hook reformats staged C# on commit. `csharpier check` already fails on `src/HexTail/HexTail.csproj`; leave that file alone.
- Prefix shell commands with `rtk`. Explore with `sb` (`sb show <file> <Symbol>`, `sb callers <Symbol>`) before opening whole files.
- Build/test commands (run from the repo root):
  - `rtk dotnet build src/HexTail.Tests`
  - `rtk dotnet test src/HexTail.Tests --no-build --filter "FullyQualifiedName~<Name>"`
  - full suite: `rtk dotnet test src/HexTail.Tests --no-build` (baseline at the start of this phase: 380 passing; 53 build warnings, do not add new ones).
- Tests must never touch the OS credential vault: every `new AppState(...)` in a test passes `credentials: new InMemoryCredentialVault()`.
- UI tests that append to a temp file use `TestWindow.CreatePolling(out var viewModel)` (polling-only tailing; macOS watcher "Created" events otherwise reset the buffer) and wait with `UiWait.Until(...)`; never assert immediately after an append.
- ReactiveUI commands keep the existing positional scheduler pattern (`ReactiveCommand.Create*(func, scheduler)`).
- Old session files must still load. The migration must not lose a label, an exclusion, its match mode, or its pinned-as-tab flag.
- Reflection bindings are used in XAML (compiled bindings are off). No UI work is planned in this part.

### Decisions taken where the proposal left room (veto points)

1. **`ViewRule` has no `Id` and no `ViewId` yet.** Identity is the record value. Stable ids for chip editing arrive in part B, where the UI needs them.
2. **`RuleScope.View` exists in the enum but is not stored or evaluated in part A.** Only `Global` (in settings) and `Source` (per open source) are persisted. A view-level rule needs a view identity that part B defines.
3. **Pinned rules stay `Search` objects.** A rule with `PinnedAsTab` produces a `Search` (view tab). Per-file pinned searches keep their existing `PersistedSearch` storage and are *not* migrated into `Rules` in part A; part B presents them as chips next to rules. `RuleSet` ignores pinned rules, because the `Search` already supplies their highlights and tab.
4. **Several show-only rules combine with AND** (each further narrows the view). Hide wins over show-only. Show-only rules also highlight their matches.
5. **Lines with no detectable level are never hidden by the level mask**, so stack-trace continuation lines stay visible next to their parent.
6. **The migration is v1 → v2 at JSON level.** Legacy `globalLabels` / `globalExcludeLabels` / `globalExcludeModes` become global rules; the first migrating load copies the original file to `session.v1.json.bak` once. Downgrading to an older build afterwards loses labels and exclusions (it ignores `rules`); the backup is the way back.

## File Structure

| Path | Status | Responsibility |
| --- | --- | --- |
| `src/HexTail/Domain/ViewRule.cs` | create | `RuleAction`, `RuleScope`, `ViewRule` |
| `src/HexTail/Domain/RuleSet.cs` | create | Compiled rules: `IsVisible`, `Highlights`, `HasVisibilityRules`, `InvalidRules`, `For`, `Combine` |
| `src/HexTail/Domain/LogLevels.cs` | create | `LogLevels` flags and `LevelDetector` |
| `src/HexTail/Persistence/RuleMigration.cs` | create | v1 → v2 JSON migration |
| `src/HexTail/Persistence/AppConfig.cs` | modify | `Version`, `AppSettings.Rules`; remove legacy label/exclusion members; per-tab `Rules` and `Levels` (task 4) |
| `src/HexTail/Persistence/JsonFileAppPersistence.cs` | modify | One-time `session.v1.json.bak` |
| `src/HexTail/Application/FileTabState.cs` | modify | `SyncGlobalRuleSearches`, `Rules`, `SetRules`, `RulesVersion`, `Levels`, `EffectiveRules` |
| `src/HexTail/Application/HighlightSpanCache.cs` | modify | Take a `RuleSet` instead of `AppSettings` |
| `src/HexTail/Application/AppState.cs` | modify | `NormalizeRules`, rule searches, persistence of source rules and levels, `SetSourceRulesAsync`, `SetLevelsAsync` |
| `src/HexTail/ViewModels/LogViewViewModel.cs` | modify | Filter via `RuleSet` and level mask; rule-change refresh; message wording |
| `src/HexTail/ViewModels/LogLineViewModel.cs` | modify | Spans from the effective `RuleSet` |
| `src/HexTail/ViewModels/SettingsViewModel.cs` | modify | Labels and Exclusions edit global rules |
| `src/HexTail/ViewModels/MainWindowViewModel.cs` | modify | Search color palette reads rules |
| `src/HexTail.Tests/Support/TestRules.cs` | create | `TestRules.Highlight/Hide` builders |
| Tests | create/modify | `Domain/RuleSetTests.cs`, `Domain/LevelDetectorTests.cs`, `Persistence/RuleMigrationTests.cs`, `Application/SourceRulesTests.cs`, plus the 8 existing files that use the legacy members (task 3) |

---

### Task 1: Rule model and compiled `RuleSet`

**Files:**
- Create: `src/HexTail/Domain/ViewRule.cs`, `src/HexTail/Domain/RuleSet.cs`
- Test: `src/HexTail.Tests/Domain/RuleSetTests.cs`

**Interfaces:**
- Produces:
  - `enum RuleAction { Highlight, ShowOnly, Hide }`, `enum RuleScope { View, Source, Global }`
  - `sealed record ViewRule { string Pattern; MatchMode Mode; bool CaseSensitive; string Color; RuleAction Action; RuleScope Scope; bool Enabled; bool PinnedAsTab }`
  - `readonly record struct ColoredRange(int Start, int Length, string Color)`
  - `RuleSet`: `static RuleSet Empty`, `static RuleSet For(IReadOnlyList<ViewRule>)` (cached by list instance), `static RuleSet Combine(IReadOnlyList<ViewRule> first, IReadOnlyList<ViewRule> second)`, `bool HasVisibilityRules`, `bool IsVisible(Line)`, `IReadOnlyList<ColoredRange> Highlights(Line)`, `IReadOnlyList<ViewRule> InvalidRules`

- [ ] **Step 1: Write the failing tests**

`src/HexTail.Tests/Domain/RuleSetTests.cs`:

```csharp
using HexTail.Domain;

namespace HexTail.Tests.Domain;

public sealed class RuleSetTests
{
    private static ViewRule Rule(
        string pattern,
        RuleAction action = RuleAction.Highlight,
        bool enabled = true,
        bool pinned = false,
        MatchMode mode = MatchMode.Literal,
        bool caseSensitive = false,
        string color = "#112233"
    ) =>
        new()
        {
            Pattern = pattern,
            Action = action,
            Enabled = enabled,
            PinnedAsTab = pinned,
            Mode = mode,
            CaseSensitive = caseSensitive,
            Color = color,
            Scope = RuleScope.Global,
        };

    private static Line L(string raw) => new(raw);

    [Fact]
    public void Hide_RemovesMatchingLinesIgnoringCaseByDefault()
    {
        var rules = new RuleSet([Rule("healthz", RuleAction.Hide)]);
        Assert.False(rules.IsVisible(L("GET /HEALTHZ 200")));
        Assert.True(rules.IsVisible(L("GET /orders 200")));
        Assert.True(rules.HasVisibilityRules);
    }

    [Fact]
    public void CaseSensitiveRule_OnlyMatchesExactCase()
    {
        var rules = new RuleSet([Rule("Error", RuleAction.Hide, caseSensitive: true)]);
        Assert.False(rules.IsVisible(L("an Error here")));
        Assert.True(rules.IsVisible(L("an error here")));
    }

    [Fact]
    public void ShowOnly_RulesCombineWithAnd_AndHideWins()
    {
        var rules = new RuleSet(
            [
                Rule("checkout", RuleAction.ShowOnly),
                Rule("timeout", RuleAction.ShowOnly),
                Rule("retry", RuleAction.Hide),
            ]
        );
        Assert.True(rules.IsVisible(L("checkout timeout")));
        Assert.False(rules.IsVisible(L("checkout ok")));
        Assert.False(rules.IsVisible(L("checkout timeout retry")));
    }

    [Fact]
    public void DisabledAndPinnedRules_AreIgnored()
    {
        var rules = new RuleSet(
            [
                Rule("a", RuleAction.Hide, enabled: false),
                Rule("b", RuleAction.Hide, pinned: true),
                Rule("c", RuleAction.Highlight, pinned: true),
            ]
        );
        Assert.False(rules.HasVisibilityRules);
        Assert.True(rules.IsVisible(L("a b c")));
        Assert.Empty(rules.Highlights(L("a b c")));
    }

    [Fact]
    public void Highlights_CoverHighlightAndShowOnlyRulesWithTheirColors()
    {
        var rules = new RuleSet(
            [
                Rule("warn", RuleAction.Highlight, color: "#aa0000"),
                Rule("db", RuleAction.ShowOnly, color: "#00aa00"),
                Rule("noise", RuleAction.Hide, color: "#0000aa"),
            ]
        );
        var ranges = rules.Highlights(L("warn from db and noise"));
        Assert.Contains(new ColoredRange(0, 4, "#aa0000"), ranges);
        Assert.Contains(new ColoredRange(10, 2, "#00aa00"), ranges);
        Assert.DoesNotContain(ranges, range => range.Color == "#0000aa");
    }

    [Fact]
    public void InvalidRegex_IsReportedAndSkippedInsteadOfThrowing()
    {
        var bad = Rule("([", RuleAction.Hide, mode: MatchMode.Regex);
        var rules = new RuleSet([bad, Rule("ok", RuleAction.Hide)]);
        Assert.Equal([bad], rules.InvalidRules);
        Assert.False(rules.IsVisible(L("ok")));
        Assert.True(rules.IsVisible(L("([")));
    }

    [Fact]
    public void For_ReturnsTheSameInstanceForTheSameList_AndCombineKeepsBothSides()
    {
        IReadOnlyList<ViewRule> list = [Rule("x", RuleAction.Hide)];
        Assert.Same(RuleSet.For(list), RuleSet.For(list));
        Assert.Same(RuleSet.Empty, RuleSet.For([]));

        var combined = RuleSet.Combine(list, [Rule("y", RuleAction.Hide)]);
        Assert.False(combined.IsVisible(L("x")));
        Assert.False(combined.IsVisible(L("y")));
        Assert.Same(RuleSet.For(list), RuleSet.Combine(list, []));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `rtk dotnet build src/HexTail.Tests`
Expected: build error `CS0246: The type or namespace name 'ViewRule' could not be found`.

- [ ] **Step 3: Implement**

`src/HexTail/Domain/ViewRule.cs`:

```csharp
namespace HexTail.Domain;

public enum RuleAction
{
    Highlight,
    ShowOnly,
    Hide,
}

public enum RuleScope
{
    View,
    Source,
    Global,
}

/// <summary>
/// One filter rule. Immutable; identity is value equality.
/// <see cref="PinnedAsTab"/> only applies to Highlight/ShowOnly: the rule then also owns a view tab
/// (a <c>Search</c>), and <see cref="RuleSet"/> ignores it because that search supplies the matches.
/// </summary>
public sealed record ViewRule
{
    public string Pattern { get; init; } = string.Empty;
    public MatchMode Mode { get; init; } = MatchMode.Literal;
    public bool CaseSensitive { get; init; }
    public string Color { get; init; } = "#F59E0B";
    public RuleAction Action { get; init; } = RuleAction.Highlight;
    public RuleScope Scope { get; init; } = RuleScope.Global;
    public bool Enabled { get; init; } = true;
    public bool PinnedAsTab { get; init; }
}
```

`src/HexTail/Domain/RuleSet.cs`:

```csharp
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace HexTail.Domain;

public readonly record struct ColoredRange(int Start, int Length, string Color);

/// <summary>
/// Compiled, immutable evaluation of a rule list. Hide wins; several ShowOnly rules must all match;
/// ShowOnly and Highlight rules contribute highlight ranges. Disabled and pinned rules are ignored.
/// Rules whose regex does not compile are skipped and listed in <see cref="InvalidRules"/>.
/// </summary>
public sealed class RuleSet
{
    private const int QueryCacheLimit = 512;

    public static RuleSet Empty { get; } = new([]);

    private static readonly ConditionalWeakTable<IReadOnlyList<ViewRule>, RuleSet> ForCache = new();
    private static readonly ConcurrentDictionary<
        (string Pattern, MatchMode Mode, bool CaseSensitive),
        CompiledQuery?
    > Queries = new();

    private readonly Matcher[] _hide;
    private readonly Matcher[] _showOnly;
    private readonly Matcher[] _highlight;

    public RuleSet(IEnumerable<ViewRule> rules)
    {
        var hide = new List<Matcher>();
        var showOnly = new List<Matcher>();
        var highlight = new List<Matcher>();
        var invalid = new List<ViewRule>();
        foreach (var rule in rules)
        {
            if (!rule.Enabled || rule.PinnedAsTab || string.IsNullOrWhiteSpace(rule.Pattern))
                continue;
            var query = Compile(rule);
            if (query is null)
            {
                invalid.Add(rule);
                continue;
            }
            var matcher = new Matcher(query, rule.Color);
            switch (rule.Action)
            {
                case RuleAction.Hide:
                    hide.Add(matcher);
                    break;
                case RuleAction.ShowOnly:
                    showOnly.Add(matcher);
                    highlight.Add(matcher);
                    break;
                default:
                    highlight.Add(matcher);
                    break;
            }
        }
        _hide = [.. hide];
        _showOnly = [.. showOnly];
        _highlight = [.. highlight];
        InvalidRules = invalid;
    }

    public IReadOnlyList<ViewRule> InvalidRules { get; }
    public bool HasVisibilityRules => _hide.Length + _showOnly.Length > 0;

    /// <summary>Cached by list instance; callers replace lists instead of mutating them.</summary>
    public static RuleSet For(IReadOnlyList<ViewRule> rules) =>
        rules.Count == 0 ? Empty : ForCache.GetValue(rules, static list => new RuleSet(list));

    public static RuleSet Combine(IReadOnlyList<ViewRule> first, IReadOnlyList<ViewRule> second) =>
        second.Count == 0 ? For(first)
        : first.Count == 0 ? For(second)
        : new RuleSet(first.Concat(second));

    public bool IsVisible(Line line)
    {
        foreach (var matcher in _hide)
            if (matcher.Query.IsMatch(line.Raw))
                return false;
        foreach (var matcher in _showOnly)
            if (!matcher.Query.IsMatch(line.Raw))
                return false;
        return true;
    }

    public IReadOnlyList<ColoredRange> Highlights(Line line)
    {
        if (_highlight.Length == 0)
            return [];
        List<ColoredRange>? ranges = null;
        foreach (var matcher in _highlight)
        foreach (var range in matcher.Query.GetHighlights(line.Raw))
            (ranges ??= []).Add(new ColoredRange(range.Start, range.Length, matcher.Color));
        return ranges ?? [];
    }

    private static CompiledQuery? Compile(ViewRule rule)
    {
        var key = (rule.Pattern, rule.Mode, rule.CaseSensitive);
        if (Queries.TryGetValue(key, out var cached))
            return cached;
        CompiledQuery? query;
        try
        {
            query = new CompiledQuery(rule.Pattern, rule.Mode, rule.CaseSensitive);
        }
        catch (ArgumentException)
        {
            query = null;
        }
        if (Queries.Count >= QueryCacheLimit)
            Queries.Clear();
        Queries[key] = query;
        return query;
    }

    private readonly record struct Matcher(CompiledQuery Query, string Color);
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `rtk dotnet build src/HexTail.Tests && rtk dotnet test src/HexTail.Tests --no-build --filter "FullyQualifiedName~RuleSetTests"`
Expected: 7 passed.

- [ ] **Step 5: Commit**

```bash
git add src/HexTail/Domain/ViewRule.cs src/HexTail/Domain/RuleSet.cs src/HexTail.Tests/Domain/RuleSetTests.cs
git commit -m "feat(rules): add the view rule model and compiled rule set"
```

---

### Task 2: Log level detection

**Files:**
- Create: `src/HexTail/Domain/LogLevels.cs`
- Test: `src/HexTail.Tests/Domain/LevelDetectorTests.cs`

**Interfaces:**
- Produces:
  - `[Flags] enum LogLevels { None = 0, Trace = 1, Debug = 2, Info = 4, Warn = 8, Error = 16, All = 31 }`
  - `static class LevelDetector { static LogLevels Detect(Line line); static bool Allows(LogLevels mask, Line line); }`
  - `Detect` returns exactly one flag, or `None` when the level cannot be determined. `Allows` returns true when `mask == All`, when the level is `None`, or when the mask contains the detected flag.

- [ ] **Step 1: Write the failing tests**

`src/HexTail.Tests/Domain/LevelDetectorTests.cs`:

```csharp
using HexTail.Domain;

namespace HexTail.Tests.Domain;

public sealed class LevelDetectorTests
{
    private static Line Fields(params (string Key, string Value)[] fields) =>
        new("raw", fields.ToDictionary(field => field.Key, field => field.Value));

    [Theory]
    [InlineData("level", "debug", LogLevels.Debug)]
    [InlineData("LEVEL", "WARNING", LogLevels.Warn)]
    [InlineData("severity", "critical", LogLevels.Error)]
    [InlineData("log.level", "Information", LogLevels.Info)]
    [InlineData("lvl", "trc", LogLevels.Trace)]
    [InlineData("level", "fatal", LogLevels.Error)]
    [InlineData("level", "whatever", LogLevels.None)]
    public void ParsedFields_AreMappedCaseInsensitively(string key, string value, LogLevels expected) =>
        Assert.Equal(expected, LevelDetector.Detect(Fields((key, value))));

    [Theory]
    [InlineData("2026-11-06 10:00:01 ERROR payment failed", LogLevels.Error)]
    [InlineData("2026-11-06 10:00:01 [WARN] slow query", LogLevels.Warn)]
    [InlineData("10:00:01 INFO started", LogLevels.Info)]
    [InlineData("[10:00:01 DBG] cache hit", LogLevels.Debug)]
    [InlineData("[10:00:01 WRN] retrying", LogLevels.Warn)]
    [InlineData("{\"x\":1} FATAL out of memory", LogLevels.Error)]
    [InlineData("TRACE entering handler", LogLevels.Trace)]
    public void PlainText_RecognizesUppercaseLevelTokens(string raw, LogLevels expected) =>
        Assert.Equal(expected, LevelDetector.Detect(new Line(raw)));

    [Theory]
    [InlineData("no error found")]
    [InlineData("info about the warning label")]
    [InlineData("   at Foo.Bar() in Foo.cs:line 12")]
    [InlineData("")]
    public void PlainText_DoesNotGuessFromLowercaseWords(string raw) =>
        Assert.Equal(LogLevels.None, LevelDetector.Detect(new Line(raw)));

    [Fact]
    public void ParsedLevel_WinsOverTokensInTheMessage() =>
        Assert.Equal(
            LogLevels.Info,
            LevelDetector.Detect(
                new Line("ERROR in text", new Dictionary<string, string> { ["level"] = "info" })
            )
        );

    [Fact]
    public void Allows_NeverHidesUnknownLevels_AndHonorsTheMask()
    {
        var error = new Line("ERROR boom");
        var info = new Line("INFO ok");
        var unknown = new Line("   at Foo.Bar()");
        var onlyErrors = LogLevels.Error;

        Assert.True(LevelDetector.Allows(LogLevels.All, info));
        Assert.True(LevelDetector.Allows(onlyErrors, error));
        Assert.False(LevelDetector.Allows(onlyErrors, info));
        Assert.True(LevelDetector.Allows(onlyErrors, unknown));
    }

    [Fact]
    public void Detect_IsStableAcrossRepeatedCalls()
    {
        var line = new Line("WARN once");
        Assert.Equal(LevelDetector.Detect(line), LevelDetector.Detect(line));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `rtk dotnet build src/HexTail.Tests`
Expected: build error `CS0103: The name 'LevelDetector' does not exist`.

- [ ] **Step 3: Implement**

`src/HexTail/Domain/LogLevels.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace HexTail.Domain;

[Flags]
public enum LogLevels
{
    None = 0,
    Trace = 1,
    Debug = 2,
    Info = 4,
    Warn = 8,
    Error = 16,
    All = Trace | Debug | Info | Warn | Error,
}

/// <summary>
/// Finds a line's level from parsed fields first (<c>level</c>, <c>severity</c>, …), then from an
/// UPPERCASE token near the start of the raw text. Lowercase words are never treated as levels.
/// Results are cached per <see cref="Line"/> because views re-filter the whole buffer on every sync.
/// </summary>
public static partial class LevelDetector
{
    private static readonly string[] FieldNames =
    [
        "level",
        "lvl",
        "severity",
        "loglevel",
        "log.level",
        "levelname",
        "@l",
    ];

    private static readonly ConditionalWeakTable<Line, StrongBox<LogLevels>> Cache = new();

    public static LogLevels Detect(Line line) =>
        Cache.GetValue(line, static item => new StrongBox<LogLevels>(Compute(item))).Value;

    public static bool Allows(LogLevels mask, Line line)
    {
        if (mask == LogLevels.All)
            return true;
        var level = Detect(line);
        return level == LogLevels.None || mask.HasFlag(level);
    }

    private static LogLevels Compute(Line line)
    {
        if (line.ParsedFields is { Count: > 0 } fields)
            foreach (var name in FieldNames)
                foreach (var field in fields)
                    if (string.Equals(field.Key, name, StringComparison.OrdinalIgnoreCase))
                    {
                        var parsed = FromWord(field.Value);
                        if (parsed != LogLevels.None)
                            return parsed;
                    }

        var raw = line.Raw;
        if (raw.Length == 0)
            return LogLevels.None;
        var head = raw.Length > 120 ? raw[..120] : raw;
        var match = Token().Match(head);
        return match.Success ? FromWord(match.Groups[1].Value) : LogLevels.None;
    }

    private static LogLevels FromWord(string word) =>
        word.Trim().ToLowerInvariant() switch
        {
            "trace" or "trc" or "verbose" or "finest" or "finer" => LogLevels.Trace,
            "debug" or "dbg" or "fine" => LogLevels.Debug,
            "info" or "information" or "inf" or "notice" => LogLevels.Info,
            "warn" or "warning" or "wrn" => LogLevels.Warn,
            "error" or "err" or "fatal" or "ftl" or "critical" or "crit" or "severe" or "panic"
                or "alert" or "emergency" => LogLevels.Error,
            _ => LogLevels.None,
        };

    [GeneratedRegex(
        @"(?:^|[\s\[\(=:|])(TRACE|TRC|DEBUG|DBG|INFO|INF|WARN|WARNING|WRN|ERROR|ERR|FATAL|FTL|CRITICAL|SEVERE)(?=$|[\s\]\):|,])",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex Token();
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `rtk dotnet build src/HexTail.Tests && rtk dotnet test src/HexTail.Tests --no-build --filter "FullyQualifiedName~LevelDetectorTests"`
Expected: all pass (7 + 4 + 1 + 1 + 1 cases). If `"{\"x\":1} FATAL out of memory"` fails, the token must be preceded by whitespace; the sample has a space, so recheck the pattern, not the test.

- [ ] **Step 5: Commit**

```bash
git add src/HexTail/Domain/LogLevels.cs src/HexTail.Tests/Domain/LevelDetectorTests.cs
git commit -m "feat(levels): detect a log line's level from fields or an uppercase token"
```

---

### Task 3: Store global rules, migrate old sessions, switch every consumer

Large integration task: one commit, but work in the numbered steps so the build is green at the end of each group. Use `sb callers <Member>` to find every consumer of a removed member.

**Files:**
- Create: `src/HexTail/Persistence/RuleMigration.cs`, `src/HexTail.Tests/Support/TestRules.cs`, `src/HexTail.Tests/Persistence/RuleMigrationTests.cs`
- Modify: `src/HexTail/Persistence/AppConfig.cs`, `src/HexTail/Persistence/JsonFileAppPersistence.cs`, `src/HexTail/Application/FileTabState.cs`, `src/HexTail/Application/HighlightSpanCache.cs`, `src/HexTail/Application/AppState.cs`, `src/HexTail/ViewModels/LogViewViewModel.cs`, `src/HexTail/ViewModels/LogLineViewModel.cs`, `src/HexTail/ViewModels/SettingsViewModel.cs`, `src/HexTail/ViewModels/MainWindowViewModel.cs`
- Modify tests that use the removed members: `Application/AppStateTests.cs` (12), `Application/HighlightSpanCacheTests.cs` (9), `Persistence/JsonFileAppPersistenceTests.cs` (19), `Ui/GlobalRuleTests.cs` (15), `Ui/LogViewTests.cs` (9), `Ui/MainWindowInteractionTests.cs` (2), `Ui/ResultStateTests.cs` (1), `Ui/FollowAndPauseTests.cs` (1)

**Interfaces:**
- Consumes: `ViewRule`, `RuleSet.For/Combine` (task 1).
- Produces:
  - `AppSettings.Rules : List<ViewRule>` (always Scope Global after `AppState` normalization). The members `GlobalLabels`, `GlobalExcludeLabels`, `GlobalExcludeModes`, `Excludes`, `GetExcludeMode`, `GetLabelHighlights` and the classes `GlobalLabel`, `LabelHighlight` are **removed**.
  - `AppConfig.Version : int` (default `RuleMigration.CurrentVersion`, which is 2).
  - `RuleMigration.CurrentVersion`, `RuleMigration.Apply(string json) : (string Json, bool Migrated)`.
  - `AppConfigJson.Deserialize` always runs `RuleMigration.Apply` first.
  - `FileTabState.SyncGlobalRuleSearches(IEnumerable<ViewRule>)`, `FileTabState.Rules : IReadOnlyList<ViewRule>` (Source scope, empty for now), `FileTabState.SetRules(IEnumerable<ViewRule>)`, `FileTabState.RulesVersion : int`, `FileTabState.EffectiveRules(AppSettings) : RuleSet` (UI thread only).
  - `AppState.NormalizeRules(IEnumerable<ViewRule>?, RuleScope) : List<ViewRule>` (internal static).
  - `HighlightSpanCache.Get(Line, IReadOnlyList<Search>, RuleSet)`.
  - `TestRules.Highlight(string pattern, string color = "#F59E0B", bool pinned = false)`, `TestRules.Hide(string pattern)` — both set `Scope = Global` and `Mode = CompiledQuery.DetectMode(pattern)` (the legacy default).

- [ ] **Step 1: Write the failing migration tests**

`src/HexTail.Tests/Persistence/RuleMigrationTests.cs`:

```csharp
using HexTail.Domain;
using HexTail.Persistence;

namespace HexTail.Tests.Persistence;

public sealed class RuleMigrationTests
{
    private const string LegacyJson = """
        { "settings": {
          "globalLabels": [
            { "text": "[ERROR]", "color": "#ff0000", "mode": "Literal", "showInOpenFile": true },
            { "text": "warn.*", "color": "#00ff00" }
          ],
          "globalExcludeLabels": ["healthz", "[a-z]+ping"],
          "globalExcludeModes": { "healthz": "Literal" }
        } }
        """;

    [Fact]
    public void LegacyLabelsAndExclusions_BecomeGlobalRulesWithModesAndPinning()
    {
        var config = AppConfigJson.Deserialize(LegacyJson);
        var rules = config.Settings.Rules;

        Assert.Equal(4, rules.Count);
        Assert.All(rules, rule => Assert.Equal(RuleScope.Global, rule.Scope));

        var error = rules[0];
        Assert.Equal(("[ERROR]", MatchMode.Literal, "#ff0000"), (error.Pattern, error.Mode, error.Color));
        Assert.Equal(RuleAction.Highlight, error.Action);
        Assert.True(error.PinnedAsTab);

        var warn = rules[1];
        Assert.Equal(MatchMode.Regex, warn.Mode); // legacy rule with no mode: detected
        Assert.False(warn.PinnedAsTab);

        Assert.Equal(("healthz", MatchMode.Literal, RuleAction.Hide), (rules[2].Pattern, rules[2].Mode, rules[2].Action));
        Assert.Equal(MatchMode.Regex, rules[3].Mode);
        Assert.Equal(RuleAction.Hide, rules[3].Action);
        Assert.False(rules[2].PinnedAsTab);
    }

    [Fact]
    public void Migration_PreservesBehavior_ForLiteralBracketPatterns()
    {
        var set = RuleSet.For(AppConfigJson.Deserialize(LegacyJson).Settings.Rules);
        Assert.True(set.IsVisible(new Line("READY")));
        Assert.False(set.IsVisible(new Line("GET /healthz")));
    }

    [Fact]
    public void RoundTrip_WritesVersionTwoAndNoLegacyFields()
    {
        var config = AppConfigJson.Deserialize(LegacyJson);
        var json = AppConfigJson.Serialize(config);

        Assert.DoesNotContain("globalLabels", json);
        Assert.DoesNotContain("globalExcludeLabels", json);
        Assert.Contains("\"version\": 2", json);
        Assert.Equal(config.Settings.Rules, AppConfigJson.Deserialize(json).Settings.Rules);
    }

    [Fact]
    public void CurrentFiles_AreNotRewritten()
    {
        var json = AppConfigJson.Serialize(new AppConfig());
        var (rewritten, migrated) = RuleMigration.Apply(json);
        Assert.False(migrated);
        Assert.Equal(json, rewritten);
    }

    [Fact]
    public void MissingSettingsAndGarbageAreTolerated()
    {
        Assert.Empty(AppConfigJson.Deserialize("{}").Settings.Rules);
        Assert.False(RuleMigration.Apply("[1,2]").Migrated);
    }

    [Fact]
    public async Task FirstMigratingLoad_KeepsAOneTimeBackup()
    {
        var directory = Directory.CreateTempSubdirectory("hextail-migration-");
        try
        {
            var path = Path.Combine(directory.FullName, JsonFileAppPersistence.FileName);
            await File.WriteAllTextAsync(path, LegacyJson, TestContext.Current.CancellationToken);
            var persistence = new JsonFileAppPersistence(path);

            var loaded = await persistence.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Equal(4, loaded!.Settings.Rules.Count);
            var backup = Path.Combine(directory.FullName, "session.v1.json.bak");
            Assert.Equal(LegacyJson, await File.ReadAllTextAsync(backup, TestContext.Current.CancellationToken));

            await File.WriteAllTextAsync(path, LegacyJson + " ", TestContext.Current.CancellationToken);
            await persistence.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(LegacyJson, await File.ReadAllTextAsync(backup, TestContext.Current.CancellationToken));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `rtk dotnet build src/HexTail.Tests`
Expected: compile errors for `Rules`, `RuleMigration` (other test files still compile at this point).

- [ ] **Step 3: Implement the migration and the settings shape**

`src/HexTail/Persistence/RuleMigration.cs`:

```csharp
using System.Text.Json.Nodes;
using HexTail.Domain;

namespace HexTail.Persistence;

/// <summary>Rewrites a v1 session (labels/exclusions) as v2 (rules) at JSON level.</summary>
internal static class RuleMigration
{
    internal const int CurrentVersion = 2;
    private const string FallbackColor = "#f59e0b";

    internal static bool NeedsMigration(string json) => Apply(json).Migrated;

    internal static (string Json, bool Migrated) Apply(string json)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return (json, false);
        }
        if (root is null)
            return (json, false);
        if (
            root["version"] is JsonValue versionValue
            && versionValue.TryGetValue<int>(out var version)
            && version >= CurrentVersion
        )
            return (json, false);

        if (root["settings"] is JsonObject settings)
        {
            var rules = settings["rules"] as JsonArray ?? [];
            var modes = (settings["globalExcludeModes"] as JsonObject)
                ?.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value?.GetValue<string>(),
                    StringComparer.OrdinalIgnoreCase
                );

            if (settings["globalLabels"] is JsonArray labels)
                foreach (var label in labels.OfType<JsonObject>())
                {
                    var text = label["text"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(text))
                        continue;
                    rules.Add(
                        Rule(
                            text.Trim(),
                            label["mode"]?.GetValue<string>(),
                            label["color"]?.GetValue<string>(),
                            "Highlight",
                            label["showInOpenFile"]?.GetValue<bool>() ?? false
                        )
                    );
                }

            if (settings["globalExcludeLabels"] is JsonArray exclusions)
                foreach (var exclusion in exclusions)
                {
                    var text = exclusion?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(text))
                        continue;
                    string? mode = null;
                    modes?.TryGetValue(text, out mode);
                    rules.Add(Rule(text.Trim(), mode, null, "Hide", false));
                }

            settings.Remove("globalLabels");
            settings.Remove("globalExcludeLabels");
            settings.Remove("globalExcludeModes");
            settings["rules"] = rules;
        }

        root["version"] = CurrentVersion;
        return (root.ToJsonString(), true);
    }

    private static JsonObject Rule(
        string pattern,
        string? mode,
        string? color,
        string action,
        bool pinned
    ) =>
        new()
        {
            ["pattern"] = pattern,
            ["mode"] = mode is "Literal" or "Regex" ? mode : CompiledQuery.DetectMode(pattern).ToString(),
            ["caseSensitive"] = false,
            ["color"] = IsHexColor(color) ? color : FallbackColor,
            ["action"] = action,
            ["scope"] = "Global",
            ["enabled"] = true,
            ["pinnedAsTab"] = pinned,
        };

    private static bool IsHexColor(string? color) =>
        color is { Length: 4 or 7 } && color[0] == '#' && color[1..].All(Uri.IsHexDigit);
}
```

`NeedsMigration` re-parses the JSON; that is fine because it is only called once per load.

In `src/HexTail/Persistence/AppConfig.cs`:

1. Add `public int Version { get; init; } = RuleMigration.CurrentVersion;` to `AppConfig`.
2. In `AppSettings`: delete `GlobalLabels`, `GlobalExcludeLabels`, `GlobalExcludeModes`, `Excludes`, `GetExcludeMode`, `GetLabelHighlights`, the two `ConditionalWeakTable` caches, `CreateGlobalQuery`, `CachedGlobalQuery`, `CompileGlobalQuery`. Add `public List<ViewRule> Rules { get; init; } = [];` (keep property order otherwise).
3. Delete `GlobalLabel` and `LabelHighlight`.
4. Make `AppConfigJson.Deserialize` migrate first:

```csharp
    public static AppConfig Deserialize(string json) =>
        JsonSerializer.Deserialize<AppConfig>(RuleMigration.Apply(json).Json, Options)
        ?? new AppConfig();
```

(Keep the existing null-handling shape of the current method; only the input changes. `sb show Persistence/AppConfig.cs AppConfigJson` shows it.)

In `src/HexTail/Persistence/JsonFileAppPersistence.cs`, change `LoadAsync` so it backs up before deserializing:

```csharp
            var json = await File.ReadAllTextAsync(Path, cancellationToken).ConfigureAwait(false);
            if (RuleMigration.NeedsMigration(json))
                BackUpLegacySession();
            return AppConfigJson.Deserialize(json);
```

and add:

```csharp
    private void BackUpLegacySession()
    {
        var backup = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Path)!,
            "session.v1.json.bak"
        );
        try
        {
            if (!File.Exists(backup))
                File.Copy(Path, backup);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
```

(A failed backup must not stop the load.)

- [ ] **Step 4: Switch the consumers (production code)**

`FileTabState.cs`: replace `SyncGlobalLabelSearches` with the rule version, and add the source-rule members.

```csharp
    private IReadOnlyList<ViewRule>? _effectiveGlobal;
    private IReadOnlyList<ViewRule>? _effectiveSource;
    private RuleSet _effectiveRules = RuleSet.Empty;

    /// <summary>Source-scope rules for this open source.</summary>
    public IReadOnlyList<ViewRule> Rules { get; private set; } = [];

    /// <summary>Bumped whenever <see cref="Rules"/> is replaced, so views re-render highlights.</summary>
    public int RulesVersion { get; private set; }

    public void SetRules(IEnumerable<ViewRule> rules)
    {
        Rules = rules.ToList();
        RulesVersion++;
    }

    /// <summary>Global + source rules. UI thread only (it caches the last combination).</summary>
    public RuleSet EffectiveRules(AppSettings settings)
    {
        if (
            !ReferenceEquals(_effectiveGlobal, settings.Rules)
            || !ReferenceEquals(_effectiveSource, Rules)
        )
        {
            _effectiveRules = RuleSet.Combine(settings.Rules, Rules);
            _effectiveGlobal = settings.Rules;
            _effectiveSource = Rules;
        }
        return _effectiveRules;
    }

    public void SyncGlobalRuleSearches(IEnumerable<ViewRule> rules)
    {
        for (var index = Searches.Count - 1; index >= 0; index--)
        {
            if (!Searches[index].IsGlobalLabel)
                continue;
            Buffer.RemoveSearch(Searches[index]);
            Searches.RemoveAt(index);
            FollowSearches.RemoveAt(index);
        }

        foreach (
            var rule in rules.Where(rule =>
                rule.Enabled && rule.PinnedAsTab && rule.Action != RuleAction.Hide
            )
        )
            try
            {
                AddSearch(
                    new Search(
                        new CompiledQuery(rule.Pattern, rule.Mode, rule.CaseSensitive),
                        rule.Color,
                        Buffer,
                        true
                    )
                );
            }
            catch (ArgumentException) { }
    }
```

`HighlightSpanCache.cs`: replace the `GlobalLabel[] _labels` field with `private RuleSet? _rules;` and change `Get`/`Compute`:

```csharp
    internal IReadOnlyList<HighlightSpan> Get(
        Line line,
        IReadOnlyList<Search> searches,
        RuleSet rules
    )
    {
        if (!_searches.SequenceEqual(searches) || !ReferenceEquals(_rules, rules))
        {
            _entries.Clear();
            _recent.Clear();
            _spanCount = 0;
            _searches = searches.ToArray();
            _rules = rules;
        }
        // ... unchanged cache lookup ...
        var spans = Compute(line, searches, rules);
        // ... unchanged eviction ...
    }

    private static IReadOnlyList<HighlightSpan> Compute(
        Line line,
        IReadOnlyList<Search> searches,
        RuleSet rules
    )
    {
        var candidates = searches
            .SelectMany(search =>
                search
                    .GetHighlights(line)
                    .Select(range => new HighlightSpan(range.Start, range.Length, search.Color))
            )
            .Concat(
                rules
                    .Highlights(line)
                    .Select(range => new HighlightSpan(range.Start, range.Length, range.Color))
            )
            .Where(/* unchanged */)
            .OrderBy(span => span.Start)
            .ThenByDescending(span => span.Length);
        // ... unchanged overlap resolution ...
    }
```

Remove the now-unused `using HexTail.Persistence;` if nothing else needs it.

`LogLineViewModel.Refresh` (the line `Spans = _file.HighlightSpans.Get(...)`):

```csharp
        Spans = _file.HighlightSpans.Get(
            Line,
            _file.Model.Searches,
            _file.Model.EffectiveRules(settings)
        );
```

`LogViewViewModel`:

```csharp
    private IReadOnlyList<Line> LinesFor()
    {
        IEnumerable<Line> lines = Search is null
            ? _file.Model.Buffer.Lines
            : Search
                .Results.Where(index => index >= 0 && index < _file.Model.Buffer.Count)
                .Select(index => _file.Model.Buffer[index]);
        var rules = _file.Model.EffectiveRules(_owner.State.Settings);
        return rules.HasVisibilityRules
            ? lines.Where(rules.IsVisible).ToList()
            : lines as IReadOnlyList<Line> ?? lines.ToList();
    }

    private IReadOnlyList<Line> ContextLinesFor()
    {
        var lines = _file.Model.Buffer.Lines;
        var rules = _file.Model.EffectiveRules(_owner.State.Settings);
        return rules.HasVisibilityRules ? lines.Where(rules.IsVisible).ToList() : lines;
    }
```

In `ResultMessage`, replace the last branch text `"All matching logs are hidden by global exclusions."` with `"All matching logs are hidden by filter rules."` and update any test or doc that asserts the old sentence (`sb`/grep for `hidden by global exclusions`).

`AppState.cs`:
- Replace the three `SyncGlobalLabelSearches(_settings.GlobalLabels)` calls and the one in `UpdateSettingsAsync` with `SyncGlobalRuleSearches(_settings.Rules)`.
- Replace the label/exclusion block of `NormalizeSettings` with `Rules = NormalizeRules(settings.Rules, RuleScope.Global),` and delete the `labels` / `exclusions` locals and the `GlobalLabels` / `GlobalExcludeLabels` / `GlobalExcludeModes` initializers.
- Add:

```csharp
    internal static List<ViewRule> NormalizeRules(IEnumerable<ViewRule>? rules, RuleScope scope) =>
        (rules ?? [])
            .OfType<ViewRule>()
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Pattern))
            .Select(rule =>
                rule with
                {
                    Pattern = rule.Pattern.Trim(),
                    Color = NormalizeColor(rule.Color),
                    Scope = scope,
                    Mode = Enum.IsDefined(rule.Mode)
                        ? rule.Mode
                        : CompiledQuery.DetectMode(rule.Pattern),
                    Action = Enum.IsDefined(rule.Action) ? rule.Action : RuleAction.Highlight,
                    PinnedAsTab = rule.PinnedAsTab && rule.Action != RuleAction.Hide,
                }
            )
            .DistinctBy(rule =>
                (
                    rule.CaseSensitive ? rule.Pattern : rule.Pattern.ToUpperInvariant(),
                    rule.CaseSensitive,
                    rule.Mode,
                    rule.Action
                )
            )
            .ToList();
```

- In `SaveAsync`, add `Version = RuleMigration.CurrentVersion,` to the `new AppConfig { ... }` initializer if the default does not already apply (it does; skip if so).

`SettingsViewModel.cs` (Labels = global Highlight rules, Exclusions = global Hide rules; the UI is unchanged until part B):

```csharp
    private static bool IsLabelRule(ViewRule rule) => rule.Action == RuleAction.Highlight;

    private static bool IsExclusionRule(ViewRule rule) => rule.Action == RuleAction.Hide;

    private static int AbsoluteIndex(
        IReadOnlyList<ViewRule> rules,
        Func<ViewRule, bool> kind,
        int index
    )
    {
        var seen = -1;
        for (var position = 0; position < rules.Count; position++)
            if (kind(rules[position]) && ++seen == index)
                return position;
        return -1;
    }
```

Replace the sync block (the two `while`/`for` loops over `settings.GlobalLabels` and `settings.GlobalExcludeLabels`) with:

```csharp
        var labelRules = settings.Rules.Where(IsLabelRule).ToList();
        while (Labels.Count > labelRules.Count)
            Labels.RemoveAt(Labels.Count - 1);
        for (var index = 0; index < labelRules.Count; index++)
        {
            if (index == Labels.Count)
                Labels.Add(new LabelSettingViewModel(this, index));
            Labels[index].Sync(labelRules[index]);
        }

        var exclusionRules = settings.Rules.Where(IsExclusionRule).ToList();
        while (Exclusions.Count > exclusionRules.Count)
            Exclusions.RemoveAt(Exclusions.Count - 1);
        for (var index = 0; index < exclusionRules.Count; index++)
        {
            if (index == Exclusions.Count)
                Exclusions.Add(new ExclusionSettingViewModel(this, index));
            Exclusions[index].Sync(exclusionRules[index].Pattern, exclusionRules[index].Mode);
        }
```

Replace the commit/add/remove methods:

```csharp
    internal Task CommitLabelAsync(int index, string text, Color color, bool showInOpenFile, MatchMode mode)
    {
        var rules = _owner.State.Settings.Rules.ToList();
        var at = AbsoluteIndex(rules, IsLabelRule, index);
        if (at < 0)
            return Task.CompletedTask;
        rules[at] = rules[at] with
        {
            Pattern = text,
            Mode = mode,
            Color = ColorToHex(color),
            PinnedAsTab = showInOpenFile,
        };
        return CommitAsync(_owner.State.Settings with { Rules = rules });
    }

    internal Task CommitExclusionAsync(int index, string text, MatchMode mode)
    {
        var rules = _owner.State.Settings.Rules.ToList();
        var at = AbsoluteIndex(rules, IsExclusionRule, index);
        if (at < 0)
            return Task.CompletedTask;
        rules[at] = rules[at] with { Pattern = text, Mode = mode };
        return CommitAsync(_owner.State.Settings with { Rules = rules });
    }

    private void AddLabel()
    {
        if (!CanAddLabel)
            return;
        _ = CommitAsync(
            _owner.State.Settings with
            {
                Rules =
                [
                    .. _owner.State.Settings.Rules,
                    new ViewRule
                    {
                        Pattern = NewLabelText,
                        Mode = NewLabelMode,
                        Color = ColorToHex(NewLabelColor),
                        Action = RuleAction.Highlight,
                        Scope = RuleScope.Global,
                        PinnedAsTab = true,
                    },
                ],
            }
        );
        NewLabelText = string.Empty;
    }

    private void RemoveLabel(LabelSettingViewModel item) => RemoveRule(IsLabelRule, item.Index);

    private void AddExclusion()
    {
        if (!CanAddExclusion)
            return;
        _ = CommitAsync(
            _owner.State.Settings with
            {
                Rules =
                [
                    .. _owner.State.Settings.Rules,
                    new ViewRule
                    {
                        Pattern = NewExclusionText,
                        Mode = NewExclusionMode,
                        Action = RuleAction.Hide,
                        Scope = RuleScope.Global,
                    },
                ],
            }
        );
        NewExclusionText = string.Empty;
    }

    private void RemoveExclusion(ExclusionSettingViewModel item) =>
        RemoveRule(IsExclusionRule, item.Index);

    private void RemoveRule(Func<ViewRule, bool> kind, int index)
    {
        var rules = _owner.State.Settings.Rules.ToList();
        var at = AbsoluteIndex(rules, kind, index);
        if (at < 0)
            return;
        rules.RemoveAt(at);
        _ = CommitAsync(_owner.State.Settings with { Rules = rules });
    }
```

Delete `ExclusionModes()`. Change `LabelSettingViewModel.Sync(GlobalLabel label)` to:

```csharp
    internal void Sync(ViewRule rule)
    {
        if (HasValidationError && rule.Pattern == _persistedText)
            return;
        _persistedText = rule.Pattern;
        _syncing = true;
        try
        {
            Text = rule.Pattern;
            Mode = rule.Mode;
            Color = Avalonia.Media.Color.Parse(rule.Color);
            ShowInOpenFile = rule.PinnedAsTab;
        }
        finally
        {
            _syncing = false;
        }
        ValidationError = SettingsViewModel.ValidateRule(Text, Mode);
    }
```

`MainWindowViewModel.AddSearchAsync`: replace `State.Settings.GlobalLabels.Select(label => label.Color)` with `State.Settings.Rules.Select(rule => rule.Color)`.

- [ ] **Step 5: Add the test helper and port the existing tests**

`src/HexTail.Tests/Support/TestRules.cs`:

```csharp
using HexTail.Domain;

namespace HexTail.Tests.Support;

internal static class TestRules
{
    public static ViewRule Highlight(string pattern, string color = "#F59E0B", bool pinned = false) =>
        new()
        {
            Pattern = pattern,
            Mode = CompiledQuery.DetectMode(pattern),
            Color = color,
            Action = RuleAction.Highlight,
            Scope = RuleScope.Global,
            PinnedAsTab = pinned,
        };

    public static ViewRule Hide(string pattern) =>
        new()
        {
            Pattern = pattern,
            Mode = CompiledQuery.DetectMode(pattern),
            Action = RuleAction.Hide,
            Scope = RuleScope.Global,
        };
}
```

Port every legacy use in the eight test files (find them with `grep -rn "GlobalLabel\|GlobalExclude\|\.Excludes(\|GetLabelHighlights" src/HexTail.Tests --include=*.cs`):

| Old | New |
| --- | --- |
| `GlobalLabels = [new GlobalLabel { Text = "x", Color = "#c", ShowInOpenFile = b, Mode = m }]` | `Rules = [TestRules.Highlight("x", "#c", pinned: b)]`; if the test sets `Mode`, use `TestRules.Highlight("x", "#c", b) with { Mode = m }` |
| `GlobalExcludeLabels = ["x"]` | `Rules = [TestRules.Hide("x")]` (add a `with { Mode = ... }` where the test set `GlobalExcludeModes`) |
| `settings.Excludes(text)` | `!RuleSet.For(settings.Rules).IsVisible(new Line(text))` |
| `settings.GetLabelHighlights(text)` | `RuleSet.For(settings.Rules).Highlights(new Line(text))` (note: pinned rules are not included; a test that relied on `includeSearchTabs: true` must read the pinned `Search` instead) |
| `cache.Get(line, searches, settings)` | `cache.Get(line, searches, RuleSet.For(settings.Rules))` |
| `"hidden by global exclusions"` | `"hidden by filter rules"` |

`JsonFileAppPersistenceTests` has two tests that feed legacy JSON (`ExplicitLiteralGlobalRules_DoNotInterpretRegexCharacters`, `GlobalModes_PreserveLegacyRegexIntentAndSeparateEqualTextCaches`). Keep their JSON inputs (they now exercise the migration) and rewrite the assertions through `RuleSet.For(config.Settings.Rules)`. Do not delete them: they guard the migration's match-mode behavior.

- [ ] **Step 6: Build and run**

Run: `rtk dotnet build src/HexTail.Tests` — expected 0 errors, 53 warnings (no new ones).
Run: `rtk dotnet test src/HexTail.Tests --no-build --filter "FullyQualifiedName~RuleMigration|FullyQualifiedName~RuleSet|FullyQualifiedName~JsonFileAppPersistence|FullyQualifiedName~GlobalRule|FullyQualifiedName~HighlightSpanCache"` — expected all pass.
Run the full suite three times: `rtk dotnet test src/HexTail.Tests --no-build` — expected 380+ passing, 0 failed each time.

- [ ] **Step 7: Commit**

```bash
git add -A src
git commit -m "feat(rules): store global labels and exclusions as rules and migrate old sessions"
```

---

### Task 4: Source-scope rules and the level mask

**Files:**
- Modify: `src/HexTail/Persistence/AppConfig.cs`, `src/HexTail/Application/FileTabState.cs`, `src/HexTail/Application/AppState.cs`, `src/HexTail/ViewModels/LogViewViewModel.cs`
- Test: `src/HexTail.Tests/Application/SourceRulesTests.cs`, `src/HexTail.Tests/Ui/LogViewTests.cs`

**Interfaces:**
- Consumes: `FileTabState.Rules/SetRules/RulesVersion/EffectiveRules` (task 3), `LogLevels`, `LevelDetector` (task 2).
- Produces:
  - `FileTabState.Levels : LogLevels` (default `All`; never `None`).
  - `PersistedFileTab.Rules : List<ViewRule>`, `PersistedFileTab.Levels : LogLevels` (default `All`); the same two members on `PersistedElasticTab`.
  - `AppState.SetSourceRulesAsync(FileTabState tab, IEnumerable<ViewRule> rules, CancellationToken ct = default) : ValueTask`
  - `AppState.SetLevelsAsync(FileTabState tab, LogLevels levels, CancellationToken ct = default) : ValueTask` (stores `All` when `levels` is `None` or has unknown bits).

- [ ] **Step 1: Write the failing tests**

`src/HexTail.Tests/Application/SourceRulesTests.cs` (self-contained; polling tailing, in-memory vault and persistence):

```csharp
using HexTail.Application;
using HexTail.Domain;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;

namespace HexTail.Tests.Application;

public sealed class SourceRulesTests
{
    private static LogSourceService NewTailers() =>
        new(new TailerOptions { PollInterval = TimeSpan.FromMilliseconds(10), UseFileSystemWatcher = false });

    private static ViewRule Source(string pattern, RuleAction action) =>
        new() { Pattern = pattern, Action = action, Scope = RuleScope.Source };

    [Fact]
    public async Task SourceRulesAndLevels_PersistAndRestoreForAFile()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllLinesAsync(path, ["INFO a", "ERROR b"], TestContext.Current.CancellationToken);
        var persistence = new TestPersistence();
        try
        {
            await using (var state = new AppState(NewTailers(), persistence, credentials: new InMemoryCredentialVault()))
            {
                var tab = await state.OpenFileAsync(path);
                await state.SetSourceRulesAsync(tab, [Source("a", RuleAction.Hide)], TestContext.Current.CancellationToken);
                await state.SetLevelsAsync(tab, LogLevels.Warn | LogLevels.Error, TestContext.Current.CancellationToken);
                Assert.Equal(RuleScope.Source, Assert.Single(tab.Rules).Scope);
            }

            await using var restored = new AppState(NewTailers(), persistence, credentials: new InMemoryCredentialVault());
            await restored.RestoreAsync();
            var tab2 = Assert.Single(restored.Files);
            Assert.Equal("a", Assert.Single(tab2.Rules).Pattern);
            Assert.Equal(LogLevels.Warn | LogLevels.Error, tab2.Levels);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SetLevels_StoresAllForNoneOrUnknownBits_AndRulesAreNormalized()
    {
        var path = Path.GetTempFileName();
        try
        {
            await using var state = new AppState(NewTailers(), new TestPersistence(), credentials: new InMemoryCredentialVault());
            var tab = await state.OpenFileAsync(path);

            await state.SetLevelsAsync(tab, LogLevels.None, TestContext.Current.CancellationToken);
            Assert.Equal(LogLevels.All, tab.Levels);
            await state.SetLevelsAsync(tab, (LogLevels)0xFF, TestContext.Current.CancellationToken);
            Assert.Equal(LogLevels.All, tab.Levels);

            await state.SetSourceRulesAsync(
                tab,
                [new ViewRule { Pattern = "  x  ", Scope = RuleScope.Global, Color = "bad" }, new ViewRule { Pattern = "  " }],
                TestContext.Current.CancellationToken
            );
            var rule = Assert.Single(tab.Rules);
            Assert.Equal(("x", RuleScope.Source, "#f59e0b"), (rule.Pattern, rule.Scope, rule.Color));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
```

If `TestPersistence` does not support a restore round trip (check `src/HexTail.Tests/Support/TestPersistence.cs`: it takes an optional initial config and records saves), mirror how `AppStateTests` restores (`MemoryPersistence` there) and use that class instead; keep the assertions identical.

Add to `src/HexTail.Tests/Ui/LogViewTests.cs` an `[AvaloniaFact]` that opens a temp file containing `INFO keep`, `ERROR boom`, `INFO healthz`, `   at Foo.Bar()` using `TestWindow.CreatePolling`, then asserts, waiting with `UiWait.Until` (copy the open/load pattern from `FollowAndPauseTests.LoadedView`):

1. After `await viewModel.State.SetSourceRulesAsync(tab, [new ViewRule { Pattern = "healthz", Action = RuleAction.Hide, Scope = RuleScope.Source }])` and `view.Sync()`, the visible rows' `Line.Raw` are `INFO keep`, `ERROR boom`, `   at Foo.Bar()`.
2. After `await viewModel.State.SetLevelsAsync(tab, LogLevels.Error)` and `view.Sync()`, they are `ERROR boom` and `   at Foo.Bar()` (the unknown-level line stays).
3. `view.ResultMessage` is empty in both cases, and equals `"All matching logs are hidden by filter rules."` after `SetLevelsAsync(tab, LogLevels.Warn)` on a file whose only line is `INFO x`.

- [ ] **Step 2: Run to verify it fails**

Run: `rtk dotnet build src/HexTail.Tests`
Expected: build errors for `SetSourceRulesAsync`, `SetLevelsAsync`, `Levels`.

- [ ] **Step 3: Implement**

`AppConfig.cs`: add to both `PersistedFileTab` and `PersistedElasticTab`:

```csharp
    public List<ViewRule> Rules { get; init; } = [];
    public LogLevels Levels { get; init; } = LogLevels.All;
```

`FileTabState.cs`: add `public LogLevels Levels { get; set; } = LogLevels.All;`.

`AppState.cs`:
- Restore (both the file-tab and Elastic-tab restore paths, next to the `tab.ShowContext = persisted.ShowContext;` lines): 

```csharp
            tab.SetRules(NormalizeRules(persisted.Rules, RuleScope.Source));
            tab.Levels = NormalizeLevels(persisted.Levels);
```

- `SaveAsync`, in both the `PersistedFileTab` and `PersistedElasticTab` initializers: `Rules = [.. tab.Rules], Levels = tab.Levels,`.
- Add (mirror the locking/notify/save shape of `SetShowContextAsync`; read it with `sb show Application/AppState.cs SetShowContextAsync`):

```csharp
    public async ValueTask SetSourceRulesAsync(
        FileTabState tab,
        IEnumerable<ViewRule> rules,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(rules);
        lock (_gate)
        {
            if (!_files.Contains(tab))
                return;
            tab.SetRules(NormalizeRules(rules, RuleScope.Source));
        }
        NotifyChanged();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetLevelsAsync(
        FileTabState tab,
        LogLevels levels,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(tab);
        lock (_gate)
        {
            if (!_files.Contains(tab))
                return;
            tab.Levels = NormalizeLevels(levels);
        }
        NotifyChanged();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static LogLevels NormalizeLevels(LogLevels levels)
    {
        var known = levels & LogLevels.All;
        return known == LogLevels.None ? LogLevels.All : known;
    }
```

`LogViewViewModel.cs`:
- Level filtering in both `LinesFor` and `ContextLinesFor` (after the rule filter):

```csharp
        var levels = _file.Model.Levels;
        if (levels != LogLevels.All)
            result = result.Where(line => LevelDetector.Allows(levels, line)).ToList();
```

  (restructure the two methods so `result` is the rule-filtered list; keep the no-filter fast path returning the original list when neither rules nor levels apply.)
- Re-render highlights when source rules change: add `private int _lastRulesVersion;` and in `Sync`, where `viewsChanged` is computed:

```csharp
        var rulesChanged = _lastRulesVersion != _file.Model.RulesVersion;
        _lastRulesVersion = _file.Model.RulesVersion;
        ...
        if (viewsChanged || rulesChanged)
        {
            InvalidateRows(Lines);
            InvalidateRows(ContextLines);
        }
```

  (replace the existing `if (viewsChanged)` block with the combined condition).

- [ ] **Step 4: Run to verify it passes**

Run: `rtk dotnet build src/HexTail.Tests && rtk dotnet test src/HexTail.Tests --no-build --filter "FullyQualifiedName~SourceRules|FullyQualifiedName~LogViewTests"`
Expected: pass. Then the full suite three times (expected 0 failed each).

- [ ] **Step 5: Commit**

```bash
git add -A src
git commit -m "feat(rules): add per-source rules and a per-source level mask"
```

---

## Verification (after task 4)

- [ ] Full suite three times in a row: 0 failures; build warnings still 53.
- [ ] Load a copy of a real v1 `session.json` (labels and exclusions present) through `AppConfigJson.Deserialize` in a throwaway test or `dotnet fsi`, and confirm the rule count equals labels + exclusions and `session.v1.json.bak` appears next to the loaded file.
- [ ] Not verified by automation (say so in the wrap-up): behavior of Settings → Labels & Exclusions in the running app (it was ported by index mapping), and the migration against the user's own session file.

## What follows (separate plans, written after this lands)

**Part B — filter bar (items 2 and 4, UI):** replace `SearchBar` with a chip row under the view tabs (global chips dashed, source chips solid, pinned searches shown as chips); one input with Enter / Shift+Enter / Alt+Enter = highlight / show only / hide and a "pin as tab" option; chip popover (edit, disable via middle-click, remove, five swatches plus custom); T/D/I/W/E segmented level control bound to `SetLevelsAsync`; saved filters in the sidebar (named rule sets applied to a source); `/`, `n`/`N`, `j`/`k` keys deferred from phase 1; remove Settings → Labels & Exclusions and the inline `ColorPicker`; add `ViewRule` identity for chip editing and decide view-scope storage.

**Part C — inspector and settings (items 4 and 10):** a row inspector (parsed fields as a key/value list, copy as JSON/raw/field, "filter on this value" actions that create rules), then Settings reduced to Appearance, Limits and Advanced as a side sheet with left navigation.
