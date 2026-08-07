# Final review fix wave report

## Status

Complete. All four Important findings from `final-review.md` are addressed in one
native-first change set. The bounded live-tail architecture remains intact, and
whole-file paging/indexing/search remains deferred.

## Changes

### CI RID restore boundary

- Removed `--no-restore` from the RID-specific publish command in
  `.github/workflows/desktop.yml`.
- This keeps the existing matrix and allows each publish to restore its own
  `net10.0/<rid>` target instead of reusing a RID-less solution restore.
- No runtime identifiers or duplicate restore steps were added.

### Incremental capped rollover

- Extended `LogViewViewModel.SyncCollection` to recognize the retained suffix of
  the current collection as the prefix of the desired capped-buffer view.
- A rollover now removes only rolled-out rows from the head and appends only new
  rows at the tail. It does not clear the stable `ObservableCollection` or raise
  `Reset` for the retained-overlap path.
- Added a capped `FileBuffer` regression that observes exactly two `Remove` and
  two `Add` notifications for a two-line rollover, with no `Reset`.
- Retained the explicit reset path used for truncation/topology replacement.

### Persistent workspace error

- Added one native `Border`/`TextBlock` workspace alert bound to
  `HasFileError`/`FileError`.
- Settings save errors remain local to the settings inspector.
- Tailer-originated selected-file errors now include the affected full path in
  the workspace message.
- Added headless coverage that opens an invalid path and asserts the persistent
  alert is visible and contains that path.

### File-tab state and close affordance

- Added a minimal `IsSelected` property on `FileTabViewModel`, with change
  notifications from the existing `SelectedFile` owner boundary.
- Added a native selected class/style to the existing file strip; no replacement
  tab framework or converter was introduced.
- Bound each file-tab tooltip to its full `Path`.
- Replaced the text `×` close glyph with the installed FluentIcons `Dismiss`
  icon. The close button tooltip and automation name both include the full path.
- Added a headless assertion that selection moves the visible selected class,
  the selected style resolves, the full-path tooltips resolve, and the close
  content is a Fluent icon with an accessible name.

## TDD evidence

The initial focused Release run failed for the intended missing behaviors:

- capped rollover emitted `Reset, Add, Add, Add` instead of bounded
  `Remove, Remove, Add, Add`;
- `FileErrorAlert` was absent;
- no file-tab element exposed the selected class.

After the minimal implementation:

- `WorkspaceViewModelTests`: 6/6 passed;
- `MainWindowInteractionTests`: 15/15 passed.

## Verification

- `dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj -c Release --no-restore`
  — 87 passed, 0 failed.
- `dotnet build src/HexTailSharp.slnx -c Release --no-restore` — 0 warnings,
  0 errors.
- Framework-dependent Release publish passed for `linux-x64`, `win-x64`,
  `osx-x64`, and `osx-arm64`.
- Pre-fix clean checkout reproduction (`restore`, then RID publish with
  `--no-restore`) failed with `NETSDK1047` because `net10.0/linux-x64` was absent.
- Post-fix temporary initialized checkout ran the workflow-equivalent clean
  `restore` -> 87-test Release suite -> `linux-x64` publish successfully.
- `git diff --check` passed.
- The obsolete source/package scan for `AtomUI`, `Material.Icons`, obsolete
  Avalonia behavior packages, `ThemeOptions`, and `MenuAlignmentOptions` returned
  no matches in `Directory.Packages.props` or `src`.
- CSharpier formatted all five touched C# files.

## Self-review

- Scope is limited to the workflow, existing native XAML/style/view-model
  boundaries, and focused regression tests.
- No package, framework, converter, collection abstraction, or whole-file engine
  was added.
- Stable `Files`, `Lines`, and `ContextLines` collection identities are preserved.
- Explicit reset behavior still covers truncation/topology changes; the new
  rollover branch only activates when reference identity proves retained overlap.
- Existing settings-local and search-local errors retain their prior ownership.
- The Task 5/Task 8 report wording was not touched because that correction was
  conditional and outside the four Important findings.

## Concerns

- Clean test compilation still emits the 19 pre-existing `xUnit1051` analyzer
  warnings documented by the final review; the Release solution build is clean.
- One temporary clean-checkout test invocation printed intermittent Avalonia/xUnit
  UI-thread ownership diagnostics after reporting all 87 tests passed with exit
  code 0. The issue did not reproduce in isolated focused runs, the working-tree
  full run, or a second fresh clean-checkout run. No production failure was
  observed.
- Native picker/drop/color-picker/tailing visual smoke remains an interactive
  release check; this headless environment does not claim it.
