# HexTail UI re-audit — October 1, 2026

Branch: `audit-ui`. Application baseline: `8485f1b` (version `0.2.2`).

The toolbar, datetime pickers, applied-range feedback, and workspace search mode are substantially improved. The next work should make the displayed source and interval trustworthy across settings changes, asynchronous metadata loads, close/reopen, and session restoration.

**The numbers below replace the previous audit's implementation priorities.** Previous findings are mapped explicitly so that a future request to fix “1–3” refers to this report.

## Scope and verification

- Revisited the whole app: shell, file/source tabs, search, log rows/context, all settings sections, themes, Elastic setup/source selection, and time ranges. Traced the controls through settings persistence and actual query construction.
- Captured current Avalonia/Skia renders at 720, 1280, and 1920 pixels, plus the range flyout and Elastic forms in all three themes. Screenshots use synthetic log rows and metadata; row timestamps deliberately need not belong to the displayed interval.
- Four temporary diagnostic checks reproduced metadata ordering/refresh, active settings, stale queued events, range restoration, ambiguous source names, and global-rule matching. All four passed their assertions of the observed failures. Removed the probes and rendering changes afterward.
- The unchanged Release suite passed **201 tests**, with **50 existing analyzer warnings**. Its minimum-width checks cover all densities and a 24px window font. This does not certify operating-system display scaling.
- Calculated palette contrast from sRGB tokens. Used the W3C guidance linked in item 11 as a design benchmark, without claiming native-app accessibility certification.
- No real Elastic cluster, native credential prompt, screen reader, or Windows/macOS scaling/keyboard pass was exercised. Pointer-only actions and missing names are source findings; native focus containment remains an unverified check.

Evidence: [observations and measurements](evidence/ui-2026-10-01/observations.txt). Diagnostic source is locally retained at `/tmp/hextail-ui-reaudit/UiReauditProbe.cs`; the reproduction steps below stand on their own.

## Recheck of the six implemented findings

| Previous item | Current result | Remaining boundary |
| --- | --- | --- |
| 1. Range replacement | Apply clears rows and rejects events from older range generations; existing regression checks pass | Generation identity is reused when a source is closed/reopened: new item 1 |
| 2. Range validation | Native date/time pickers, one-clock resolution, ordering checks, safe parsing, and UTC/local interpretation are present | Native OS/DST usability still needs a manual pass |
| 3. Configuration preservation | Unedited source IDs, additional sources, and namespace values survive editor round-trip | Multiple sources remain indistinguishable by name: new item 8 |
| 4. Toolbar fit | Primary actions fit the tested widths/densities; range controls use a flyout | Settings still require horizontal navigation: new item 14 |
| 5. Time selection and state | Presets, custom interval, zone, dirty/applied state, loading, and live/historical labels are present; fixed-ended loads stop network polling | Session restore loses the interval, and empty/capped results remain unclear: new items 4 and 15 |
| 6. Explicit search mode | Workspace search defaults to Literal, exposes Regex, retains error/query feedback, and persists each search mode | Global labels/exclusions still infer regex: new item 5 |

The [720px workspace](evidence/ui-2026-10-01/workspace-720.png) and [range draft](evidence/ui-2026-10-01/range-draft.png) show the improvements. The [search placeholder](evidence/ui-2026-10-01/search-placeholder-720.png) is centered and unobstructed; its subdued color is a separate item 11 concern.

## Prioritized changes

P0: incorrect source/results. P1: misleading or blocked common work. P2: clarity, inspection, and accessibility. P3: discovery and convenience. S/M/L describe relative effort.

| New order | Priority | Change | Effort | Previous audit relationship |
| --- | --- | --- | --- | --- |
| 1 | P0 | Reject events from a closed source instance after reopening | M | New boundary of old 1 |
| 2 | P0 | Make the selected data view match the metadata used to save/query | S | Reproduced risk in old 7 |
| 3 | P0 | Apply saved Elastic settings to open sources, or visibly require reload | M | Newly reproduced |
| 4 | P1 | Restore the applied historical interval with the session | M | Old 15, raised priority |
| 5 | P1 | Make global label/exclusion matching explicit | S | Sibling flow of old 6 |
| 6 | P1 | Refresh metadata and show setup prerequisites/recovery | M | Old 7 |
| 7 | P1 | Show truthful connection state and source-open failures | M | Old 8 |
| 8 | P1 | Identify each source by its filter, including preserved sources | S | Newly reproduced after old 3 |
| 9 | P1 | Expose Elastic draft/save/delete state | M | Old 9 |
| 10 | P2 | Make field discovery and row composition understandable | M | Old 10 |
| 11 | P2 | Improve selected/placeholder differentiation and native accents | M | Old 11, additional placeholder observation |
| 12 | P2 | Finish accessible names, keyboard actions, and focus checks | M | Old 12, partly improved |
| 13 | P2 | Expose full log text and copy actions | M | Old 13 |
| 14 | P2 | Reflow narrow settings forms | M | Old 14 |
| 15 | P2 | Distinguish empty, filtered, capped, and failed results | M | Old 16, loading feedback improved |
| 16 | P3 | Make long-tab overflow and reordering discoverable | S | Old 17 |
| 17 | P3 | Update onboarding, guidance, and timezone descriptions | S | Old 18 / remaining terminology from old 5 |

### 1. Reject rows and status from a closed source instance

**Reproduced with queued events.** Open a source, leave a generation-0 row undrained, close it, and reopen the same source ID. The new tailer starts at generation 0, so `DrainTailerEvents` accepts the old row into the new tab. The probe changed the filter before reopening and still displayed `OLD FILTER ROW` from the old filter. Old errors and range-completion events use the same identity check and are exposed to the same collision.

**Change:** Give each source opening a distinct event identity in addition to its range generation. Reject rows, errors, recovery, and completion from a prior opening, including partially drained batches.

**Acceptance:** Queue A rows/status, close A, reopen with changed configuration, then drain: none of A's events affect the new tab. Existing A-range → B-range replacement continues to pass.

Evidence: [event routing](../../src/HexTail/Application/AppState.cs), [tailer generation](../../src/HexTail/Elastic/ElasticTailer.cs), [source events](../../src/HexTail/Tailing/SourceEvent.cs).

### 2. Keep data-view selection and loaded metadata consistent

**Reproduced with delayed responses.** Select A, then B; complete B's metadata request first and A's last. The selector remains B, while `ToSettings()` saves A's ID, `logs-A-*`, and `time-A`. The query can therefore target an index/timestamp different from the visible selection. There is also no loading gate to prevent saving the previously loaded metadata while the new selection is unresolved.

**Change:** Reject obsolete metadata responses, expose loading/error state, and block Save/Open until the current selected view has usable metadata. Cancellation can reduce work but must not replace the response identity check.

**Acceptance:** Every A/B response ordering leaves the selector, saved ID, title, timestamp, and field list on B. Saving during B's load cannot persist A under B's selection.

Evidence: [SelectedDataViewId/LoadDataViewAsync/ToSettings](../../src/HexTail/ViewModels/ElasticViewEditorViewModel.cs).

### 3. Make saved settings and active queries agree

**Reproduced.** Open filter `api`, save the same source as `changed-api`, then poll the already open tailer. Persisted settings contain `changed-api`; the next request still contains `api`. The tailer keeps readonly snapshots of connection, view, source, and credential. Saving updates settings/health checks but does not replace that tailer.

**Change:** Reload affected open sources using the saved configuration and their applied intervals, rejecting old events as in item 1. If automatic reload is undesirable, expose an explicit “Saved; reload this source to apply” action and the active configuration until it completes. Do not present saved settings as already active.

**Acceptance:** Filter, index, endpoint, output-field, and credential edits either change the next active query or show a clear pending-reload state. Rows from the old configuration cannot mix with the new one. Keep existing save-failure rollback of settings and credentials.

Evidence: [SaveElasticConnectionAsync](../../src/HexTail/Application/AppState.cs), [tailer configuration snapshots](../../src/HexTail/Elastic/ElasticTailer.cs).

### 4. Restore historical investigations without switching to live

**Reproduced.** Apply `2026-08-20T10:00:00Z → 2026-08-20T11:00:00Z`, save, and restore the session. The restored source uses `now-5m → now`. `PersistedElasticTab` omits the range, and restore opens the source with defaults.

**Change:** Persist the successfully applied endpoints/mode and restore them before starting the request. Preserve absolute instants exactly; document that relative presets resolve against the new clock. Persist the input/display zone if users expect that choice to survive restart.

**Acceptance:** A restored fixed-ended tab queries its saved history immediately, stays historical, and retains its searches. No default live request runs first.

Evidence: [persisted tab](../../src/HexTail/Persistence/AppConfig.cs), [SaveAsync/RestoreAsync](../../src/HexTail/Application/AppState.cs).

### 5. Remove implicit regex from global rules

**Reproduced.** A global exclusion `[ERROR]` hides `READY`, because it is interpreted as a case-insensitive regex character class. A lone `[` is silently ignored after compilation fails. The labels form describes matching as text, and neither global form exposes a mode or pattern-validation feedback. Workspace Literal/Regex controls do not affect these rules.

**Change:** Use explicit matching semantics for both labels and exclusions. Default new rules to literal text; retain existing regex intent through an explicit stored mode/migration. Show invalid-pattern errors when Regex is chosen.

**Acceptance:** Literal `[ERROR]` hides/highlights only that fragment; Regex is deliberate; invalid regex remains visible and cannot silently behave as an inactive rule.

Evidence: [CompileGlobalQuery](../../src/HexTail/Persistence/AppConfig.cs), [labels/exclusions form](../../src/HexTail/Views/SettingsPanel.axaml).

### 6. Refresh metadata and explain setup prerequisites

**Reproduced and observed.** Testing an already saved server returns two data views and says `Connected (2 data views)`, but its selector still contains only the single saved view. `TestConnectionAsync` skips refresh when URLs are unchanged; existing titles are not updated either. A successful metadata load also leaves a prior `Metadata unavailable` error visible. The detected time field remains hidden, and missing mappings produce generic validation.

**Change:** Refresh available choices without discarding saved mappings, update titles, clear resolved errors, and show the detected timestamp plus missing prerequisites beside their fields. Explain Test connection → choose data view → filter/output fields → Save/Open. Show loading and empty metadata states.

**Acceptance:** New/renamed views appear after testing unchanged endpoints; the saved selection/outputs remain intact; missing timestamp or metadata errors are actionable and clear on recovery.

Evidence: [server editor](../../src/HexTail/ViewModels/ElasticConnectionEditorViewModel.cs), [view editor](../../src/HexTail/ViewModels/ElasticViewEditorViewModel.cs), [validation](../../src/HexTail/Application/AppState.cs).

### 7. Make status truthful and opening failures visible

The [failed connection render](evidence/ui-2026-10-01/elastic-failed-1280.png) still has a green server dot beside “Connection failed.” The dot is a constant success color. Source-open exceptions are caught and reduced to reverting a checkbox; health messages are not surfaced. Health checks also describe a separate request, rather than proving the current log load succeeded.

**Change:** Bind icon/color/text to Not tested, Checking, Connected, or a specific failure. Show pending/open errors beside the source and separate server reachability from log-load state.

**Acceptance:** Missing credentials, incomplete mappings, and failed log requests give an actionable message; no failed/untested card uses a success indicator.

Evidence: [settings status](../../src/HexTail/Views/SettingsPanel.axaml), [ToggleAsync](../../src/HexTail/ViewModels/ElasticSourceOptionViewModel.cs), [health checks](../../src/HexTail/Elastic/ElasticHealthMonitor.cs).

### 8. Distinguish sources that share a view

**Reproduced.** Preserved `api/prod` and `worker/stage` sources both produce `Production logs-API service` tab names. Their tooltips also omit their filter values. Preserving multiple sources is correct, but operators cannot identify which one they opened from those labels.

**Change:** Include the active filter field/value in source choices, tab details, and close-action names. Surface preserved secondary sources and namespace configuration as readable details; do not imply a namespace constraint unless the query actually applies it.

**Acceptance:** Two sources under one view are distinguishable before opening and when selecting/closing their tabs. Renaming/saving refreshes displayed identity consistently with item 3.

Evidence: [OpenElasticSourceAsync](../../src/HexTail/Application/AppState.cs), [source option identity](../../src/HexTail/ViewModels/ElasticSourceOptionViewModel.cs), [FileStrip](../../src/HexTail/Views/FileStrip.axaml).

### 9. Expose draft, save, and delete behavior

Elastic edits still require a small save icon; other settings autosave. There is no Elastic Unsaved/Saving/Saved acknowledgement. Closing via Escape/backdrop offers no draft choice; Save Session does not submit editor drafts. Removing a saved server immediately persists removal and closes its tabs.

**Change:** Label Save, show card-level draft/save state, and offer Save/Discard/Keep editing when closing drafts. Provide deletion confirmation naming affected sources, or reliable undo. Preserve failed-save drafts and existing rollback.

**Acceptance:** Users can tell whether edits are stored and active, and accidental close/delete does not silently lose configuration.

Evidence: [server SaveAsync](../../src/HexTail/ViewModels/ElasticConnectionEditorViewModel.cs), [settings commands](../../src/HexTail/ViewModels/SettingsViewModel.cs).

### 10. Make output-field discovery understandable

Clearing the field query shows only selected fields. A new view can have an empty field area even when metadata is available. Search appears below the fields; types/searchability, selected count, output order, and composed-row preview are absent.

**Change:** Place search/browse before choices, show a bounded initial list or explicit Browse action, retain a selected summary, and explain field composition/order. Reuse existing metadata rather than introducing another discovery service.

**Acceptance:** A new view can select its first field without guessing that typing reveals it; filter/time fields communicate relevant metadata; output composition is predictable.

Evidence: [FilterFields/ToSettings](../../src/HexTail/ViewModels/ElasticViewEditorViewModel.cs), [field form](evidence/ui-2026-10-01/elastic-fields-cyber-tail.png).

### 11. Improve selected states, placeholders, and native accents

Palette contrast is unchanged:

| Theme | Selected border / surface | Selected border / border token | Main text / surface | Muted text / surface |
| --- | --- | --- | --- | --- |
| Cyber Tail | 2.72:1 | 1.76:1 | 16.53:1 | 7.33:1 |
| Catppuccin Mocha | 2.74:1 | 1.53:1 | 11.34:1 | 7.37:1 |
| Spotify | 5.02:1 | 3.84:1 | 18.73:1 | 8.93:1 |

Main/muted text is strong; keep it. The selected outline in the first two themes remains weak. These are token measurements, not a claim that every actual tab has exactly those adjacent colors. Native checked chips/settings underline remain blue/cyan even in the [green Spotify form](evidence/ui-2026-10-01/elastic-fields-spotify.png). The search placeholder is now aligned correctly but visually subdued; template text uses translucent white, with additional opacity on one placeholder element. No numeric placeholder contrast claim is made from those two template elements alone.

**Change:** Add a strong selected/focused shape cue, align Fluent checked/focus accents with each palette, and give visible placeholders adequate contrast without replacing persistent labels. Check hover, pressed, disabled, error, and grayscale states.

**Acceptance:** Active tabs and focus are recognizable in all themes; placeholder and typed search text remain visible in each density. Use [W3C non-text contrast](https://www.w3.org/WAI/WCAG22/Understanding/non-text-contrast.html) (3:1 for meaningful state cues) and [text contrast](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html) as benchmarks.

Evidence: [ThemeManager](../../src/HexTail/ThemeManager.cs), [styles](../../src/HexTail/Styles/CyberTail.axaml), [search placeholder](evidence/ui-2026-10-01/search-placeholder-720.png).

### 12. Complete keyboard and accessible naming

New range controls and search mode have names; toolbar/tab-close naming is retained. Most settings save/remove icons and form inputs still lack explicit names/label associations. Row expansion is pointer double-tap only; tab reorder is pointer-only. Modal focus entry, containment, restoration, and native picker announcements remain unverified.

**Change:** Name existing controls, associate visible labels, expose keyboard row-details actions, and verify focus and error announcements with native assistive technology. Provide keyboard reorder if that interaction is supported.

**Acceptance:** A keyboard-only pass can configure/open Elastic, apply an interval, search, inspect a row, and exit settings with useful focus restored.

Evidence: [settings controls](../../src/HexTail/Views/SettingsPanel.axaml), [row gestures](../../src/HexTail/Views/LogLineView.axaml.cs), [tab reorder](../../src/HexTail/Behaviors/TabReorderBehavior.cs).

### 13. Expose full text and copying

Long raw rows still use nonselectable text and ellipsis. Double-tap reveals parsed fields, but there is no full raw-message/copy action or visible details affordance.

**Change:** Add Copy line/fields and Expand details to the existing row action/context flow, with keyboard equivalents. Show the full raw value in details while keeping virtualization.

**Acceptance:** An operator can inspect and copy the exact long line without changing output configuration.

Evidence: [truncated row render](evidence/ui-2026-10-01/workspace-720.png), [LogLineView](../../src/HexTail/Views/LogLineView.axaml).

### 14. Reflow settings at narrow widths

The [720px labels form](evidence/ui-2026-10-01/labels-720.png) still clips Exclusions and its add action to the right; horizontal scrolling is required. Elastic's two-column form also leaves little room for long endpoint/filter text, while expanded nested cards separate fields from save/error feedback.

**Change:** Stack settings columns at narrow widths, retain vertical scrolling, and keep save/error feedback near the active edit. Collapse inactive cards where helpful.

**Acceptance:** All actions and labels are visible without horizontal navigation at 720px and larger text sizes.

Evidence: [narrow Elastic form](evidence/ui-2026-10-01/elastic-failed-720.png), [SettingsPanel](../../src/HexTail/Views/SettingsPanel.axaml).

### 15. Explain empty, filtered, capped, and failed results

The new range status handles loading/live/historical/failure, but an empty historical interval still yields a blank list. No matches and global exclusions are not distinguished from no source rows. Initial loads stop at the configured cap (10,000 by default) without an “initial limit reached” indication.

**Change:** Show context-specific empty states and returned/visible counts; disclose the initial limit when reached without claiming a total/exhaustive result count. Keep bounded fetching.

**Acceptance:** Users can distinguish no logs, no search matches, all rows excluded, and request failure. A full initial buffer cannot be mistaken for all history in the selected interval.

Evidence: [workspace/list](../../src/HexTail/Views/LogWorkspace.axaml), [initial load](../../src/HexTail/Elastic/ElasticTailer.cs).

### 16. Improve long-tab navigation

File headers have no bounded-name/overflow discovery path; dragging is the only reorder affordance. Current state-preserving selection/reordering should remain.

**Change:** Provide bounded names with full source details and discoverable overflow navigation; add a reorder hint/action if needed.

**Acceptance:** Ten long source/file tabs can be individually selected and closed at supported widths. Verify native interaction; this audit did not execute a ten-tab OS pass.

Evidence: [FileStrip](../../src/HexTail/Views/FileStrip.axaml), [search tabs](../../src/HexTail/Views/LogWorkspace.axaml).

### 17. Update onboarding and timezone descriptions

The empty workspace still offers only opening a file, and Elastic remains hidden until configured. The guide describes old server/namespace pairs and lacks the new picker/preset/live workflow. Appearance's “Time zone” remains separate from range input/display zone and does not reformat raw message timestamps.

**Change:** Add a Set up Elastic entry point, update the guide to current filters/pickers and persistence behavior, and label each timezone setting by its actual effect. Keep concise examples for fixed history versus live retrieval.

**Acceptance:** A new user can configure one source and load a specific historical interval without guessing date syntax or which timezone control applies.

Evidence: [empty workspace](../../src/HexTail/Views/LogWorkspace.axaml), [appearance](evidence/ui-2026-10-01/appearance-1280.png), [user guide](../user-guide.md).

## Recommended next batch

Implement **1–3** first, one commit each. Then **4–8** to make historical investigation and setup reliable. Finish save/field usability before the remaining whole-app presentation work. Native keyboard, screen-reader, scaling, and real-cluster checks should accompany those changes; the headless evidence does not substitute for them.

No application changes were retained by this audit.
