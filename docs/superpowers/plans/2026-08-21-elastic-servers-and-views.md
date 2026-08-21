# Elastic Servers and Views Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split Elastic server credentials/endpoints from nested persisted view configurations and add configurable Local/UTC time handling.

**Architecture:** Keep `ElasticConnectionSettings` as the persisted server identity for backward compatibility, add nested `ElasticViewSettings`, and normalize old flat connections into one view. Pass server and view separately through source lookup, health checks, and tailers; keep credential-vault keys on server IDs.

**Tech Stack:** C#/.NET 10, Avalonia, ReactiveUI, System.Text.Json, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-21-elastic-servers-and-views-design.md`

## Global Constraints

- Default time zone mode is Local; missing JSON properties must continue loading.
- API keys/passwords remain in the credential vault and never enter session JSON.
- Existing server IDs and source IDs remain stable during normalization.
- Use the existing HTTP client and view-model patterns; add no new dependency.
- Every task ends with a focused test and a conventional commit.

---

### Task 1: Add nested view and time-zone persistence

**Files:**
- Modify: `src/HexTailSharp/Persistence/ElasticSettings.cs`
- Modify: `src/HexTailSharp/Persistence/AppConfig.cs`
- Modify: `src/HexTailSharp/Application/AppState.cs`
- Test: `src/HexTailSharp.Tests/Persistence/JsonFileAppPersistenceTests.cs`
- Test: `src/HexTailSharp.Tests/Application/AppStateTests.cs`

- [ ] Add `ElasticViewSettings` with stable `Id`, `Name`, selected data-view metadata, field mappings, output fields, and sources; add `Views` and legacy normalization helpers while retaining server credential fields.
- [ ] Add `TimeZoneMode { Local, Utc }` and `AppSettings.TimeZoneMode = Local`.
- [ ] Normalize a flat connection into one view, trim nested values, preserve IDs, and avoid serializing credential material.
- [ ] Write failing round-trip and legacy-normalization tests, run them red, implement the model/normalization, then run focused tests green.
- [ ] Commit `feat(elastic): persist servers with nested views`.

### Task 2: Route runtime operations through server plus view

**Files:**
- Modify: `src/HexTailSharp/Tailing/LogSourceService.cs`
- Modify: `src/HexTailSharp/Application/AppState.cs`
- Modify: `src/HexTailSharp/Elastic/ElasticHealthMonitor.cs`
- Modify: `src/HexTailSharp/Elastic/ElasticTailer.cs`
- Modify: `src/HexTailSharp/ViewModels/MainWindowViewModel.cs`
- Test: `src/HexTailSharp.Tests/Application/AppStateTests.cs`
- Test: `src/HexTailSharp.Tests/Elastic/ElasticHealthMonitorTests.cs`
- Test: `src/HexTailSharp.Tests/Elastic/ElasticTailerTests.cs`

- [ ] Add server/view lookup for source IDs and pass the matched view into tailers and health checks.
- [ ] Move data-view, time, filter, output, and source reads from server-level legacy fields to the matched view.
- [ ] Preserve vault reads/writes by server ID and keep server deletion as the only credential deletion path.
- [ ] Add tests proving a nested view opens, health checks use its selected data-view, and migrated source IDs remain openable.
- [ ] Commit `refactor(elastic): route sources through nested views`.

### Task 3: Add configurable time-zone behavior

**Files:**
- Modify: `src/HexTailSharp/Application/AppState.cs`
- Modify: `src/HexTailSharp/Elastic/ElasticTailer.cs`
- Modify: `src/HexTailSharp/Elastic/ElasticHealthMonitor.cs`
- Modify: `src/HexTailSharp/Elastic/ElasticApiClient.cs`
- Modify: `src/HexTailSharp/ViewModels/SettingsViewModel.cs`
- Modify: `src/HexTailSharp/Views/SettingsPanel.axaml`
- Test: `src/HexTailSharp.Tests/Elastic/ElasticTailerTests.cs`
- Test: `src/HexTailSharp.Tests/ViewModels/ElasticSettingsViewModelTests.cs`

- [ ] Add a Local/UTC settings selector under Appearance and expose the persisted enum through `SettingsViewModel`.
- [ ] Use the configured zone for relative range evaluation and diagnostic timestamps; keep the selected instant equivalent across zones.
- [ ] Add tests for Local default, UTC selection, and configured-zone time evaluation.
- [ ] Commit `feat(settings): configure application time zone`.

### Task 4: Restructure the Elastic settings UI into servers and views

**Files:**
- Modify: `src/HexTailSharp/ViewModels/ElasticConnectionEditorViewModel.cs`
- Create: `src/HexTailSharp/ViewModels/ElasticViewEditorViewModel.cs`
- Modify: `src/HexTailSharp/ViewModels/SettingsViewModel.cs`
- Modify: `src/HexTailSharp/Views/SettingsPanel.axaml`
- Test: `src/HexTailSharp.Tests/ViewModels/ElasticSettingsViewModelTests.cs`
- Test: `src/HexTailSharp.Tests/Ui/MainWindowInteractionTests.cs`

- [ ] Split server editor fields from view editor fields; preserve selected data-view ID/title when syncing and saving.
- [ ] Add server/view add/remove commands and aggregate server settings without clearing existing editor values during state notifications.
- [ ] Render outer server expanders and nested view expanders headed by view name; put data-view, filter, output, and source controls inside the view expander.
- [ ] Add tests that select a data view, save, resync, and observe the same view selection and field values.
- [ ] Commit `feat(ui): organize Elastic servers and views`.

### Task 5: Full verification and push

- [ ] Run `dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --no-restore`.
- [ ] Run `dotnet build src/HexTailSharp/HexTailSharp.csproj --no-restore -c Release`.
- [ ] Run `git diff --check` and confirm the worktree contains only intended changes.
- [ ] Push `feat/elastic-search-tail` to `origin`.
