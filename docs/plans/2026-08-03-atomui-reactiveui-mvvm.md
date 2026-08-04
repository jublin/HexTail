# AtomUI + ReactiveUI MVVM Migration

## Summary

Replace the event-heavy `MainWindow` implementation with an AtomUI 6.1.2 interface backed by ReactiveUI view models and compiled XAML bindings. Preserve every existing HexTail workflow and persisted setting.

Use AtomUI for the window, command bar, buttons, inputs, tabs, drawer, alerts, empty states, color picker, icons, and theming. Keep Avalonia's `ListBox`/`VirtualizingStackPanel` for the performance-critical 100,000-line log and context views.

## Implementation Changes

1. **Bootstrap AtomUI and ReactiveUI**
   - Pin `AtomUI.Desktop.Controls` 6.1.2, `AtomUI.Desktop.Controls.ColorPicker` 6.1.2, `AtomUI.Fonts.AlibabaSans` 6.1.2, `AtomUI.Icons.AntDesign` 6.1.2, and `ReactiveUI.Avalonia` 12.0.3 through central package management.
   - Remove `Avalonia.Themes.Fluent` and the standalone Avalonia color-picker reference.
   - Configure `UseReactiveUI()`, `UseAtomUIPlatformDetect()`, `WithAtomUIDefaultOptions()`, and `UseAtomUI(...)`.

2. **Introduce the reactive presentation layer**
   - Add internal `MainWindowViewModel`, `FileTabViewModel`, `LogViewViewModel`, and `SettingsViewModel` types based on `ReactiveObject`.
   - Expose bindable collections, selection properties, derived counts/visibility, and `ReactiveCommand`s for open, close, save, search, follow, context, row selection/expansion, settings, and drawer state.
   - Use a ReactiveUI `Interaction` for the native file picker; dropped and command-line paths enter through one shared open-paths command.
   - Move tail draining to an activation-scoped reactive interval on `RxApp.MainThreadScheduler` and dispose subscriptions with the window view model.
   - Add focused `AppState` mutation methods so state changes and persistence remain paired.

3. **Build the AtomUI interface**
   - Replace the root with `atom:Window` and compiled `x:DataType` bindings.
   - Create an Ant Design command bar, closable file tabs, search controls, result tabs, inline alerts, empty state, follow/context controls, and a settings `Drawer`.
   - Represent labels and exclusions with bound item templates and commands instead of constructing controls in C#.
   - Bind drawer placement and theme/density settings to the existing persisted settings.

4. **Isolate native log and platform mechanics**
   - Move virtualized log/context rendering into a small reusable view bound to `LogViewViewModel`.
   - Preserve highlighted runs, expansion, selection, scroll-follow behavior, filtering, context navigation, and incremental append synchronization.
   - Keep only unavoidable view concerns in code-behind: storage interaction handling, drag/drop extraction, window geometry, template-level scroll detection, and scroll-to-end.
   - Reduce `MainWindow.axaml.cs` to initialization, activation, and those platform bridges.

5. **Verify and document the cutover**
   - Update native build documentation with AtomUI initialization and MVVM ownership boundaries.
   - Run tests, Release build, current-RID publish, and the streaming smoke workflow.

## Interfaces and Behavior

- Existing persistence JSON and domain/tailing contracts remain compatible.
- New presentation types remain internal to the desktop executable.
- `MainWindowViewModel.PickFiles` is the sole view interaction contract; file paths are returned to the shared open command.
- Command failures update bindable file/search error state. Reactive command exception streams must be subscribed so errors never terminate the UI pipeline.
- Platform callbacks may remain event-based only inside views; application behavior must not be implemented in those callbacks.

## Test Plan

- Preserve all existing tests.
- Add view-model tests for restore/startup paths, file selection/close, command enablement, valid and invalid searches, follow/context persistence, settings edits, error projection, and disposal.
- Add incremental synchronization tests proving append-only updates preserve collection identity, while truncation, rollover, exclusions, and topology changes reset correctly.
- Compile all XAML in Release with compiled bindings enabled.
- Smoke-test picker, drag/drop, command-line paths, multiple files/searches, append/truncate/rotate, follow behavior, context navigation/resizing, labels/exclusions, themes/densities, restart persistence, and a high-rate 100,000-line stream.

## Assumptions

- This is a full AtomUI visual refresh with behavioral parity, not a workflow redesign.
- Native Avalonia virtualization remains for log rendering.
- Published AtomUI binaries under LGPL-3.0 are accepted; AtomUI source will not be modified or vendored.
- Work continues on the existing non-protected `hextail-impl-56-luna` branch.
- Each logical task receives its own Conventional Commit. Nothing is pushed.
- Installers, navigation, plugin architecture, headless UI automation, and pixel-perfect screenshot tests remain out of scope.
