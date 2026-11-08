# UX Phase 4 part A — Timestamps and Columns Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every log line a timestamp, and show structured sources (JSONL, logfmt, Elastic) as columns — Time | Level | Message plus user-picked fields — with a Columns menu, resizable column widths, an absolute/relative time display, an error-row tint, and an inspector "Add column" action.

**Architecture:** `Line` gains `Timestamp` and `TimestampInherited`. A pure `TimestampDetector` reads a timestamp from parsed fields or a leading ISO-8601 / syslog / common-log prefix; a per-source `TimestampingParser` wraps the existing parser, picks one extraction strategy per source and lets lines without a timestamp inherit the previous one. Elastic lines take the hit's timestamp. A per-source `ColumnLayout` (on `FileTabState`, persisted as optional fields) drives a column header bar and per-row cells rendered by `LogLineViewModel`/`LogLineView`; plain-text sources and the "composed row" option keep today's single text row.

**Tech Stack:** .NET 10, Avalonia 12.1.1 (headless tests via `[AvaloniaFact]`), ReactiveUI, xUnit v3, System.Text.Json.

This is Phase 4 part A of `docs/ux/2026-11-04-ux-proposals.md` (item 4 "columns" and "time display", plus the `Line` timestamp plumbing of item 3). Part B (merged streams) and part C (timeline histogram and scrollbar markers) follow and build on this part. Terminology: "Phase 4 part A".

## Global Constraints

- Branch `audit-ui-phase-4a`, cut from `origin/main` at `67d34f6` (PR #14 merged). Never work on `main`. **Never `git push` unless the user explicitly says so.**
- Conventional Commits, one commit per task. End each commit message with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- A CSharpier pre-commit hook reformats staged C# on commit. `csharpier check` already fails on `src/HexTail/HexTail.csproj`; leave that file alone.
- Prefix shell commands with `rtk`. Explore with `sb` (`sb show <file> <Symbol>`, `sb callers <Symbol>`) before opening whole files.
- Build/test commands (run from the repo root):
  - `rtk dotnet build src/HexTail.Tests`
  - `rtk dotnet test src/HexTail.Tests --no-build --filter "FullyQualifiedName~<Name>"`
  - full suite: `rtk dotnet test src/HexTail.Tests --no-build`. Baseline at the start: **857 passing, 53 build warnings** (do not add warnings).
- Tests must never touch the OS credential vault (`InMemoryCredentialVault`; `TestWindow.Create*` already does) and never call a real Kibana/Elasticsearch (`FakeElasticApiClient`).
- UI tests use `[AvaloniaFact]` and wait through `UiWait.Until(...)`; tests that append with `file.Model.Buffer.Append([...])` follow with `file.SyncViews()`. Plain `[Fact]` tests that await ReactiveUI commands have hung the test host before; use `[AvaloniaFact]` for those.
- ReactiveUI commands keep the positional scheduler pattern (`ReactiveCommand.Create*(func, scheduler)`). Every command body catches everything except `OperationCanceledException` and shows it on a view model's `Error` (no unobserved `ThrownExceptions`).
- Reflection bindings are used in XAML (compiled bindings are off). Every interactive control gets an `AutomationProperties.Name` that contains its visible text.
- Old sessions must still load: new persisted fields are optional with defaults; `AppConfig.Version` stays 2. A session written by this build must still load in the previous build (unknown members are ignored).
- Performance: the log list is virtualized and a busy source appends thousands of lines per second. Timestamp detection runs once per line on the tailer thread (no regex backtracking blowups: anchored patterns, bounded prefix length ≤ 64 chars); cell text and spans are computed only for realized (visible) rows, as today's `Render()` does.
- Known flaky test: `AccessibilityTests.SettingsFocus…` (if it flakes again, report it, do not paper over it).

### Decisions taken where the proposal left room (veto points)

1. **Naive timestamps (no offset) are read in the display zone** (`AppSettings.TimeZoneMode`, Local or UTC) at the moment the line is parsed. The user confirmed sources use the same zone as the display, so there is no per-source zone setting. Changing the display zone later re-renders times but does not re-parse lines already loaded.
2. **One extraction strategy per source.** The first line that yields a timestamp fixes the strategy (field name or prefix pattern); later lines use only that strategy. A line where it fails inherits the previous line's timestamp and is marked `TimestampInherited` (stack traces and continuation lines stay with their entry). After 200 consecutive failures the detector resets and tries all strategies again (a rotated file with a new format). The proposal's per-source override menu is **not** built.
3. **Recognised timestamps:** parsed fields `@timestamp`, `timestamp`, `time`, `ts`, `@t`, `t`, `date`, `datetime`, `eventTime` (ISO-8601/RFC 3339 strings, or numbers as Unix seconds / milliseconds / microseconds / nanoseconds chosen by magnitude); line prefixes: ISO-8601 with `T` or space separator, optional fraction (`.` or `,`) and optional `Z`/offset, optionally wrapped in `[` `]`; classic syslog `Mmm dd HH:mm:ss` (year = current year in the display zone, minus one if that puts it more than a day in the future); common-log `[dd/Mmm/yyyy:HH:mm:ss ±zzzz]`.
4. **Column mode applies to sources whose lines carry parsed fields** (JSONL, logfmt, Elastic). Plain-text sources stay one composed text row (a Time column is not added to them in this part). Any structured source can switch back with **Composed row** in the Columns menu.
5. **Default columns:** files → `@time`, `@level`, `@message`; Elastic → `@time`, `@level`, then the view's output fields in order (Elastic's composed string is no longer the row). `@message` shows the first present of `message`, `msg`, `log`, `event`, `text`, `body` (case-insensitive), else the raw line. Pseudo-columns use the `@` prefix; every other column key is a parsed field name (dotted, as flattened today).
6. **Column state is per source**, persisted on `PersistedFileTab` / `PersistedElasticTab` as optional `Columns` (list of `{ key, width }`), `ComposedRows` (bool) and `TimeDisplay` (`Absolute` | `Relative`). Absent → defaults. Widths are pixels, clamped 40..1200; the last column always fills the remaining width.
7. **Time column format:** `HH:mm:ss.fff` in the display zone, prefixed with `MM-dd ` when the date differs from today in that zone; inherited timestamps are shown dimmed. **Relative** mode shows `+1.204 s` from the previous **visible** row (first visible row shows the absolute time); the tooltip always shows the full ISO-8601 time and the delta.
8. **Highlights in cells** use the same Search and Highlight rules, applied to each cell's own text (a match spanning two fields is not highlighted). Hide/Show-only rules still filter on the raw line.
9. **Level column** shows `TRC`/`DBG`/`INF`/`WRN`/`ERR` coloured per level; rows whose level is Error get a faint error tint in both column and composed modes.
10. **Columns menu** (a button at the right end of the header bar): one checkbox per known field (pseudo-columns first, then parsed field names seen in the last 500 lines of the source, alphabetical, capped at 100), **Composed row**, **Time: Absolute / Relative**, **Reset columns**. The inspector's field menu gains **Add column** (adds the field at the end; switches a composed structured source to columns).

### Not in part A

Per-source timestamp override, merged streams and the `source:` chip (part B), histogram and scrollbar markers (part C), column sorting, horizontal scrolling of very wide column sets (columns are clipped/trimmed), multi-select and export, a Time column for plain text.

## File Structure

| Path | Status | Responsibility |
| --- | --- | --- |
| `src/HexTail/Domain/Line.cs` | modify | add `Timestamp`, `TimestampInherited` |
| `src/HexTail/Domain/TimestampDetector.cs` | create | pure strategies: `TryDetect`, `TryExtract(strategy)` |
| `src/HexTail/Domain/TimestampingParser.cs` | create | per-source stateful wrapper around `ILogParser` |
| `src/HexTail/Domain/RuleSet.cs`, `Search.cs` | modify | `Highlights(string text)` overloads |
| `src/HexTail/Elastic/ElasticApiClient.cs` | modify | hits carry `Line.Timestamp` |
| `src/HexTail/Application/ColumnLayout.cs` | create | `ColumnSpec`, `ColumnLayout`, `TimeDisplay`, defaults, message-field pick |
| `src/HexTail/Application/FileTabState.cs`, `AppState.cs` | modify | layout state, persistence, `SetColumnLayoutAsync`, parser wrapping, known fields |
| `src/HexTail/Persistence/AppConfig.cs` | modify | optional persisted column fields |
| `src/HexTail/ViewModels/LogCellViewModel.cs` | create | one cell: text, spans, width, tooltip, classes |
| `src/HexTail/ViewModels/ColumnHeaderViewModel.cs` | create | header bar + Columns menu state |
| `src/HexTail/ViewModels/LogLineViewModel.cs`, `LogViewViewModel.cs`, `InspectorViewModel.cs`, `InspectorFieldViewModel.cs` | modify | cells, previous timestamp, error tint, Add column |
| `src/HexTail/Views/LogLineView.axaml(.cs)`, `LogView.axaml(.cs)`, `ColumnHeaderBar.axaml(.cs)`, `InspectorPanel.axaml` | modify/create | cell row template, header with resize thumbs, menu |
| `README.md`, `docs/user-guide.md`, `docs/architecture.md` | modify | document |

---

### Task 1: Timestamps on lines

**Files:**
- Modify: `src/HexTail/Domain/Line.cs`, `src/HexTail/Application/AppState.cs` (wrap the parser where file tabs are created, ~line 506 `LogParserSelector.ForPath(fullPath)`; and the ephemeral sample tab ~line 588), `src/HexTail/Elastic/ElasticApiClient.cs` (`MapHit`)
- Create: `src/HexTail/Domain/TimestampDetector.cs`, `src/HexTail/Domain/TimestampingParser.cs`
- Test: `src/HexTail.Tests/Domain/TimestampDetectorTests.cs`, `TimestampingParserTests.cs` (create); `src/HexTail.Tests/Elastic/ElasticApiClientTests.cs`

**Interfaces (produced):**

```csharp
// Line.cs
public sealed record Line(string Raw, IReadOnlyDictionary<string, string>? ParsedFields = null)
{
    public DateTimeOffset? Timestamp { get; init; }
    public bool TimestampInherited { get; init; }
}

// TimestampDetector.cs
public enum TimestampSource { Field, IsoPrefix, SyslogPrefix, CommonLogPrefix }
public readonly record struct TimestampStrategy(TimestampSource Source, string? FieldName);
public static class TimestampDetector
{
    // Tries every strategy in a fixed order: fields (in the decision-3 order), ISO prefix, common-log, syslog.
    public static bool TryDetect(Line line, TimeZoneInfo naiveZone, DateTimeOffset now,
        out TimestampStrategy strategy, out DateTimeOffset timestamp);
    public static bool TryExtract(Line line, TimestampStrategy strategy, TimeZoneInfo naiveZone,
        DateTimeOffset now, out DateTimeOffset timestamp);
}

// TimestampingParser.cs
public sealed class TimestampingParser : ILogParser
{
    public TimestampingParser(ILogParser inner, Func<TimeZoneInfo> naiveZone, Func<DateTimeOffset>? now = null);
    public ILogParser Inner { get; }
    public TimestampStrategy? Strategy { get; }   // the chosen one, for diagnostics/tests
    public Line Parse(string rawText);            // thread-safe (lock): tailers call it from a worker thread
}
```

`naiveZone` comes from `AppSettings.TimeZoneMode` (`Local` → `TimeZoneInfo.Local`, `Utc` → `TimeZoneInfo.Utc`) read through a delegate so a settings change applies to newly parsed lines.

- [ ] **Step 1: Failing tests.** `TimestampDetectorTests` (theory-driven; `now = 2026-11-07T12:00:00Z`, `naiveZone = UTC` unless stated):
  - fields: `{"@timestamp":"2026-11-07T10:11:12.345Z"}` → that instant, strategy `Field "@timestamp"`; `time` with `+02:00` offset; `ts` numeric `1762510272` (seconds), `1762510272345` (ms), `1762510272345678` (µs), `1762510272345678901` (ns); field names matched case-insensitively; a non-date value in `time` falls through to the next candidate.
  - ISO prefix: `2026-11-07T10:11:12Z msg`, `2026-11-07 10:11:12,345 INFO x`, `[2026-11-07T10:11:12.345+01:00] x`, naive `2026-11-07 10:11:12` with `naiveZone` = a fixed `+05:00` zone (use `TimeZoneInfo.CreateCustomTimeZone`) → `05:11:12Z`.
  - common log: `127.0.0.1 - - [07/Nov/2026:10:11:12 +0000] "GET /"` — the bracket is not at the start; accept the first `[dd/Mmm/yyyy:HH:mm:ss ±zzzz]` within the first 64 chars.
  - syslog: `Nov  7 10:11:12 host app[1]: x` → 2026-11-07T10:11:12 in the naive zone; `Dec 31 23:59:59 …` with `now` = 2026-01-01T00:00:10Z → year 2025 (the "more than a day in the future" rule).
  - rejects: `hello`, `12:30 meeting`, `2026-13-40 99:99:99`, an empty line, a 10,000-char line with no timestamp (finishes quickly: assert under 50 ms).
  - `TryExtract` with a fixed strategy only uses that strategy (an ISO-prefixed line with a `Field "time"` strategy and no `time` field → false).

  `TimestampingParserTests` (inner = `JsonlParser` / `PlainTextParser`):
  - the first timestamped line fixes `Strategy`; the next line without one gets the previous `Timestamp` and `TimestampInherited = true`; a line before any timestamp has `Timestamp == null`;
  - 200 consecutive misses reset the strategy, then a line in a different format is detected;
  - the zone delegate is read per parse (switching it changes the next naive line's offset);
  - `Parse` never throws on garbage; parallel `Parse` calls from 4 threads do not corrupt state (assert all results are non-null and the strategy is one of the expected ones).

  `ElasticApiClientTests`: `SearchAsync`/`PreviewAsync` hits have `Line.Timestamp == hit.Timestamp`.

- [ ] **Step 2: Run, expect compile errors.**

- [ ] **Step 3: Implement.** `Line`: keep the positional record, add the two `init` properties (record equality now includes them — check that nothing relies on `Line` value equality: `rg -n "new Line\(" src` and `sb callers Line`; all buffers use `ReferenceEqualityComparer`; say what you found). `TimestampDetector`: anchored `[GeneratedRegex]` patterns applied to `raw.AsSpan(0, Math.Min(64, raw.Length))`; parse with `DateTimeOffset.TryParseExact` / `TryParse` using `CultureInfo.InvariantCulture` and `DateTimeStyles.AllowWhiteSpaces`; convert naive values with `TimeZoneInfo.ConvertTimeToUtc(dateTime, zone)` (handle invalid/ambiguous local times by falling back to `zone.GetUtcOffset`); never throw. `TimestampingParser`: `lock` around the strategy state; inheritance per decision 2. `AppState`: wrap file parsers as `new TimestampingParser(LogParserSelector.ForPath(fullPath), () => ZoneFor(_settings.TimeZoneMode))` (also the sample-log tab). Keep `FileTabState.Parser` typed `ILogParser`. `ElasticApiClient.MapHit`: `line with { Timestamp = timestamp }`.

- [ ] **Step 4: Run** the new tests, `ElasticApiClient*`, `AppState*`, `FileTailer*`, then the full suite.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(rows): give log lines a timestamp from fields, prefixes or Elastic hits"
```

---

### Task 2: Column layout model and persistence

**Files:**
- Create: `src/HexTail/Application/ColumnLayout.cs`
- Modify: `src/HexTail/Application/FileTabState.cs`, `src/HexTail/Application/AppState.cs`, `src/HexTail/Persistence/AppConfig.cs`, `src/HexTail/Domain/RuleSet.cs`, `src/HexTail/Domain/Search.cs`
- Test: `src/HexTail.Tests/Application/ColumnLayoutTests.cs` (create), `AppStateTests.cs`, `src/HexTail.Tests/Persistence/JsonFileAppPersistenceTests.cs`, `src/HexTail.Tests/Domain/RuleSetTests.cs` (or wherever RuleSet is tested: `rg -l "RuleSet" src/HexTail.Tests`)

**Interfaces (produced):**

```csharp
public enum TimeDisplay { Absolute, Relative }
public sealed record ColumnSpec(string Key, double Width);          // Key: "@time" | "@level" | "@message" | field name
public sealed record ColumnLayout(IReadOnlyList<ColumnSpec> Columns, bool ComposedRows, TimeDisplay TimeDisplay)
{
    public const string Time = "@time", Level = "@level", Message = "@message";
    public const double MinWidth = 40, MaxWidth = 1200;
    public static ColumnLayout DefaultFor(LogSourceDescriptor source, IReadOnlyList<string>? elasticOutputFields);
    public ColumnLayout WithColumn(string key);            // appends (no duplicates), default width per key
    public ColumnLayout WithoutColumn(string key);         // keeps at least one column
    public ColumnLayout WithWidth(string key, double width);   // clamped
    public static double DefaultWidth(string key);         // @time 150, @level 48, @message 400, field 140
}
public static class MessageField
{
    public static readonly string[] Candidates = ["message", "msg", "log", "event", "text", "body"];
    public static string? Pick(IReadOnlyDictionary<string, string>? fields);   // case-insensitive, first present
}
// FileTabState
public ColumnLayout? Columns { get; set; }        // null = defaults
public ColumnLayout EffectiveColumns(IReadOnlyList<string>? elasticOutputFields);
public int ColumnsVersion { get; }                // bumped by AppState on every change
public IReadOnlyList<string> KnownFields(int sample = 500, int cap = 100);   // keys seen in the last `sample` lines, sorted, capped
public bool IsStructured { get; }                 // true for JSONL/logfmt/Elastic sources (parser type / source kind)
// AppState
public ValueTask SetColumnLayoutAsync(FileTabState tab, ColumnLayout layout, CancellationToken cancellationToken = default);
// Persistence (optional, both tab types)
public List<PersistedColumn>? Columns { get; init; }   // PersistedColumn { string Key; double Width }
public bool ComposedRows { get; init; }
public TimeDisplay TimeDisplay { get; init; }
// Highlighting cells
public IReadOnlyList<ColoredRange> Highlights(string text);       // RuleSet
public IReadOnlyList<HighlightRange> GetHighlights(string text);   // Search (forwards to Query)
```

- [ ] **Step 1: Failing tests:** defaults per decision 5 (file source, Elastic with output fields `["service","message"]`); `WithColumn` no duplicates and default widths; `WithoutColumn` keeps ≥ 1; `WithWidth` clamps; `MessageField.Pick` order and case-insensitivity; `KnownFields` from mixed lines (sorted, capped, sampled from the tail); `IsStructured` for each parser/source kind (including the wrapped `TimestampingParser` — use its `Inner`); `SetColumnLayoutAsync` persists and bumps `ColumnsVersion`, and a save failure leaves the previous layout (follow the existing `SetLevelsAsync` rollback pattern); round trip of `Columns`/`ComposedRows`/`TimeDisplay` for file and Elastic tabs; an old session JSON without these fields loads with `Columns == null`; `RuleSet.Highlights(string)` and `Search.GetHighlights(string)` match the `Line` overloads on the same text.

- [ ] **Step 2: Run, expect compile errors.**

- [ ] **Step 3: Implement** the records and methods; persistence through the same save/restore code that handles `Levels` (`rg -n "Levels =" src/HexTail/Application/AppState.cs`); sanitize on load (drop blank keys and duplicates, clamp widths, unknown `TimeDisplay` → `Absolute`). `ColumnsVersion` is how views learn they must re-render (same pattern as `RulesVersion`).

- [ ] **Step 4: Run** the new tests and `AppState*`, `JsonFileAppPersistence*`, `SessionTolerance*`, then the full suite.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(rows): add a per-source column layout with persistence"
```

---

### Task 3: Cells in rows — time, level, message, fields, tint

**Files:**
- Create: `src/HexTail/ViewModels/LogCellViewModel.cs`
- Modify: `src/HexTail/ViewModels/LogLineViewModel.cs`, `src/HexTail/ViewModels/LogViewViewModel.cs`, `src/HexTail/Views/LogLineView.axaml(.cs)`, `src/HexTail/Styles/CyberTail.axaml` (level colours, error tint, dimmed inherited time; reuse existing brushes where possible)
- Test: `src/HexTail.Tests/ViewModels/LogCellTests.cs` (create), `src/HexTail.Tests/Ui/LogLineInteractionTests.cs` or a new `ColumnRowTests.cs`

**Interfaces (produced):**

```csharp
internal sealed class LogCellViewModel : ReactiveObject
{
    public string Key { get; }
    public string Text { get; }
    public IReadOnlyList<HighlightSpan> Spans { get; }
    public double Width { get; }            // ignored for the last cell (fills)
    public bool IsLast { get; }
    public string? ToolTip { get; }         // time cells: ISO-8601 + delta; others: full text when long
    public bool IsDimmed { get; }           // inherited timestamp
    public string? LevelClass { get; }      // "trc" | "dbg" | "inf" | "wrn" | "err" | null
}
// LogLineViewModel
public bool UsesColumns { get; }            // IsStructured && !layout.ComposedRows
public IReadOnlyList<LogCellViewModel> Cells { get; }
public bool IsErrorRow { get; }
internal DateTimeOffset? PreviousTimestamp { get; set; }   // set by LogViewViewModel.SyncRows for relative time
// static, pure, unit-testable
internal static string FormatTime(DateTimeOffset value, TimeZoneInfo zone, DateTimeOffset now);
internal static string FormatDelta(TimeSpan delta);   // "+1.204 s", "+12 ms", "+3 min 4 s", "-0.5 s"
```

- [ ] **Step 1: Failing tests:** `FormatTime` (today vs other day, display zone conversion), `FormatDelta` boundaries (<1 s in ms, seconds with 3 decimals, minutes, negative deltas for out-of-order lines); a structured row in column mode exposes cells for the layout (time text, level `ERR` with class `err`, message from `msg`, field `service`, missing field → empty text); spans in a cell come from rules applied to the cell text (a Highlight rule `checkout` highlights inside the `service` cell, not in `@time`); relative mode uses `PreviousTimestamp` and the first visible row shows the absolute time; inherited timestamps are dimmed; `IsErrorRow` for Error lines in both modes; a plain-text source (or `ComposedRows`) keeps the single composed text (existing tests stay green); changing the layout through `AppState.SetColumnLayoutAsync` re-renders realized rows (via `ColumnsVersion` in `LogViewViewModel.Sync`, like `RulesVersion`); UI: the realized row shows one `HighlightedTextBlock` per cell with the cell widths, the last filling the row.

- [ ] **Step 2: Run, expect failures.**

- [ ] **Step 3: Implement.** Compute cells in `LogLineViewModel.Render()` only when visible (as `Spans` are today); spans per cell = searches + rules applied to the cell text (new string overloads from Task 2), then the same overlap resolution as `HighlightSpanCache.Compute` (extract that loop into a shared static helper rather than duplicating it). `LogViewViewModel.SyncRows` sets `PreviousTimestamp` on rows it creates or refreshes (the previous row in `desired`), and a `ColumnsVersion` change or a `TimeZoneMode` change invalidates realized rows. `LogLineView.axaml`: keep the existing composed `Grid` for `!UsesColumns`; add an `ItemsControl x:Name="RowCells"` (visible when `UsesColumns`) with a `DockPanel LastChildFill="True"` items panel and a container style that docks every cell left; each cell is a `HighlightedTextBlock` with `Width` bound (unset for the last), `TextTrimming="CharacterEllipsis"`, `ToolTip.Tip`, `Classes.dimmed`, and the level classes; the bookmark glyph stays before the cells. Error tint: a row `Classes.error` style on the row `Border` using a low-alpha error brush (add `ErrorRowBrush` to each theme dictionary that defines `ErrorBrush`).

- [ ] **Step 4: Run** the new tests and `LogView*`, `LogLineInteraction*`, `RowKey*`, `Inspector*`, `FilterBar*`, `HighlightedTextBlock*`, then the full suite twice.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(rows): show structured sources as time, level, message and field columns"
```

---

### Task 4: Header bar and Columns menu

**Files:**
- Create: `src/HexTail/ViewModels/ColumnHeaderViewModel.cs`, `src/HexTail/Views/ColumnHeaderBar.axaml(.cs)`
- Modify: `src/HexTail/Views/LogView.axaml` (header above `LogList`, shown when the source uses columns), `src/HexTail/ViewModels/LogViewViewModel.cs` (expose `ColumnHeader`), `src/HexTail/ViewModels/MainWindowViewModel.cs` (palette entries "Columns…"/"Composed rows" if cheap)
- Test: `src/HexTail.Tests/Ui/ColumnHeaderTests.cs` (create)

**Interfaces (produced):**

```csharp
internal sealed class ColumnHeaderViewModel : ReactiveObject
{
    public bool IsVisible { get; }                                    // structured source && !ComposedRows
    public ObservableCollection<ColumnHeaderItemViewModel> Columns { get; }   // Title ("Time","Level","Message", field name), Key, Width, IsLast
    public ObservableCollection<ColumnChoiceViewModel> Choices { get; }       // Key, Title, IsOn (two-way → add/remove)
    public bool ComposedRows { get; set; }
    public TimeDisplay TimeDisplay { get; set; }
    public ReactiveCommand<Unit, Unit> ResetCommand { get; }
    public string? Error { get; }
    internal void Resize(string key, double delta);                   // from header thumbs; persisted on drag end
    internal void Sync();                                             // on ColumnsVersion / file change / KnownFields refresh when the menu opens
}
```

- [ ] **Step 1: Failing tests:** the header shows one title per column in layout order with the layout widths, aligned with the row cells (same left offset as the cells: the header reserves the bookmark-glyph slot); dragging a header thumb (`Thumb.DragDelta` raised in the test) changes the width of that column in the header and in realized rows, and `DragCompleted` persists it (`persistence.Config` shows the new width), clamped to 40..1200; the Columns button opens a menu listing `Time`, `Level`, `Message` then known fields; toggling a field on appends a column, off removes it (the last remaining column cannot be removed: its checkbox is disabled); **Composed row** hides the header and switches rows to the composed text; **Time: Relative** switches time cells to deltas; **Reset columns** restores defaults; a failed save shows the error and keeps the previous layout; every control has an accessible name containing its visible text (resize thumbs: `Resize {title} column`); header hidden for plain-text sources.

- [ ] **Step 2: Run, expect failures.**

- [ ] **Step 3: Implement.** Header: a `Grid`/`DockPanel` mirroring the row cell layout, each title followed by a 4 px `Thumb` (cursor `SizeWestEast`) except the last; a `Button x:Name="ColumnsMenuButton"` (icon `mdi-table-column`, name "Columns") with a `Flyout` holding the choices (`CheckBox` list in a `ScrollViewer` capped at 360 px), a `ToggleSwitch`/`CheckBox` for Composed row, two `RadioButton`s for Time, and Reset. Width changes during a drag update the in-memory layout (rows re-render through `ColumnsVersion`, throttle to one re-render per frame with `Dispatcher.UIThread.Post` coalescing) and persist once on `DragCompleted`. `KnownFields` is refreshed when the flyout opens.

- [ ] **Step 4: Run** the new tests and `LogView*`, `Accessibility*`, `MainWindowInteraction*`, then the full suite twice.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(rows): add a column header with resizable widths and a Columns menu"
```

---

### Task 5: Inspector "Add column"

**Files:**
- Modify: `src/HexTail/ViewModels/InspectorViewModel.cs`, `InspectorFieldViewModel.cs`, `src/HexTail/Views/InspectorPanel.axaml`
- Test: `src/HexTail.Tests/ViewModels/InspectorViewModelTests.cs`, `src/HexTail.Tests/Ui/InspectorPanelTests.cs`

**Interfaces (produced):** `InspectorFieldViewModel.AddColumnCommand`; `InspectorViewModel.AddColumnAsync(InspectorFieldViewModel field)` → `AppState.SetColumnLayoutAsync(file, layout.WithColumn(field.Key) with { ComposedRows = false })`; disabled (with tooltip "Columns are available for structured sources") for plain-text sources; already-present columns show the item checked/disabled ("In columns").

- [ ] **Step 1: Failing tests:** Add column on a JSONL row appends the field and persists; on a composed structured source it also turns columns on; on plain text the command cannot execute; adding an existing column is a no-op; a save failure lands in `Inspector.Error`.
- [ ] **Step 2–4:** implement (menu item `Add column` in the field flyout, accessible name "Add {key} as a column"), run `Inspector*`, `ColumnHeader*`, then the full suite.
- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(inspector): add a field as a column from the inspector"
```

---

### Task 6: Documentation

**Files:** `README.md`, `docs/user-guide.md`, `docs/architecture.md`

- [ ] **Step 1:** Document timestamps (what is recognised, naive = display zone, inherited lines), column mode (which sources, defaults, `@message` choice, Columns menu, widths, Composed row, Time absolute/relative with the tooltip), the error tint and level colours, the inspector's **Add column**; architecture: `Line.Timestamp`, `TimestampDetector`/`TimestampingParser` on the tailer thread, `ColumnLayout` per source, cells rendered for realized rows only. Do not edit historical plans or `docs/ux`.
- [ ] **Step 2:** `rg -n -i "joined with spaces|single text row" README.md docs` — remaining hits intentional.
- [ ] **Step 3: Commit** `docs: describe timestamps and columns`.

---

### Task 7: Final verification

- [ ] **Step 1:** Full suite twice (green; 53 build warnings).
- [ ] **Step 2:** Load a copy of the real `session.json` (never modify the original): it loads with `Columns == null` defaults, round-trips unchanged except the new optional fields, and Elastic/file tabs restore.
- [ ] **Step 3:** Throughput check (scratch test, not committed): parse 100,000 mixed JSONL/plain lines through `TimestampingParser` in under 1 s on this machine; report the number.
- [ ] **Step 4:** Report what could not be checked by eye (header/cell alignment, resize feel, level colours in each theme, tint contrast).
