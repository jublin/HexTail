# Fix the October 1 UI re-audit

## Goal and boundaries

Implement all 17 findings in [the approved re-audit](../audits/2026-10-01-ui-reaudit.md) on `audit-ui`. Preserve native date/time pickers, visible search input/placeholder alignment, bounded fetching, virtualization, saved searches, and credential-vault storage. Extend the existing application; add no UI framework or dependencies. Commit this plan first, then one conventional `fix` commit per finding. Keep commits local.

The audit supplies the product changes and acceptance criteria. Implementation choices below make those changes concrete. Native screen-reader/OS scaling and real-cluster checks cannot be certified by this environment; record that limit, without leaving repository changes unfinished.

## Implementation units

### 1. Reject closed-instance events

- Files: `Tailing/SourceEvent.cs`, `Elastic/ElasticTailer.cs`, `Application/AppState.cs`, relevant application/tailer tests.
- Give each Elastic tailer opening a unique identity; carry and check it alongside range generation, including partial channel batches and completion/error events.
- Proof first: queue rows/status from an old opening, reopen the same source, and prove rejection while accepting current events. Keep range replacement tests.

### 2. Keep selected metadata consistent

- Files: `ViewModels/ElasticViewEditorViewModel.cs`, metadata fake and view-model tests.
- Reject stale metadata responses, expose loading/current-selection validity, and prevent unresolved views from producing saveable settings. Keep selected output fields.
- Proof first: delayed A/B responses in both orders and save during a load.

### 3. Reload saved settings in open sources

- Depends on 1. Files: `Application/AppState.cs`, `FileTabState.cs`, relevant tests.
- Replace affected tailers after a successful settings save, preserving tab/search state and applied range, clearing old results and rejecting old events. Retain save-failure rollback.
- Proof first: save a filter edit while open and inspect the next request; verify preserved range/searches and failure rollback.

### 4. Restore applied historical ranges

- Depends on 3. Files: persistence model, state restore/save, tailer startup, range view-model/tests.
- Persist endpoints and input/display zone. Supply restored endpoints before the tailer starts, keeping old sessions on their prior default range. Validate saved endpoints through the normal parser.
- Proof first: round-trip a fixed interval and prove the first request uses it; relative ranges resolve at restart.

### 5. Make global rules explicit

- Files: `Persistence/AppConfig.cs`, state normalization, global-label search creation, `SettingsViewModel.cs`, settings XAML/tests.
- Store explicit modes for global labels/exclusions. Migrate old inferred modes without losing intended regex rules; default new rules to Literal and expose Regex validation in the editor.
- Proof first: literal `[ERROR]` does not hide READY; deliberate regex works; legacy rules migrate; invalid regex gives visible feedback.

### 6. Refresh metadata and prerequisites

- Depends on 2. Files: Elastic connection/view editors, settings XAML and tests.
- Refresh choices/titles on every successful test while preserving mappings. Show timestamp, current loading/missing prerequisites, and clear recovered errors. Disable saving unresolved/incomplete views.
- Proof first: unchanged URLs discover new/renamed views without losing saved mappings; failure then success clears the error.

### 7. Show truthful connection/open status

- Depends on 6. Files: connection/source option editors, main VM/source flyout, settings XAML/tests.
- Bind status icon/color/text to actual state; show health detail, opening progress, and actionable open failures. Distinguish reachability from log-load failure.
- Proof first: missing credentials/open failure keeps an error beside the source; failed/untested cards have no success indication.

### 8. Identify distinct sources

- Depends on 3, 7. Files: source descriptor/state, source option sync, source/tab controls/tests.
- Include filter values and readable preserved-source/namespace details in choices and tab/close names; refresh identity after saves. Describe only constraints actually applied.
- Proof first: two sources under the same view have distinct names and details.

### 9. Show drafts and protect deletion

- Depends on 6. Files: settings/connection editors, main settings-close flow, settings/main XAML/tests.
- Explicit Save, Unsaved/Saving/Saved/Failed state. On closing drafts offer Save/Discard/Keep editing. Confirm saved-server deletion with affected source details. Keep failed drafts intact.
- Proof first: close a draft via button/backdrop/Escape; discard restores saved values; failed save stays open; cancel deletion preserves config/tabs.

### 10. Make fields discoverable

- Depends on 6. Files: field/view editors, settings XAML/tests.
- Move field search above choices, show bounded initial choices plus selected count/type/searchability, and provide predictable output ordering and a composed-row preview.
- Proof first: first field is discoverable with blank search and selected fields survive searching/metadata refresh.

### 11. Improve theme differentiation

- Files: `ThemeManager.cs`, app/shared styles, theme/contrast tests.
- Strengthen selected-state colors/shapes, align native Fluent accent/focus/checked states with the active palette, and brighten visible placeholders while retaining centered alignment and density heights.
- Styling exception to proof-first: compare palette ratios and rendered controls in all themes; use existing theme tests for resource changes.

### 12. Complete accessibility/keyboard flow

- Depends on 7–11, 13, 14, 16. Files: main/settings/search/range controls, log/tab keyboard behavior/tests.
- Associate/name controls, expose keyboard details/reorder actions, and keep modal focus entry/return usable. Verify repository-level names and keyboard commands; report native assistive-technology limits.
- Proof first for keyboard/focus behavior; direct inspection/render for label-only changes.

### 13. Inspect and copy full rows

- Files: `Views/LogLineView.*`, `ViewModels/LogLineViewModel.cs`, relevant UI tests.
- Add row context actions and keyboard equivalents for full raw text, copy raw line/fields, and expand details. Reuse existing expansion state and native clipboard. Preserve virtualization.
- Proof first: long raw text remains available exactly and actions address the selected row.

### 14. Reflow narrow settings

- Depends on 5–10. Files: settings layout/styles/code-behind and UI checks.
- Stack columns below a practical width; keep vertical scrolling and nearby save/error controls. Do not require horizontal scrolling to reach actions.
- Styling/layout verification: renders and action bounds at 720/1280/1920, every density and larger fonts.

### 15. Explain result states and caps

- Depends on 1, 4, 5. Files: range-completion event/tailer, tab/log/main VMs, log workspace/list XAML/tests.
- Track initial limit reached, show source/visible counts and separate loading, failure, no logs, no matches, and excluded-all states. Do not claim a total count the API does not supply.
- Proof first: empty history, zero matches, exclusions, failure, and initial cap communicate different states.

### 16. Navigate long tabs

- Depends on 8. Files: file strip, main/tab commands, reorder behavior/tests.
- Bound visible names, retain full tooltips, offer an overflow/source selector and keyboard reorder. Preserve selected identity and fixed All search tab.
- Proof first: ten long tabs remain selectable/closable at minimum width; moving a tab preserves selection.

### 17. Update onboarding and guidance

- Depends on all behavior/layout changes. Files: empty workspace, appearance labels, `docs/user-guide.md` and relevant UI check.
- Add Set up Elastic beside Open file; document actual setup, presets/custom range, timezone effects, saved/live history, filters, explicit matching, result caps, and row/tab actions.
- Verify links/text against implemented controls and render the empty state. No test for prose-only changes.

## Execution and verification

Use native execution. Independent units may use shared-workspace subagents only with exclusive file ownership; workers do not build, modify Git, or commit. The host owns proof runs, integration, and all commits. Start an independent wave with units 1, 2, 11, and 13; serialize the settings/state units that share files.

Use existing xUnit/Avalonia headless tests for meaningful behavior regressions. Run focused checks per unit, the full Release suite after coordinated changes, and current Skia renders for all three themes and supported widths. Inspect actual diffs and remove temporary probes. Review the completed branch and resolve actionable findings before finishing. Save durable context and report test evidence and native verification limits.

## Definition of done

All 17 acceptance criteria are represented in code and verified through focused tests, UI evidence, or documentation checks as appropriate. There is a separate fix commit per finding, no unfinished experimental code, and no unrelated pre-existing files are included. The complete Release suite passes. Native-only limitations are explicit; no push/PR is part of this request.
