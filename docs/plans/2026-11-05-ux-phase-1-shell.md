# UX Phase 1 — Shell, Time Pill, Keyboard Flow, Empty State Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the stacked toolbar/tab-strip chrome with a source sidebar, one view bar, a status bar, a compact time-range pill, pause/new-lines affordances, keyboard shortcuts, a command palette, and a useful empty state. Source: phase 1 (items 1, 6, 8, 9) of [the UX proposals](../ux/2026-11-04-ux-proposals.md), with [mockups A, B, D](../ux/2026-11-04-ux-mockups.html).

**Architecture:** View-model-first, using the existing Avalonia 12 + ReactiveUI patterns. All new state lives on `MainWindowViewModel` or on small, focused view models that `MainWindowViewModel.SyncFromState()` keeps current. Time parsing and label formatting are pure static classes with unit tests. The shell layout uses a built-in `SplitView`: inline at 1000 px and wider, an overlay below that. Phase 1 adds **no NuGet packages** and makes **no breaking changes to persisted data**.

**Tech Stack:** .NET 10, Avalonia 12.1.1 (Fluent theme, `SplitView`, `Thumb`), ReactiveUI.Avalonia, Optris Material Design icons (`mdi-*`), xUnit v3 + Avalonia.Headless.XUnit.

## Global Constraints

- Branch: `audit-ui` (not a protected branch; no new branch needed). **Never `git push`.**
- The first commit is this plan (Task 0). After that, make **one Conventional Commit per task**, formatted as `<type>(<scope>): <description>`.
- Before the first commit, run `dotnet tool restore && dotnet husky install` once. The pre-commit hook formats staged C# with CSharpier.
- Test command: `dotnet test src/HexTail.Tests/HexTail.Tests.csproj -c Release` (baseline at plan time: **249 passed**). Filtered runs use `--filter "FullyQualifiedName~<ClassName>"`.
- Minimum window: 720×720 (`MainWindow.MinWidth`/`MinHeight`). Every new control must stay inside the window at 720, 1280, and 1920 px wide.
- Persistence: `session.json` must still load. Add properties only with defaults. A removed property (`AppWindowState.VerticalFileTabs`) is silently ignored by `System.Text.Json` defaults.
- Copy: sentence case, no paragraphs of explanation in the main UI, and a tooltip on every icon button that includes its shortcut.
- Use theme brushes only (`{DynamicResource …Brush}`). No hard-coded colors in XAML.
- Bindings are reflection bindings (compiled bindings are not enabled), so `$parent[UserControl].DataContext.X` works as it does in existing views.
- Keep `x:Name`s that existing tests use unless a task explicitly renames them: `SettingsButton`, `OpenFileButton`, `ElasticTimeRangePanel`, `ElasticTimeRangeButton`, `ApplyElasticTimeRangeButton`, `ElasticToNowCheckBox`, `AutoScrollToggle`, `InlineContextToggle`, `ResultMessage`, `ResultCount`, `InitialLimitMessage`, `OpenEmptyFileButton`, `SetUpElasticButton`, `QueryBox`, `AddSearchButton`, `LogList`, `ContextList`, `SettingsDialogHost`.

### Decisions taken where the proposal left questions open

1. **One time-zone control.** The global `AppSettings.TimeZoneMode` only shifts clock offsets (same instant; see `AppState.Now()`). It becomes the single **display zone**: a status-bar picker replaces both Appearance → *Source clock time zone* and the range flyout's zone picker. Typed range times without an offset are read in this zone. `FileTabState.ElasticInputZone` is still persisted; Apply sets it to the display zone it used, and nothing else reads it.
2. **Native date/time pickers are removed** from the range flyout in favor of presets plus parseable text inputs. "Recent ranges" are deferred.
3. **Deferred to phase 2:** `n`/`N` match navigation and `j`/`k` row keys (these depend on the phase-2 filter model; arrow keys already move rows), and the sidebar's *Saved filters* and *Streams* sections.
4. **Pause is global** (toolbar). It freezes row appends in the visible view while tailers keep buffering.

## File Structure

| Path | Status | Responsibility |
| --- | --- | --- |
| `src/HexTail/Application/TimeExpression.cs` | create | Parse user time text into tailer expressions (`now`, `now-Nu`, ISO-8601); format expressions for inputs |
| `src/HexTail/Application/TimeRangeLabel.cs` | create | Human label for an applied range (`Last 15 min`, `A → B`) |
| `src/HexTail/Application/SampleLogWriter.cs` | create | Write and append a demo log for "Try a sample log" |
| `src/HexTail/Input/ShellShortcuts.cs` | create | Pure key → `ShellCommand` mapping |
| `src/HexTail/ViewModels/ElasticConnectionGroupViewModel.cs` | create | Sidebar group: one Elastic connection plus its sources |
| `src/HexTail/ViewModels/CommandPaletteViewModel.cs` | create | Palette query, ranking, selection, execution |
| `src/HexTail/ViewModels/RecentFileViewModel.cs` | create | Empty-state recent file row |
| `src/HexTail/Behaviors/ListReorderBehavior.cs` | create | Alt+Up/Down reorder for vertical `ItemsControl`s |
| `src/HexTail/Views/SourceSidebar.axaml(.cs)` | create | Files and Elastic sections |
| `src/HexTail/Views/StatusBar.axaml(.cs)` | create | Source summary, result count, range state, pause state, zone picker |
| `src/HexTail/Views/CommandPalette.axaml(.cs)` | create | Ctrl+K overlay |
| `src/HexTail/Views/FileStrip.axaml(.cs)` | **delete** | Replaced by the sidebar |
| `src/HexTail/MainWindow.axaml(.cs)` | modify | New shell grid, top bar, shortcut dispatch, width tracking |
| `src/HexTail/ViewModels/MainWindowViewModel.cs` | modify | Sidebar, range text, pause, palette, recents, status members |
| `src/HexTail/ViewModels/ElasticSourceOptionViewModel.cs` | modify | Connection id, short name, selection, activate/close commands |
| `src/HexTail/ViewModels/FileTabViewModel.cs` | modify | `HasError`, `LineCountText`, `SelectedView` notifications |
| `src/HexTail/ViewModels/LogViewViewModel.cs` | modify | Pause freeze, unseen-line tracking, jump-to-latest |
| `src/HexTail/Views/LogWorkspace.axaml` | modify | View toolbar (Follow/Context), empty state |
| `src/HexTail/Views/LogView.axaml(.cs)` | modify | Jump-to-latest overlay, `ScrollToTop()` |
| `src/HexTail/Views/SettingsPanel.axaml` | modify | Remove *Source clock time zone* |
| `src/HexTail/Styles/CyberTail.axaml` | modify | Sidebar, status, palette, pill styles |
| `src/HexTail/Persistence/AppConfig.cs` | modify | `SidebarCollapsed`, `SidebarWidth`, `RecentFiles`, `RecentElasticSourceIds` |
| `src/HexTail/Application/AppState.cs` | modify | Track and persist recents |
| `src/HexTail.Tests/Support/{ElasticFixtures,UiWait}.cs` | create | Shared fixtures |
| `src/HexTail.Tests/Support/TestPersistence.cs` | modify | Optional initial config |
| Tests (new) | create | `Application/TimeExpressionTests.cs`, `ViewModels/SidebarViewModelTests.cs`, `Ui/SidebarTests.cs`, `Ui/StatusBarTests.cs`, `Ui/FollowAndPauseTests.cs`, `Input/ShellShortcutsTests.cs`, `Ui/ShortcutDispatchTests.cs`, `ViewModels/CommandPaletteViewModelTests.cs`, `Ui/CommandPaletteTests.cs`, `Application/RecentSourcesTests.cs`, `Application/SampleLogWriterTests.cs` |
| Tests (modified) | modify | `Ui/MainWindowInteractionTests.cs`, `Ui/TabNavigationTests.cs`, `Ui/OnboardingTests.cs` |
| `README.md`, `docs/user-guide.md` | modify | Shortcuts, sidebar, time range, pause |

---

### Task 0: Commit the plan

**Files:**
- Create: `docs/plans/2026-11-05-ux-phase-1-shell.md` (this file)

- [ ] **Step 1: Commit**

```bash
git add docs/plans/2026-11-05-ux-phase-1-shell.md docs/ux/2026-11-04-ux-proposals.md docs/ux/2026-11-04-ux-mockups.html
git commit -m "docs(plans): add UX phase 1 shell plan and proposals"
```

---

### Task 1: Time expression parser and range label

**Files:**
- Create: `src/HexTail/Application/TimeExpression.cs`
- Create: `src/HexTail/Application/TimeRangeLabel.cs`
- Test: `src/HexTail.Tests/Application/TimeExpressionTests.cs`

**Interfaces:**
- Consumes: `ElasticTailer.ParseTime(string expression, DateTimeOffset now)` (internal static, `src/HexTail/Elastic/ElasticTailer.cs:340`). It accepts `now`, `now-<n>[smhd]`, and absolute timestamps. `AppTimeZoneMode { Local, Utc }` lives in `HexTail.Persistence`.
- Produces:
  - `public readonly record struct ParsedTime(string Expression, DateTimeOffset Resolved);`
  - `public static bool TimeExpression.TryParse(string? input, DateTimeOffset now, AppTimeZoneMode zone, out ParsedTime result, out string? error)`
  - `public static string TimeExpression.FormatForInput(string expression, AppTimeZoneMode zone)`
  - `internal static DateTimeOffset TimeExpression.ToZone(DateTimeOffset value, AppTimeZoneMode zone)`
  - `public static string TimeRangeLabel.Describe(string from, string to, AppTimeZoneMode zone)`

- [ ] **Step 1: Write the failing tests**

```csharp
// src/HexTail.Tests/Application/TimeExpressionTests.cs
using HexTail.Application;
using HexTail.Elastic;
using HexTail.Persistence;

namespace HexTail.Tests.Application;

public sealed class TimeExpressionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 14, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("now", "now")]
    [InlineData("NOW", "now")]
    [InlineData("now-15m", "now-15m")]
    [InlineData("-15m", "now-15m")]
    [InlineData(" -2h ", "now-2h")]
    [InlineData("-30s", "now-30s")]
    [InlineData("now-1w", "now-7d")]
    public void RelativeInputNormalizesToTailerExpression(string input, string expected)
    {
        Assert.True(
            TimeExpression.TryParse(input, Now, AppTimeZoneMode.Utc, out var parsed, out var error)
        );
        Assert.Null(error);
        Assert.Equal(expected, parsed.Expression);
        Assert.Equal(ElasticTailer.ParseTime(expected, Now), parsed.Resolved);
    }

    [Theory]
    [InlineData("2026-08-20T10:15:30Z", "2026-08-20T10:15:30.0000000+00:00")]
    [InlineData("2026-08-20T10:15:30+02:00", "2026-08-20T10:15:30.0000000+02:00")]
    [InlineData("2026-08-20 10:15:30", "2026-08-20T10:15:30.0000000+00:00")]
    [InlineData("2026-08-20 10:15", "2026-08-20T10:15:00.0000000+00:00")]
    [InlineData("2026-08-20", "2026-08-20T00:00:00.0000000+00:00")]
    [InlineData("09:05", "2026-11-05T09:05:00.0000000+00:00")]
    [InlineData("09:05:10", "2026-11-05T09:05:10.0000000+00:00")]
    [InlineData("today", "2026-11-05T00:00:00.0000000+00:00")]
    [InlineData("yesterday 22:10", "2026-11-04T22:10:00.0000000+00:00")]
    public void AbsoluteInputUsesUtcWallTime(string input, string expected)
    {
        Assert.True(
            TimeExpression.TryParse(input, Now, AppTimeZoneMode.Utc, out var parsed, out var error)
        );
        Assert.Null(error);
        Assert.Equal(expected, parsed.Expression);
        Assert.Equal(DateTimeOffset.Parse(expected), parsed.Resolved);
    }

    [Fact]
    public void LocalWallTimeUsesThisComputersOffset()
    {
        Assert.True(
            TimeExpression.TryParse(
                "2026-08-20 10:00",
                Now,
                AppTimeZoneMode.Local,
                out var parsed,
                out _
            )
        );
        var wall = new DateTime(2026, 8, 20, 10, 0, 0);
        Assert.Equal(new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall)), parsed.Resolved);
    }

    [Theory]
    [InlineData("", "Enter a time")]
    [InlineData("   ", "Enter a time")]
    [InlineData("now-15x", "not a recognized time")]
    [InlineData("2026-13-40", "not a recognized time")]
    [InlineData("25:00", "not a recognized time")]
    [InlineData("yesterday 99:00", "not a recognized time")]
    public void InvalidInputReportsAnError(string input, string fragment)
    {
        Assert.False(
            TimeExpression.TryParse(input, Now, AppTimeZoneMode.Utc, out _, out var error)
        );
        Assert.Contains(fragment, error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("now", "now")]
    [InlineData("now-15m", "now-15m")]
    [InlineData("2026-08-20T12:15:30.0000000+02:00", "2026-08-20 10:15:30")]
    [InlineData("not a time", "not a time")]
    public void FormatForInputRendersAbsoluteTimesInZone(string expression, string expected) =>
        Assert.Equal(expected, TimeExpression.FormatForInput(expression, AppTimeZoneMode.Utc));

    [Theory]
    [InlineData("now-15m", "now", "Last 15 min")]
    [InlineData("now-1m", "now", "Last minute")]
    [InlineData("now-1h", "now", "Last hour")]
    [InlineData("now-4h", "now", "Last 4 hours")]
    [InlineData("now-24h", "now", "Last 24 hours")]
    [InlineData("now-7d", "now", "Last 7 days")]
    [InlineData("now-15m", "now-5m", "now-15m → now-5m")]
    [InlineData(
        "2026-08-20T10:15:30.0000000+00:00",
        "2026-08-20T10:16:45.0000000+00:00",
        "2026-08-20 10:15:30 → 2026-08-20 10:16:45"
    )]
    [InlineData("2026-08-20T10:15:30.0000000+00:00", "now", "2026-08-20 10:15:30 → now")]
    public void RangeLabelIsShortAndReadable(string from, string to, string expected) =>
        Assert.Equal(expected, TimeRangeLabel.Describe(from, to, AppTimeZoneMode.Utc));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HexTail.Tests/HexTail.Tests.csproj -c Release --filter "FullyQualifiedName~TimeExpressionTests"`
Expected: build FAILS with `CS0103: The name 'TimeExpression' does not exist`.

- [ ] **Step 3: Implement `TimeExpression`**

```csharp
// src/HexTail/Application/TimeExpression.cs
using System.Globalization;
using System.Text.RegularExpressions;
using HexTail.Elastic;
using HexTail.Persistence;

namespace HexTail.Application;

public readonly record struct ParsedTime(string Expression, DateTimeOffset Resolved);

/// <summary>
/// Parses the time text users type into the expressions <see cref="ElasticTailer"/> accepts:
/// <c>now</c>, <c>now-&lt;n&gt;[smhd]</c>, or a round-trip ("O") timestamp.
/// </summary>
public static partial class TimeExpression
{
    private static readonly string[] WallFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-dd",
    ];

    private static readonly string[] ClockFormats = ["HH:mm:ss", "HH:mm", "H:mm:ss", "H:mm"];

    [GeneratedRegex(
        @"^(?:now)?-(\d+(?:\.\d+)?)([smhdw])$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex RelativePattern();

    [GeneratedRegex(
        @"(?:Z|[+-]\d{2}:?\d{2})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex OffsetSuffix();

    public static bool TryParse(
        string? input,
        DateTimeOffset now,
        AppTimeZoneMode zone,
        out ParsedTime result,
        out string? error
    )
    {
        result = default;
        error = null;
        var value = input?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            error = "Enter a time.";
            return false;
        }

        if (value.Equals("now", StringComparison.OrdinalIgnoreCase))
        {
            result = new("now", now);
            return true;
        }

        var relative = RelativePattern().Match(value);
        if (relative.Success)
        {
            var amount = relative.Groups[1].Value;
            var unit = char.ToLowerInvariant(relative.Groups[2].Value[0]);
            if (unit == 'w')
            {
                amount = (double.Parse(amount, CultureInfo.InvariantCulture) * 7).ToString(
                    CultureInfo.InvariantCulture
                );
                unit = 'd';
            }
            var expression = $"now-{amount}{unit}";
            result = new(expression, ElasticTailer.ParseTime(expression, now));
            return true;
        }

        if (
            OffsetSuffix().IsMatch(value)
            && DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var absolute
            )
        )
        {
            result = new(absolute.ToString("O", CultureInfo.InvariantCulture), absolute);
            return true;
        }

        var today = ToZone(now, zone).Date;
        DateTime? wall = null;
        if (
            DateTime.TryParseExact(
                value,
                WallFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dated
            )
        )
            wall = dated;
        else if (TryParseDayWord(value, today, out var day))
            wall = day;
        else if (TryParseClock(value, out var clock))
            wall = today + clock;

        if (wall is null)
        {
            error =
                $"“{value}” is not a recognized time. Try now-15m, -2h, 14:00, yesterday 09:00, or 2026-11-05 14:00.";
            return false;
        }

        return TryResolveWallTime(wall.Value, zone, out result, out error);
    }

    public static string FormatForInput(string expression, AppTimeZoneMode zone)
    {
        var value = expression.Trim();
        if (
            value.Equals("now", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("now-", StringComparison.OrdinalIgnoreCase)
        )
            return value.ToLowerInvariant();
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var time
        )
            ? ToZone(time, zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : value;
    }

    internal static DateTimeOffset ToZone(DateTimeOffset value, AppTimeZoneMode zone) =>
        zone == AppTimeZoneMode.Utc ? value.ToUniversalTime() : value.ToLocalTime();

    private static bool TryParseDayWord(string value, DateTime today, out DateTime wall)
    {
        wall = default;
        var parts = value.Split(
            ' ',
            2,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        DateTime day;
        if (parts[0].Equals("today", StringComparison.OrdinalIgnoreCase))
            day = today;
        else if (parts[0].Equals("yesterday", StringComparison.OrdinalIgnoreCase))
            day = today.AddDays(-1);
        else
            return false;

        if (parts.Length == 1)
        {
            wall = day;
            return true;
        }
        if (!TryParseClock(parts[1], out var clock))
            return false;
        wall = day + clock;
        return true;
    }

    private static bool TryParseClock(string value, out TimeSpan clock)
    {
        clock = default;
        if (
            !DateTime.TryParseExact(
                value,
                ClockFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.NoCurrentDateDefault,
                out var parsed
            )
        )
            return false;
        clock = parsed.TimeOfDay;
        return true;
    }

    private static bool TryResolveWallTime(
        DateTime wall,
        AppTimeZoneMode zone,
        out ParsedTime result,
        out string? error
    )
    {
        result = default;
        error = null;
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (
            zone == AppTimeZoneMode.Local
            && (TimeZoneInfo.Local.IsInvalidTime(wall) || TimeZoneInfo.Local.IsAmbiguousTime(wall))
        )
        {
            error = "This local time is missing or ambiguous due to daylight saving. Switch to UTC.";
            return false;
        }
        var offset =
            zone == AppTimeZoneMode.Utc ? TimeSpan.Zero : TimeZoneInfo.Local.GetUtcOffset(wall);
        var resolved = new DateTimeOffset(wall, offset);
        result = new(resolved.ToString("O", CultureInfo.InvariantCulture), resolved);
        return true;
    }
}
```

- [ ] **Step 4: Implement `TimeRangeLabel`**

```csharp
// src/HexTail/Application/TimeRangeLabel.cs
using System.Text.RegularExpressions;
using HexTail.Persistence;

namespace HexTail.Application;

public static partial class TimeRangeLabel
{
    [GeneratedRegex(
        @"^now-(\d+(?:\.\d+)?)([smhd])$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex LastPattern();

    public static string Describe(string from, string to, AppTimeZoneMode zone)
    {
        if (to.Trim().Equals("now", StringComparison.OrdinalIgnoreCase) && TryDescribeLast(from, out var last))
            return last;
        return $"{TimeExpression.FormatForInput(from, zone)} → {TimeExpression.FormatForInput(to, zone)}";
    }

    private static bool TryDescribeLast(string from, out string label)
    {
        label = string.Empty;
        var match = LastPattern().Match(from.Trim());
        if (!match.Success)
            return false;
        var amount = match.Groups[1].Value;
        var one = amount == "1";
        label = char.ToLowerInvariant(match.Groups[2].Value[0]) switch
        {
            's' => one ? "Last second" : $"Last {amount} sec",
            'm' => one ? "Last minute" : $"Last {amount} min",
            'h' => one ? "Last hour" : $"Last {amount} hours",
            'd' => one ? "Last day" : $"Last {amount} days",
            _ => string.Empty,
        };
        return label.Length > 0;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/HexTail.Tests/HexTail.Tests.csproj -c Release --filter "FullyQualifiedName~TimeExpressionTests"`
Expected: PASS (31 cases).

- [ ] **Step 6: Commit**

```bash
git add src/HexTail/Application/TimeExpression.cs src/HexTail/Application/TimeRangeLabel.cs src/HexTail.Tests/Application/TimeExpressionTests.cs
git commit -m "feat(time): parse typed range times and describe applied ranges"
```

---

### Task 2: Sidebar view models (file projection, Elastic connection groups)

**Files:**
- Create: `src/HexTail/ViewModels/ElasticConnectionGroupViewModel.cs`
- Create: `src/HexTail.Tests/Support/ElasticFixtures.cs`, `src/HexTail.Tests/Support/UiWait.cs`
- Modify: `src/HexTail.Tests/Support/TestPersistence.cs`
- Modify: `src/HexTail/ViewModels/ElasticSourceOptionViewModel.cs`
- Modify: `src/HexTail/ViewModels/FileTabViewModel.cs`
- Modify: `src/HexTail/ViewModels/MainWindowViewModel.cs` (constructor, `SyncFromState` Elastic block at ~L713-740, new members)
- Test: `src/HexTail.Tests/ViewModels/SidebarViewModelTests.cs`

**Interfaces:**
- Consumes: `AppState.OpenElasticSourceAsync(string sourceId, …)`, which selects the tab whether it is new or already open; `ElasticSourceOptionViewModel` constructor `(MainWindowViewModel owner, string sourceId, string displayName, string toolTip)` (keep this signature because `ElasticStatusTests` uses it).
- Produces:
  - `MainWindowViewModel.FileTabs : ObservableCollection<FileTabViewModel>` (file-kind tabs only, user-reorderable)
  - `MainWindowViewModel.HasFileTabs : bool`
  - `MainWindowViewModel.ElasticConnectionGroups : ObservableCollection<ElasticConnectionGroupViewModel>`
  - `MainWindowViewModel.Scheduler : IScheduler` (internal)
  - `ElasticConnectionGroupViewModel { Id, Name, Sources, IsConnected, IsFailed, StatusText, StatusMessage }`
  - `ElasticSourceOptionViewModel { ConnectionId, ShortName, IsSelected, HasOpenError, ActivateCommand, CloseCommand, SyncConnection(string, string), SyncSelection(bool) }`
  - `FileTabViewModel { HasError, LineCountText }`, `FileTabViewModel.CompactCount(int)` (internal static)
  - Test helpers: `ElasticFixtures.Connection(string id, string name, params string[] values)`, `ElasticFixtures.Workspace(params ElasticConnectionSettings[])`, `UiWait.Until(Func<bool>)`, `new TestPersistence(AppConfig? config = null)`

- [ ] **Step 1: Add the test support helpers**

```csharp
// src/HexTail.Tests/Support/TestPersistence.cs (replace the class header and Config line)
internal sealed class TestPersistence(AppConfig? config = null) : IAppPersistence
{
    public AppConfig? Config { get; private set; } = config ?? new();
    // …rest unchanged…
```

```csharp
// src/HexTail.Tests/Support/UiWait.cs
using Avalonia.Threading;

namespace HexTail.Tests.Support;

internal static class UiWait
{
    public static async Task Until(Func<bool> condition, int timeoutMs = 2000)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < timeout)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(condition());
    }
}
```

```csharp
// src/HexTail.Tests/Support/ElasticFixtures.cs
using System.Reactive.Concurrency;
using HexTail.Application;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.ViewModels;

namespace HexTail.Tests.Support;

internal static class ElasticFixtures
{
    public static ElasticConnectionSettings Connection(string id, string name, params string[] values) =>
        new()
        {
            Id = id,
            Name = name,
            KibanaUrl = "https://kibana/",
            ElasticsearchUrl = "https://elastic/",
            Views =
            [
                new ElasticViewSettings
                {
                    Id = $"{id}-view",
                    Name = "Logs",
                    DataViewTitle = "logs-*",
                    TimeFieldName = "@timestamp",
                    ServerField = "service.name",
                    OutputFields = ["message"],
                    Sources =
                    [
                        .. values.Select(value => new ElasticSourceSettings
                        {
                            Id = $"{id}-{value}",
                            ServerValue = value,
                        }),
                    ],
                },
            ],
        };

    public static MainWindowViewModel Workspace(params ElasticConnectionSettings[] connections) =>
        new(
            new AppState(
                new LogSourceService(),
                new TestPersistence(
                    new AppConfig
                    {
                        Settings = new AppSettings { ElasticConnections = [.. connections] },
                    }
                ),
                credentials: new InMemoryCredentialVault(),
                elastic: new FakeElasticApiClient()
            ),
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
}
```

- [ ] **Step 2: Write the failing tests**

```csharp
// src/HexTail.Tests/ViewModels/SidebarViewModelTests.cs
using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.ViewModels;

public sealed class SidebarViewModelTests
{
    [AvaloniaFact]
    public async Task ElasticSourcesAreGroupedByConnectionInSettingsOrder()
    {
        await using var viewModel = ElasticFixtures.Workspace(
            ElasticFixtures.Connection("c-2", "staging", "billing"),
            ElasticFixtures.Connection("c-1", "prod-eu", "checkout", "billing")
        );
        await viewModel.InitializeAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["staging", "prod-eu"], viewModel.ElasticConnectionGroups.Select(g => g.Name));
        Assert.Equal(
            ["Logs · checkout", "Logs · billing"],
            viewModel.ElasticConnectionGroups[1].Sources.Select(source => source.ShortName)
        );
        Assert.All(
            viewModel.ElasticConnectionGroups[1].Sources,
            source => Assert.Equal("c-1", source.ConnectionId)
        );
    }

    [AvaloniaFact]
    public async Task ActivatingASourceOpensAndSelectsItAndCloseRemovesIt()
    {
        await using var viewModel = ElasticFixtures.Workspace(
            ElasticFixtures.Connection("c-1", "prod", "checkout")
        );
        await viewModel.InitializeAsync();
        var source = viewModel.ElasticConnectionGroups.Single().Sources.Single();

        await source.ActivateCommand.Execute();
        await UiWait.Until(() => source.IsOpen);

        Assert.True(source.IsSelected);
        Assert.True(viewModel.IsElasticSelected);
        Assert.Empty(viewModel.FileTabs);

        await source.CloseCommand.Execute();
        await UiWait.Until(() => !source.IsOpen);
        Assert.Empty(viewModel.Files);
    }

    [AvaloniaFact]
    public async Task FileTabsProjectOnlyFileSourcesAndReportLineCounts()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllLinesAsync(path, ["a", "b", "c"]);
        try
        {
            await using var viewModel = ElasticFixtures.Workspace(
                ElasticFixtures.Connection("c-1", "prod", "checkout")
            );
            await viewModel.InitializeAsync();
            await viewModel.OpenPathsCommand.Execute([path]);
            await viewModel.ElasticConnectionGroups[0].Sources[0].ActivateCommand.Execute();
            await UiWait.Until(() =>
            {
                viewModel.State.DrainTailerEvents();
                return viewModel.FileTabs.Count == 1 && viewModel.FileTabs[0].LineCountText == "3";
            });

            Assert.Equal(2, viewModel.Files.Count);
            Assert.True(viewModel.HasFileTabs);
            Assert.False(viewModel.FileTabs[0].HasError);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(12_400, "12.4k")]
    [InlineData(999_999, "999.9k")]
    [InlineData(1_250_000, "1.2M")]
    public void CompactCountIsShortAndNeverRoundsUp(int count, string expected) =>
        Assert.Equal(expected, FileTabViewModel.CompactCount(count));
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test src/HexTail.Tests/HexTail.Tests.csproj -c Release --filter "FullyQualifiedName~SidebarViewModelTests"`
Expected: build FAILS: `'MainWindowViewModel' does not contain a definition for 'ElasticConnectionGroups'`.

- [ ] **Step 4: Create `ElasticConnectionGroupViewModel`**

```csharp
// src/HexTail/ViewModels/ElasticConnectionGroupViewModel.cs
using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal sealed class ElasticConnectionGroupViewModel(string id) : ReactiveObject
{
    private string _name = string.Empty;
    private string _status = "Checking";
    private string _statusMessage = string.Empty;

    public string Id { get; } = id;
    public ObservableCollection<ElasticSourceOptionViewModel> Sources { get; } = [];

    public string Name
    {
        get => _name;
        private set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public bool IsConnected => _status == "Connected";
    public bool IsFailed => _status is not ("Connected" or "Checking" or "Not tested");
    public string StatusText =>
        _status switch
        {
            "Connected" => string.Empty,
            "Checking" or "Not tested" => "checking",
            "Unauthorized" => "auth failed",
            "Unreachable" => "unreachable",
            "Misconfigured" => "misconfigured",
            _ => _status.ToLowerInvariant(),
        };

    internal void Sync(string name, IReadOnlyList<ElasticSourceOptionViewModel> sources)
    {
        Name = name;
        if (!Sources.SequenceEqual(sources))
        {
            Sources.Clear();
            foreach (var source in sources)
                Sources.Add(source);
        }
        var first = sources.FirstOrDefault();
        StatusMessage = first?.HealthMessage ?? string.Empty;
        var status = first?.Status ?? "Checking";
        if (_status == status)
            return;
        _status = status;
        this.RaisePropertyChanged(nameof(IsConnected));
        this.RaisePropertyChanged(nameof(IsFailed));
        this.RaisePropertyChanged(nameof(StatusText));
    }
}
```

- [ ] **Step 5: Extend `ElasticSourceOptionViewModel`**

Add `using System.Reactive;`. Add these fields, properties, and constructor lines, and change the `OpenError` setter so it raises `HasOpenError`:

```csharp
    private bool _isSelected;

    // constructor body, after ToolTip = toolTip;
        ShortName = displayName;
        ActivateCommand = ReactiveCommand.CreateFromTask(ActivateAsync, owner.Scheduler);
        CloseCommand = ReactiveCommand.CreateFromTask(CloseAsync, owner.Scheduler);

    public string ConnectionId { get; private set; } = string.Empty;
    public string ShortName { get; private set; }
    public bool IsSelected
    {
        get => _isSelected;
        private set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
    public bool HasOpenError => !string.IsNullOrEmpty(OpenError);
    public ReactiveCommand<Unit, Unit> ActivateCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    public string? OpenError
    {
        get => _openError;
        private set
        {
            this.RaiseAndSetIfChanged(ref _openError, value);
            this.RaisePropertyChanged(nameof(HasOpenError));
        }
    }

    private async Task ActivateAsync()
    {
        if (IsOpening)
            return;
        IsOpening = true;
        OpenError = null;
        try
        {
            await _owner.State.OpenElasticSourceAsync(SourceId);
        }
        catch (Exception exception)
        {
            OpenError = exception.Message;
        }
        finally
        {
            IsOpening = false;
        }
    }

    internal void SyncConnection(string connectionId, string shortName)
    {
        ConnectionId = connectionId;
        if (ShortName == shortName)
            return;
        ShortName = shortName;
        this.RaisePropertyChanged(nameof(ShortName));
    }

    internal void SyncSelection(bool selected) => IsSelected = selected;
```

- [ ] **Step 6: Extend `FileTabViewModel`**

Add `using System.Globalization;` and these members:

```csharp
    public bool HasError => Model.Error is not null;
    public string LineCountText => CompactCount(Model.Buffer.Count);

    internal static string CompactCount(int count) =>
        count switch
        {
            < 1_000 => count.ToString(CultureInfo.InvariantCulture),
            < 1_000_000 => (Math.Floor(count / 100d) / 10).ToString("0.#", CultureInfo.InvariantCulture) + "k",
            _ => (Math.Floor(count / 100_000d) / 10).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        };
```

In `SyncViews`, extend the snapshot so these members notify. Add a `LineCount` field and raise `LineCountText`; raise `HasError` next to the existing `Error`:

```csharp
        var snapshot = new FileSnapshot(
            FollowAll,
            ShowContext,
            DisplayName,
            SourceDetails,
            Error,
            Model.Searches.Count,
            Model.Buffer.Count
        );
        // …existing comparisons…
        if (previous is null || previous.Value.Error != snapshot.Error)
        {
            this.RaisePropertyChanged(nameof(Error));
            this.RaisePropertyChanged(nameof(HasError));
        }
        if (previous is null || previous.Value.LineCount != snapshot.LineCount)
            this.RaisePropertyChanged(nameof(LineCountText));

    private readonly record struct FileSnapshot(
        bool FollowAll,
        bool ShowContext,
        string DisplayName,
        string SourceDetails,
        string? Error,
        int SearchCount,
        int LineCount
    );
```

- [ ] **Step 7: Extend `MainWindowViewModel`**

Add members. Notify `HasFileTabs` from the constructor:

```csharp
    // members
    public ObservableCollection<FileTabViewModel> FileTabs { get; } = [];
    public bool HasFileTabs => FileTabs.Count > 0;
    public ObservableCollection<ElasticConnectionGroupViewModel> ElasticConnectionGroups { get; } = [];
    internal IScheduler Scheduler => _scheduler;

    // constructor, after _subscriptions.Add(...state.Changed...):
        FileTabs.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(HasFileTabs));
```

In `SyncFromState`, add this right after the `foreach (var file in files)` loop that builds `Files`:

```csharp
        for (var index = FileTabs.Count - 1; index >= 0; index--)
            if (!Files.Contains(FileTabs[index]))
                FileTabs.RemoveAt(index);
        foreach (var file in Files)
            if (file.Model.Source.Kind == LogSourceKind.File && !FileTabs.Contains(file))
                FileTabs.Add(file);
```

Replace the `var configured = …` block through the end of the `foreach (var option in ElasticSources)` loop with:

```csharp
        var configured = _state
            .Settings.ElasticConnections.SelectMany(connection =>
                connection.Views.SelectMany(view =>
                    view.Sources.Select(source =>
                        (
                            ConnectionId: connection.Id,
                            Descriptor: LogSourceDescriptor.Elastic(connection, view, source),
                            ShortName: $"{(string.IsNullOrWhiteSpace(view.Name) ? view.DataViewTitle : view.Name)} · {source.ServerValue}"
                        )
                    )
                )
            )
            .ToArray();
        for (var index = ElasticSources.Count - 1; index >= 0; index--)
            if (!configured.Any(item => item.Descriptor.Id == ElasticSources[index].SourceId))
                ElasticSources.RemoveAt(index);
        foreach (var item in configured)
            if (!ElasticSources.Any(option => option.SourceId == item.Descriptor.Id))
                ElasticSources.Add(
                    new ElasticSourceOptionViewModel(
                        this,
                        item.Descriptor.Id,
                        item.Descriptor.DisplayName,
                        item.Descriptor.ToolTip
                    )
                );
        var selectedSourceId = SelectedFile?.Model.Source.ElasticSourceId;
        foreach (var option in ElasticSources)
        {
            var item = configured.First(source => source.Descriptor.Id == option.SourceId);
            option.SyncIdentity(item.Descriptor);
            option.SyncConnection(item.ConnectionId, item.ShortName);
            option.SyncSelection(option.SourceId == selectedSourceId);
            var status =
                _state.ElasticSourceStatuses.GetValueOrDefault(option.SourceId)?.Status.ToString()
                ?? "Checking";
            var health = _state.ElasticSourceStatuses.GetValueOrDefault(option.SourceId);
            var tab = _state.Files.FirstOrDefault(tab => tab.Id == option.SourceId);
            var load =
                tab is null ? "Logs: closed"
                : tab.Error is not null ? $"Logs: {tab.Error}"
                : tab.ElasticLoading ? "Logs: loading…"
                : tab.ElasticTo == "now" ? "Logs: live"
                : "Logs: historical";
            option.Sync(tab is not null, status, health?.Message, load);
        }
        SyncConnectionGroups();
```

Add the method:

```csharp
    private void SyncConnectionGroups()
    {
        var connections = _state.Settings.ElasticConnections;
        for (var index = ElasticConnectionGroups.Count - 1; index >= 0; index--)
            if (!connections.Any(connection => connection.Id == ElasticConnectionGroups[index].Id))
                ElasticConnectionGroups.RemoveAt(index);
        for (var index = 0; index < connections.Count; index++)
        {
            var connection = connections[index];
            var group = ElasticConnectionGroups.FirstOrDefault(item => item.Id == connection.Id);
            if (group is null)
            {
                group = new ElasticConnectionGroupViewModel(connection.Id);
                ElasticConnectionGroups.Insert(index, group);
            }
            else if (ElasticConnectionGroups.IndexOf(group) is var current && current != index)
                ElasticConnectionGroups.Move(current, index);
            group.Sync(
                connection.Name,
                ElasticSources.Where(option => option.ConnectionId == connection.Id).ToArray()
            );
        }
    }
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test src/HexTail.Tests/HexTail.Tests.csproj -c Release --filter "FullyQualifiedName~SidebarViewModelTests|FullyQualifiedName~ElasticStatusTests"`
Expected: PASS.

- [ ] **Step 9: Run the full suite**

Run: `dotnet test src/HexTail.Tests/HexTail.Tests.csproj -c Release`
Expected: PASS (baseline + new tests; no regressions).

- [ ] **Step 10: Commit**

```bash
git add src/HexTail/ViewModels src/HexTail.Tests/Support src/HexTail.Tests/ViewModels/SidebarViewModelTests.cs
git commit -m "feat(sidebar): group Elastic sources by connection and project file tabs"
```

---

### Task 3: Shell layout — sidebar, top bar, no file strip

**Files:**
- Create: `src/HexTail/Views/SourceSidebar.axaml`, `src/HexTail/Views/SourceSidebar.axaml.cs`
- Create: `src/HexTail/Behaviors/ListReorderBehavior.cs`
- Delete: `src/HexTail/Views/FileStrip.axaml`, `src/HexTail/Views/FileStrip.axaml.cs`
- Modify: `src/HexTail/MainWindow.axaml` (full replacement of the body below `DialogContent`)
- Modify: `src/HexTail/MainWindow.axaml.cs`
- Modify: `src/HexTail/ViewModels/MainWindowViewModel.cs`
- Modify: `src/HexTail/ViewModels/ElasticSourceOptionViewModel.cs` (dismiss the narrow overlay after activation)
- Modify: `src/HexTail/Persistence/AppConfig.cs` (`AppWindowState`)
- Modify: `src/HexTail/Styles/CyberTail.axaml`
- Test: `src/HexTail.Tests/Ui/SidebarTests.cs` (new); modify `src/HexTail.Tests/Ui/TabNavigationTests.cs` and `src/HexTail.Tests/Ui/MainWindowInteractionTests.cs`

**Interfaces:**
- Consumes: Task 2's `FileTabs`, `HasFileTabs`, `ElasticConnectionGroups`, `ElasticSourceOptionViewModel.ActivateCommand/CloseCommand/IsSelected/ShortName/HasOpenError`, `FileTabViewModel.HasError/LineCountText`, `UiWait.Until`, `ElasticFixtures`.
- Produces:
  - `MainWindowViewModel.SidebarOpen : bool` (persisted preference), `SidebarWidth : double` (clamped 160–480), `SidebarPaneOpen : bool` (effective, two-way bound), `SidebarDisplayMode : SplitViewDisplayMode`, `IsNarrowShell : bool`, `ToggleSidebarCommand`, `SetShellWidth(double)` (internal), `DismissNarrowSidebar()` (internal), `const double NarrowShellWidth = 1000`
  - `AppWindowState.SidebarCollapsed : bool`, `AppWindowState.SidebarWidth : double`
  - XAML names: `SidebarToggleButton`, `ShellSplitView`, `SidebarResizeThumb`, `SidebarOpenFileButton`, `SidebarElasticSetupButton`, `NoFilesHint`, `ElasticEmptyHint`, `FileSourceList`, `ElasticGroupList`; row class `source-row` (+ `selected`), button classes `source-select` and `tab-close`

- [ ] **Step 1: Write the failing sidebar UI tests**

```csharp
// src/HexTail.Tests/Ui/SidebarTests.cs
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Tests.Support;
using HexTail.ViewModels;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class SidebarTests
{
    [AvaloniaFact]
    public async Task FileRowsSelectCloseAndShowLineCounts()
    {
        var first = Path.GetTempFileName();
        var second = Path.GetTempFileName();
        await File.WriteAllLinesAsync(first, ["a", "b", "c"]);
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Width = 1280;
            window.Show();
            await viewModel.OpenPathsCommand.Execute([first, second]);
            await UiWait.Until(() =>
            {
                viewModel.State.DrainTailerEvents();
                return viewModel.FileTabs[0].LineCountText == "3";
            });
            var firstTab = viewModel.FileTabs[0];

            Assert.Equal(2, Rows(window).Length);
            Assert.Equal(first, ToolTip.GetTip(Row(window, firstTab)));
            SelectButton(window, firstTab).Command!.Execute(firstTab);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(firstTab, viewModel.SelectedFile);
            Assert.Contains("selected", Row(window, firstTab).Classes);
            Assert.Equal($"Close {first}", ToolTip.GetTip(CloseButton(window, firstTab)));

            CloseButton(window, firstTab).Command!.Execute(firstTab);
            await UiWait.Until(() => !viewModel.FileTabs.Contains(firstTab));
            window.Close();
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [AvaloniaFact]
    public async Task AltDownReordersFileRowsAndKeepsSelection()
    {
        var paths = Enumerable.Range(0, 3).Select(_ => Path.GetTempFileName()).ToArray();
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Width = 1280;
            window.Show();
            await viewModel.OpenPathsCommand.Execute(paths);
            Dispatcher.UIThread.RunJobs();
            var moved = viewModel.FileTabs[0];
            viewModel.SelectFileCommand.Execute(moved).Subscribe();
            Dispatcher.UIThread.RunJobs();

            var button = SelectButton(window, moved);
            button.Focus();
            button.RaiseEvent(
                new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Down,
                    KeyModifiers = KeyModifiers.Alt,
                }
            );
            Dispatcher.UIThread.RunJobs();

            Assert.Same(moved, viewModel.FileTabs[1]);
            Assert.Same(moved, viewModel.SelectedFile);
            window.Close();
        }
        finally
        {
            foreach (var path in paths)
                File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ShortcutCollapsesSidebarAndClosePersistsPreference()
    {
        var persistence = new TestPersistence();
        var window = TestWindow.Create(persistence, out var viewModel);
        window.Width = 1280;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SplitViewDisplayMode.Inline, viewModel.SidebarDisplayMode);
        Assert.True(viewModel.SidebarPaneOpen);

        window.KeyPress(Key.B, RawInputModifiers.Control, PhysicalKey.None, null);
        Assert.False(viewModel.SidebarOpen);
        Assert.False(viewModel.SidebarPaneOpen);

        window.Close();
        await UiWait.Until(() => persistence.Config?.Window.SidebarCollapsed == true);
    }

    [AvaloniaFact]
    public async Task NarrowWindowUsesAnOverlayThatClosesAfterSelection()
    {
        var paths = Enumerable.Range(0, 2).Select(_ => Path.GetTempFileName()).ToArray();
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Width = 720;
            window.Show();
            await viewModel.OpenPathsCommand.Execute(paths);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(SplitViewDisplayMode.Overlay, viewModel.SidebarDisplayMode);
            Assert.False(viewModel.SidebarPaneOpen);

            await viewModel.ToggleSidebarCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            Assert.True(viewModel.SidebarPaneOpen);
            Assert.True(viewModel.SidebarOpen);

            var target = viewModel.FileTabs[0];
            SelectButton(window, target).Command!.Execute(target);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(target, viewModel.SelectedFile);
            Assert.False(viewModel.SidebarPaneOpen);
            Assert.True(viewModel.SidebarOpen);
            window.Close();
        }
        finally
        {
            foreach (var path in paths)
                File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ElasticRowsOpenSelectAndCloseSources()
    {
        var viewModel = ElasticFixtures.Workspace(
            ElasticFixtures.Connection("c-1", "prod-eu", "checkout")
        );
        var window = new MainWindow(viewModel, registerNativePicker: false) { Width = 1280 };
        window.Show();
        await UiWait.Until(() => viewModel.ElasticConnectionGroups.Count == 1);
        var source = viewModel.ElasticConnectionGroups[0].Sources[0];

        Assert.Contains(
            Sidebar(window).GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text == "prod-eu"
        );
        SelectButton(window, source).Command!.Execute(null);
        await UiWait.Until(() => source.IsOpen && source.IsSelected);
        Assert.Contains("selected", Row(window, source).Classes);
        var close = CloseButton(window, source);
        Assert.True(close.IsVisible);
        close.Command!.Execute(null);
        await UiWait.Until(() => !source.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void ElasticSectionOffersSetupWhenNothingIsConfigured()
    {
        var window = TestWindow.Create(out var viewModel);
        window.Width = 1280;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var sidebar = Sidebar(window);

        Assert.True(sidebar.FindControl<TextBlock>("ElasticEmptyHint")!.IsVisible);
        var setup = sidebar.FindControl<Button>("SidebarElasticSetupButton")!;
        setup.Command!.Execute(null);
        Assert.True(viewModel.SettingsOpen);
        Assert.Equal(2, viewModel.Settings.SectionIndex);
        window.Close();
    }

    private static SourceSidebar Sidebar(Window window) =>
        window.GetVisualDescendants().OfType<SourceSidebar>().Single();

    private static Border[] Rows(Window window) =>
        Sidebar(window)
            .GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.Classes.Contains("source-row"))
            .ToArray();

    private static Border Row(Window window, object item) =>
        Rows(window).Single(row => ReferenceEquals(row.DataContext, item));

    private static Button SelectButton(Window window, object item) =>
        Sidebar(window)
            .GetVisualDescendants()
            .OfType<Button>()
            .Single(button =>
                ReferenceEquals(button.DataContext, item) && button.Classes.Contains("source-select")
            );

    private static Button CloseButton(Window window, object item) =>
        Sidebar(window)
            .GetVisualDescendants()
            .OfType<Button>()
            .Single(button =>
                ReferenceEquals(button.DataContext, item) && button.Classes.Contains("tab-close")
            );
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HexTail.Tests/HexTail.Tests.csproj -c Release --filter "FullyQualifiedName~SidebarTests"`
Expected: build FAILS: `The type or namespace name 'SourceSidebar' could not be found`.

- [ ] **Step 3: Persisted sidebar preference**

In `src/HexTail/Persistence/AppConfig.cs`, replace `AppWindowState`:

```csharp
public sealed class AppWindowState
{
    public bool SidebarCollapsed { get; init; }
    public double SidebarWidth { get; init; }
    public int ContextPaneSize { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public int? X { get; init; }
    public int? Y { get; init; }
}
```

Then run `grep -rn "VerticalFileTabs" src`. The only hits must be `MainWindow.axaml.cs`, which is fixed in Step 7.

- [ ] **Step 4: View-model sidebar state**

In `MainWindowViewModel`, add `using Avalonia.Controls;` and:

```csharp
    internal const double NarrowShellWidth = 1000;
    internal const double MinSidebarWidth = 160;
    internal const double MaxSidebarWidth = 480;
    private bool _sidebarOpen = true;
    private bool _narrowShell;
    private bool _narrowPaneOpen;
    private double _sidebarWidth = 240;

    public ReactiveCommand<Unit, Unit> ToggleSidebarCommand { get; }

    // constructor:
        ToggleSidebarCommand = ReactiveCommand.Create(
            () =>
            {
                SidebarPaneOpen = !SidebarPaneOpen;
            },
            _scheduler
        );

    /// <summary>Persisted preference for wide windows.</summary>
    public bool SidebarOpen
    {
        get => _sidebarOpen;
        set
        {
            if (_sidebarOpen == value)
                return;
            this.RaiseAndSetIfChanged(ref _sidebarOpen, value);
            if (!_narrowShell)
                this.RaisePropertyChanged(nameof(SidebarPaneOpen));
        }
    }

    /// <summary>Effective pane state: the preference when wide, a transient overlay when narrow.</summary>
    public bool SidebarPaneOpen
    {
        get => _narrowShell ? _narrowPaneOpen : _sidebarOpen;
        set
        {
            if (_narrowShell)
            {
                if (_narrowPaneOpen == value)
                    return;
                _narrowPaneOpen = value;
                this.RaisePropertyChanged();
            }
            else
                SidebarOpen = value;
        }
    }

    public double SidebarWidth
    {
        get => _sidebarWidth;
        set => this.RaiseAndSetIfChanged(ref _sidebarWidth, Math.Clamp(value, MinSidebarWidth, MaxSidebarWidth));
    }

    public bool IsNarrowShell => _narrowShell;
    public SplitViewDisplayMode SidebarDisplayMode =>
        _narrowShell ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;

    internal void SetShellWidth(double width)
    {
        var narrow = width < NarrowShellWidth;
        if (narrow == _narrowShell)
            return;
        _narrowShell = narrow;
        _narrowPaneOpen = false;
        this.RaisePropertyChanged(nameof(IsNarrowShell));
        this.RaisePropertyChanged(nameof(SidebarDisplayMode));
        this.RaisePropertyChanged(nameof(SidebarPaneOpen));
    }

    internal void DismissNarrowSidebar()
    {
        if (_narrowShell)
            SidebarPaneOpen = false;
    }
```

In `InitializeAsync`, right after `await _state.RestoreAsync();`:

```csharp
            SidebarOpen = !_state.Window.SidebarCollapsed;
            if (_state.Window.SidebarWidth > 0)
                SidebarWidth = _state.Window.SidebarWidth;
```

In `SelectFile`, append `DismissNarrowSidebar();`. In `ElasticSourceOptionViewModel.ActivateAsync`, after the `await _owner.State.OpenElasticSourceAsync(SourceId);` line, add `_owner.DismissNarrowSidebar();`.

- [ ] **Step 5: `ListReorderBehavior`**

```csharp
// src/HexTail/Behaviors/ListReorderBehavior.cs
using System.Collections;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;
using HexTail.ViewModels;

namespace HexTail.Behaviors;

/// <summary>Alt+Up / Alt+Down moves the focused item of a vertical list.</summary>
internal sealed class ListReorderBehavior : Behavior<ItemsControl>
{
    protected override void OnAttached() =>
        AssociatedObject!.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

    protected override void OnDetaching() =>
        AssociatedObject!.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (
            e.KeyModifiers != KeyModifiers.Alt
            || e.Key is not (Key.Up or Key.Down)
            || AssociatedObject is not { ItemsSource: IList items } list
        )
            return;
        var container = (e.Source as Visual)
            ?.GetSelfAndVisualAncestors()
            .OfType<Control>()
            .FirstOrDefault(control => list.IndexFromContainer(control) >= 0);
        if (container is null)
            return;
        var from = list.IndexFromContainer(container);
        var to = from + (e.Key == Key.Up ? -1 : 1);
        e.Handled = true;
        if (to < 0 || to >= items.Count)
            return;
        if (items is ObservableCollection<FileTabViewModel> files)
            files.Move(from, to);
        else
        {
            var item = items[from];
            items.RemoveAt(from);
            items.Insert(to, item);
        }
        Dispatcher.UIThread.Post(() =>
            list.ContainerFromIndex(to)?.GetVisualDescendants().OfType<Button>().FirstOrDefault()?.Focus()
        );
    }
}
```

- [ ] **Step 6: `SourceSidebar` view**

```xml
<!-- src/HexTail/Views/SourceSidebar.axaml -->
<UserControl
  xmlns="https://github.com/avaloniaui"
  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
  xmlns:vm="using:HexTail.ViewModels"
  xmlns:ic="https://github.com/projektanker/icons.avalonia"
  xmlns:i="using:Avalonia.Xaml.Interactivity"
  xmlns:behaviors="using:HexTail.Behaviors"
  x:Class="HexTail.Views.SourceSidebar"
  x:DataType="vm:MainWindowViewModel"
  AutomationProperties.Name="Sources"
>
  <ScrollViewer HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto">
    <StackPanel Margin="8" Spacing="2">
      <Grid ColumnDefinitions="*,Auto" Margin="4,4,0,2">
        <TextBlock Text="FILES" Classes="sidebar-heading" />
        <Button
          x:Name="SidebarOpenFileButton"
          Grid.Column="1"
          Classes="icon tab-close"
          Width="24"
          Height="24"
          FontSize="16"
          Command="{Binding OpenCommand}"
          ToolTip.Tip="Open log files (Ctrl+O)"
          AutomationProperties.Name="Open log files from sidebar"
        >
          <ic:Icon Value="mdi-plus" />
        </Button>
      </Grid>
      <TextBlock
        x:Name="NoFilesHint"
        Text="No files open"
        Classes="hint"
        Margin="6,2"
        IsVisible="{Binding !HasFileTabs}"
      />
      <ItemsControl x:Name="FileSourceList" ItemsSource="{Binding FileTabs}">
        <i:Interaction.Behaviors>
          <behaviors:ListReorderBehavior />
        </i:Interaction.Behaviors>
        <ItemsControl.ItemTemplate>
          <DataTemplate x:DataType="vm:FileTabViewModel">
            <Border
              Classes="source-row"
              Classes.selected="{Binding IsSelected}"
              ToolTip.Tip="{Binding SourceDetails}"
            >
              <Grid ColumnDefinitions="Auto,*,Auto,Auto" ColumnSpacing="4">
                <Ellipse
                  Classes="source-status"
                  Classes.connected="{Binding !HasError}"
                  Classes.failed="{Binding HasError}"
                  ToolTip.Tip="{Binding Error}"
                />
                <Button
                  Grid.Column="1"
                  Classes="source-select"
                  Command="{Binding $parent[UserControl].DataContext.SelectFileCommand}"
                  CommandParameter="{Binding}"
                  AutomationProperties.Name="{Binding SourceDetails, StringFormat='Select {0}'}"
                >
                  <TextBlock Text="{Binding DisplayName}" TextTrimming="CharacterEllipsis" />
                </Button>
                <TextBlock Grid.Column="2" Text="{Binding LineCountText}" Classes="source-count" />
                <Button
                  Grid.Column="3"
                  Classes="icon tab-close"
                  Width="22"
                  Height="22"
                  FontSize="14"
                  Command="{Binding $parent[UserControl].DataContext.CloseFileCommand}"
                  CommandParameter="{Binding}"
                  ToolTip.Tip="{Binding SourceDetails, StringFormat='Close {0}'}"
                  AutomationProperties.Name="{Binding SourceDetails, StringFormat='Close {0}'}"
                >
                  <ic:Icon Value="mdi-close" />
                </Button>
              </Grid>
            </Border>
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>

      <Grid ColumnDefinitions="*,Auto" Margin="4,12,0,2">
        <TextBlock Text="ELASTIC" Classes="sidebar-heading" />
        <Button
          x:Name="SidebarElasticSetupButton"
          Grid.Column="1"
          Classes="icon tab-close"
          Width="24"
          Height="24"
          FontSize="16"
          Command="{Binding SetUpElasticCommand}"
          ToolTip.Tip="Set up Elastic"
          AutomationProperties.Name="Set up Elastic from sidebar"
        >
          <ic:Icon Value="mdi-plus" />
        </Button>
      </Grid>
      <TextBlock
        x:Name="ElasticEmptyHint"
        Text="No Elastic servers"
        Classes="hint"
        Margin="6,2"
        IsVisible="{Binding !HasElasticSources}"
      />
      <ItemsControl x:Name="ElasticGroupList" ItemsSource="{Binding ElasticConnectionGroups}">
        <ItemsControl.ItemTemplate>
          <DataTemplate x:DataType="vm:ElasticConnectionGroupViewModel">
            <StackPanel Spacing="1" Margin="0,2">
              <Grid
                ColumnDefinitions="Auto,*,Auto"
                ColumnSpacing="6"
                Margin="6,4,4,2"
                ToolTip.Tip="{Binding StatusMessage}"
              >
                <Ellipse
                  Classes="source-status"
                  Classes.connected="{Binding IsConnected}"
                  Classes.failed="{Binding IsFailed}"
                />
                <TextBlock
                  Grid.Column="1"
                  Text="{Binding Name}"
                  FontWeight="SemiBold"
                  TextTrimming="CharacterEllipsis"
                />
                <TextBlock Grid.Column="2" Text="{Binding StatusText}" Classes="source-count" />
              </Grid>
              <ItemsControl ItemsSource="{Binding Sources}">
                <ItemsControl.ItemTemplate>
                  <DataTemplate x:DataType="vm:ElasticSourceOptionViewModel">
                    <StackPanel>
                      <Border
                        Classes="source-row"
                        Classes.selected="{Binding IsSelected}"
                        Margin="12,0,0,0"
                        ToolTip.Tip="{Binding ToolTip}"
                      >
                        <Grid ColumnDefinitions="*,Auto">
                          <Button
                            Classes="source-select"
                            Command="{Binding ActivateCommand}"
                            AutomationProperties.Name="{Binding DisplayName, StringFormat='Open {0}'}"
                          >
                            <TextBlock
                              Text="{Binding ShortName}"
                              Classes.muted="{Binding !IsOpen}"
                              TextTrimming="CharacterEllipsis"
                            />
                          </Button>
                          <Button
                            Grid.Column="1"
                            Classes="icon tab-close"
                            Width="22"
                            Height="22"
                            FontSize="14"
                            IsVisible="{Binding IsOpen}"
                            Command="{Binding CloseCommand}"
                            ToolTip.Tip="{Binding DisplayName, StringFormat='Close {0}'}"
                            AutomationProperties.Name="{Binding DisplayName, StringFormat='Close {0}'}"
                          >
                            <ic:Icon Value="mdi-close" />
                          </Button>
                        </Grid>
                      </Border>
                      <TextBlock
                        Text="{Binding OpenError}"
                        IsVisible="{Binding HasOpenError}"
                        Classes="error"
                        TextWrapping="Wrap"
                        FontSize="12"
                        Margin="16,0,0,4"
                      />
                    </StackPanel>
                  </DataTemplate>
                </ItemsControl.ItemTemplate>
              </ItemsControl>
            </StackPanel>
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>
    </StackPanel>
  </ScrollViewer>
</UserControl>
```

```csharp
// src/HexTail/Views/SourceSidebar.axaml.cs
using Avalonia.Controls;

namespace HexTail.Views;

public partial class SourceSidebar : UserControl
{
    public SourceSidebar() => InitializeComponent();
}
```

- [ ] **Step 7: Replace the window shell**

In `src/HexTail/MainWindow.axaml`, keep the `<Window …>` attributes and the `DialogHost`/`DialogContent` exactly as they are. Replace the `<Grid RowDefinitions="Auto,Auto,Auto,*" …>` element and everything inside it with the block below. The `ElasticTimeRangePanel` content is **moved unchanged** from the old file (lines 178-302: the `Button` with its `Flyout` and the two `TextBlock`s after it). Only the panel's own attributes change, as shown.

```xml
    <Grid x:Name="ShellGrid" RowDefinitions="Auto,*" Background="{DynamicResource SurfaceBrush}">
      <Border
        x:Name="TopBar"
        Background="{DynamicResource ToolbarBrush}"
        BorderBrush="{DynamicResource BorderBrush}"
        BorderThickness="0,0,0,1"
        Padding="6,4"
      >
        <Grid ColumnDefinitions="Auto,Auto,*,Auto,Auto,Auto" ColumnSpacing="6">
          <Button
            x:Name="SidebarToggleButton"
            Classes="icon"
            Command="{Binding ToggleSidebarCommand}"
            ToolTip.Tip="Toggle sidebar (Ctrl+B)"
            AutomationProperties.Name="Toggle sidebar"
          >
            <i:Icon Value="mdi-dock-left" Foreground="{DynamicResource AccentBrush}" />
          </Button>
          <Button
            x:Name="OpenFileButton"
            Grid.Column="1"
            Classes="icon"
            Command="{Binding OpenCommand}"
            ToolTip.Tip="Open log files (Ctrl+O)"
            AutomationProperties.Name="Open log files"
          >
            <i:Icon Value="mdi-folder-open" Foreground="{DynamicResource AccentBrush}" />
          </Button>
          <StackPanel
            x:Name="ElasticTimeRangePanel"
            Grid.Column="2"
            Orientation="Horizontal"
            Spacing="8"
            VerticalAlignment="Center"
            IsVisible="{Binding IsElasticSelected}"
          >
            <!-- moved unchanged: ElasticTimeRangeButton (+Flyout), ElasticAppliedSummary, range status TextBlock.
                 On the two TextBlocks set TextWrapping="NoWrap" TextTrimming="CharacterEllipsis" VerticalAlignment="Center". -->
          </StackPanel>
          <ToggleSwitch
            x:Name="InlineContextToggle"
            Grid.Column="3"
            AutomationProperties.Name="Show inline context"
            OnContent="Inline Shown"
            OffContent="No Inline"
            FontSize="12"
            DataContext="{Binding SelectedFile}"
            IsVisible="{Binding $parent[Window].DataContext.HasFile}"
            IsChecked="{Binding ShowContext, Mode=TwoWay}"
          />
          <ToggleSwitch
            x:Name="AutoScrollToggle"
            Grid.Column="4"
            AutomationProperties.Name="Auto-scroll logs"
            ToolTip.Tip="Auto-scroll follows new rows; it does not pause log retrieval."
            OnContent="Auto-scroll on"
            OffContent="Auto-scroll off"
            FontSize="12"
            DataContext="{Binding SelectedFile}"
            IsVisible="{Binding $parent[Window].DataContext.HasFile}"
            IsChecked="{Binding SelectedView.IsFollowing, Mode=TwoWay}"
          />
          <Button
            x:Name="SettingsButton"
            Grid.Column="5"
            Classes="icon"
            Command="{Binding ToggleSettingsCommand}"
            ToolTip.Tip="Open settings"
            AutomationProperties.Name="Open settings"
          >
            <i:Icon Value="mdi-cog" Foreground="{DynamicResource AccentBrush}" />
          </Button>
        </Grid>
      </Border>
      <SplitView
        x:Name="ShellSplitView"
        Grid.Row="1"
        IsPaneOpen="{Binding SidebarPaneOpen, Mode=TwoWay}"
        DisplayMode="{Binding SidebarDisplayMode}"
        OpenPaneLength="{Binding SidebarWidth}"
        CompactPaneLength="0"
        PaneBackground="{DynamicResource SurfaceAltBrush}"
      >
        <SplitView.Pane>
          <Grid ColumnDefinitions="*,4">
            <views:SourceSidebar />
            <Thumb
              x:Name="SidebarResizeThumb"
              Grid.Column="1"
              Cursor="SizeWestEast"
              Background="{DynamicResource BorderBrush}"
              DragDelta="OnSidebarResize"
              AutomationProperties.Name="Resize sidebar"
            />
          </Grid>
        </SplitView.Pane>
        <Grid RowDefinitions="Auto,Auto,*">
          <views:SearchBar Grid.Row="0" />
          <views:WorkspaceError Grid.Row="1" />
          <views:LogWorkspace Grid.Row="2" />
        </Grid>
      </SplitView>
    </Grid>
```

This removes the `CommandBar`, the **Save Session** button, the Elastic sources flyout, and `views:FileStrip`. Delete `src/HexTail/Views/FileStrip.axaml` and `FileStrip.axaml.cs`. `SaveCommand` and Ctrl+S stay.

- [ ] **Step 8: Code-behind**

In `MainWindow.axaml.cs`:

```csharp
// constructor, after Closed += OnClosed;
        SizeChanged += (_, args) => ViewModel.SetShellWidth(args.NewSize.Width);

// OnOpened, first line (guard: Bounds can still be 0 here, which would wrongly force narrow mode):
        if (ClientSize.Width > 0)
            ViewModel.SetShellWidth(ClientSize.Width);

// OnClosed, replace the VerticalFileTabs initializer line with:
                SidebarCollapsed = !ViewModel.SidebarOpen,
                SidebarWidth = ViewModel.SidebarWidth,

// new handler
    private void OnSidebarResize(object? sender, VectorEventArgs e) =>
        ViewModel.SidebarWidth += e.Vector.X;

// OnKeyDown switch, new case before default:
            case Key.B:
                ViewModel.ToggleSidebarCommand.Execute().Subscribe();
                break;
```

Add `using Avalonia.Input;` if missing (it is present) and `using Avalonia.Controls.Primitives;` for `VectorEventArgs`.

- [ ] **Step 9: Styles**

Append to `src/HexTail/Styles/CyberTail.axaml`, before `</Styles>`:

```xml
  <Style Selector="Border.source-row">
    <Setter Property="CornerRadius" Value="6" />
    <Setter Property="Padding" Value="6,0,2,0" />
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="BorderThickness" Value="2,0,0,0" />
    <Setter Property="BorderBrush" Value="Transparent" />
  </Style>
  <Style Selector="Border.source-row.selected">
    <Setter Property="Background" Value="{DynamicResource RaisedAltBrush}" />
    <Setter Property="BorderBrush" Value="{DynamicResource SelectedTabBrush}" />
  </Style>
  <Style Selector="Button.source-select">
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="BorderThickness" Value="0" />
    <Setter Property="Padding" Value="4,5" />
    <Setter Property="HorizontalAlignment" Value="Stretch" />
    <Setter Property="HorizontalContentAlignment" Value="Left" />
  </Style>
  <Style Selector="TextBlock.sidebar-heading">
    <Setter Property="FontSize" Value="11" />
    <Setter Property="FontWeight" Value="SemiBold" />
    <Setter Property="LetterSpacing" Value="0.8" />
    <Setter Property="Foreground" Value="{DynamicResource FaintTextBrush}" />
    <Setter Property="VerticalAlignment" Value="Center" />
  </Style>
  <Style Selector="TextBlock.source-count">
    <Setter Property="FontSize" Value="11" />
    <Setter Property="FontFamily" Value="Cascadia Mono,Consolas,Menlo,monospace" />
    <Setter Property="Foreground" Value="{DynamicResource FaintTextBrush}" />
    <Setter Property="VerticalAlignment" Value="Center" />
    <Setter Property="Margin" Value="4,0" />
  </Style>
  <Style Selector="TextBlock.hint">
    <Setter Property="FontSize" Value="12" />
    <Setter Property="Foreground" Value="{DynamicResource MutedBrush}" />
    <Setter Property="TextWrapping" Value="Wrap" />
  </Style>
  <Style Selector="TextBlock.muted">
    <Setter Property="Foreground" Value="{DynamicResource MutedBrush}" />
  </Style>
  <Style Selector="Ellipse.source-status">
    <Setter Property="Width" Value="8" />
    <Setter Property="Height" Value="8" />
    <Setter Property="VerticalAlignment" Value="Center" />
    <Setter Property="Fill" Value="{DynamicResource MutedBrush}" />
  </Style>
  <Style Selector="Ellipse.source-status.connected">
    <Setter Property="Fill" Value="{DynamicResource SuccessBrush}" />
  </Style>
  <Style Selector="Ellipse.source-status.failed">
    <Setter Property="Fill" Value="{DynamicResource ErrorBrush}" />
  </Style>
```

Remove the now-unused `Border.file-tab` and `Border.file-tab.selected` styles.

- [ ] **Step 10: Update existing tests for the new shell**

`src/HexTail.Tests/Ui/TabNavigationTests.cs`: replace `TenLongTabsCanBeSelectedReorderedAndClosedAtMinimumWidth` with the test below. Keep `KeyboardReorderPreservesSelectedIdentityAndKeepsAllPinned` unchanged, because it covers the search-view tab reorder.

```csharp
    [AvaloniaFact]
    public async Task TenLongFileNamesFitTheSidebarAndCanBeSelectedAndClosed()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var paths = Enumerable
            .Range(0, 10)
            .Select(index => Path.Combine(directory, $"{index}-" + new string('x', 150) + ".log"))
            .ToArray();
        foreach (var path in paths)
            File.WriteAllText(path, "");
        var window = TestWindow.Create(out var workspace);
        try
        {
            window.Width = 1280;
            window.Height = 720;
            await workspace.OpenPathsCommand.Execute(paths);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var sidebar = window.GetVisualDescendants().OfType<SourceSidebar>().Single();
            var selectButtons = sidebar
                .GetVisualDescendants()
                .OfType<Button>()
                .Where(button => button.Classes.Contains("source-select"))
                .ToArray();
            Assert.Equal(10, selectButtons.Length);
            foreach (var button in selectButtons)
            {
                var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), sidebar)!.Value.X;
                Assert.True(right <= sidebar.Bounds.Width, "row text must trim, not overflow");
                button.Command!.Execute(button.CommandParameter);
                Dispatcher.UIThread.RunJobs();
                Assert.Same(button.DataContext, workspace.SelectedFile);
            }

            foreach (var file in workspace.FileTabs.ToArray())
            {
                await workspace.CloseFileCommand.Execute(file);
                await UiWait.Until(() => !workspace.Files.Contains(file));
            }
        }
        finally
        {
            window.Close();
            await workspace.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }
```

`src/HexTail.Tests/Ui/MainWindowInteractionTests.cs`:
1. Delete `ElasticToolbarButtonIsHiddenWhenNoSourcesAreConfigured`. `SidebarTests.ElasticSectionOffersSetupWhenNothingIsConfigured` replaces it.
2. In `ElasticTimeRangePanelIsVisibleForAnOpenElasticTab`, change the control-name array to `"SettingsButton", "OpenFileButton", "SidebarToggleButton", "AutoScrollToggle", "InlineContextToggle", "ElasticTimeRangeButton"` (drop `"SaveSessionButton"`).
3. In `FileTabsExposeSelectionPathAndFluentCloseAction`: change `border.Classes.Contains("file-tab")` to `border.Classes.Contains("source-row")`. Delete the three assertions on `Background`, `BorderThickness`, and `BorderBrush`. Keep the `selected` class, tooltip, and close-button assertions. Set `window.Width = 1280;` before `window.Show();`.
4. In `FileTabsSelectAndCloseThroughWindowCommands` and `SwitchingFilesPreservesEachFilesFollowState`, set `window.Width = 1280;` before `Show()`. The button lookups (`DataContext` + `CommandParameter` + `Content is TextBlock`) match sidebar rows unchanged.
5. In `FileAndSearchTabsUseTabalonia`, replace the three assertions after `.ToArray();` with:
   ```csharp
            var tab = Assert.Single(tabs);
            Assert.IsType<FileTabViewModel>(tab.DataContext);
            Assert.NotNull(tab.ContentTemplate);
   ```
6. In `SettingsOpenCloseAndEscape`, change `FindControl<CommandBarButton>("SettingsButton")` to `FindControl<Button>("SettingsButton")`.

- [ ] **Step 11: Run the full suite**

Run: `dotnet test src/HexTail.Tests/HexTail.Tests.csproj -c Release`
Expected: PASS. If `OnboardingTests`, `AccessibilityTests`, or a 720-px fit test fails, the cause is the overlay sidebar covering content. Assert `viewModel.SidebarPaneOpen == false` at 720 px before suspecting the layout.

- [ ] **Step 12: Commit**

```bash
git add -A src/HexTail src/HexTail.Tests
git commit -m "feat(shell): replace file tab strip with a collapsible source sidebar"
```

---

### Task 4: View toolbar, status bar, single time-zone control

**Files:**
- Create: `src/HexTail/Views/StatusBar.axaml`, `src/HexTail/Views/StatusBar.axaml.cs`
- Modify: `src/HexTail/Views/LogWorkspace.axaml` (view content header)
- Modify: `src/HexTail/MainWindow.axaml` (remove the two `ToggleSwitch`es and the two range `TextBlock`s, add a status bar row)
- Modify: `src/HexTail/Views/SettingsPanel.axaml` (remove *Source clock time zone*, lines 281-293)
- Modify: `src/HexTail/ViewModels/FileTabViewModel.cs` (`SelectedView` notifications)
- Modify: `src/HexTail/ViewModels/MainWindowViewModel.cs` (`SourceSummary`)
- Modify: `src/HexTail/Styles/CyberTail.axaml`
- Test: `src/HexTail.Tests/Ui/StatusBarTests.cs`; modify `src/HexTail.Tests/Ui/MainWindowInteractionTests.cs`

**Interfaces:**
- Consumes: `LogViewViewModel.ResultCount`, `IsFollowing`, `ShowContext`; `SettingsViewModel.TimeZoneModes`/`TimeZoneMode`; `MainWindowViewModel.ElasticRangeStatus`.
- Produces: `MainWindowViewModel.SourceSummary : string`; `FileTabViewModel.SelectedView` raises `PropertyChanged`; XAML names `ViewToolbar`, `SourceSummary`, `ResultCount` (moved to the status bar), `StatusRange`, `DisplayZonePicker`. `AutoScrollToggle`/`InlineContextToggle` become `ToggleButton`s inside each view's template.

- [ ] **Step 1: Write the failing tests**

```csharp
// src/HexTail.Tests/Ui/StatusBarTests.cs
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Persistence;
using HexTail.Tests.Support;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class StatusBarTests
{
    [AvaloniaFact]
    public async Task StatusBarShowsSourceSummaryAndSelectedViewCount()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllLinesAsync(path, ["a", "b", "c"]);
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log");
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await viewModel.OpenPathsCommand.Execute([path]);
            await UiWait.Until(() =>
            {
                viewModel.State.DrainTailerEvents();
                return Text(window, "ResultCount") == "3 visible · 3 loaded from source";
            });
            Assert.Equal("1 source open", Text(window, "SourceSummary"));

            await viewModel.OpenPathsCommand.Execute([missing]);
            await UiWait.Until(() => Text(window, "SourceSummary") == "2 sources open · 1 with errors");
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task DisplayZonePickerIsTheOnlyTimeZoneControl()
    {
        var window = TestWindow.Create(out var viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var picker = window
            .GetVisualDescendants()
            .OfType<StatusBar>()
            .Single()
            .FindControl<ComboBox>("DisplayZonePicker")!;

        picker.SelectedItem = AppTimeZoneMode.Local;
        await UiWait.Until(() => viewModel.State.Settings.TimeZoneMode == AppTimeZoneMode.Local);

        viewModel.SettingsOpen = true;
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(
            window.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text == "Source clock time zone"
        );
        window.Close();
    }

    [AvaloniaFact]
    public async Task ViewToolbarTogglesFollowAndContextForTheSelectedView()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await viewModel.OpenPathsCommand.Execute([path]);
            Dispatcher.UIThread.RunJobs();
            var follow = Toggle(window, "AutoScrollToggle");
            var context = Toggle(window, "InlineContextToggle");

            follow.IsChecked = false;
            context.IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            Assert.False(viewModel.SelectedFile!.Model.FollowAll);
            Assert.True(viewModel.SelectedFile.Model.ShowContext);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string? Text(Window window, string name) =>
        window
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .SingleOrDefault(block => block.Name == name)
            ?.Text;

    private static ToggleButton Toggle(Window window, string name) =>
        window
            .GetVisualDescendants()
            .OfType<ToggleButton>()
            .Single(toggle => toggle.Name == name && toggle.IsEffectivelyVisible);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HexTail.Tests/HexTail.Tests.csproj -c Release --filter "FullyQualifiedName~StatusBarTests"`
Expected: build FAILS: `The type or namespace name 'StatusBar' could not be found`.

- [ ] **Step 3: `SelectedView` notifications**

In `FileTabViewModel`, replace the `SelectedViewIndex` setter and raise `SelectedView` when the view topology changes:

```csharp
    public int SelectedViewIndex
    {
        get => _selectedViewIndex;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedViewIndex, Math.Max(0, value));
            this.RaisePropertyChanged(nameof(SelectedView));
        }
    }

    // in SyncViews, inside `if (topologyChanged) { … }`, after `_viewsVersion++;`
            this.RaisePropertyChanged(nameof(SelectedView));
```

- [ ] **Step 4: `SourceSummary`**

In `MainWindowViewModel`:

```csharp
    private string _sourceSummary = "No sources open";

    public string SourceSummary
    {
        get => _sourceSummary;
        private set => this.RaiseAndSetIfChanged(ref _sourceSummary, value);
    }

    internal static string DescribeSources(IReadOnlyCollection<FileTabState> files)
    {
        if (files.Count == 0)
            return "No sources open";
        var text = files.Count == 1 ? "1 source open" : $"{files.Count} sources open";
        var failing = files.Count(file => file.Error is not null);
        return failing == 0 ? text : $"{text} · {failing} with errors";
    }

    // SyncFromState, right after `var files = _state.Files;`
        SourceSummary = DescribeSources(files);
```

- [ ] **Step 5: Status bar view**

```xml
<!-- src/HexTail/Views/StatusBar.axaml -->
<UserControl
  xmlns="https://github.com/avaloniaui"
  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
  xmlns:vm="using:HexTail.ViewModels"
  x:Class="HexTail.Views.StatusBar"
  x:DataType="vm:MainWindowViewModel"
  AutomationProperties.Name="Status"
>
  <Border
    Background="{DynamicResource ToolbarBrush}"
    BorderBrush="{DynamicResource BorderBrush}"
    BorderThickness="0,1,0,0"
    Padding="10,2"
  >
    <Grid x:Name="StatusGrid" ColumnDefinitions="Auto,Auto,*,Auto,Auto" ColumnSpacing="16">
      <TextBlock x:Name="SourceSummary" Classes="status" Text="{Binding SourceSummary}" />
      <TextBlock
        x:Name="ResultCount"
        Grid.Column="1"
        Classes="status"
        Text="{Binding SelectedFile.SelectedView.ResultCount}"
        IsVisible="{Binding HasFile}"
      />
      <TextBlock
        x:Name="StatusRange"
        Grid.Column="3"
        Classes="status"
        Text="{Binding ElasticRangeStatus}"
        IsVisible="{Binding IsElasticSelected}"
        TextTrimming="CharacterEllipsis"
      />
      <ComboBox
        x:Name="DisplayZonePicker"
        Grid.Column="4"
        MinHeight="0"
        Padding="6,0"
        FontSize="12"
        ItemsSource="{Binding Settings.TimeZoneModes}"
        SelectedItem="{Binding Settings.TimeZoneMode, Mode=TwoWay}"
        ToolTip.Tip="Time zone for status times and typed range times. Log text is unchanged."
        AutomationProperties.Name="Display time zone"
      />
    </Grid>
  </Border>
</UserControl>
```

```csharp
// src/HexTail/Views/StatusBar.axaml.cs
using Avalonia.Controls;

namespace HexTail.Views;

public partial class StatusBar : UserControl
{
    public StatusBar() => InitializeComponent();
}
```

Style (append to `CyberTail.axaml`):

```xml
  <Style Selector="TextBlock.status">
    <Setter Property="FontSize" Value="12" />
    <Setter Property="Foreground" Value="{DynamicResource MutedBrush}" />
    <Setter Property="VerticalAlignment" Value="Center" />
  </Style>
```

- [ ] **Step 6: Mount the status bar and remove the moved controls**

In `MainWindow.axaml`:
- Change `ShellGrid` to `RowDefinitions="Auto,*,Auto"` and add `<views:StatusBar Grid.Row="2" />` after the `SplitView`.
- Delete both `ToggleSwitch` elements (`InlineContextToggle`, `AutoScrollToggle`) from the top bar. Change the top-bar grid to `ColumnDefinitions="Auto,Auto,*,Auto"` and move `SettingsButton` to `Grid.Column="3"`.
- Inside `ElasticTimeRangePanel`, delete the `ElasticAppliedSummary` `TextBlock` and the `ElasticRangeStatus` `TextBlock` that follow the button. Set `ToolTip.Tip="{Binding ElasticAppliedRange}"` on `ElasticTimeRangeButton`.

- [ ] **Step 7: View toolbar in `LogWorkspace.axaml`**

Replace the `<StackPanel Margin="12,6" Spacing="4">…</StackPanel>` inside `TabControl.ContentTemplate` (the block holding `ResultMessage`, `ResultCount`, and `InitialLimitMessage`) with:

```xml
                  <Grid x:Name="ViewToolbar" ColumnDefinitions="*,Auto" Margin="12,4,8,4">
                    <StackPanel Spacing="2" VerticalAlignment="Center">
                      <TextBlock
                        x:Name="ResultMessage"
                        Text="{Binding ResultMessage}"
                        IsVisible="{Binding HasResultMessage}"
                        TextWrapping="Wrap"
                        Foreground="{DynamicResource TextBrush}"
                      />
                      <TextBlock
                        x:Name="InitialLimitMessage"
                        Text="{Binding InitialLimitMessage}"
                        IsVisible="{Binding HasInitialLimitMessage}"
                        TextWrapping="Wrap"
                        Foreground="{DynamicResource AccentBrush}"
                      />
                    </StackPanel>
                    <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="4">
                      <ToggleButton
                        x:Name="InlineContextToggle"
                        IsChecked="{Binding ShowContext, Mode=TwoWay}"
                        ToolTip.Tip="Show surrounding lines for the selected row"
                        AutomationProperties.Name="Show inline context"
                      >
                        <StackPanel Orientation="Horizontal" Spacing="4">
                          <ic:Icon Value="mdi-arrow-expand-vertical" />
                          <TextBlock Text="Context" />
                        </StackPanel>
                      </ToggleButton>
                      <ToggleButton
                        x:Name="AutoScrollToggle"
                        IsChecked="{Binding IsFollowing, Mode=TwoWay}"
                        ToolTip.Tip="Keep the newest line in view (F)"
                        AutomationProperties.Name="Auto-scroll logs"
                      >
                        <StackPanel Orientation="Horizontal" Spacing="4">
                          <ic:Icon Value="mdi-arrow-collapse-down" />
                          <TextBlock Text="Follow" />
                        </StackPanel>
                      </ToggleButton>
                    </StackPanel>
                  </Grid>
```

- [ ] **Step 8: Remove the Appearance zone control**

In `SettingsPanel.axaml`, delete the *Source clock time zone* `TextBlock`, its `ComboBox`, and the following explanatory `TextBlock` (old lines 281-293). Leave `SettingsViewModel.TimeZoneMode`/`TimeZoneModes` in place; the status bar uses them.

- [ ] **Step 9: Update the affected existing tests**

In `MainWindowInteractionTests`:
- `SwitchingFilesPreservesEachFilesFollowState`: replace `window.FindControl<ToggleSwitch>("AutoScrollToggle")!` with
  `window.GetVisualDescendants().OfType<ToggleButton>().Single(toggle => toggle.Name == "AutoScrollToggle" && toggle.IsEffectivelyVisible)`.
- `ElasticTimeRangePanelIsVisibleForAnOpenElasticTab`: replace `window.FindControl<Control>(name)` in the fit loop with
  `window.FindControl<Control>(name) ?? window.GetVisualDescendants().OfType<Control>().First(control => control.Name == name && control.IsEffectivelyVisible)`.
  Add `using Avalonia.Controls.Primitives;` if it is missing.

- [ ] **Step 10: Run the full suite**

Run: `dotnet test src/HexTail.Tests/HexTail.Tests.csproj -c Release`
Expected: PASS, including `ResultStateTests`, which finds `ResultCount` by name in the visual tree.

- [ ] **Step 11: Commit**

```bash
git add -A src/HexTail src/HexTail.Tests
git commit -m "feat(shell): add status bar and per-view follow/context toolbar"
```

---

### Task 5: Compact time-range pill with typed inputs

**Files:**
- Modify: `src/HexTail/MainWindow.axaml` (`ElasticTimeRangePanel` contents)
- Modify: `src/HexTail/ViewModels/MainWindowViewModel.cs`
- Modify: `src/HexTail/Styles/CyberTail.axaml`
- Test: modify `src/HexTail.Tests/Ui/MainWindowInteractionTests.cs` (`ElasticTimeRangePanelIsVisibleForAnOpenElasticTab`)

**Interfaces:**
- Consumes: `TimeExpression.TryParse/FormatForInput`, `TimeRangeLabel.Describe` (Task 1); `AppState.SetElasticTimeRange(FileTabState, string, string)`, which throws `ArgumentException` when From > To.
- Produces (on `MainWindowViewModel`): `RangeFromText`, `RangeToText`, `ElasticToNow` (kept), `RangeFromPreview`, `RangeToPreview`, `RangeZoneHint`, `RangeError`, `ElasticRangePill`, `ElasticRangeDirty` (kept), `ElasticDraftStatus` (kept, new text), `ApplyElasticTimeRangeCommand` (kept), `ApplyRangePresetCommand : ReactiveCommand<string, Unit>`, `DisplayZone` (private). **Removed:** `ElasticFromDate`, `ElasticFromTime`, `ElasticToDate`, `ElasticToTime`, `ElasticPresets`, `ElasticPresetIndex`, `ElasticInputZones`, `ElasticInputZone`, `PresetExpression`, `SetPickerValues`, `PickerTimestamp`. XAML names `ElasticRangePillText`, `ElasticRangeFromBox`, `ElasticRangeToBox`, `ElasticRangeError`, and `RangePreset5m`, `RangePreset15m`, `RangePreset1h`, `RangePreset4h`, `RangePreset24h`, `RangePreset7d`.

- [ ] **Step 1: Rewrite the range test (failing)**

In `MainWindowInteractionTests.ElasticTimeRangePanelIsVisibleForAnOpenElasticTab`, keep everything up to and including the width/density fit loop. Replace the rest of the method, from `window.Width = 720;` to the end, with:

```csharp
        window.Width = 720;
        window.UpdateLayout();
        var rangeButton = window.FindControl<Button>("ElasticTimeRangeButton")!;
        Assert.True(
            rangeButton.TranslatePoint(default, window)!.Value.X + rangeButton.Bounds.Width
                <= window.Bounds.Width
        );
        var flyout = Assert.IsType<Flyout>(rangeButton.Flyout);
        flyout.ShowAt(rangeButton);
        Dispatcher.UIThread.RunJobs();
        var editor = Assert.IsType<StackPanel>(flyout.Content);
        Assert.Empty(editor.GetVisualDescendants().OfType<DatePicker>());
        Assert.Empty(editor.GetVisualDescendants().OfType<TimePicker>());
        Assert.Equal(2, editor.GetVisualDescendants().OfType<TextBox>().Count());

        viewModel.RangeFromText = "2026-08-20 10:15:30";
        viewModel.ElasticToNow = false;
        viewModel.RangeToText = "2026-08-20 10:16:45";
        Assert.True(viewModel.ElasticRangeDirty);
        Assert.Equal("= 2026-08-20 10:15:30 UTC", viewModel.RangeFromPreview);
        await viewModel.ApplyElasticTimeRangeCommand.Execute().FirstAsync();
        Assert.False(viewModel.ElasticRangeDirty);
        Assert.Null(viewModel.RangeError);
        Assert.Equal("2026-08-20T10:15:30.0000000+00:00", state.SelectedFile!.ElasticFrom);
        Assert.Equal("2026-08-20T10:16:45.0000000+00:00", state.SelectedFile.ElasticTo);
        Assert.Equal(
            "2026-08-20 10:15:30 → 2026-08-20 10:16:45 · loading…",
            viewModel.ElasticRangePill
        );

        viewModel.RangeToText = "2026-08-20 10:14:00";
        await viewModel.ApplyElasticTimeRangeCommand.Execute().FirstAsync();
        Assert.Contains("From must be at or before To", viewModel.RangeError);
        Assert.Equal("2026-08-20T10:16:45.0000000+00:00", state.SelectedFile.ElasticTo);

        viewModel.RangeFromText = "tomorrowish";
        await viewModel.ApplyElasticTimeRangeCommand.Execute().FirstAsync();
        Assert.StartsWith("From:", viewModel.RangeError);

        foreach (var expression in new[] { "now-5m", "now-15m", "now-1h", "now-4h", "now-24h", "now-7d" })
        {
            await viewModel.ApplyRangePresetCommand.Execute(expression).FirstAsync();
            Assert.Equal(expression, state.SelectedFile.ElasticFrom);
            Assert.Equal("now", state.SelectedFile.ElasticTo);
            Assert.False(viewModel.ElasticRangeDirty);
        }
        Assert.StartsWith("Last 7 days", viewModel.ElasticRangePill);

        viewModel.Settings.TimeZoneMode = AppTimeZoneMode.Local;
        await WaitFor(() => state.Settings.TimeZoneMode == AppTimeZoneMode.Local);
        viewModel.RangeFromText = "2026-08-20 10:00";
        await viewModel.ApplyElasticTimeRangeCommand.Execute().FirstAsync();
        var wall = new DateTime(2026, 8, 20, 10, 0, 0);
        Assert.Equal(
            new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall)),
            DateTimeOffset.Parse(state.SelectedFile.ElasticFrom)
        );
        Assert.Equal(AppTimeZoneMode.Local, state.SelectedFile.ElasticInputZone);
        flyout.Hide();
        window.Close();
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test src/HexTail.Tests/HexTail.Tests.csproj -c Release --filter "FullyQualifiedName~ElasticTimeRangePanelIsVisibleForAnOpenElasticTab"`
Expected: build FAILS: `'MainWindowViewModel' does not contain a definition for 'RangeFromText'`.

- [ ] **Step 3: Replace the picker state in `MainWindowViewModel`**

Delete the fields `_elasticFromDate`, `_elasticToDate`, `_elasticFromTime`, `_elasticToTime`, `_elasticPresetIndex`, and `_elasticInputZone`, the members listed under **Removed** above, and the `using HexTail.Elastic;` if nothing else needs it. Add `using System.Globalization;`. Add:

```csharp
    private string _rangeFromText = "now-5m";
    private string _rangeToText = "now";
    private string? _rangeError;

    public ReactiveCommand<string, Unit> ApplyRangePresetCommand { get; }

    // constructor, next to ApplyElasticTimeRangeCommand:
        ApplyRangePresetCommand = ReactiveCommand.Create<string>(ApplyRangePreset, _scheduler);

    private AppTimeZoneMode DisplayZone => _state.Settings.TimeZoneMode;
    private string ZoneLabel => DisplayZone == AppTimeZoneMode.Utc ? "UTC" : "local";

    public string RangeFromText
    {
        get => _rangeFromText;
        set
        {
            if (_rangeFromText == value)
                return;
            this.RaiseAndSetIfChanged(ref _rangeFromText, value);
            this.RaisePropertyChanged(nameof(RangeFromPreview));
            if (!_syncingRange)
                ElasticRangeDirty = true;
        }
    }

    public string RangeToText
    {
        get => _rangeToText;
        set
        {
            if (_rangeToText == value)
                return;
            this.RaiseAndSetIfChanged(ref _rangeToText, value);
            this.RaisePropertyChanged(nameof(RangeToPreview));
            if (!_syncingRange)
                ElasticRangeDirty = true;
        }
    }

    public bool ElasticToNow
    {
        get => _elasticToNow;
        set
        {
            if (_elasticToNow == value)
                return;
            this.RaiseAndSetIfChanged(ref _elasticToNow, value);
            this.RaisePropertyChanged(nameof(RangeToPreview));
            if (!_syncingRange)
                ElasticRangeDirty = true;
        }
    }

    public string RangeFromPreview => Preview(RangeFromText);
    public string RangeToPreview => ElasticToNow ? "Live — new logs keep appending" : Preview(RangeToText);
    public string RangeZoneHint =>
        $"Times without an offset are read as {ZoneLabel}. Try now-15m, -2h, 14:00, yesterday 09:00.";

    public string? RangeError
    {
        get => _rangeError;
        private set => this.RaiseAndSetIfChanged(ref _rangeError, value);
    }

    public string ElasticDraftStatus => ElasticRangeDirty ? "Unapplied — press Enter or Apply" : string.Empty;

    public string ElasticRangePill =>
        SelectedFile?.Model is not { } tab || !IsElasticSelected
            ? string.Empty
            : $"{TimeRangeLabel.Describe(tab.ElasticFrom, tab.ElasticTo, DisplayZone)} · {RangeState(tab)}";

    private static string RangeState(FileTabState tab) =>
        tab.Error is not null ? "failed"
        : tab.ElasticLoading ? "loading…"
        : tab.ElasticTo == "now" ? "live"
        : "historical";

    private string Preview(string text) =>
        TimeExpression.TryParse(text, DateTimeOffset.UtcNow, DisplayZone, out var parsed, out var error)
            ? $"= {TimeExpression.FormatForInput(parsed.Resolved.ToString("O", CultureInfo.InvariantCulture), DisplayZone)} {ZoneLabel}"
            : error!;

    private void LoadRangeInputs(FileTabState tab)
    {
        _syncingRange = true;
        RangeFromText = TimeExpression.FormatForInput(tab.ElasticFrom, DisplayZone);
        ElasticToNow = string.Equals(tab.ElasticTo, "now", StringComparison.OrdinalIgnoreCase);
        RangeToText = ElasticToNow ? "now" : TimeExpression.FormatForInput(tab.ElasticTo, DisplayZone);
        _syncingRange = false;
        ElasticRangeDirty = false;
        RangeError = null;
    }

    private void ApplyRangePreset(string expression)
    {
        _syncingRange = true;
        RangeFromText = expression;
        ElasticToNow = true;
        RangeToText = "now";
        _syncingRange = false;
        ApplyElasticTimeRange();
    }

    private void ApplyElasticTimeRange()
    {
        if (!IsElasticSelected || SelectedFile is null)
            return;
        var now = DateTimeOffset.UtcNow;
        if (!TimeExpression.TryParse(RangeFromText, now, DisplayZone, out var from, out var fromError))
        {
            RangeError = $"From: {fromError}";
            return;
        }
        var to = "now";
        if (!ElasticToNow)
        {
            if (!TimeExpression.TryParse(RangeToText, now, DisplayZone, out var parsedTo, out var toError))
            {
                RangeError = $"To: {toError}";
                return;
            }
            to = parsedTo.Expression;
        }
        try
        {
            _state.SetElasticTimeRange(SelectedFile.Model, from.Expression, to);
            SelectedFile.Model.ElasticInputZone = DisplayZone;
            ElasticRangeDirty = false;
            RangeError = null;
            SetFileError(null);
        }
        catch (ArgumentException exception)
        {
            RangeError = exception.Message;
        }
    }
```

Then make these edits:
- In the `SelectedFile` setter, replace the whole `if (value is not null) { _syncingRange = true; … ElasticRangeDirty = false; }` block with `if (value is not null) LoadRangeInputs(value.Model);`.
- In the `ElasticRangeDirty` setter, keep the `ElasticDraftStatus` notification.
- In `ElasticAppliedRange`, replace `ElasticInputZone` with `DisplayZone`.
- In `SyncFromState`, next to the existing `ElasticAppliedRange`/`ElasticRangeStatus` notifications, add:

```csharp
        if (SelectedFile is not null && !ElasticRangeDirty)
            LoadRangeInputs(SelectedFile.Model);
        this.RaisePropertyChanged(nameof(ElasticRangePill));
        this.RaisePropertyChanged(nameof(RangeFromPreview));
        this.RaisePropertyChanged(nameof(RangeToPreview));
        this.RaisePropertyChanged(nameof(RangeZoneHint));
```

Verify that nothing else references the removed members:
`grep -rnE "ElasticPresetIndex|ElasticFromDate|ElasticFromTime|ElasticToDate|ElasticToTime|ElasticInputZones|PickerTimestamp|SetPickerValues" src` must return nothing.

- [ ] **Step 4: Replace the range panel contents**

In `MainWindow.axaml`, replace everything inside `ElasticTimeRangePanel` with:

```xml
            <Button
              x:Name="ElasticTimeRangeButton"
              Classes="range-pill"
              ToolTip.Tip="{Binding ElasticAppliedRange}"
              AutomationProperties.Name="Elastic log time range"
            >
              <StackPanel Orientation="Horizontal" Spacing="6">
                <i:Icon Value="mdi-clock-outline" />
                <TextBlock
                  x:Name="ElasticRangePillText"
                  Text="{Binding ElasticRangePill}"
                  TextTrimming="CharacterEllipsis"
                  MaxWidth="420"
                />
              </StackPanel>
              <Button.Flyout>
                <Flyout Placement="BottomEdgeAlignedLeft">
                  <StackPanel x:Name="ElasticRangeEditor" Spacing="8" Width="360">
                    <TextBlock Text="Quick select" Classes="sidebar-heading" />
                    <UniformGrid Columns="3">
                      <Button x:Name="RangePreset5m" Content="5 min" Margin="2" HorizontalAlignment="Stretch" Command="{Binding ApplyRangePresetCommand}" CommandParameter="now-5m" AutomationProperties.Name="Last 5 minutes" />
                      <Button x:Name="RangePreset15m" Content="15 min" Margin="2" HorizontalAlignment="Stretch" Command="{Binding ApplyRangePresetCommand}" CommandParameter="now-15m" AutomationProperties.Name="Last 15 minutes" />
                      <Button x:Name="RangePreset1h" Content="1 hour" Margin="2" HorizontalAlignment="Stretch" Command="{Binding ApplyRangePresetCommand}" CommandParameter="now-1h" AutomationProperties.Name="Last hour" />
                      <Button x:Name="RangePreset4h" Content="4 hours" Margin="2" HorizontalAlignment="Stretch" Command="{Binding ApplyRangePresetCommand}" CommandParameter="now-4h" AutomationProperties.Name="Last 4 hours" />
                      <Button x:Name="RangePreset24h" Content="24 hours" Margin="2" HorizontalAlignment="Stretch" Command="{Binding ApplyRangePresetCommand}" CommandParameter="now-24h" AutomationProperties.Name="Last 24 hours" />
                      <Button x:Name="RangePreset7d" Content="7 days" Margin="2" HorizontalAlignment="Stretch" Command="{Binding ApplyRangePresetCommand}" CommandParameter="now-7d" AutomationProperties.Name="Last 7 days" />
                    </UniformGrid>
                    <TextBlock Text="From" Classes="sidebar-heading" />
                    <TextBox
                      x:Name="ElasticRangeFromBox"
                      Text="{Binding RangeFromText, Mode=TwoWay}"
                      FontFamily="Cascadia Mono,Consolas,Menlo,monospace"
                      AutomationProperties.Name="Range start"
                    >
                      <TextBox.KeyBindings>
                        <KeyBinding Gesture="Enter" Command="{Binding ApplyElasticTimeRangeCommand}" />
                      </TextBox.KeyBindings>
                    </TextBox>
                    <TextBlock Text="{Binding RangeFromPreview}" Classes="hint" />
                    <Grid ColumnDefinitions="*,Auto">
                      <TextBlock Text="To" Classes="sidebar-heading" />
                      <CheckBox
                        x:Name="ElasticToNowCheckBox"
                        Grid.Column="1"
                        Content="Live"
                        IsChecked="{Binding ElasticToNow, Mode=TwoWay}"
                        AutomationProperties.Name="To now (live logs)"
                      />
                    </Grid>
                    <TextBox
                      x:Name="ElasticRangeToBox"
                      Text="{Binding RangeToText, Mode=TwoWay}"
                      IsEnabled="{Binding !ElasticToNow}"
                      FontFamily="Cascadia Mono,Consolas,Menlo,monospace"
                      AutomationProperties.Name="Range end"
                    >
                      <TextBox.KeyBindings>
                        <KeyBinding Gesture="Enter" Command="{Binding ApplyElasticTimeRangeCommand}" />
                      </TextBox.KeyBindings>
                    </TextBox>
                    <TextBlock Text="{Binding RangeToPreview}" Classes="hint" />
                    <TextBlock
                      x:Name="ElasticRangeError"
                      Text="{Binding RangeError}"
                      IsVisible="{Binding RangeError, Converter={x:Static ObjectConverters.IsNotNull}}"
                      Classes="error"
                      TextWrapping="Wrap"
                    />
                    <TextBlock Text="{Binding RangeZoneHint}" Classes="hint" />
                    <Grid ColumnDefinitions="*,Auto">
                      <TextBlock Text="{Binding ElasticDraftStatus}" Classes="hint" VerticalAlignment="Center" />
                      <Button
                        x:Name="ApplyElasticTimeRangeButton"
                        Grid.Column="1"
                        Content="Apply"
                        Classes="primary"
                        Command="{Binding ApplyElasticTimeRangeCommand}"
                      />
                    </Grid>
                  </StackPanel>
                </Flyout>
              </Button.Flyout>
            </Button>
```

Style:

```xml
  <Style Selector="Button.range-pill">
    <Setter Property="CornerRadius" Value="999" />
    <Setter Property="Padding" Value="10,4" />
    <Setter Property="Background" Value="{DynamicResource RaisedBrush}" />
    <Setter Property="BorderBrush" Value="{DynamicResource BorderStrongBrush}" />
    <Setter Property="BorderThickness" Value="1" />
  </Style>
```

- [ ] **Step 5: Run the full suite**

Run: `dotnet test src/HexTail.Tests/HexTail.Tests.csproj -c Release`
Expected: PASS. `AppStateTests` still use `inputZone:` on `OpenElasticSourceAsync`; the model property is unchanged.

- [ ] **Step 6: Commit**

```bash
git add -A src/HexTail src/HexTail.Tests
git commit -m "feat(range): replace date pickers with a time pill and typed range inputs"
```

---

### Task 6: Pause and "new lines" pill

**Files:**
- Modify: `src/HexTail/ViewModels/MainWindowViewModel.cs` (`IsPaused`, `TogglePauseCommand`)
- Modify: `src/HexTail/ViewModels/LogViewViewModel.cs` (freeze, unseen tracking, `JumpToLatestCommand`)
- Modify: `src/HexTail/Views/LogView.axaml` (overlay button)
- Modify: `src/HexTail/MainWindow.axaml` (`PauseToggle`)
- Modify: `src/HexTail/Views/StatusBar.axaml` (`PausedIndicator`)
- Test: `src/HexTail.Tests/Ui/FollowAndPauseTests.cs`

**Interfaces:**
- Consumes: `LogViewViewModel.Sync(bool)`, `LinesFor()`, `Lines` (`AvaloniaList<LogLineViewModel>`, each with `.Line`), `IsFollowing`.
- Produces: `MainWindowViewModel.IsPaused : bool`, `TogglePauseCommand`; `LogViewViewModel.UnseenCount : int`, `HasUnseen : bool`, `UnseenLabel : string`, `JumpToLatestCommand`, `const int UnseenScanLimit = 10_000`; XAML names `PauseToggle`, `JumpToLatestButton`, `PausedIndicator`.

- [ ] **Step 1: Write the failing tests**

```csharp
// src/HexTail.Tests/Ui/FollowAndPauseTests.cs
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.Ui;

public sealed class FollowAndPauseTests
{
    [AvaloniaFact]
    public async Task ScrolledAwayViewCountsNewLinesAndJumpsToLatest()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllLinesAsync(path, Enumerable.Range(0, 20).Select(i => $"line {i}"));
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await viewModel.OpenPathsCommand.Execute([path]);
            var view = await LoadedView(viewModel, 20);

            view.IsFollowing = false;
            await File.AppendAllLinesAsync(path, ["new 1", "new 2", "new 3"]);
            await UiWait.Until(() =>
            {
                viewModel.State.DrainTailerEvents();
                return view.UnseenCount == 3;
            });
            Assert.Equal("↓ 3 new lines", view.UnseenLabel);
            var pill = window
                .GetVisualDescendants()
                .OfType<Button>()
                .Single(button => button.Name == "JumpToLatestButton");
            Assert.True(pill.IsVisible);

            await view.JumpToLatestCommand.Execute();
            Assert.True(view.IsFollowing);
            Assert.Equal(0, view.UnseenCount);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task PauseFreezesRowsWhileBufferingAndResumeCatchesUp()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllLinesAsync(path, Enumerable.Range(0, 10).Select(i => $"line {i}"));
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await viewModel.OpenPathsCommand.Execute([path]);
            var view = await LoadedView(viewModel, 10);

            await viewModel.TogglePauseCommand.Execute();
            Assert.True(viewModel.IsPaused);
            await File.AppendAllLinesAsync(path, ["late 1", "late 2"]);
            await UiWait.Until(() =>
            {
                viewModel.State.DrainTailerEvents();
                return viewModel.SelectedFile!.Model.Buffer.Count == 12;
            });
            Assert.Equal(10, view.Lines.Count);
            Assert.Equal(2, view.UnseenCount);

            await viewModel.TogglePauseCommand.Execute();
            Assert.Equal(12, view.Lines.Count);
            Assert.Equal(0, view.UnseenCount);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<LogViewViewModel> LoadedView(MainWindowViewModel viewModel, int lines)
    {
        await UiWait.Until(() =>
        {
            viewModel.State.DrainTailerEvents();
            return viewModel.SelectedFile?.SelectedView.Lines.Count == lines;
        });
        return viewModel.SelectedFile!.SelectedView;
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HexTail.Tests/HexTail.Tests.csproj -c Release --filter "FullyQualifiedName~FollowAndPauseTests"`
Expected: build FAILS: `'LogViewViewModel' does not contain a definition for 'UnseenCount'`.

- [ ] **Step 3: Pause on `MainWindowViewModel`**

```csharp
    private bool _isPaused;
    public ReactiveCommand<Unit, Unit> TogglePauseCommand { get; }

    // constructor:
        TogglePauseCommand = ReactiveCommand.Create(
            () =>
            {
                IsPaused = !IsPaused;
            },
            _scheduler
        );

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (_isPaused == value)
                return;
            this.RaiseAndSetIfChanged(ref _isPaused, value);
            if (!value)
                SelectedFile?.SelectedView.Sync();
        }
    }
```

- [ ] **Step 4: Freeze and unseen tracking on `LogViewViewModel`**

Add `using System.Reactive;` if it is missing, then:

```csharp
    internal const int UnseenScanLimit = 10_000;
    private Line? _seenAnchor;
    private int _unseenCount;

    public int UnseenCount => _unseenCount;
    public bool HasUnseen => _unseenCount > 0;
    public string UnseenLabel =>
        _unseenCount >= UnseenScanLimit ? $"↓ {UnseenScanLimit:N0}+ new lines"
        : _unseenCount == 1 ? "↓ 1 new line"
        : $"↓ {_unseenCount:N0} new lines";
    public ReactiveCommand<Unit, Unit> JumpToLatestCommand { get; }

    // constructor (after the existing commands):
        JumpToLatestCommand = ReactiveCommand.Create(JumpToLatest, owner.Scheduler);

    private void JumpToLatest()
    {
        _owner.IsPaused = false;
        IsFollowing = true;
        Sync();
    }

    private void UpdateUnseen(IReadOnlyList<Line> lines, bool frozen)
    {
        var count = 0;
        if (!frozen && IsFollowing)
            _seenAnchor = lines.Count > 0 ? lines[^1] : null;
        else
        {
            _seenAnchor ??= Lines.Count > 0 ? Lines[^1].Line : null;
            for (var index = lines.Count - 1; index >= 0 && count < UnseenScanLimit; index--)
            {
                if (ReferenceEquals(lines[index], _seenAnchor))
                    break;
                count++;
            }
        }
        if (count == _unseenCount)
            return;
        _unseenCount = count;
        this.RaisePropertyChanged(nameof(UnseenCount));
        this.RaisePropertyChanged(nameof(HasUnseen));
        this.RaisePropertyChanged(nameof(UnseenLabel));
    }
```

In `IsFollowing`'s setter, add this before `this.RaisePropertyChanged(nameof(IsFollowing));`:

```csharp
            if (!value)
                _seenAnchor = Lines.Count > 0 ? Lines[^1].Line : null;
```

In `Sync`, replace:

```csharp
        var lines = LinesFor();
        SyncRows(Lines, lines, isContext: false, resetItems, refreshRows);
```

with:

```csharp
        var lines = LinesFor();
        var frozen = _owner.IsPaused && Lines.Count > 0;
        if (!frozen)
            SyncRows(Lines, lines, isContext: false, resetItems, refreshRows);
        UpdateUnseen(lines, frozen);
```

- [ ] **Step 5: UI**

`LogView.axaml`: add this as the last child of `LogLayout`. Row 0 is shared, so it overlays the list:

```xml
    <Button
      x:Name="JumpToLatestButton"
      Grid.Row="0"
      Classes="primary"
      HorizontalAlignment="Center"
      VerticalAlignment="Bottom"
      Margin="0,0,0,12"
      IsVisible="{Binding HasUnseen}"
      Content="{Binding UnseenLabel}"
      Command="{Binding JumpToLatestCommand}"
      AutomationProperties.Name="Jump to latest log lines"
    />
```

`MainWindow.axaml` top bar: insert a column before `SettingsButton`. The grid becomes `ColumnDefinitions="Auto,Auto,*,Auto,Auto"`, `SettingsButton` moves to `Grid.Column="4"`, and the new toggle is:

```xml
          <ToggleButton
            x:Name="PauseToggle"
            Grid.Column="3"
            IsChecked="{Binding IsPaused, Mode=TwoWay}"
            ToolTip.Tip="Pause view updates; logs keep buffering (Space)"
            AutomationProperties.Name="Pause view updates"
          >
            <StackPanel Orientation="Horizontal" Spacing="4">
              <i:Icon Value="mdi-pause" />
              <TextBlock Text="Pause" />
            </StackPanel>
          </ToggleButton>
```

`StatusBar.axaml`: put this in `Grid.Column="2"` (currently empty, `*`):

```xml
      <TextBlock
        x:Name="PausedIndicator"
        Grid.Column="2"
        Classes="status"
        Foreground="{DynamicResource AccentBrush}"
        Text="Paused — rows are buffering"
        IsVisible="{Binding IsPaused}"
      />
```

Add `"PauseToggle"` to the fit-loop name list in `MainWindowInteractionTests.ElasticTimeRangePanelIsVisibleForAnOpenElasticTab`.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test src/HexTail.Tests/HexTail.Tests.csproj -c Release`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add -A src/HexTail src/HexTail.Tests
git commit -m "feat(view): pause view updates and offer jump-to-latest for unseen lines"
```

---

### Task 7: Keyboard shortcuts

**Files:**
- Create: `src/HexTail/Input/ShellShortcuts.cs`
- Modify: `src/HexTail/MainWindow.axaml.cs` (`OnKeyDown` dispatch)
- Modify: `src/HexTail/Views/LogView.axaml.cs` (`ScrollToTop`)
- Test: `src/HexTail.Tests/Input/ShellShortcutsTests.cs`, `src/HexTail.Tests/Ui/ShortcutDispatchTests.cs`

**Interfaces:**
- Consumes: `MainWindowViewModel.OpenCommand`, `SaveCommand`, `ToggleSidebarCommand`, `TogglePauseCommand`, `CloseFileCommand`, `RemoveSearchCommand`, `SelectedFile.SelectedViewIndex`/`Views`/`SelectedView.IsFollowing`; `SearchBar.FocusQuery()`.
- Produces:
  - `internal enum ShellAction { None, OpenFiles, FocusSearch, SaveSession, ToggleSidebar, CloseView, NextView, PreviousView, SelectView, ToggleFollow, TogglePause, ScrollToTop, ScrollToBottom }`
  - `internal readonly record struct ShellCommand(ShellAction Action, int Index = 0) { public static readonly ShellCommand None; }`
  - `internal static ShellCommand ShellShortcuts.Resolve(Key key, KeyModifiers modifiers, bool textInputFocused)`
  - `internal void LogView.ScrollToTop()`

- [ ] **Step 1: Write the failing tests**

```csharp
// src/HexTail.Tests/Input/ShellShortcutsTests.cs
using Avalonia.Input;
using HexTail.Input;

namespace HexTail.Tests.Input;

public sealed class ShellShortcutsTests
{
    [Theory]
    [InlineData(Key.O, KeyModifiers.Control, ShellAction.OpenFiles)]
    [InlineData(Key.O, KeyModifiers.Meta, ShellAction.OpenFiles)]
    [InlineData(Key.F, KeyModifiers.Control, ShellAction.FocusSearch)]
    [InlineData(Key.S, KeyModifiers.Control, ShellAction.SaveSession)]
    [InlineData(Key.B, KeyModifiers.Control, ShellAction.ToggleSidebar)]
    [InlineData(Key.W, KeyModifiers.Control, ShellAction.CloseView)]
    [InlineData(Key.Tab, KeyModifiers.Control, ShellAction.NextView)]
    [InlineData(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift, ShellAction.PreviousView)]
    public void PrimaryShortcutsWorkEvenInTextBoxes(Key key, KeyModifiers modifiers, ShellAction expected) =>
        Assert.Equal(expected, ShellShortcuts.Resolve(key, modifiers, textInputFocused: true).Action);

    [Theory]
    [InlineData(Key.D1, 0)]
    [InlineData(Key.D9, 8)]
    public void ControlDigitSelectsView(Key key, int index) =>
        Assert.Equal(
            new ShellCommand(ShellAction.SelectView, index),
            ShellShortcuts.Resolve(key, KeyModifiers.Control, textInputFocused: false)
        );

    [Theory]
    [InlineData(Key.OemQuestion, KeyModifiers.None, ShellAction.FocusSearch)]
    [InlineData(Key.F, KeyModifiers.None, ShellAction.ToggleFollow)]
    [InlineData(Key.Space, KeyModifiers.None, ShellAction.TogglePause)]
    [InlineData(Key.G, KeyModifiers.None, ShellAction.ScrollToTop)]
    [InlineData(Key.G, KeyModifiers.Shift, ShellAction.ScrollToBottom)]
    public void SingleKeysWorkOutsideTextInput(Key key, KeyModifiers modifiers, ShellAction expected) =>
        Assert.Equal(expected, ShellShortcuts.Resolve(key, modifiers, textInputFocused: false).Action);

    [Theory]
    [InlineData(Key.F)]
    [InlineData(Key.Space)]
    [InlineData(Key.G)]
    [InlineData(Key.OemQuestion)]
    public void SingleKeysAreIgnoredWhileTyping(Key key) =>
        Assert.Equal(ShellCommand.None, ShellShortcuts.Resolve(key, KeyModifiers.None, textInputFocused: true));

    [Fact]
    public void AltCombinationsAreLeftForReordering() =>
        Assert.Equal(ShellCommand.None, ShellShortcuts.Resolve(Key.Down, KeyModifiers.Alt, false));
}
```

```csharp
// src/HexTail.Tests/Ui/ShortcutDispatchTests.cs
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Tests.Support;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class ShortcutDispatchTests
{
    [AvaloniaFact]
    public async Task SpaceAndFToggleViewStateButNotWhileTyping()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await viewModel.OpenPathsCommand.Execute([path]);
            Dispatcher.UIThread.RunJobs();
            var view = viewModel.SelectedFile!.SelectedView;

            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Assert.True(viewModel.IsPaused);
            window.KeyPress(Key.F, RawInputModifiers.None, PhysicalKey.F, "f");
            Assert.False(view.IsFollowing);

            var query = window.GetVisualDescendants().OfType<SearchBar>().Single().FindControl<TextBox>("QueryBox")!;
            query.Focus();
            window.KeyPress(Key.F, RawInputModifiers.None, PhysicalKey.F, "f");
            Assert.False(view.IsFollowing);
            Assert.True(viewModel.IsPaused);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ControlWClosesSearchViewThenSource()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await viewModel.OpenPathsCommand.Execute([path]);
            viewModel.Query = "error";
            await viewModel.AddSearchCommand.Execute();
            var file = viewModel.SelectedFile!;
            file.SelectedViewIndex = 1;
            Dispatcher.UIThread.RunJobs();

            window.KeyPress(Key.W, RawInputModifiers.Control, PhysicalKey.W, null);
            await UiWait.Until(() => file.Model.Searches.Count == 0);
            window.KeyPress(Key.W, RawInputModifiers.Control, PhysicalKey.W, null);
            await UiWait.Until(() => viewModel.Files.Count == 0);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ControlDigitAndControlTabMoveBetweenViews()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await viewModel.OpenPathsCommand.Execute([path]);
            viewModel.Query = "a";
            await viewModel.AddSearchCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            var file = viewModel.SelectedFile!;

            window.KeyPress(Key.D2, RawInputModifiers.Control, PhysicalKey.Digit2, null);
            Assert.Equal(1, file.SelectedViewIndex);
            window.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
            Assert.Equal(0, file.SelectedViewIndex);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HexTail.Tests/HexTail.Tests.csproj -c Release --filter "FullyQualifiedName~ShellShortcutsTests|FullyQualifiedName~ShortcutDispatchTests"`
Expected: build FAILS: `The type or namespace name 'Input' does not exist in the namespace 'HexTail'`.

- [ ] **Step 3: Implement `ShellShortcuts`**

```csharp
// src/HexTail/Input/ShellShortcuts.cs
using Avalonia.Input;

namespace HexTail.Input;

internal enum ShellAction
{
    None,
    OpenFiles,
    FocusSearch,
    SaveSession,
    ToggleSidebar,
    CloseView,
    NextView,
    PreviousView,
    SelectView,
    ToggleFollow,
    TogglePause,
    ScrollToTop,
    ScrollToBottom,
}

internal readonly record struct ShellCommand(ShellAction Action, int Index = 0)
{
    public static readonly ShellCommand None = new(ShellAction.None);
}

internal static class ShellShortcuts
{
    public static ShellCommand Resolve(Key key, KeyModifiers modifiers, bool textInputFocused)
    {
        var primary = (modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        var shift = (modifiers & KeyModifiers.Shift) != 0;
        var alt = (modifiers & KeyModifiers.Alt) != 0;
        if (alt)
            return ShellCommand.None;
        if (primary)
            return key switch
            {
                Key.O => new(ShellAction.OpenFiles),
                Key.F => new(ShellAction.FocusSearch),
                Key.S => new(ShellAction.SaveSession),
                Key.B => new(ShellAction.ToggleSidebar),
                Key.W => new(ShellAction.CloseView),
                Key.Tab => new(shift ? ShellAction.PreviousView : ShellAction.NextView),
                >= Key.D1 and <= Key.D9 => new(ShellAction.SelectView, key - Key.D1),
                _ => ShellCommand.None,
            };
        if (textInputFocused)
            return ShellCommand.None;
        return key switch
        {
            Key.OemQuestion or Key.Divide when !shift => new(ShellAction.FocusSearch),
            Key.F when !shift => new(ShellAction.ToggleFollow),
            Key.Space => new(ShellAction.TogglePause),
            Key.G => new(shift ? ShellAction.ScrollToBottom : ShellAction.ScrollToTop),
            _ => ShellCommand.None,
        };
    }
}
```

- [ ] **Step 4: Dispatch in `MainWindow.OnKeyDown`**

Add `using HexTail.Input;`. Replace the whole body of `OnKeyDown` after `base.OnKeyDown(e);` with:

```csharp
        if (e.Key == Key.Escape && ViewModel.SettingsOpen)
        {
            ViewModel.SettingsOpen = false;
            e.Handled = true;
            return;
        }
        if (ViewModel.SettingsOpen || e.Handled)
            return;

        var command = ShellShortcuts.Resolve(
            e.Key,
            e.KeyModifiers,
            FocusManager?.GetFocusedElement() is TextBox
        );
        if (command.Action == ShellAction.None)
            return;
        Execute(command);
        e.Handled = true;
    }

    private void Execute(ShellCommand command)
    {
        var file = ViewModel.SelectedFile;
        switch (command.Action)
        {
            case ShellAction.OpenFiles:
                Dispatcher.UIThread.Post(() => ViewModel.OpenCommand.Execute().Subscribe());
                break;
            case ShellAction.FocusSearch:
                this.GetVisualDescendants().OfType<SearchBar>().FirstOrDefault()?.FocusQuery();
                break;
            case ShellAction.SaveSession:
                ViewModel.SaveCommand.Execute().Subscribe();
                break;
            case ShellAction.ToggleSidebar:
                ViewModel.ToggleSidebarCommand.Execute().Subscribe();
                break;
            case ShellAction.TogglePause:
                ViewModel.TogglePauseCommand.Execute().Subscribe();
                break;
            case ShellAction.CloseView when file is not null:
                if (file.SelectedView.Search is not null)
                    ViewModel.RemoveSearchCommand.Execute(file.SelectedView).Subscribe();
                else
                    ViewModel.CloseFileCommand.Execute(file).Subscribe();
                break;
            case ShellAction.NextView or ShellAction.PreviousView when file is not null:
                var step = command.Action == ShellAction.NextView ? 1 : -1;
                file.SelectedViewIndex = (file.SelectedViewIndex + step + file.Views.Count) % file.Views.Count;
                break;
            case ShellAction.SelectView when file is not null && command.Index < file.Views.Count:
                file.SelectedViewIndex = command.Index;
                break;
            case ShellAction.ToggleFollow when file is not null:
                file.SelectedView.IsFollowing = !file.SelectedView.IsFollowing;
                break;
            case ShellAction.ScrollToBottom when file is not null:
                file.SelectedView.IsFollowing = true;
                break;
            case ShellAction.ScrollToTop:
                this.GetVisualDescendants().OfType<LogView>().FirstOrDefault(view => view.IsEffectivelyVisible)?.ScrollToTop();
                break;
        }
    }
```

Remove the old `Key.B` case added in Task 3; it is now covered by the `ShellShortcuts` mapping.

Avalonia's keyboard focus navigation consumes Tab before the bubbling `OnKeyDown` runs, so Ctrl+Tab gets its own tunnel handler. Add this to the constructor:

```csharp
        AddHandler(
            KeyDownEvent,
            (_, e) =>
            {
                if (e.Key != Key.Tab || ViewModel.SettingsOpen)
                    return;
                var command = ShellShortcuts.Resolve(e.Key, e.KeyModifiers, textInputFocused: false);
                if (command.Action is not (ShellAction.NextView or ShellAction.PreviousView))
                    return;
                Execute(command);
                e.Handled = true;
            },
            RoutingStrategies.Tunnel
        );
```

(It needs `using Avalonia.Interactivity;`.)

- [ ] **Step 5: `LogView.ScrollToTop`**

```csharp
    internal void ScrollToTop()
    {
        if (_viewModel is null)
            return;
        _viewModel.IsFollowing = false;
        if (LogList.ItemCount > 0)
            LogList.ScrollIntoView(0);
    }
```

- [ ] **Step 6: Run the full suite**

Run: `dotnet test src/HexTail.Tests/HexTail.Tests.csproj -c Release`
Expected: PASS, including the existing `OpenShortcutAcceptsControlOrMeta`, `FindShortcutAcceptsControlOrMeta`, `SaveShortcutAcceptsControlOrMeta`, and `WorkspaceFindShortcutCannotStealFocusFromSettings`.

- [ ] **Step 7: Commit**

```bash
git add -A src/HexTail src/HexTail.Tests
git commit -m "feat(keys): add shell shortcuts for views, follow, pause, and sidebar"
```

---

### Task 8: Command palette (Ctrl+K)

**Files:**
- Create: `src/HexTail/ViewModels/CommandPaletteViewModel.cs`
- Create: `src/HexTail/Views/CommandPalette.axaml`, `src/HexTail/Views/CommandPalette.axaml.cs`
- Modify: `src/HexTail/Input/ShellShortcuts.cs` (`CommandPalette` action)
- Modify: `src/HexTail/MainWindow.axaml(.cs)`
- Modify: `src/HexTail/ViewModels/MainWindowViewModel.cs` (`Palette`, `OpenPaletteCommand`, `FocusSearchRequested`, `BuildPaletteItems`)
- Modify: `src/HexTail/Styles/CyberTail.axaml`
- Test: `src/HexTail.Tests/ViewModels/CommandPaletteViewModelTests.cs`, `src/HexTail.Tests/Ui/CommandPaletteTests.cs`; extend `src/HexTail.Tests/Input/ShellShortcutsTests.cs`

**Interfaces:**
- Consumes: commands from Tasks 3, 5, 6, 7; `ElasticSourceOptionViewModel.ActivateCommand`.
- Produces:
  - `internal sealed record PaletteItem(string Title, string Category, string? Gesture, Func<Task> Execute)`
  - `CommandPaletteViewModel(Func<IEnumerable<PaletteItem>> source)` with `Query`, `Results`, `SelectedIndex`, `IsOpen`, `Open()`, `Close()`, `Move(int)`, `ExecuteSelectedAsync()`, `static int Score(string title, string query)`
  - `MainWindowViewModel.Palette`, `OpenPaletteCommand`, `event EventHandler? FocusSearchRequested`, `IEnumerable<PaletteItem> BuildPaletteItems()` (internal)
  - `ShellAction.CommandPalette`; XAML names `CommandPaletteButton`, `PaletteQuery`, `PaletteResults`, `PaletteBackdrop`

- [ ] **Step 1: Write the failing tests**

```csharp
// src/HexTail.Tests/ViewModels/CommandPaletteViewModelTests.cs
using HexTail.ViewModels;

namespace HexTail.Tests.ViewModels;

public sealed class CommandPaletteViewModelTests
{
    [Theory]
    [InlineData("api.log", "", 1)]
    [InlineData("api.log", "api", 300)]
    [InlineData("Toggle sidebar", "side", 200)]
    [InlineData("worker-api.log", "api", 200)]
    [InlineData("checkout", "kout", 100)]
    [InlineData("Open files", "ofs", 50)]
    [InlineData("Open files", "zzz", 0)]
    public void ScoreRanksPrefixThenWordThenContainsThenSubsequence(string title, string query, int expected) =>
        Assert.Equal(expected, CommandPaletteViewModel.Score(title, query));

    [Fact]
    public async Task OpenFiltersMovesAndExecutes()
    {
        var executed = new List<string>();
        PaletteItem Item(string title) => new(title, "Test", null, () =>
        {
            executed.Add(title);
            return Task.CompletedTask;
        });
        var palette = new CommandPaletteViewModel(() => [Item("Open files…"), Item("api.log"), Item("worker-api.log")]);

        palette.Open();
        Assert.True(palette.IsOpen);
        Assert.Equal(3, palette.Results.Count);
        palette.Query = "api";
        Assert.Equal(["api.log", "worker-api.log"], palette.Results.Select(item => item.Title));
        Assert.Equal(0, palette.SelectedIndex);
        palette.Move(1);
        Assert.Equal(1, palette.SelectedIndex);
        palette.Move(1);
        Assert.Equal(0, palette.SelectedIndex);

        await palette.ExecuteSelectedAsync();
        Assert.Equal(["api.log"], executed);
        Assert.False(palette.IsOpen);
    }
}
```

```csharp
// src/HexTail.Tests/Ui/CommandPaletteTests.cs
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Tests.Support;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class CommandPaletteTests
{
    [AvaloniaFact]
    public async Task ControlKOpensPaletteAndEnterSwitchesSource()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var api = Path.Combine(directory, "api.log");
        var worker = Path.Combine(directory, "worker.log");
        File.WriteAllText(api, "");
        File.WriteAllText(worker, "");
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await viewModel.OpenPathsCommand.Execute([api, worker]);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("worker.log", viewModel.SelectedFile!.DisplayName);

            window.KeyPress(Key.K, RawInputModifiers.Control, PhysicalKey.K, null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(viewModel.Palette.IsOpen);
            var query = window.GetVisualDescendants().OfType<CommandPalette>().Single().FindControl<TextBox>("PaletteQuery")!;
            Assert.True(query.IsFocused);

            query.Text = "api.log";
            Dispatcher.UIThread.RunJobs();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await UiWait.Until(() => viewModel.SelectedFile?.DisplayName == "api.log");
            Assert.False(viewModel.Palette.IsOpen);

            window.KeyPress(Key.K, RawInputModifiers.Control, PhysicalKey.K, null);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False(viewModel.Palette.IsOpen);
            window.Close();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
```

Add these cases to `ShellShortcutsTests.PrimaryShortcutsWorkEvenInTextBoxes`:

```csharp
    [InlineData(Key.K, KeyModifiers.Control, ShellAction.CommandPalette)]
    [InlineData(Key.P, KeyModifiers.Control | KeyModifiers.Shift, ShellAction.CommandPalette)]
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HexTail.Tests/HexTail.Tests.csproj -c Release --filter "FullyQualifiedName~CommandPalette|FullyQualifiedName~ShellShortcutsTests"`
Expected: build FAILS: `The type or namespace name 'CommandPaletteViewModel' could not be found`.

- [ ] **Step 3: `CommandPaletteViewModel`**

```csharp
// src/HexTail/ViewModels/CommandPaletteViewModel.cs
using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal sealed record PaletteItem(string Title, string Category, string? Gesture, Func<Task> Execute);

internal sealed class CommandPaletteViewModel(Func<IEnumerable<PaletteItem>> source) : ReactiveObject
{
    private static readonly char[] WordSeparators = [' ', '/', '·', ':', '.', '-', '_'];
    private string _query = string.Empty;
    private bool _isOpen;
    private int _selectedIndex = -1;

    public ObservableCollection<PaletteItem> Results { get; } = [];

    public string Query
    {
        get => _query;
        set
        {
            if (_query == value)
                return;
            this.RaiseAndSetIfChanged(ref _query, value);
            Refresh();
        }
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set => this.RaiseAndSetIfChanged(ref _isOpen, value);
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedIndex, value);
    }

    public void Open()
    {
        _query = string.Empty;
        this.RaisePropertyChanged(nameof(Query));
        Refresh();
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    public void Move(int delta) =>
        SelectedIndex = Results.Count == 0 ? -1 : (SelectedIndex + delta + Results.Count) % Results.Count;

    public async Task ExecuteSelectedAsync()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Results.Count)
            return;
        var item = Results[SelectedIndex];
        Close();
        await item.Execute();
    }

    internal static int Score(string title, string query)
    {
        if (query.Length == 0)
            return 1;
        if (title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 300;
        var words = title.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(word => word.StartsWith(query, StringComparison.OrdinalIgnoreCase)))
            return 200;
        if (title.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 100;
        var position = 0;
        foreach (var character in title)
            if (position < query.Length && char.ToLowerInvariant(character) == char.ToLowerInvariant(query[position]))
                position++;
        return position == query.Length ? 50 : 0;
    }

    private void Refresh()
    {
        var ranked = source()
            .Select(item => (Item: item, Score: Score(item.Title, Query)))
            .Where(entry => entry.Score > 0)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .Select(entry => entry.Item)
            .ToArray();
        Results.Clear();
        foreach (var item in ranked)
            Results.Add(item);
        SelectedIndex = Results.Count > 0 ? 0 : -1;
    }
}
```

- [ ] **Step 4: Palette items on `MainWindowViewModel`**

```csharp
    public CommandPaletteViewModel Palette { get; }
    public ReactiveCommand<Unit, Unit> OpenPaletteCommand { get; }
    internal event EventHandler? FocusSearchRequested;

    // constructor:
        Palette = new CommandPaletteViewModel(BuildPaletteItems);
        OpenPaletteCommand = ReactiveCommand.Create(Palette.Open, _scheduler);

    internal IEnumerable<PaletteItem> BuildPaletteItems()
    {
        yield return new("Open files…", "File", "Ctrl+O", async () => await OpenCommand.Execute());
        yield return new("Set up Elastic", "Elastic", null, async () => await SetUpElasticCommand.Execute());
        yield return new("Settings", "App", null, async () => await ToggleSettingsCommand.Execute());
        yield return new("Toggle sidebar", "View", "Ctrl+B", async () => await ToggleSidebarCommand.Execute());
        yield return new(
            IsPaused ? "Resume view updates" : "Pause view updates",
            "View",
            "Space",
            async () => await TogglePauseCommand.Execute()
        );
        if (SelectedFile is { } file)
        {
            yield return new("Focus search", "Search", "/", () =>
            {
                FocusSearchRequested?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            });
            yield return new("Follow newest lines", "View", "F", () =>
            {
                file.SelectedView.IsFollowing = true;
                return Task.CompletedTask;
            });
            foreach (var view in file.Views)
                yield return new($"View: {view.Header}", "View", null, () =>
                {
                    file.SelectedViewIndex = file.Views.IndexOf(view);
                    return Task.CompletedTask;
                });
        }
        if (IsElasticSelected)
            foreach (var (label, expression) in new[]
            {
                ("last 15 min", "now-15m"),
                ("last hour", "now-1h"),
                ("last 24 hours", "now-24h"),
            })
                yield return new($"Time range: {label}", "Elastic", null, async () => await ApplyRangePresetCommand.Execute(expression));
        foreach (var tab in Files)
            yield return new(tab.DisplayName, "Open source", null, () =>
            {
                SelectFile(tab);
                return Task.CompletedTask;
            });
        foreach (var option in ElasticSources.Where(option => !option.IsOpen))
            yield return new(option.DisplayName, "Elastic source", null, async () => await option.ActivateCommand.Execute());
    }
```

Add `using System.Reactive.Linq;` if `await command.Execute()` does not compile. The file already imports it.

- [ ] **Step 5: Shortcut mapping**

In `ShellShortcuts`, add `CommandPalette` to `ShellAction`. In the `primary` switch, add these two arms before `Key.O`:

```csharp
                Key.K => new(ShellAction.CommandPalette),
                Key.P when shift => new(ShellAction.CommandPalette),
```

In `MainWindow.Execute`, add:

```csharp
            case ShellAction.CommandPalette:
                ViewModel.OpenPaletteCommand.Execute().Subscribe();
                break;
```

In `OnKeyDown`, change `if (ViewModel.SettingsOpen || e.Handled)` to `if (ViewModel.SettingsOpen || ViewModel.Palette.IsOpen || e.Handled)`. In the constructor, add:

```csharp
        ViewModel.FocusSearchRequested += (_, _) =>
            Dispatcher.UIThread.Post(() => this.GetVisualDescendants().OfType<SearchBar>().FirstOrDefault()?.FocusQuery());
```

- [ ] **Step 6: Palette view**

```xml
<!-- src/HexTail/Views/CommandPalette.axaml -->
<UserControl
  xmlns="https://github.com/avaloniaui"
  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
  xmlns:vm="using:HexTail.ViewModels"
  x:Class="HexTail.Views.CommandPalette"
  x:DataType="vm:CommandPaletteViewModel"
  IsVisible="{Binding IsOpen}"
>
  <Panel>
    <Border x:Name="PaletteBackdrop" Background="#99000000" PointerPressed="OnBackdropPressed" />
    <Border
      Classes="palette"
      Width="560"
      MaxHeight="420"
      HorizontalAlignment="Center"
      VerticalAlignment="Top"
      Margin="16,64,16,0"
    >
      <DockPanel>
        <TextBox
          x:Name="PaletteQuery"
          DockPanel.Dock="Top"
          Text="{Binding Query, Mode=TwoWay}"
          PlaceholderText="Type a command or source name"
          AutomationProperties.Name="Command palette search"
          Margin="0,0,0,8"
        />
        <ListBox
          x:Name="PaletteResults"
          ItemsSource="{Binding Results}"
          SelectedIndex="{Binding SelectedIndex, Mode=TwoWay}"
          DoubleTapped="OnResultActivated"
          AutomationProperties.Name="Commands"
        >
          <ListBox.ItemTemplate>
            <DataTemplate x:DataType="vm:PaletteItem">
              <Grid ColumnDefinitions="*,Auto,Auto" ColumnSpacing="12">
                <TextBlock Text="{Binding Title}" TextTrimming="CharacterEllipsis" />
                <TextBlock Grid.Column="1" Text="{Binding Category}" Classes="status" />
                <TextBlock Grid.Column="2" Text="{Binding Gesture}" Classes="source-count" />
              </Grid>
            </DataTemplate>
          </ListBox.ItemTemplate>
        </ListBox>
      </DockPanel>
    </Border>
  </Panel>
</UserControl>
```

```csharp
// src/HexTail/Views/CommandPalette.axaml.cs
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HexTail.ViewModels;

namespace HexTail.Views;

public partial class CommandPalette : UserControl
{
    private CommandPaletteViewModel? _viewModel;
    private IInputElement? _opener;

    public CommandPalette()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPaletteKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
                _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel = DataContext as CommandPaletteViewModel;
            if (_viewModel is not null)
                _viewModel.PropertyChanged += OnViewModelChanged;
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CommandPaletteViewModel.IsOpen) || _viewModel is null)
            return;
        var focus = TopLevel.GetTopLevel(this)?.FocusManager;
        if (_viewModel.IsOpen)
        {
            _opener = focus?.GetFocusedElement();
            Dispatcher.UIThread.Post(() => PaletteQuery.Focus());
        }
        else if (_opener is Control { IsEffectivelyVisible: true } opener)
            Dispatcher.UIThread.Post(() => opener.Focus());
    }

    private void OnPaletteKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is not { IsOpen: true })
            return;
        switch (e.Key)
        {
            case Key.Escape:
                _viewModel.Close();
                break;
            case Key.Down:
                _viewModel.Move(1);
                break;
            case Key.Up:
                _viewModel.Move(-1);
                break;
            case Key.Enter:
                _ = _viewModel.ExecuteSelectedAsync();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e) => _viewModel?.Close();

    private void OnResultActivated(object? sender, TappedEventArgs e) =>
        _ = _viewModel?.ExecuteSelectedAsync();
}
```

`MainWindow.axaml`:
- Add `<views:CommandPalette Grid.RowSpan="3" DataContext="{Binding Palette}" />` as the last child of `ShellGrid`.
- Add a palette button in the top bar. The grid becomes `ColumnDefinitions="Auto,Auto,Auto,*,Auto,Auto"`: `ElasticTimeRangePanel` → column 3, `PauseToggle` → 4, `SettingsButton` → 5.

```xml
          <Button
            x:Name="CommandPaletteButton"
            Grid.Column="2"
            Command="{Binding OpenPaletteCommand}"
            ToolTip.Tip="Commands and sources (Ctrl+K)"
            AutomationProperties.Name="Open command palette"
          >
            <StackPanel Orientation="Horizontal" Spacing="8">
              <i:Icon Value="mdi-magnify" />
              <TextBlock Text="Ctrl+K" Classes="source-count" />
            </StackPanel>
          </Button>
```

Style:

```xml
  <Style Selector="Border.palette">
    <Setter Property="Background" Value="{DynamicResource RaisedBrush}" />
    <Setter Property="BorderBrush" Value="{DynamicResource BorderStrongBrush}" />
    <Setter Property="BorderThickness" Value="1" />
    <Setter Property="CornerRadius" Value="8" />
    <Setter Property="Padding" Value="12" />
  </Style>
```

Add `"CommandPaletteButton"` to the fit-loop name list in `ElasticTimeRangePanelIsVisibleForAnOpenElasticTab`.

- [ ] **Step 7: Run the full suite**

Run: `dotnet test src/HexTail.Tests/HexTail.Tests.csproj -c Release`
Expected: PASS. `AccessibilityTests.SettingsFocusEntersCyclesAndReturnsToItsOpener` must still pass, because the palette is invisible by default.

- [ ] **Step 8: Commit**

```bash
git add -A src/HexTail src/HexTail.Tests
git commit -m "feat(palette): add Ctrl+K command palette for commands, views, and sources"
```

---

### Task 9: Empty state with recents and a sample log

**Files:**
- Create: `src/HexTail/Application/SampleLogWriter.cs`
- Create: `src/HexTail/ViewModels/RecentFileViewModel.cs`
- Modify: `src/HexTail/Persistence/AppConfig.cs` (`RecentFiles`, `RecentElasticSourceIds`)
- Modify: `src/HexTail/Application/AppState.cs` (`OpenFileAsync`, `OpenElasticSourceAsync`, `RestoreAsync`, `SaveAsync`)
- Modify: `src/HexTail/ViewModels/MainWindowViewModel.cs`
- Modify: `src/HexTail/Views/LogWorkspace.axaml` (empty-state `StackPanel`)
- Test: `src/HexTail.Tests/Application/RecentSourcesTests.cs`, `src/HexTail.Tests/Application/SampleLogWriterTests.cs`; extend `src/HexTail.Tests/Ui/OnboardingTests.cs`

**Interfaces:**
- Consumes: `AppState.OpenFileAsync(path, save, cancellationToken)`, `OpenElasticSourceAsync`, `ElasticSourceOptionViewModel.ActivateCommand`.
- Produces:
  - `AppConfig.RecentFiles : List<string>`, `AppConfig.RecentElasticSourceIds : List<string>`
  - `AppState.RecentFiles : IReadOnlyList<string>`, `AppState.RecentElasticSourceIds : IReadOnlyList<string>`, `const int MaxRecent = 10`
  - `AppState.OpenFileAsync(string path, bool save = true, CancellationToken cancellationToken = default, bool remember = true)`
  - `SampleLogWriter.Start(string path, TimeSpan interval, int backlog = 200)`, `SampleLogWriter.Path`, `SampleLogWriter.NextLine(DateTimeOffset)` (internal), `DisposeAsync()`
  - `MainWindowViewModel.RecentFiles : ObservableCollection<RecentFileViewModel>`, `RecentElasticSources : ObservableCollection<ElasticSourceOptionViewModel>`, `HasRecents`, `OpenRecentCommand : ReactiveCommand<string, Unit>`, `OpenSampleCommand`
  - XAML names `SampleLogButton`, `RecentFilesList`, `RecentElasticList`

- [ ] **Step 1: Write the failing tests**

```csharp
// src/HexTail.Tests/Application/RecentSourcesTests.cs
using HexTail.Application;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;

namespace HexTail.Tests.Application;

public sealed class RecentSourcesTests
{
    [Fact]
    public async Task OpeningFilesRecordsMostRecentFirstWithoutDuplicates()
    {
        var paths = Enumerable.Range(0, 3).Select(_ => Path.GetTempFileName()).ToArray();
        var persistence = new TestPersistence();
        try
        {
            await using (var state = new AppState(new LogSourceService(), persistence))
            {
                foreach (var path in paths)
                    await state.OpenFileAsync(path);
                await state.OpenFileAsync(paths[0]);
                Assert.Equal([paths[0], paths[2], paths[1]], state.RecentFiles);
            }
            Assert.Equal([paths[0], paths[2], paths[1]], persistence.Config!.RecentFiles);
        }
        finally
        {
            foreach (var path in paths)
                File.Delete(path);
        }
    }

    [Fact]
    public async Task RecentListIsCappedAndRestoreDoesNotReorderIt()
    {
        var paths = Enumerable.Range(0, 12).Select(_ => Path.GetTempFileName()).ToArray();
        try
        {
            var saved = new AppConfig
            {
                RecentFiles = [.. paths.Take(10)],
                OpenFiles = [new PersistedFileTab { Path = paths[5] }],
            };
            await using var state = new AppState(new LogSourceService(), new TestPersistence(saved));
            await state.RestoreAsync();
            Assert.Equal(paths.Take(10), state.RecentFiles);

            await state.OpenFileAsync(paths[11]);
            Assert.Equal(AppState.MaxRecent, state.RecentFiles.Count);
            Assert.Equal(paths[11], state.RecentFiles[0]);

            await state.OpenFileAsync(paths[10], remember: false);
            Assert.DoesNotContain(paths[10], state.RecentFiles);
        }
        finally
        {
            foreach (var path in paths)
                File.Delete(path);
        }
    }
}
```

```csharp
// src/HexTail.Tests/Application/SampleLogWriterTests.cs
using HexTail.Application;

namespace HexTail.Tests.Application;

public sealed class SampleLogWriterTests
{
    [Fact]
    public async Task WritesBacklogThenAppendsTimestampedLeveledLines()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log");
        try
        {
            await using (var writer = SampleLogWriter.Start(path, TimeSpan.FromMilliseconds(20), backlog: 50))
            {
                Assert.Equal(path, writer.Path);
                Assert.Equal(50, File.ReadAllLines(path).Length);
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (File.ReadAllLines(path).Length < 52 && DateTime.UtcNow < deadline)
                    await Task.Delay(20);
            }
            var lines = File.ReadAllLines(path);
            Assert.True(lines.Length >= 52);
            Assert.All(
                lines,
                line => Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z (DEBUG|INFO |WARN |ERROR) \S", line)
            );
        }
        finally
        {
            File.Delete(path);
        }
    }
}
```

Add to `OnboardingTests`:

```csharp
    [AvaloniaFact]
    public async Task EmptyStateListsRecentFilesAndOpensTheSample()
    {
        var recent = Path.GetTempFileName();
        try
        {
            var persistence = new TestPersistence(new AppConfig { RecentFiles = [recent] });
            var window = TestWindow.Create(persistence, out var viewModel);
            window.Show();
            await UiWait.Until(() => viewModel.RecentFiles.Count == 1);
            Assert.True(viewModel.HasRecents);
            Assert.Equal(Path.GetFileName(recent), viewModel.RecentFiles[0].Name);

            await viewModel.OpenRecentCommand.Execute(recent);
            await UiWait.Until(() => viewModel.SelectedFile?.Path == recent);
            await viewModel.CloseFileCommand.Execute(viewModel.SelectedFile!);

            await viewModel.OpenSampleCommand.Execute();
            await UiWait.Until(() => viewModel.SelectedFile?.DisplayName == "hextail-sample.log");
            Assert.DoesNotContain(viewModel.State.RecentFiles, path => path.EndsWith("hextail-sample.log"));
            window.Close();
        }
        finally
        {
            File.Delete(recent);
        }
    }
```

(It needs `using HexTail.Persistence;` and `using HexTail.Tests.Support;` in `OnboardingTests`.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HexTail.Tests/HexTail.Tests.csproj -c Release --filter "FullyQualifiedName~RecentSourcesTests|FullyQualifiedName~SampleLogWriterTests|FullyQualifiedName~OnboardingTests"`
Expected: build FAILS: `'AppConfig' does not contain a definition for 'RecentFiles'`.

- [ ] **Step 3: Persistence and `AppState` recents**

`AppConfig`:

```csharp
    public List<string> RecentFiles { get; init; } = [];
    public List<string> RecentElasticSourceIds { get; init; } = [];
```

`AppState`:

```csharp
    public const int MaxRecent = 10;
    private readonly List<string> _recentFiles = [];
    private readonly List<string> _recentElasticSourceIds = [];

    public IReadOnlyList<string> RecentFiles
    {
        get { lock (_gate) return _recentFiles.ToArray(); }
    }

    public IReadOnlyList<string> RecentElasticSourceIds
    {
        get { lock (_gate) return _recentElasticSourceIds.ToArray(); }
    }

    private static void Remember(List<string> list, string value, StringComparer comparer)
    {
        list.RemoveAll(item => comparer.Equals(item, value));
        list.Insert(0, value);
        if (list.Count > MaxRecent)
            list.RemoveRange(MaxRecent, list.Count - MaxRecent);
    }
```

- `OpenFileAsync`: add the trailing parameter `bool remember = true`. Inside the existing `lock (_gate)` block, after either branch, add `if (remember) Remember(_recentFiles, fullPath, StringComparer.OrdinalIgnoreCase);`.
- `OpenElasticSourceAsync`: add `bool remember = true` as the last parameter. In both `lock (_gate)` blocks where `SelectedFile` is assigned, add `if (remember) Remember(_recentElasticSourceIds, sourceId, StringComparer.Ordinal);`.
- `RestoreAsync`:
  - Pass `remember: false` to both reopen calls.
  - Before the final `lock (_gate)` that picks `SelectedFile`, add:

    ```csharp
        lock (_gate)
        {
            _recentFiles.Clear();
            _recentFiles.AddRange(config.RecentFiles.Take(MaxRecent));
            _recentElasticSourceIds.Clear();
            _recentElasticSourceIds.AddRange(config.RecentElasticSourceIds.Take(MaxRecent));
        }
    ```

- `SaveAsync`: add `RecentFiles = [.. _recentFiles], RecentElasticSourceIds = [.. _recentElasticSourceIds],` to the `new AppConfig { … }` initializer.

- [ ] **Step 4: `SampleLogWriter`**

```csharp
// src/HexTail/Application/SampleLogWriter.cs
using System.Globalization;

namespace HexTail.Application;

/// <summary>Writes a small demo log and keeps appending to it so new users can watch live tailing.</summary>
internal sealed class SampleLogWriter : IAsyncDisposable
{
    private static readonly (string Level, string Message)[] Events =
    [
        ("INFO", "api request method=GET path=/orders status=200 duration_ms={0}"),
        ("INFO", "api request method=POST path=/orders status=201 duration_ms={0}"),
        ("INFO", "worker job=charge order_id={0} attempt=1"),
        ("INFO", "session resumed user=u-{0}"),
        ("INFO", "healthcheck GET /healthz status=200"),
        ("DEBUG", "cache lookup key=order:{0} hit=true"),
        ("DEBUG", "queue depth={0} lag_ms=12"),
        ("WARN", "upstream retry route=/payments attempt=2 order_id={0}"),
        ("WARN", "slow query table=orders duration_ms={0}"),
        ("ERROR", "payment gateway timeout after 5000ms order_id={0}"),
    ];

    private readonly Random _random = new(1337);
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;

    private SampleLogWriter(string path) => Path = path;

    public string Path { get; }

    public static SampleLogWriter Start(string path, TimeSpan interval, int backlog = 200)
    {
        var writer = new SampleLogWriter(path);
        var start = DateTimeOffset.UtcNow - interval * backlog;
        File.WriteAllLines(path, Enumerable.Range(0, backlog).Select(index => writer.NextLine(start + interval * index)));
        writer._loop = writer.RunAsync(interval);
        return writer;
    }

    internal string NextLine(DateTimeOffset timestamp)
    {
        var (level, message) = Events[_random.Next(Events.Length)];
        var text = string.Format(CultureInfo.InvariantCulture, message, _random.Next(1, 9999));
        return $"{timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture)}Z {level,-5} {text}";
    }

    private async Task RunAsync(TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                await File.AppendAllTextAsync(Path, NextLine(DateTimeOffset.UtcNow) + Environment.NewLine, _stop.Token)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }
}
```

- [ ] **Step 5: View-model recents and sample**

```csharp
// src/HexTail/ViewModels/RecentFileViewModel.cs
namespace HexTail.ViewModels;

internal sealed record RecentFileViewModel(string Path, bool Exists)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
    public string ToolTip => Exists ? Path : $"{Path} (missing)";
}
```

In `MainWindowViewModel`:

```csharp
    private SampleLogWriter? _sample;
    private string _recentKey = string.Empty;

    public ObservableCollection<RecentFileViewModel> RecentFiles { get; } = [];
    public ObservableCollection<ElasticSourceOptionViewModel> RecentElasticSources { get; } = [];
    public bool HasRecents => RecentFiles.Count > 0 || RecentElasticSources.Count > 0;
    public ReactiveCommand<string, Unit> OpenRecentCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenSampleCommand { get; }

    // constructor:
        OpenRecentCommand = ReactiveCommand.CreateFromTask<string>(path => OpenPathsAsync([path]), _scheduler);
        OpenSampleCommand = ReactiveCommand.CreateFromTask(OpenSampleAsync, _scheduler);
    // and add OpenRecentCommand.ThrownExceptions, OpenSampleCommand.ThrownExceptions to the existing Observable.Merge(...)

    private async Task OpenSampleAsync()
    {
        _sample ??= SampleLogWriter.Start(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hextail-sample.log"),
            TimeSpan.FromMilliseconds(500)
        );
        await _state.OpenFileAsync(_sample.Path, remember: false);
    }

    private void SyncRecents()
    {
        var files = _state.RecentFiles;
        var sources = _state.RecentElasticSourceIds;
        var key = $"{string.Join('\n', files)}\0{string.Join('\n', sources)}\0{ElasticSources.Count}";
        if (key == _recentKey)
            return;
        _recentKey = key;
        RecentFiles.Clear();
        foreach (var path in files)
            RecentFiles.Add(new RecentFileViewModel(path, File.Exists(path)));
        RecentElasticSources.Clear();
        foreach (var id in sources)
            if (ElasticSources.FirstOrDefault(option => option.SourceId == id) is { } option)
                RecentElasticSources.Add(option);
        this.RaisePropertyChanged(nameof(HasRecents));
    }
```

- In `SyncFromState`, after `SyncConnectionGroups();`, add `SyncRecents();`. Then stop the sample writer if its tab was closed:

  ```csharp
        if (_sample is { } sample && !_state.Files.Any(file => string.Equals(file.Path, sample.Path, StringComparison.OrdinalIgnoreCase)))
        {
            _sample = null;
            _ = sample.DisposeAsync().AsTask();
        }
  ```

- In `DisposeAsync`, before `await _state.DisposeAsync();`, add `if (_sample is not null) await _sample.DisposeAsync();`.

- [ ] **Step 6: Empty-state UI**

In `LogWorkspace.axaml`, replace the `IsVisible="{Binding ShowEmpty}"` `StackPanel` with:

```xml
    <ScrollViewer IsVisible="{Binding ShowEmpty}" HorizontalScrollBarVisibility="Disabled">
      <StackPanel HorizontalAlignment="Center" VerticalAlignment="Center" Margin="24" Spacing="16" MaxWidth="720">
        <TextBlock
          Text="Drop log files here, or"
          TextAlignment="Center"
          FontSize="16"
          Foreground="{DynamicResource TextBrush}"
        />
        <WrapPanel HorizontalAlignment="Center">
          <Button x:Name="OpenEmptyFileButton" Content="Open file…" Command="{Binding OpenCommand}" Classes="primary" Margin="4" />
          <Button x:Name="SetUpElasticButton" Content="Connect to Elastic" Command="{Binding SetUpElasticCommand}" Margin="4" />
          <Button x:Name="SampleLogButton" Content="Try a sample log" Command="{Binding OpenSampleCommand}" Margin="4" />
        </WrapPanel>
        <TextBlock Text="Press Ctrl+K for commands" Classes="hint" TextAlignment="Center" />
        <Grid ColumnDefinitions="*,*" ColumnSpacing="24" IsVisible="{Binding HasRecents}">
          <StackPanel Spacing="2">
            <TextBlock Text="RECENT FILES" Classes="sidebar-heading" />
            <ItemsControl x:Name="RecentFilesList" ItemsSource="{Binding RecentFiles}">
              <ItemsControl.ItemTemplate>
                <DataTemplate x:DataType="vm:RecentFileViewModel">
                  <Button
                    Classes="source-select"
                    Command="{Binding $parent[UserControl].DataContext.OpenRecentCommand}"
                    CommandParameter="{Binding Path}"
                    IsEnabled="{Binding Exists}"
                    ToolTip.Tip="{Binding ToolTip}"
                    AutomationProperties.Name="{Binding Path, StringFormat='Open recent {0}'}"
                  >
                    <StackPanel>
                      <TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis" />
                      <TextBlock Text="{Binding Folder}" Classes="hint" TextTrimming="CharacterEllipsis" TextWrapping="NoWrap" />
                    </StackPanel>
                  </Button>
                </DataTemplate>
              </ItemsControl.ItemTemplate>
            </ItemsControl>
          </StackPanel>
          <StackPanel Grid.Column="1" Spacing="2">
            <TextBlock Text="RECENT ELASTIC SOURCES" Classes="sidebar-heading" />
            <ItemsControl x:Name="RecentElasticList" ItemsSource="{Binding RecentElasticSources}">
              <ItemsControl.ItemTemplate>
                <DataTemplate x:DataType="vm:ElasticSourceOptionViewModel">
                  <Button
                    Classes="source-select"
                    Command="{Binding ActivateCommand}"
                    ToolTip.Tip="{Binding ToolTip}"
                    AutomationProperties.Name="{Binding DisplayName, StringFormat='Open recent {0}'}"
                  >
                    <TextBlock Text="{Binding DisplayName}" TextTrimming="CharacterEllipsis" />
                  </Button>
                </DataTemplate>
              </ItemsControl.ItemTemplate>
            </ItemsControl>
          </StackPanel>
        </Grid>
      </StackPanel>
    </ScrollViewer>
```

Check `OnboardingTests.EmptyWorkspaceOffersFileAndElasticSetupAtMinimumWidth` for asserted copy, such as "Set up Elastic" or the old prompt text. If it asserts that text, update the expected strings to the new copy ("Connect to Elastic", "Drop log files here, or").

- [ ] **Step 7: Run the full suite**

Run: `dotnet test src/HexTail.Tests/HexTail.Tests.csproj -c Release`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add -A src/HexTail src/HexTail.Tests
git commit -m "feat(onboarding): show recent sources and a live sample log in the empty state"
```

---

### Task 10: Documentation and verification

**Files:**
- Modify: `README.md` (Using HexTail, Keyboard shortcuts table)
- Modify: `docs/user-guide.md` (Start tailing, Set an Elastic log interval, Inspect rows and navigate tabs)
- Create: `docs/audits/evidence/ux-phase-1/` (screenshots from the manual pass)

- [ ] **Step 1: README shortcuts**

Replace the *Keyboard shortcuts* table with:

```markdown
| Shortcut | Action |
| --- | --- |
| `Ctrl+K` / `Cmd+K` (or `Ctrl+Shift+P`) | Command palette: commands, views, sources |
| `Ctrl+O` / `Cmd+O` | Open files |
| `Ctrl+F` / `Cmd+F`, or `/` | Focus the search box |
| `Ctrl+B` / `Cmd+B` | Toggle the source sidebar |
| `Ctrl+W` / `Cmd+W` | Close the selected search view, or the source when **All** is selected |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next / previous view |
| `Ctrl+1`…`Ctrl+9` | Select a view |
| `F` | Toggle follow for the selected view |
| `Space` | Pause / resume view updates (logs keep buffering) |
| `g` / `Shift+G` | Jump to the first / newest line |
| `Alt+Up` / `Alt+Down` | Reorder the focused file in the sidebar |
| `Ctrl+S` / `Cmd+S` | Save the session now (it also saves automatically) |
| `Esc` | Close settings or the palette |
```

Update *Using HexTail* steps 1–5:
- The sidebar lists files and Elastic sources.
- Follow and Context are on each view's toolbar.
- The status bar shows counts and the display time zone.
- The ⏱ pill opens the time range.

- [ ] **Step 2: User guide**

Rewrite these sections of `docs/user-guide.md` to match the behavior you shipped:
- **Start tailing**: sidebar, empty state, sample log.
- **Set an Elastic log interval**:
  - The pill opens quick-select presets that apply immediately.
  - The From and To fields accept `now-15m`, `-2h`, `14:00`, `yesterday 09:00`, ISO, and `YYYY-MM-DD HH:mm[:ss]`.
  - **Live** keeps appending new logs, and Enter applies.
  - The status-bar zone sets how typed times without an offset are read.
- Delete the paragraph about **Appearance → Source clock time zone**.
- **Inspect rows and navigate tabs**: the sidebar replaces **Tabs**, Alt+Up/Down reorders files, there are view shortcuts, Pause, and the "new lines" pill.

- [ ] **Step 3: Full verification**

Run: `dotnet build src/HexTail.slnx -c Release && dotnet test src/HexTail.Tests/HexTail.Tests.csproj -c Release`
Expected: build succeeds with no new warnings beyond the baseline 54 analyzer warnings; all tests pass.

Run: `dotnet csharpier check src`
Expected: no unformatted files.

- [ ] **Step 4: Manual smoke pass**

In one terminal, run:

```bash
dotnet run --project src/HexTail.LogGenerator -- --output /tmp/hextail.log --interval 250 --truncate
```

In another:

```bash
dotnet run --project src/HexTail/HexTail.csproj -- /tmp/hextail.log
```

Check each item:
- At 1280 px, the sidebar is inline and drag-resizes, and Ctrl+B hides it.
- At 720 px, the sidebar is an overlay that closes after a selection.
- Follow toggles from the view toolbar and with `F`. Scrolling up shows the "↓ N new lines" pill, and clicking it resumes following.
- Space pauses: the row count stays frozen while the status bar says "Paused", and resuming catches up.
- Ctrl+K finds `hextail.log` and the commands. Esc returns focus to where it was.
- With no files open (close all), recents and **Try a sample log** work.
- If an Elastic test cluster is available, confirm that the pill shows "Last 5 min · live", that presets apply immediately, that typed input shows its resolved preview, and that an invalid entry shows an error and leaves the applied range unchanged.

Capture screenshots at 720 and 1280 px into `docs/audits/evidence/ux-phase-1/`.

- [ ] **Step 5: Commit**

```bash
git add README.md docs/user-guide.md docs/audits/evidence/ux-phase-1
git commit -m "docs: document sidebar, time pill, pause, palette, and shortcuts"
```

---

## Self-review notes (completed while writing)

- **Spec coverage.** The phase-1 items from the proposals map to tasks as follows:
  - Item 1: sidebar → T2/T3; one view bar → T4; status bar → T4; drop Save Session → T3; per-view controls → T4.
  - Item 6: pill → T5; text inputs → T1/T5; one zone → T4/T5; Pause → T6.
  - Item 8: new-lines pill → T6; palette → T8; keys → T7.
  - Item 9: recents, sample, drop prompt, Ctrl+K hint → T9.

  Deferred, with a stated reason: `n`/`N` and `j`/`k`, Saved filters and Streams in the sidebar, recent ranges, and native pickers (see Decisions).
- **Name consistency.** Task 2 defines `SidebarPaneOpen`, `SidebarOpen`, `SidebarWidth`, `ToggleSidebarCommand`, `DismissNarrowSidebar`, `FileTabs`, `ElasticConnectionGroups`, `ActivateCommand`, `CloseCommand`, `ShortName`, `IsSelected`, and `HasOpenError`. Tasks 3–9 use the same names. `ShellAction.CommandPalette` is introduced in Task 8 and nowhere earlier.
- **Existing-test impact** is listed per task. These tests reference removed or moved controls: `ElasticToolbarButtonIsHiddenWhenNoSourcesAreConfigured` (deleted in T3), `TenLongTabs…` (rewritten in T3), `FileAndSearchTabsUseTabalonia`, `SettingsOpenCloseAndEscape`, `FileTabsExpose…`, `SwitchingFiles…`, and the range test (T3/T4/T5).
