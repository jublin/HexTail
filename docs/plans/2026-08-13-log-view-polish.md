# Log View Polish Implementation Plan

**Goal:** Make log text readable, collapse the context pane correctly, reorder file tabs by drag, and provide an editor-style automatic search bar with an inline case toggle.

**Architecture:** Keep the current Avalonia views and ReactiveUI view models. Use the existing compiled-query model with a small automatic mode detector, bind the context row height to the existing visibility state, and use `ListReorderDragBehavior` on the existing file-tab `ItemsControl`.

**Tech Stack:** .NET 10, Avalonia 12.1.1, ReactiveUI, Xaml.Behaviors 12.0.5, xUnit v3.

## Tasks

1. Add regression tests for segment foreground inheritance, context row collapse, automatic search mode selection, and the inline case-toggle contract.
2. Make normal log segments explicitly inherit the row text brush and collapse the context grid row when hidden.
3. Add automatic search-mode detection and replace the match-mode dropdown/checkbox layout with an editor-style inline `Aa` toggle.
4. Add `Xaml.Behaviors.Interactions.Draggable` and attach `ListReorderDragBehavior` to the file tabs.
5. Run focused tests, the full test suite, and a Release build.
