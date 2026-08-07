# Native Avalonia desktop migration plan

Date: 2026-08-03  
Status: Implemented

## Outcome

Replace the Blazor WebAssembly PWA with one native Avalonia desktop application targeting .NET 10 and Avalonia 12.1.1. Preserve the current HexTail workflow and visual hierarchy without hosting HTML, JavaScript, Blazor, or a WebView.

This is an Avalonia rewrite, not WPF: Avalonia has its own XAML, controls, rendering, and desktop lifetime. Windows, macOS, and Linux remain supported from one UI project.

## Constraints

- Pin all Avalonia packages to `12.1.1` through central package management. The version is present on the official NuGet feed as of this plan.
- Use documented Avalonia controls and platform services; do not introduce a third-party control suite, WebView, embedded server, or MVVM framework.
- Keep `Domain`, `Tailing`, and the platform-neutral parts of `Application` intact.
- Keep the current dark, dense log-tool appearance through Avalonia resources and styles rather than reproducing Radzen themes.
- Keep changes buildable and commit each task separately using Conventional Commits.

## Native control map

| Current surface | Avalonia replacement |
| --- | --- |
| Browser page and Radzen layout | `Window`, `Grid`, `Border`, and `StackPanel` |
| Settings sidebar | `SplitView` with a `ListBox` section selector |
| File and result tabs | `TabControl` and `TabItem`; close buttons are composed in the tab header |
| Search controls | `TextBox`, `ComboBox`, `CheckBox`, `ColorPicker`, and `Button` |
| Log and context rows | Virtualized `ListBox` using `VirtualizingStackPanel`, with `TextBlock`/`Run` highlights |
| Resizable context pane | `Grid` rows and `GridSplitter` |
| Errors and empty states | Styled `Border` and `TextBlock` |
| File selection | `TopLevel.StorageProvider.OpenFilePickerAsync` |
| File drag-and-drop | Avalonia `DragDrop` events and file data format |
| Runtime theme | `FluentTheme`, `RequestedThemeVariant`, and app resource brushes |

Control references: [controls](https://docs.avaloniaui.net/controls), [SplitView](https://docs.avaloniaui.net/controls/layout/containers/splitview), [GridSplitter](https://docs.avaloniaui.net/controls/layout/panels/gridsplitter), [ColorPicker](https://docs.avaloniaui.net/controls/input/selectors/colorpicker), [storage provider](https://docs.avaloniaui.net/docs/services/storage/storage-provider), and [drag-and-drop](https://docs.avaloniaui.net/docs/input-interaction/drag-and-drop).

## Task 1: Replace the web host with a desktop foundation

- Convert `HexTailSharp.csproj` from the Blazor WebAssembly SDK to a .NET executable using `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, and `Avalonia.Controls.ColorPicker` 12.1.1.
- Add the standard Avalonia desktop entry point, `App.axaml`, and an initially buildable `MainWindow.axaml` plus code-behind.
- Construct the existing `TailerService`, persistence implementation, and `AppState` explicitly at startup. A DI container adds nothing for three concrete services.
- Pass command-line file paths from the classic desktop lifetime to `AppState.OpenFileAsync`.
- Remove the Blazor/Radzen package references, Razor files, web assets, launch profile, and browser-only methods from `AppState`.
- Delete `HexTailSharp.Tool`; remove it and its tests from the solution because a loopback HTTP host has no purpose in a desktop application.
- Update VS Code launch/tasks to build, test, run, and publish the desktop executable.
- Verify with `dotnet build src/HexTailSharp.slnx` and `dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj`.

Commit: `refactor(platform): replace pwa host with avalonia desktop`

## Task 2: Replace browser persistence with local JSON

- Replace `BrowserLocalStoragePersistence` with `JsonFileAppPersistence` using the existing `AppConfigJson` serializer.
- Store the session under the OS application-data directory, create its parent directory on first save, and write through a temporary file before replacement to avoid corrupting the last good session.
- Treat missing or malformed configuration as a clean first launch, matching current behavior.
- Persist and restore open file paths, searches, follow state, settings, selected file, window size/position, and context-pane size.
- Narrow the old Radzen theme catalog to `System`, `Light`, and `Dark`; map the legacy default to `Dark`. Browser `localStorage` is intentionally not migrated because the native process cannot reliably discover a browser profile.
- Add one focused persistence round-trip/corrupt-file test using a temporary directory.

Commit: `refactor(persistence): store desktop session as json`

## Task 3: Build the native workspace

- Recreate the current toolbar, file tabs, search toolbar, result tabs, follow toggles, line count, settings pane, and inline context pane in `MainWindow.axaml`.
- Keep event handling in the window code-behind and call `AppState` directly. A one-window application does not justify view-model wrappers, commands, or a navigation framework.
- Bind file and search tabs to the existing state collections; refresh the UI from `AppState.Changed` on the Avalonia dispatcher.
- Drain tailer events with one dispatcher timer and update only the visible file/view.
- Render log rows through a virtualized `ListBox`; generate `Run` inlines from existing search/label ranges so overlapping ranges retain the current first-range-wins behavior.
- Preserve line selection, double-click structured-field expansion, per-view follow behavior, exclusions, invalid-regex feedback, close-file behavior, and the resizable context view.
- Verify with the existing domain/application/tailer suite plus an application build that compiles all XAML.

Commit: `feat(ui): rebuild log workspace with native avalonia controls`

## Task 4: Add native desktop interaction

- Open multiple logs with `StorageProvider.OpenFilePickerAsync`; pass local paths to the existing `FileTailer` rather than reading snapshots.
- Accept file drops on the main workspace through Avalonia's documented file drag-and-drop format.
- Open positional command-line paths on startup and select the last successfully opened file.
- Follow the active log/search by scrolling its `ListBox` to the final item after new tailer events; disable follow when the user scrolls away from the end.
- Keep the existing Save action as an explicit session save; it does not export log content.
- Surface file/tailer failures in the existing inline error area; no toast dependency.
- Manually smoke-test picker, drag/drop, startup arguments, append, truncate, rotate, follow, and close on the development OS.

Commit: `feat(desktop): add native file interactions`

## Task 5: Restore the PWA look

- Define a small resource palette for the dark slate surfaces, amber accent, borders, muted text, error state, and monospace log rows used by the PWA.
- Apply shared styles for comfortable/cozy/compact spacing and the four existing log font sizes.
- Support system/light/dark theme variants while keeping dark as the default.
- Preserve keyboard focus indicators, accessible labels, keyboard-operable tabs/buttons, and `GridSplitter` keyboard resizing.

Commit: `style(ui): match the pwa desktop appearance`

## Task 6: Document and verify the cutover

- Remove obsolete PWA/tool installation documentation and update the primary design/run documentation for native desktop execution and RID-based `dotnet publish` output. Installer packaging remains out of scope until a distribution target is chosen.
- Run `dotnet test`, a Release build, and one Release publish for the current runtime identifier; confirm the published app starts without a browser or listening socket.

Commit: `docs(desktop): document native build and publish`

## Acceptance criteria

- Starting HexTail opens a native Avalonia window and never starts a browser, WebView, HTTP server, or JavaScript runtime.
- File picker, file drop, and command-line paths all start the existing native tailer.
- Multiple file tabs and multiple search tabs retain independent selection and follow state.
- Literal/regex search, case sensitivity, colored highlights, global labels/exclusions, logfmt expansion, and inline context behave as they do in the PWA.
- The log list remains responsive at the configured 100,000-line cap through Avalonia virtualization.
- Session and window state survive restart in a local JSON file.
- The solution has no Blazor, Radzen, service-worker, browser-storage, or loopback-tool code left.
- Tests, Release build, and a RID publish pass on .NET 10 with Avalonia 12.1.1.

## Deliberately deferred

- Native installers, signing, auto-update, tray integration, and platform-specific menus. Add them only after distribution requirements exist.
- A separate view-model layer or UI component library. Add either only when the single-window code-behind becomes measurably hard to maintain.
- Automated pixel/UI tests. The compiled XAML, state tests, and manual desktop smoke pass cover the migration without introducing a brittle test harness.
