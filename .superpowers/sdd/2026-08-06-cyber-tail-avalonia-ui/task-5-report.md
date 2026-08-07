# Task 5 Report: Replace the Atom shell with native Avalonia

## Status and commit

- Status: PASS
- Commit: this commit, `refactor(ui): replace atom shell with native avalonia`

## Changed files

- `Directory.Packages.props`
  - Removed AtomUI, material icon, draggable, and obsolete behavior package versions.
  - Added the Avalonia headless/xUnit v3 test packages and the three exact 12.0.5 behavior packages.
- `src/HexTailSharp/HexTailSharp.csproj`
  - Replaced AtomUI references with the approved native Fluent icon and behavior package set.
- `src/HexTailSharp/App.axaml.cs`
  - Removed Atom initialization and retained dark-theme selection plus compiled XAML loading.
- `src/HexTailSharp/Program.cs`
  - Switched startup to native Avalonia platform detection and the ReactiveUI 24 integration namespace.
- `src/HexTailSharp/MainWindow.axaml`
  - Replaced the Atom shell with a native right-side `SplitView`, concrete settings sections, editable label/exclusion rows, display controls, command bar, file tabs, search controls, and the existing virtualized `LogView` content boundary.
- `src/HexTailSharp/MainWindow.axaml.cs`
  - Added injected construction, native file picking and drop handling, Control/Meta shortcuts, Escape behavior, and deterministic Overlay/Inline responsive modes.
- `src/HexTailSharp/ViewModels/MainWindowViewModel.cs`
  - Removed Atom theme/pane-placement behavior, localized settings persistence failures, merged asynchronous command errors, added Enter-event filtering, and made schedulers/polling injectable for deterministic headless tests.
- `src/HexTailSharp/ViewModels/SettingsViewModel.cs`
  - Removed theme/alignment UI properties and added settings-local saving/error status.
- `src/HexTailSharp/ViewModels/FileTabViewModel.cs`
  - Removed duplicate file-level selection/close commands in favor of parameterized window commands.
- `src/HexTailSharp/ViewModels/LogViewViewModel.cs`
  - Removed obsolete Atom imports and moved to the ReactiveUI 24 namespace.
- `src/HexTailSharp/Views/LogView.axaml`
  - Replaced Atom check/list controls with native controls while retaining both `VirtualizingStackPanel` item panels.
- `src/HexTailSharp/Views/LogView.axaml.cs`
  - Removed obsolete runtime Atom theme adaptation and kept the approved dark palette behavior.
- `src/HexTailSharp/Styles/CyberTail.axaml`
  - Qualified native control selectors required by the Avalonia XAML compiler after Atom removal.
- `src/HexTailSharp.Tests/HexTailSharp.Tests.csproj`
  - Migrated to the Avalonia headless xUnit v3 harness while retaining the Visual Studio runner required for actual discovery.
- `src/HexTailSharp.Tests/Support/TestPersistence.cs`
  - Added deterministic in-memory persistence with injectable save failures.
- `src/HexTailSharp.Tests/Support/TestWindow.cs`
  - Added the injected headless-window factory with an immediate scheduler and polling disabled.
- `src/HexTailSharp.Tests/Ui/HeadlessApp.cs`
  - Added Avalonia headless app bootstrap and disabled parallel UI tests because Avalonia application state is process-global.
- `src/HexTailSharp.Tests/Ui/AppThemeTests.cs`
  - Added dark-theme/resource loading coverage.
- `src/HexTailSharp.Tests/Ui/MainWindowInteractionTests.cs`
  - Added 13 interaction test methods / 14 discovered cases covering settings controls and save errors, editable rows, button/Enter search creation, invalid regex visibility, file-tab commands, Control/Meta shortcuts, Escape, and responsive pane modes.

## Commands and output

The initial focused TDD run failed during compilation with `CS0433` because AtomUI brought ReactiveUI 23 alongside ReactiveUI Core 24. After test discovery was corrected, the first genuinely discovered native interaction run failed 10 of 15 cases, providing the expected red state before implementation.

`rtk proxy dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --filter "AppThemeTests|MainWindowInteractionTests" --logger "console;verbosity=minimal"`

- PASS: 14 passed, 0 failed, 0 skipped; 959 ms.
- A clean test compilation emitted 18 xUnit1051 analyzer warnings in existing persistence, tailer, and app-state tests.

`rtk proxy dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --logger "console;verbosity=minimal"`

- PASS: 81 passed, 0 failed, 0 skipped; 1 s.

`rtk dotnet build src/HexTailSharp.slnx`

- PASS: 3 projects, 0 errors, 0 warnings; 0.94 s.

`rtk proxy dotnet list src/HexTailSharp/HexTailSharp.csproj package --include-transitive`

- PASS: the approved 11 top-level app packages are present; the ReactiveUI graph contains `ReactiveUI.Core` 24.1.0 and `ReactiveUI.Reactive` 24.1.0 with no ReactiveUI 23 package.

`rtk rg -n "AtomUI|AtomUI\\.|Material\\.Icons|Xaml\\.Behaviors\\.Avalonia|Interactions\\.Draggable|Interactions\\.ReactiveUI" Directory.Packages.props src`

- PASS: no matches (`rg` exit 1, expected for an empty result).

`rtk git diff --check`

- PASS: no whitespace errors.

## Concerns

- Migrating the test project to xUnit v3 exposes 18 xUnit1051 cancellation-token warnings in pre-existing tests when they are recompiled. They are unrelated to this atomic UI cutover; the solution build itself is warning-free.
- `KeyDownEventTrigger` 12.0.5 does not expose the `Key` property shown in the plan snippet. The narrow behavior passes `KeyEventArgs` to `AddSearchOnKeyCommand`, which ignores non-Enter keys and delegates Enter to the normal add command; the headless test exercises both paths.
- The headless harness disables tail polling and native picker registration through internal constructor seams. Production constructors keep polling, native storage picking, and native drop handling enabled.
- Persisted `AppSettings.Theme` and `SettingsMenuAlignment` data remain for configuration compatibility, but their removed shell/view-model properties and all Atom theme application code are gone.
