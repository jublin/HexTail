# Elastic Incremental Batching Implementation Plan

**Goal:** Keep Elastic polling incremental and make multiple active sources update the UI in one bounded batch without refreshing inactive views.

**Architecture:** Elastic tails retain their timestamp/ID cursor, discard every hit older than that cursor, and sort accepted hits chronologically before emitting them. `AppState` drains all source events first, coalesces line batches per source, then performs one buffer append per source and one state notification. View models keep inactive views unloaded and only raise view-level property changes for the selected view.

**Tech Stack:** .NET 10, C#, Avalonia, ReactiveUI, xUnit.

**Spec:** User request in this task.

## Global Constraints

- Preserve the existing five-minute initial Elastic lookback and ten-thousand-line initial cap.
- Preserve per-file follow state when switching files.
- Keep Elastic requests read-only and close the active PIT.
- Use conventional commits and verify with the focused and full test suites.

---

### Task 1: Make Elastic polling strictly incremental

**Files:**
- Modify: `src/HexTailSharp/Elastic/ElasticTailer.cs`
- Test: `src/HexTailSharp.Tests/Elastic/ElasticTailerTests.cs`

**Interfaces:**
- Consumes: `ElasticHit.Timestamp`, `ElasticHit.Id`, and the existing cursor fields.
- Produces: chronological `SourceLines` batches containing only unseen hits.

- [ ] Add a regression test where the second inclusive poll returns an older hit, an already-seen cursor hit, and one new hit; assert that only the new hit is emitted and that accepted lines are chronological.
- [ ] Run the focused test and confirm it fails against the current cursor filter.
- [ ] Skip hits older than the cursor, skip known IDs at the cursor timestamp, sort accepted hits by timestamp/ID, and retain cursor state across polls.
- [ ] Run the focused Elastic tests and confirm they pass.
- [ ] Commit as `fix(elastic): emit only incremental sorted hits`.

### Task 2: Coalesce source events before updating state

**Files:**
- Modify: `src/HexTailSharp/Application/AppState.cs`
- Test: `src/HexTailSharp.Tests/Application/AppStateTests.cs`

**Interfaces:**
- Consumes: the shared `ChannelReader<SourceEvent>`.
- Produces: one `FileBuffer.Append` per source per drain and one `Changed` notification per drain.

- [ ] Add a test with multiple `SourceLines` events for one source; assert one state notification and the combined ordered buffer contents.
- [ ] Run the focused application test and confirm it fails with the current per-event append behavior.
- [ ] Drain all events into source batches, preserve reset/error semantics, append each source's lines once, and notify once after the batch.
- [ ] Run the focused application tests and confirm they pass.
- [ ] Commit as `fix(state): coalesce tailer updates per source`.

### Task 3: Suppress inactive-view property notifications

**Files:**
- Modify: `src/HexTailSharp/ViewModels/FileTabViewModel.cs`
- Modify: `src/HexTailSharp/ViewModels/LogViewViewModel.cs`
- Modify: `src/HexTailSharp/ViewModels/ElasticSourceOptionViewModel.cs`
- Test: `src/HexTailSharp.Tests/ViewModels/WorkspaceViewModelTests.cs`

**Interfaces:**
- Consumes: the selected-file state already supplied by `MainWindowViewModel`.
- Produces: no row/view property notifications for inactive files and no redundant status notifications.

- [ ] Add a regression test that syncs an inactive file after an append and observes no view property changes or row materialization.
- [ ] Run the focused view-model tests and confirm it fails before the guard.
- [ ] Keep topology synchronization available, but only sync rows and raise view/file properties for the active file; make Elastic source status sync change-aware.
- [ ] Run focused and full test suites.
- [ ] Commit as `fix(ui): skip inactive tail view notifications`.

### Task 4: Push verified changes

- [ ] Review the diff and working tree.
- [ ] Run the full test suite and confirm success.
- [ ] Push the committed branch to its configured remote.
