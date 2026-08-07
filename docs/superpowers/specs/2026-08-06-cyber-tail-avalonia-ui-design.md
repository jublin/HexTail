# Cyber Tail Avalonia UI Design

**Date:** 2026-08-06  
**Status:** Approved  
**Scope:** First of two delivery phases; this document covers the UI overhaul only.

## Problem

The current desktop shell uses AtomUI over Avalonia. Its visual language does not fit HexTail,
several interactive controls are unusable, and the recent ReactiveUI 12.1.1 package update no
longer compiles against the existing command and scheduler types.

HexTail needs a polished, dark desktop interface that works consistently on Windows, Linux, and
macOS without compromising the virtualized live-tail path. Existing workflows must remain
available. Whole-file random access and whole-file search are required, but they are a separate
data-engine phase and are not implemented by this UI overhaul.

## Decisions

- Remove AtomUI completely.
- Use Avalonia's native Fluent theme and controls.
- Ship one dark-only visual mode named **Cyber Tail**.
- Use a right-side settings inspector that is inline on wide windows and overlays on narrow ones.
- Keep ReactiveUI 12.1.1 for view-model state, asynchronous commands, validation, and error flow.
- Use XAML Behaviors only when ordinary command binding cannot express the interaction cleanly.
- Preserve the existing virtualized, bounded live-tail window during this phase.
- Deliver the whole-file navigation and search engine as a second design and implementation cycle.

## Goals

- Make every visible control keyboard- and pointer-operable.
- Preserve multi-file tabs, literal and regex searches, labels, exclusions, context, follow mode,
  session restore, file picking, file dropping, and command-line paths.
- Avoid replacing collections or rebuilding tabs during ordinary tail appends.
- Establish a stable `Lines` binding boundary that a later paged source can satisfy.
- Support Windows, Linux, and macOS equally.
- Keep the implementation native-first and small; add a custom control only when a native control
  demonstrably cannot meet behavior or accessibility requirements.

## Non-goals

- Whole-file paging, indexing, or historical search.
- Light or system theme variants.
- Runtime theme switching.
- Configurable settings-pane placement.
- A reusable component library, navigation framework, dependency-injection container, or custom
  control framework.
- Pixel-perfect screenshot tests across operating systems.

## Visual Direction

Cyber Tail uses near-black surfaces inspired by the existing HexTail identity:

- Cyan identifies primary actions, focus, and brand details.
- Green identifies healthy live/follow state.
- Magenta identifies selection and the active tab.
- Warning and error colors remain semantic rather than decorative.
- Glow is limited to the brand and small focus accents. Log text and large surfaces remain quiet to
  reduce fatigue during long sessions.
- Log content remains high-contrast monospace text with restrained separators.

The Avalonia Fluent theme supplies the complete accessible control templates. Application
resources and targeted styles supply the Cyber Tail palette, sizing, focus treatment, and control
classes. The application does not re-template every Fluent control.

## Architecture

### Application bootstrap

- Use a native Avalonia `Window`.
- Register the version-aligned `FluentTheme` and request `ThemeVariant.Dark` at startup.
- Remove AtomUI initialization, theme-manager calls, font registration, icon namespaces, control
  namespaces, and package references.
- Keep explicit construction of `AppState`, `TailerService`, and persistence. Three concrete
  services do not justify a container.

### Views

- `MainWindow` is the native XAML shell.
- `LogView` remains a focused user control containing virtualized log and context lists.
- Native `SplitView`, `ComboBox`, `TabControl`, `Button`, `TextBox`, `CheckBox`, and `ColorPicker`
  provide interaction semantics.
- FluentIcons provides the one icon vocabulary. Text glyphs are not used as substitute icons.
- Code-behind is limited to operating-system-owned concerns such as the native file picker and
  window geometry when a declarative binding or behavior is not appropriate.

### View models

Retain the existing ReactiveUI responsibilities but move the current combined file into focused
types:

- `MainWindowViewModel`: workspace lifecycle, commands, selection, and error aggregation.
- `FileTabViewModel`: one open file and its result views.
- `LogViewViewModel`: one virtualized result/context presentation.
- `SettingsViewModel`: editable global settings and persistence feedback.
- Small label and exclusion item view models remain subordinate to settings.

The ReactiveUI 12.1.1 command output and scheduler changes must be handled directly. Every async
command exposes and centrally subscribes to `ThrownExceptions`; no command may rely on an
unobserved UI-thread rethrow.

ReactiveUI does not own tail processing, file parsing, searching, buffering, or row rendering.
Those responsibilities remain in the existing core/application types.

### XAML Behaviors

Normal `Command` and `CommandParameter` bindings remain the default. Behaviors are reserved for
interactions without a useful native command surface:

- Enter-to-add-search.
- Double-tap structured-row expansion.
- Scroll-away detection that disables follow mode.
- File-drop forwarding where the behavior package covers the platform event correctly.

Each behavior or subscription detaches with the associated view. Platform behavior is verified on
all three desktop operating systems. Duplicate or unused behavior packages are removed.

### Live log boundary

The first phase keeps the existing `ObservableCollection<Line>` and virtualized `ListBox`. Normal
tail events update the collection incrementally. Search, exclusion, or expansion changes may
replace an affected view once but must not rebuild unrelated tabs or files.

The view continues to bind to a `Lines` source rather than depending on buffer internals. The
second phase can replace that source with a paged collection. No speculative single-implementation
interface is added during this phase.

## Workspace Layout

### Command bar

The top command bar contains a restrained HexTail mark, Open Files, Save Session, file count,
live/error status, and a settings button. Primary actions use cyan; status is not communicated by
color alone.

### File strip

Open files appear in a horizontally scrollable native tab strip. Each tab has an unambiguous active
state, a close icon, and a tooltip containing its full path. Tabs scroll instead of shrinking below
readable width.

### Search controls

The active file exposes:

- Query input.
- Literal/regex native `ComboBox`.
- Case-sensitive toggle.
- Search highlight color picker.
- Add Search action.

Enter invokes Add Search. Invalid regular expressions display beside the controls without clearing
the query.

### Result and context views

The All view and saved searches use a styled native tab control. Follow state, match count, and the
context toggle stay visible without taking space from log rows. The context area remains resizable
and uses the same line rendering rules.

Rows are monospace, high contrast, and compact by default. Their height is stable except when a
structured row is explicitly expanded. Streaming appends do not steal selection or cause source
replacement.

### Settings inspector

Settings use a 380-pixel right `SplitView`:

- Inline on wide windows.
- Overlay on narrow windows.
- Labels, Exclusions, and Display sections.
- Immediate application and persistence.
- Visible saved/error feedback.

Labels are editable in place with a color picker and remove action. Exclusions are editable in
place with a remove action. Each section has a clear add row. Display retains density and log font
size.

The approved dark-only/right-inspector design removes theme and pane-placement selectors from the
UI.

### Empty and responsive states

The empty workspace shows restrained branding, a primary Open Files action, and file-drop
guidance. Search controls wrap before becoming cramped. The file and result strips scroll. At the
narrow breakpoint, settings overlay instead of compressing the log viewport.

## State Flow

1. Startup loads the persisted session and normalizes obsolete appearance values.
2. Restored and command-line files are opened through `AppState`.
3. User input enters through commands or narrowly scoped behaviors.
4. Commands update `AppState`; views do not mutate tailers or persistence directly.
5. `AppState.Changed` is observed on Avalonia's UI scheduler.
6. Topology changes update file/search tabs. Ordinary tail events append or remove affected rows.
7. Settings validate, update state, persist immediately, and refresh only affected presentation.
8. Closing a file stops its tailer and detaches its view subscriptions before removing it.

Density and font-size changes may rebuild realized row presentation. Label and exclusion changes
re-evaluate affected visible collections. Neither operation recreates file tabs or blocks unrelated
views.

## Failure Handling

- File-picker cancellation is silent.
- Inaccessible paths and tailer failures appear in a persistent workspace alert containing the
  affected path.
- Invalid regex errors remain local to search controls and preserve the query.
- Settings-persistence failures appear inside the inspector and keep the edited value available for
  retry.
- Corrupt persisted state falls back to defaults without blocking startup.
- Async command failures flow through subscribed ReactiveUI error streams.
- Optional icon or style lookup failures cannot make functional controls invisible or unusable.

## Accessibility and Keyboard Interaction

- Visible focus rings on all interactive controls.
- Logical tab order through command bar, file tabs, search controls, results, and settings.
- Accessible names and tooltips for every icon-only control.
- Escape closes settings.
- Ctrl/Cmd+O opens files.
- Ctrl/Cmd+F focuses the active search input.
- Ctrl/Cmd+S saves the session.
- Selection, follow state, warnings, and errors use text/icon cues in addition to color.

## Persistence Migration

- Existing `light` and `system` theme values normalize to `dark`.
- Existing left settings placement normalizes to right.
- The obsolete serialized fields may remain readable for compatibility, but they are not editable
  in this UI and new saves use the approved values.
- All other file, search, follow, context, label, exclusion, density, font, window, and session data
  remains compatible.

## Package Strategy

- Add the Avalonia Fluent theme package at the same version as Avalonia.
- Remove all AtomUI packages.
- Keep ReactiveUI.Avalonia 12.1.1 and System.Reactive.
- Keep only the XAML Behaviors packages proven necessary by the implemented interactions.
- Keep FluentIcons.Avalonia.
- Remove Material.Icons.Avalonia and duplicate/obsolete draggable packages when unused.
- Add the version-aligned Avalonia headless test package for interaction coverage.

## Verification

### Automated

- Preserve existing domain, persistence, tailer, and view-model tests.
- Add headless Avalonia tests covering:
  - Opening and selecting every combo box.
  - Opening and closing the settings inspector.
  - Editing, adding, and removing labels and exclusions.
  - Search creation by button and Enter.
  - Keyboard shortcuts and focus movement.
  - File/search tab selection and closing.
  - Invalid-regex and persistence-error presentation.
- Verify settings updates retain line-collection and file-tab instances when appropriate.
- Verify a 100,000-line source remains virtualized without flaky timing assertions.
- Build and test on Windows, Linux, and macOS.
- Publish-smoke `win-x64`, `linux-x64`, `osx-x64`, and `osx-arm64`. Windows and Linux ARM
  packaging remain outside this phase unless the repository adds those targets separately.

### Manual smoke gate

- Empty state and file drop.
- Native file picker.
- Every combo box and color picker.
- File append, truncate, and rotate.
- Follow scrolling and automatic disable after upward scrolling.
- Multiple files and searches.
- Inline context and structured-row expansion.
- Session restore and window restart.
- Narrow and wide layouts.
- Keyboard-only navigation and visible focus.
- Final screenshot review against the approved Cyber Tail direction.

Pixel screenshot baselines are deliberately excluded because platform font and compositor
differences make them brittle. Interaction tests and deliberate visual review cover the useful
failure modes.

## Acceptance Criteria

- No AtomUI runtime or package references remain.
- Release builds complete with zero warnings.
- Automated tests pass on Windows, Linux, and macOS.
- Every dropdown, picker, tab, and settings action works with pointer and keyboard input.
- Ordinary streaming appends do not replace the log source or flicker.
- Existing workflows remain intact except for the approved dark-only and right-inspector
  migrations.
- The design matches Cyber Tail: near-black, restrained neon accents, readable log content, and
  consistent interaction states.
- Whole-file navigation and search are documented as the next phase rather than represented as
  complete.

## Next Phase

After the UI phase ships, create a separate design for disk-backed random access and whole-file
search, labels, and exclusions. That design must cover indexing, file mutation/rotation, paging,
cancellation, progress, cache invalidation, and integration with the stable `Lines` binding.

## References

- [Avalonia themes](https://docs.avaloniaui.net/docs/styling/themes)
- [Avalonia SplitView](https://docs.avaloniaui.net/controls/layout/containers/splitview)
- [Avalonia controls](https://docs.avaloniaui.net/docs/reference/controls/)
- [ReactiveUI with Avalonia](https://www.reactiveui.net/vs/avalonia/)
- [ReactiveUI commands](https://www.reactiveui.net/documentation/handbook/commands/)
- [XAML Behaviors for Avalonia](https://github.com/wieslawsoltes/Avalonia.Xaml.Behaviors)
