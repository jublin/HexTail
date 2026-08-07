# Task 6 Report: Fix duplicate context persistence

## Scope

Added `WorkspaceViewModelTests.SetShowContext_PersistsOnce`, using `TestPersistence`, an `AppState` backed by `TailerService`, and `ImmediateScheduler`. The test opens a temporary file, toggles context visibility once, and asserts that the persistence save count increases by exactly one.

`MainWindowViewModel.SetShowContextAsync` was inspected before editing. The implementation already contained exactly one `_state.SetShowContextAsync(file.Model, value)` call in the Task 5 baseline, so no production-code edit was necessary; the regression test now protects that invariant.

The focused test needs explicit ReactiveUI core initialization because it is a plain `[Fact]` and may run independently of the Avalonia headless application fixture. This keeps the brief's immediate-scheduler test isolated and runnable by its filter.

## Verification

- `dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --filter SetShowContext_PersistsOnce`: 1 passed.
- `dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --no-restore`: 82 passed, 0 failed, 0 skipped.
- `rtk` emitted a contradictory full-test summary (`82 passed, 1 failed`) despite the test runner reporting 0 failures and a nonzero wrapper result; the direct `dotnet test --no-restore` command exited 0 and is the authoritative rerun.

## Review

The change is limited to the requested regression test. No unrelated UI or package files were changed. The only existing warning output is the repository's xUnit cancellation-token analyzer warnings on the focused run; the direct full test run produced no warnings.
