# UX Phase 4 part B — Merged Streams Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user combine several open sources (files and Elastic) into a named, persisted **stream** whose rows are interleaved by timestamp, with a source column and colour stripe, per-source toggles, its own rules/levels/columns, and one time range that drives its Elastic members and filters its file members.

**Architecture:** Every `Line` carries its `SourceId`. A pure `StreamMerger` does a watermark merge: lines from each member are held until every active member has reached their timestamp, or for at most 5 s, then released in timestamp order; a line older than what is already shown is appended with a "late" marker instead of reshuffling rows. A stream is a `FileTabState` of a new kind (`LogSourceKind.Stream`) with a null tailer whose `Buffer` is fed by the merger from `AppState.DrainTailerEvents`, so views, filters, the inspector, bookmarks and columns work on it unchanged. A member reset (truncation, Elastic re-query) rebuilds the stream from the members' current buffers.

**Tech Stack:** .NET 10, Avalonia 12.1.1 (headless tests via `[AvaloniaFact]`), ReactiveUI, xUnit v3, System.Text.Json.

This is Phase 4 part B of `docs/ux/2026-11-04-ux-proposals.md` (item 3). Part A (timestamps and columns) is merged; part C (histogram and scrollbar markers) follows. The user confirmed: merged streams interleave rows by timestamp; sources use the display zone. Terminology: "Phase 4 part B".

## Global Constraints

- Branch `audit-ui-phase-4b`, cut from `origin/main` at `e90cfa2` (PR #15 merged). Never work on `main`. **Never `git push` unless the user explicitly says so.**
- Conventional Commits, one commit per task. Every commit message ends EXACTLY with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- A CSharpier pre-commit hook reformats staged C# on commit. `csharpier check` already fails on `src/HexTail/HexTail.csproj`; leave that file alone.
- Prefix shell commands with `rtk`. Explore with `sb` (`sb show <file> <Symbol>`, `sb callers <Symbol>`) before opening whole files.
- Build/test commands (run from the repo root):
  - `rtk dotnet build src/HexTail.Tests`
  - `rtk dotnet test src/HexTail.Tests --no-build --filter "FullyQualifiedName~<Name>"`
  - full suite: `rtk dotnet test src/HexTail.Tests --no-build`. Baseline at the start: **1037 passing, 53 build warnings** (do not add warnings).
- Tests never touch the OS credential vault (`InMemoryCredentialVault`; `TestWindow.Create*` already does) and never call a real Kibana/Elasticsearch (`FakeElasticApiClient`). Any plain `[Fact]` test that constructs ReactiveUI objects must initialise ReactiveUI the way `WorkspaceViewModelTests` does (or be `[AvaloniaFact]`) — an uninitialised one once broke 430 later tests depending on run order.
- UI tests wait through `UiWait.Until(...)`; tests that append with `file.Model.Buffer.Append([...])` follow with `file.SyncViews()`.
- ReactiveUI commands keep the positional scheduler pattern. Every command body catches everything except `OperationCanceledException` and shows it on a view model's `Error`.
- Reflection bindings in XAML (compiled bindings off). Every interactive control gets an `AutomationProperties.Name` containing its visible text.
- Old sessions must still load; new persisted fields are optional; `AppConfig.Version` stays 2; the previous build must still load a session written by this build (unknown members ignored; a stream it cannot understand is simply dropped there).
- Performance: busy members append thousands of lines per second. `DrainTailerEvents` runs under `_gate` with an 8 ms budget; merging must be O(lines) per drain plus O(members) bookkeeping, no per-drain sort of the whole stream, no LINQ allocations per line on the hot path.
- Known flaky test: `AccessibilityTests.SettingsFocus…` (if it flakes, report it, don't paper over it).

### Decisions taken where the proposal left room (veto points)

1. **A stream is a tab of its own kind** (`LogSourceKind.Stream`), listed in a new **STREAMS** sidebar section above Files. Its members stay ordinary open sources (still listed under Files/Elastic and usable on their own). Opening a stream opens any member that is not open; a member that cannot be opened (missing file, unknown Elastic source) is shown as missing in the stream and does not block it.
2. **Watermark merge with a 5 s hold.** Released order is by timestamp, ties by member order then arrival. A member counts toward the watermark while it produced a line in the last 5 s; idle members don't hold others back. Lines older than the last released timestamp are appended (never inserted) and marked **late** (a small marker in the row; tooltip "Arrived late from {source}"). Lines with no timestamp take the previous released timestamp of the same member, or the arrival time.
3. **Member resets rebuild the stream.** When a member's buffer is cleared (truncation, rotation, Elastic re-query/range change), the stream clears its buffer and re-merges all members' current buffers in one sorted pass (selection/bookmarks of the stream are cleared, like any buffer clear). Member eviction (a member hitting its own max lines) does not remove rows from the stream; the stream has its own max lines (`AppSettings.MaxLines`).
4. **Rules, levels, searches and columns are the stream's own** (the stream tab's Source rules + Global rules); a member's own Source rules do not apply inside the stream. Default stream columns: `@time`, `@source`, `@level`, `@message`. A stream always uses columns (plain lines show their raw text as the message), so the Source column identifies members by text, not only colour.
5. **Per-source toggles instead of a `source:` text chip:** the stream's filter bar shows one coloured toggle chip per member; turning one off hides that member's rows (persisted per stream tab as `HiddenSources`). Member colours come from a fixed 8-colour palette assigned by member order when the stream is created and stored with the stream.
6. **One time range per stream:** the stream tab shows the time pill. Applying it applies the same range to every Elastic member (their own pills change too) and filters the stream's rows from file members by timestamp (rows with no timestamp stay visible). With no Elastic member, the pill is a display filter only. The default range for a new stream is "Live" (no filter).
7. **Creating and editing:** a **New stream** button in the STREAMS header and palette entry opens an editor flyout: name (prefilled "Stream N"), one checkbox per open source (at least two required), Save. The stream row has edit (same flyout, members can be added/removed) and close; delete is inside the editor with an inline confirmation. Drag-to-merge is **not** built.
8. **Persistence:** `AppConfig.Streams` (definitions: id, name, members `{ key, color }` where key is `file:{full path}` or `elastic:{source id}`), `AppConfig.OpenStreams` (open stream tabs: stream id, rules, levels, searches, columns/composed/time display, hidden sources, from/to/live), `AppConfig.SelectedStreamId`. Definitions persist when closed; deleting removes the definition and closes the tab.

### Not in part B

Drag-to-merge in the sidebar, nested streams, per-member Source rules inside a stream, histogram and scrollbar markers (part C), column sorting, a stream-wide search across members that are not open.

## File Structure

| Path | Status | Responsibility |
| --- | --- | --- |
| `src/HexTail/Domain/Line.cs` | modify | `SourceId` |
| `src/HexTail/Domain/TimestampingParser.cs`, `Elastic/ElasticTailer.cs` (or `ElasticApiClient.MapHit` caller) | modify | stamp `SourceId` in the same clone |
| `src/HexTail/Domain/StreamMerger.cs` | create | pure watermark merge |
| `src/HexTail/Application/LogSourceDescriptor.cs` | modify | `LogSourceKind.Stream`, `LogSourceDescriptor.Stream(...)` |
| `src/HexTail/Application/StreamState.cs` | create | per-open-stream: definition, merger, member map, late set, hidden sources, range |
| `src/HexTail/Application/AppState.cs`, `FileTabState.cs` | modify | stream CRUD, open/close/restore, drain integration, rebuild on reset |
| `src/HexTail/Persistence/AppConfig.cs` | modify | `StreamSettings`, `StreamMember`, `PersistedStreamTab`, fields + sanitize |
| `src/HexTail/Application/ColumnLayout.cs` | modify | `@source` pseudo-column, stream defaults |
| `src/HexTail/ViewModels/LogLineViewModel.cs`, `LogViewViewModel.cs`, `FilterBarViewModel.cs`, `InspectorViewModel.cs` | modify | source cell + stripe + late marker, hidden-source filter, source chips, inspector source line |
| `src/HexTail/ViewModels/StreamEditorViewModel.cs`, `StreamTabViewModel` (if needed), `MainWindowViewModel.cs` | create/modify | sidebar section, editor, palette, selection |
| `src/HexTail/Views/SourceSidebar.axaml`, `FilterBar.axaml`, `LogLineView.axaml`, `MainWindow.axaml` (pill) | modify | UI |
| `README.md`, `docs/user-guide.md`, `docs/architecture.md` | modify | document |

---

### Task 1: `Line.SourceId`

**Files:** modify `src/HexTail/Domain/Line.cs`, `src/HexTail/Domain/TimestampingParser.cs`, `src/HexTail/Application/AppState.cs` (where the parser is constructed, pass the tab id), the Elastic line path (`ElasticTailer` where it emits `hit.Line`, or `ElasticApiClient.MapHit` if it can know the source id); tests in `src/HexTail.Tests/Domain/TimestampingParserTests.cs`, `src/HexTail.Tests/Elastic/ElasticTailerTests.cs`, an AppState test.

**Interfaces (produced):** `Line.SourceId` (`string?`, `init`); `TimestampingParser(ILogParser inner, Func<TimeZoneInfo> naiveZone, Func<DateTimeOffset>? now = null, string? sourceId = null)` stamps `SourceId` on every returned line (timestamped or not) in the same `with` clone it already makes (a line that previously was returned unchanged is now cloned once); Elastic lines get `SourceId = <elastic source id>` in one clone together with the timestamp if both are set at the same place, otherwise one extra clone.

- [ ] **Step 1: Failing tests:** every line from a `TimestampingParser` built with `sourceId: "a"` has `SourceId == "a"` (timestamped, inherited and never-timestamped lines); without a source id `SourceId` is null (existing behaviour); Elastic tailer lines carry the source id; a file opened through `AppState` yields buffer lines whose `SourceId == tab.Id`.
- [ ] **Step 2:** run, expect failures.
- [ ] **Step 3:** implement (no second clone on the file path).
- [ ] **Step 4:** run `Timestamp*`, `ElasticTailer*`, `ElasticApiClient*`, `AppState*`, then the full suite.
- [ ] **Step 5: Commit** `feat(streams): stamp every line with its source id`.

---

### Task 2: `StreamMerger` (pure)

**Files:** create `src/HexTail/Domain/StreamMerger.cs`; test `src/HexTail.Tests/Domain/StreamMergerTests.cs`.

**Interface (produced):**

```csharp
namespace HexTail.Domain;

public sealed class StreamMerger
{
    public static readonly TimeSpan DefaultHold = TimeSpan.FromSeconds(5);

    public StreamMerger(IReadOnlyList<string> memberIds, TimeSpan hold, Func<DateTimeOffset> now);

    /// Queues lines from one member in arrival order. Unknown member ids are ignored.
    public void Add(string memberId, IReadOnlyList<Line> lines);

    /// Returns lines that are ready, in release order (may be empty). Never returns a line twice.
    public IReadOnlyList<Line> Release();

    /// Releases everything pending in timestamp order (used for the initial merge and rebuilds).
    public IReadOnlyList<Line> FlushAll();

    /// Forgets pending lines and state (used before a rebuild).
    public void Reset();

    /// True for lines released with a timestamp older than the last released timestamp at release time.
    public bool IsLate(Line line);

    public DateTimeOffset? LastReleased { get; }
}
```

Algorithm (decision 2): per member a FIFO queue of `(Line line, DateTimeOffset key, long seq)` where `key` = `line.Timestamp ?? memberLastKey ?? now()`; per member `LatestSeen` (max key seen) and `LastArrival` (wall clock of the last `Add`). On `Release()`: active members = members with `now - LastArrival <= hold`; `watermark` = min over active members' `LatestSeen` (if no active member, watermark = +∞); a pending entry is ready when `key <= watermark` or `now - arrivalOf(entry) > hold`; ready entries from all queues are merged with a k-way merge over the queue heads (queues are FIFO and near-ordered; take heads while ready, comparing `(key, memberIndex, seq)`); an entry whose `key < LastReleased` is recorded as late (a `ConditionalWeakTable<Line, object>` or a bounded `HashSet<Line>` with reference equality, trimmed to the last 10,000 late lines). `FlushAll()` merges every queue completely (sorting each queue first if it is not ordered by key — check monotonicity in one pass and sort only when needed) and does not mark late lines. Not thread-safe (called under `AppState._gate`).

- [ ] **Step 1: Failing tests** (`now` from a controllable clock):
  - two members interleave: A lines at t=1,3,5 and B lines at t=2,4 added in separate batches → released order 1,2,3,4 once both have reached t≥4, 5 held until B passes 5 or the hold expires;
  - an idle member (no line for > hold) does not block; a member that becomes active again joins the watermark;
  - the hold: a line pending longer than 5 s wall-clock is released even if another member lags;
  - late: after releasing up to t=10, a B line at t=7 is released next and `IsLate` is true; later lines at t=11 are not late;
  - null timestamps take the member's previous key (or `now`) and keep arrival order within the member;
  - ties ordered by member index then arrival; nothing is released twice; unknown member ignored;
  - `FlushAll` on unsorted input (a member whose lines are out of order) returns them sorted and stable; `Reset` empties everything;
  - performance: 4 members × 50,000 lines in 100 batches each → total `Release` time under 200 ms in Debug (assert generously, e.g. < 1 s) and allocations bounded (no per-line LINQ).
- [ ] **Step 2:** run, expect compile errors.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** run `StreamMerger*`, then the full suite.
- [ ] **Step 5: Commit** `feat(streams): merge member lines by timestamp with a watermark`.

---

### Task 3: Stream model, persistence and AppState integration

**Files:** modify `src/HexTail/Application/LogSourceDescriptor.cs`, `src/HexTail/Application/FileTabState.cs`, `src/HexTail/Application/AppState.cs`, `src/HexTail/Persistence/AppConfig.cs`; create `src/HexTail/Application/StreamState.cs`; tests `src/HexTail.Tests/Application/StreamStateTests.cs`, `AppStateStreamTests.cs` (create), `src/HexTail.Tests/Persistence/JsonFileAppPersistenceTests.cs`.

**Interfaces (produced):**

```csharp
// Persistence
public sealed record StreamMember(string Key, string Color);      // Key: "file:{full path}" | "elastic:{source id}"
public sealed record StreamSettings(string Id, string Name, List<StreamMember> Members);
public sealed class PersistedStreamTab { required string StreamId; Rules; Levels; Searches; Columns; ComposedRows; TimeDisplay; List<string> HiddenSources; string From = "now-15m"; string To = "now"; bool Live = true; /* follow flags as other tabs */ }
// AppConfig: List<StreamSettings> Streams = []; List<PersistedStreamTab> OpenStreams = []; string? SelectedStreamId
// LogSourceKind.Stream; LogSourceDescriptor.Stream(StreamSettings s) => new($"stream:{s.Id}", Stream, s.Name, tooltip listing members)
// FileTabState: public StreamState? Stream { get; }   (non-null only for stream tabs)
// StreamState: Definition, Merger, MemberIds (tab ids of open members, by member order), MissingMembers, ColorFor(sourceId), DisplayNameFor(sourceId), HiddenSources (HashSet<string>), RangeFrom/RangeTo/Live, IsLate(line)
// AppState
public IReadOnlyList<StreamSettings> Streams { get; }
public ValueTask<StreamSettings> CreateStreamAsync(string name, IReadOnlyList<FileTabState> members, CancellationToken ct = default);   // ≥ 2 members
public ValueTask UpdateStreamAsync(string streamId, string name, IReadOnlyList<FileTabState> members, CancellationToken ct = default);
public ValueTask DeleteStreamAsync(string streamId, CancellationToken ct = default);
public ValueTask<FileTabState?> OpenStreamAsync(string streamId, CancellationToken ct = default);   // opens missing members, selects the stream
public ValueTask SetHiddenSourcesAsync(FileTabState stream, IReadOnlySet<string> hidden, CancellationToken ct = default);
internal static string MemberKey(FileTabState tab);   // "file:"+full path | "elastic:"+source id
```

Drain integration (`DrainTailerEvents`): after a member's lines are appended to the member buffer (`FlushLines`), also `Add` them to every open stream that has that member; on `SourceReset` of a member, mark its streams for rebuild; after the event loop, for each open stream: rebuild if marked (`Merger.Reset()`, `stream.Buffer.Clear()`, `Add` every member's current `Buffer.Lines`, `Append(FlushAll())`), else `Append(Merger.Release())` — a release runs on every drain even without events so the 5 s hold expires on idle ticks. Closing a member tab removes it from its open streams (pending lines dropped, it shows as missing); closing a stream never closes members. Stream tabs are saved/restored with the session (`OpenStreams`, `SelectedStreamId`), restored after file and Elastic tabs.

- [ ] **Step 1: Failing tests:** create a stream of two open file tabs → definition persisted with members `file:…` and two palette colours; opening it produces a stream tab whose buffer contains both files' existing lines in timestamp order (FlushAll on open); appending to member files (via `TestWindow.CreatePolling` + real temp files, or via member `Buffer.Append` + a drain call) appends merged lines after the watermark/hold (use an injected clock for AppState's merger if one exists; otherwise assert with `UiWait.Until` and short holds via an internal test hook to set the hold); truncating a member (or `Buffer.Clear()` + SourceReset path) rebuilds the stream; closing a member marks it missing and keeps the stream open; deleting the stream closes its tab and removes the definition; restore from a saved session reopens the stream and its members; a stream referencing a missing file opens with that member missing; persistence round trip and sanitize (blank names → "Stream", unknown member keys kept but missing, duplicate members removed, < 2 valid members still loads); an old session without the fields loads; `UpdateStreamAsync` adding/removing a member rebuilds.
- [ ] **Step 2:** run, expect failures.
- [ ] **Step 3:** implement. The stream tab uses a no-op `ILogTailer` (`NullTailer` — reuse one from tests if suitable, otherwise add `src/HexTail/Tailing/NullTailer.cs`) and `PlainTextParser`; `FileTabState.IsStructured` is always true for a stream (`StreamState.AnyMemberStructured` stays available); Elastic loading/flags stay false for streams.
- [ ] **Step 4:** run the new tests and `AppState*`, `JsonFileAppPersistence*`, `SessionTolerance*`, then the full suite twice.
- [ ] **Step 5: Commit** `feat(streams): add named streams that merge open sources`.

---

### Task 4: Rendering — source column, colour stripe, late marker, per-source toggles

**Files:** modify `src/HexTail/Application/ColumnLayout.cs` (`@source` pseudo-column, `DefaultFor` for streams: `@time`, `@source`, `@level`, `@message`), `src/HexTail/ViewModels/LogLineViewModel.cs`/`LogCellViewModel.cs` (source cell text = member display name, a 3 px left stripe in the member colour, a "late" marker), `src/HexTail/ViewModels/LogViewViewModel.cs` (`ApplyFilters` hides rows whose `SourceId` is in `HiddenSources`), `src/HexTail/ViewModels/FilterBarViewModel.cs` + `src/HexTail/Views/FilterBar.axaml` (a row of member toggle chips for stream tabs: colour dot, name, toggled = visible; missing members shown disabled with "missing"), `src/HexTail/ViewModels/InspectorViewModel.cs` + panel (a "Source" line with the member name for stream rows), `src/HexTail/Views/LogLineView.axaml`, `ColumnHeaderBar` (title "Source"); tests `ColumnLayoutTests`, `LogCellTests`, `StreamRenderingTests.cs` (create), `FilterBarTests`.

- [ ] **Step 1: Failing tests:** stream default columns; the source cell shows the member display name and the row stripe brush equals the member colour (composed mode shows the stripe too); late rows show the marker with tooltip "Arrived late from {name}"; toggling a member chip off hides its rows in all views of the stream and persists `HiddenSources`; missing members' chips are disabled and labelled; the inspector shows the source line for stream rows only; the `@source` column is offered in the Columns menu only for streams ("Source (detected)").
- [ ] **Step 2:** run, expect failures.
- [ ] **Step 3:** implement (resolve colour/name through `FileTabState.Stream` once per render; cache brushes per colour).
- [ ] **Step 4:** run the new tests and `LogView*`, `ColumnHeader*`, `FilterBar*`, `Inspector*`, then the full suite.
- [ ] **Step 5: Commit** `feat(streams): show each row's source and toggle sources in a stream`.

---

### Task 5: Sidebar section, editor and palette

**Files:** modify `src/HexTail/ViewModels/MainWindowViewModel.cs` (`StreamTabs`/stream rows, select/close/edit commands, palette entries "New stream", "Open stream {name}"), `src/HexTail/Views/SourceSidebar.axaml` (STREAMS section above FILES with a `+` button, rows with colour dots of members, edit and close buttons); create `src/HexTail/ViewModels/StreamEditorViewModel.cs` and the editor flyout (in the sidebar view or a small `StreamEditor.axaml`); tests `StreamSidebarTests.cs`, `StreamEditorTests.cs` (create).

**Interfaces:** `StreamEditorViewModel { string Name; ObservableCollection<StreamMemberChoice> Choices (TabId, DisplayName, IsChecked); bool CanSave (name non-blank, ≥ 2 checked); SaveCommand; DeleteCommand + inline confirm (edit mode only); string? Error }`; `MainWindowViewModel.NewStreamCommand`, `EditStreamCommand(streamId)`, `OpenStreamCommand(streamId)`, `CloseStreamCommand(tab)`.

- [ ] **Step 1: Failing tests:** the STREAMS section lists saved streams (open and closed; closed ones open on click); `+` opens the editor with all open sources as choices and "Stream N" prefilled; Save is disabled with fewer than two members or a blank name; saving creates and opens the stream (it becomes the selected tab); edit mode pre-checks members and saving updates them; delete asks inline then removes; closing a stream tab keeps its definition; palette entries work; accessible names (`New stream`, `Edit {name}`, `Close {name}`, `Open {name}`); a narrow sidebar dismisses after selection like other rows.
- [ ] **Step 2:** run, expect failures.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** run the new tests and `Sidebar*`, `CommandPalette*`, `Accessibility*`, `MainWindowInteraction*`, then the full suite twice.
- [ ] **Step 5: Commit** `feat(streams): create, edit and open streams from the sidebar`.

---

### Task 6: One time range per stream

**Files:** modify `src/HexTail/ViewModels/MainWindowViewModel.cs` (the time pill shows for Elastic tabs and stream tabs; applying on a stream calls the existing Elastic range apply for each open Elastic member and stores the range on `StreamState`), `src/HexTail/ViewModels/LogViewViewModel.cs` (`ApplyFilters` on a stream drops file-member rows outside `[From, To]` unless Live; rows without a timestamp stay), `src/HexTail/Application/AppState.cs` (persist stream range in `PersistedStreamTab`); tests `StreamRangeTests.cs` (create).

- [ ] **Step 1: Failing tests:** applying "last 15 minutes" on a stream sets that range on each Elastic member (fake client receives searches for the new range) and filters file-member rows by timestamp; "Live" removes the file filter; rows without timestamps remain; the range persists and restores; on a stream without Elastic members the pill still filters; a member range change triggers the stream rebuild (Task 3) without losing the stream's own range.
- [ ] **Step 2–4:** implement; run the new tests and `Elastic*`, `StatusBar*`, `AppState*`, then the full suite.
- [ ] **Step 5: Commit** `feat(streams): apply one time range to a whole stream`.

---

### Task 7: Documentation

- [ ] Document streams in `README.md`, `docs/user-guide.md` (new "Streams" section: create/edit/delete, ordering and the 5 s hold, late rows, source column and stripe, toggles, own rules/columns, time range behaviour for Elastic vs file members, rebuild on member reset, persistence) and `docs/architecture.md` (`Line.SourceId`, `StreamMerger`, stream tabs fed from `DrainTailerEvents`). Do not edit historical plans or `docs/ux`. Commit `docs: describe merged streams`.

---

### Task 8: Final verification

- [ ] Full suite twice (green; 53 warnings).
- [ ] Load a copy of the real `session.json` (never modify the original): loads; round trip adds only the new optional keys.
- [ ] Scratch throughput check (not committed): 4 members × 50,000 lines through `DrainTailerEvents`-equivalent merging; report the time.
- [ ] Report what could not be checked by eye (stripe colours, chip row layout, late marker visibility, sidebar section).
