# HexTail UX proposals — November 4, 2026

Branch: `audit-ui` at `11c3dcb`. Visual examples: [mockups](2026-11-04-ux-mockups.html) (sections A–D, open in a browser).

The previous audits fixed correctness: state, ranges, validation, and accessibility names. What's still clunky is **information architecture**. Each feature was added where there was room, so related concepts now sit in unrelated places. Most items below regroup existing capability. Few require new engines.

## What feels clunky today (evidence)

| Symptom | Where |
| --- | --- |
| About 270px of chrome above the first log row at 1280×720. The rows are: command bar + toggles, search row, time-range button, two lines of applied/status text, source tabs, view tabs, then a count line. | [MainWindow.axaml](../../src/HexTail/MainWindow.axaml), [workspace-1280.png](../audits/evidence/ui-fixes-2026-10-01/workspace-1280.png) |
| Two tab rows (sources, then searches). They come with a **Tabs** combobox and a second close button that duplicate the source strip. | [FileStrip.axaml](../../src/HexTail/Views/FileStrip.axaml) |
| Things that change what you see live in four places. Searches are in the search bar, labels and exclusions are in the Settings modal, the Elastic filter value is buried inside the view editor, and the time range sits in a flyout. Nothing shows everything that currently affects the view. | SearchBar / SettingsPanel / view editor / range flyout |
| A search always becomes a tab. You can't filter the current view to matches, and you can't exclude ad hoc without opening Settings. | `AddSearchAsync` |
| A full `ColorPicker` sits inline in the search bar, for a choice that is almost always "next palette color". | [SearchBar.axaml](../../src/HexTail/Views/SearchBar.axaml) |
| Toggle labels read as states rather than actions ("No Inline", "Inline Shown"). They are bound to the selected view but placed in the global toolbar. | MainWindow toggles |
| The **Save Session** button is redundant: `AppState` already saves after every mutation. | `AppState.SaveAsync` callers |
| Elastic setup is one nested Server → View → Fields scroll inside a 1600×920 modal. It carries explanatory prose ("Saved namespace metadata (not queried)") and orders fields with "uncheck and recheck to move it last". The row preview is `<message> <level>` instead of real data. | [SettingsPanel.axaml](../../src/HexTail/Views/SettingsPanel.axaml) |
| Elastic sources open from an icon-only flyout of checkboxes, and that flyout is hidden until a source is configured. | `ElasticSourcesButton` |
| There are two time-zone settings: Appearance → *Source clock* and the range picker's *Input and display zone*. Each needs its own explanatory text. | Appearance tab, range flyout |
| Date/time entry uses Fluent spinner pickers: six spinners plus help text for one interval. | range flyout |
| Log levels aren't recognized, timestamps aren't parsed, and structured sources render as a single string, so JSONL, logfmt and Elastic rows lose their structure. | [Line.cs](../../src/HexTail/Domain/Line.cs), `LogLineView` |
| `AppWindowState.VerticalFileTabs` is persisted but nothing reads it. A sidebar was apparently intended. | AppConfig.cs |

## Recommendations

Effort: S < 1 day, M a few days, L about a week or more. Order is the suggested sequence: the early items unblock the later ones.

### 1. Shell: replace the source tab strip with a sidebar (M) — mockup A

- Left sidebar (collapsible, `Ctrl+B`) with sections **Streams**, **Files**, **Elastic** (connection → sources), **Saved filters**. Each entry shows a health dot, line count, and per-source color swatch. Missing or rotated files and failed auth show inline ("missing", "auth failed") instead of in a flyout.
- This removes the source TabControl, the **Tabs** combobox, the duplicate close button, and the Elastic sources flyout. Elastic sources become first-class: always visible and opened with a click, not a checkbox.
- Keep exactly **one** tab row: views of the selected source (All plus pinned searches), with counts.
- Move per-view controls (Follow, Wrap, Columns, Inspector, Context) to that view bar, right-aligned. They stop pretending to be global.
- Add a **status bar** for counts (visible / loaded / buffer %), live state, applied range, poll time, and a display-zone switch. It absorbs the three status `TextBlock`s currently stacked under the time-range button.
- Drop **Save Session**. Keep `Ctrl+S` as a silent no-op or "export session".

Avalonia: `SplitView` or a `Grid` with a `GridSplitter`, and `TreeView` for the sidebar. The app already has the persisted `VerticalFileTabs` flag for the collapsed state.

### 2. One filter model: chips with action and scope (M–L) — mockups A, B

Merge searches, global labels, and exclusions into one rule type:

```csharp
public sealed record ViewRule(
    string Pattern, MatchMode Mode, bool CaseSensitive, string Color,
    RuleAction Action,   // Highlight | ShowOnly | Hide
    RuleScope Scope,     // View | Source | Global
    bool Enabled, bool PinnedAsTab);
```

- A **filter bar** under the view tabs shows every active rule as a chip, including global ones (dashed border), so the question "why is this row hidden?" always has a visible answer. Click a chip to edit it in a popover, middle-click to disable, and use ✕ to remove.
- Typing goes into one input. **Enter** highlights, **Shift+Enter** shows only matching rows, **Alt+Enter** hides them. The **Pin as tab** option reproduces today's behavior (search → tab).
- The color comes from the palette automatically. The popover offers five swatches plus "custom…". The inline `ColorPicker` goes away.
- **Level filter**: a segmented T/D/I/W/E control (see item 4).
- **Saved filters**: named sets of rules in the sidebar ("hide healthchecks", "level ≥ warn") that can be applied to any source.
- Settings → *Labels & Exclusions* then disappears. Global rules are just rules whose scope is Global.

Migration: map existing `GlobalLabel` entries to rules with action Highlight, scope Global, and `PinnedAsTab = ShowInOpenFile`. Map `GlobalExcludeLabels` to Hide/Global, and map per-file `PersistedSearch` entries to Highlight/View with pinning on. The data maps one-to-one, so nothing is lost.

### 3. Merged streams: files and Elastic in one timeline (L) — mockup A

The goal is one "logging view" that interleaves multiple sources.

- A **Stream** is a named, persisted set of source IDs. Create one by selecting several sidebar entries and choosing *Merge*, or by dragging a source onto another.
- Rows are ordered by timestamp, with a source color stripe and a source column. Rules apply across the whole stream, and a `source:` chip narrows it.
- Required plumbing:
  - Add `DateTimeOffset? Timestamp` and `LogLevel? Level` to `Line`. Elastic already knows its timestamp field. For files, detect the timestamp once per file from parsed fields (`@timestamp`, `time`, `ts`, `timestamp`) or a leading ISO-8601/RFC3339/syslog prefix. Cache the detected extractor per source and allow an override in the source context menu.
  - Merge with a k-way merge over per-source buffers into one bounded merged buffer. Late arrivals (Elastic polling lag) are inserted within a reorder window, for example 5 s. Anything older is appended with a "late" marker rather than reshuffling what is on screen.
  - Lines without timestamps inherit the previous line's timestamp from the same source. Multi-line stack traces stay attached.
- Show a single time range per stream. It drives the Elastic sources; for file sources it acts as a display filter.

This is the largest item, but it's also the most distinctive feature a native tailer can offer over Kibana.

### 4. Structured rows: levels, columns, inspector (M) — mockup A

- **Level detection** from parsed fields (`level`, `log.level`, `severity`) or a token near the start of the line (`ERROR`, `WARN`, `E`, …). It drives a level column color, a faint row tint for errors, the level filter, and the histogram.
- **Column mode** for JSONL, logfmt and Elastic: Time | Level | Source | Message plus user-picked columns. Plain text stays single-column. A *Columns* menu adds or removes fields, and so does "Add column" in the inspector. Elastic output fields become columns instead of a string joined with spaces. Keep "composed row" as an option for people who prefer it.
- **Inspector panel** (right, toggled with `Ctrl+I`) replaces inline expand. It shows a field table for the selected row. Field actions are *Show only this value*, *Hide value*, *Highlight*, *Add column*, and *Copy*. Row actions are *Copy raw*, *Copy JSON*, *Show ±N context*, and *Bookmark*. This also replaces the "Inline context" toggle: context becomes an action on a row, not a mode.
- **Time display**: absolute or relative ("+1.204 s from previous row"), with the delta shown on hover. That's useful for latency hunting.
- Multi-select rows with Shift/Ctrl+click and copy or export the selection. The view can also be exported as a filtered `.log` or `.jsonl`.

### 5. Elastic setup as a stepped flow (M) — mockup C

Split **connection** from **source**, and give each its own short flow in a dedicated page or large side sheet instead of the Settings modal:

1. **Connection** — name, URLs, and auth. Accept an *Elastic Cloud ID* as an alternative: it encodes both the Elasticsearch and Kibana hosts, which removes the most common two-URL confusion. The connection test runs automatically (debounced) when you leave a field. Results show per endpoint (✓ Kibana 8.15 · ✓ Elasticsearch · ✓ credentials stored in keyring).
2. **Data view** — a searchable list showing the title, index pattern and timestamp field. Metadata loads on selection, and Next is disabled until it resolves. That's the same gate audit item 2 introduced, made visible.
3. **Filter** — choose a field (searchable only), then enter values as chips. Show a live `_count` for the last 15 minutes per value ("service = checkout · ~3.2k docs"), so a typo shows up immediately as "0 docs". One value gives one source; several values give several sources, or one merged stream.
4. **Columns** — two lists: available (filter, type, searchability, suggested fields first) and selected (**drag to reorder**). Underneath, preview **five real recent documents** rendered exactly as rows will appear.
5. **Name & open** — prefilled name, with **Open now** checked by default.

Plus:
- **Paste a Kibana Discover URL** to prefill everything. Discover's `_a`/`_g` Rison state contains the data view ID, query, filters, columns, and time. This is likely the single biggest time saver for anyone who already lives in Kibana.
- Drafts autosave per step, which removes the "Unsaved Elastic changes / Save / Discard / Keep editing" interstitial for incomplete setups.
- Legacy namespace fields: hide them behind *Advanced*, or migrate them into source tags. Don't show "(not queried)" prose in the main form.
- Editing a source later reopens the same steps, starting at the step being edited.

### 6. Time range: one compact control (S–M) — mockup B

- Show a toolbar pill, `⏱ Last 15 min ● live`, in place of the button plus two lines of status text.
- The popover has a preset grid, From and To **text inputs** that accept `now-15m`, ISO, `14:00`, `-2h`, or `yesterday 09:00` with a resolved preview underneath, a **Live** checkbox, and recent ranges. Keep the native pickers behind a calendar icon for mouse users.
- **One time-zone concept**: "Display times in UTC / Local". Put it in the status bar and in the picker header, applying everywhere. If anyone actually needs the *Source clock* setting to differ from the display zone, move it to Advanced. Otherwise delete it.
- **Pause** as an explicit toolbar action, separate from Follow. Pause stops appending rows to the view while buffering continues. Follow controls scrolling. Today both ideas share an "Auto-scroll" switch with an explanatory tooltip.

### 7. Timeline histogram and scroll markers (M) — mockup A

- A 40px histogram of row counts per bucket, stacked by level, above the log list. Drag on it to set the range (Elastic re-queries; files filter), click to jump, and double-click to reset. It's collapsible.
- Scrollbar markers for rule matches and errors, as in VS Code, so `n`/`N` navigation has visual context.
- Both are cheap with a custom Skia-drawn control: the data is already in the buffers.

### 8. Follow behavior and keyboard flow (S)

- When scrolled up during live tailing, show a floating **"↓ 128 new lines — jump to latest"** pill instead of silently toggling the follow state.
- **Command palette** (`Ctrl+K`) to open a file or source, add a rule, switch views, jump to a time, or toggle panels. This is the best discoverability fix for a toolbar that is now icon-only.
- Keys: `/` focuses the filter input, `n`/`N` move to the next or previous match, `j`/`k` move between rows, `g`/`G` jump to top or bottom, `f` toggles follow, `Space` pauses, `Ctrl+W` closes the view or source, `Ctrl+Tab` cycles views, and `Ctrl+1..9` selects a view. Show these in the palette next to each command.

### 9. Empty state and onboarding (S) — mockup D

Show recent files, recent streams and sources, and a **Try a sample log** button that runs the existing LogGenerator in-process into a temp file. Drag-and-drop gets a visible drop zone. The first-run hint mentions `Ctrl+K`.

### 10. Settings becomes small (S)

Once items 2, 5 and 6 land, Settings holds only Appearance (theme, density, font, wrap default, timestamp format), Limits (max lines, initial Elastic fetch), and Advanced. It can become a simple page or side sheet with left-hand navigation. That fixes the horizontal settings navigation the October re-audit flagged.

### 11. Visual polish (S, ongoing)

- Remove most inline prose. Put explanations behind `ⓘ` tooltips, and keep text only for errors and state.
- Use one spacing scale (4/8/12/16) and one control height per density. Today heights are bound individually (`SecondaryControlHeight`, `TabHeight`, `FileStripHeight`, …), and the toolbar mixes 22px icons with 12px toggle text.
- Add a log-font picker (monospace families present on the system) and line-height control.
- Give every icon button a tooltip with its shortcut key.

## Blazor Hybrid via Avalonia: recommendation

**Don't migrate.** The friction above is layout and concept modeling, and a toolkit swap fixes none of it. It would also cost real things:

- **Product identity.** The README sells HexTail as native, without a browser, WebView, JavaScript runtime, or local HTTP server. The repo history shows it has already migrated *away* from a browser-based design (offline PWA → Avalonia).
- **Hot path.** The core of the app is a high-rate, virtualized, span-highlighted log list. Today that is Skia-drawn `HighlightedTextBlock` rows. In Blazor Hybrid every append batch crosses the .NET ↔ WebView interop boundary and re-renders DOM. `<Virtualize>` handles static lists well and live-tailing lists at 100k rows poorly. You would end up hand-writing a canvas or JS renderer, which reintroduces the JS runtime.
- **Distribution.** Linux needs WebKitGTK at runtime. The current `dotnet tool` and self-contained ZIPs would gain a system dependency. That's the same class of problem as the `libsecret` requirement, only bigger.
- **Two UI stacks.** Hosting Blazor *inside* Avalonia only for setup and settings forms means two styling systems, two focus and accessibility models, and theme sync across the boundary.

What Blazor would buy (rich chips, date-range pickers, data grids, drag lists) is available natively:

| Need | Avalonia option |
| --- | --- |
| Tag/chip input, date-range picker, drawer, banner, form layout, nav menu | **Ursa.Avalonia** (+ Semi.Avalonia theme), or build on its primitives |
| NavigationView, InfoBar, TaskDialog, refined CommandBar | **FluentAvalonia** |
| Column-mode log list | Keep the custom virtualized row control. Add a shared column-width header rather than adopting a generic DataGrid, so the existing highlight span cache keeps working. |
| Histogram / scroll markers | Custom `Control.Render` with `DrawingContext`. No library needed. |
| Drag-reorder lists | `Xaml.Behaviors` (already referenced) or the existing `TabReorderBehavior` pattern |

Check each library's Avalonia 12 compatibility before committing to it. If a library lags, build the two or three missing controls (chip, range text box, drag list) in-house; each is small.

**If you still want the web stack** for its own sake (team skills, sharing code with a web dashboard), do it as a full move with a clear reason, not as a fix for the clunkiness. Prototype the live log list first, at 1k lines/s for 10 minutes on Linux, before building anything else.

## Suggested sequence

| Phase | Items | Outcome |
| --- | --- | --- |
| 1 | 1, 6, 8 (pill, palette), 9 | New shell: sidebar, single view bar, status bar, compact time pill. Immediate vertical-space and navigation win. |
| 2 | 2, 4 (levels, inspector), 10 | One filter model; settings shrink; structured inspection. |
| 3 | 5 | Elastic stepped flow, Cloud ID, Discover URL import, real previews. |
| 4 | 3, 4 (columns), 7 | Merged streams with timestamps, columns, histogram. |

Each phase ships independently. Phase 1 changes no persisted data. Phase 2 needs one config migration (item 2), and phase 4 extends `Line` without breaking existing sessions.

## Open questions

1. Is "merged streams" meant as interleaving by timestamp (assumed here), or as side-by-side panes of different sources?
2. Do people actually rely on *Source clock time zone* being different from the display zone? If not, item 6 deletes it.
3. Are Kibana Discover URLs part of the target users' workflow? That decides whether the import shortcut is phase 3 or a later nice-to-have.
4. "Blaizio": which component library is meant (Blazorise, MudBlazor, Fluent UI Blazor, or something else)? It doesn't change the recommendation, but it helps when comparing components.
