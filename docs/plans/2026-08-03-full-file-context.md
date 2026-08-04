# Full-file context navigation plan

Date: 2026-08-03
Status: Planned

## Outcome

Make the optional context pane a virtualized, full-file view with the same
search/global-label highlighting as the main log view. Selecting a row in the
main view becomes a go-to-line action for the context pane without disabling
free scrolling there.

## Tasks

1. Render the full filtered file in each context `ListBox` and reuse the
   existing highlighted-row rendering path.
2. Preserve the selected line and scroll the context list to it when the main
   view selection changes or the context source is rebuilt.
3. Verify the domain/application tests, Release build, and a live context
   streaming smoke test.

## Constraints

- Keep Avalonia's documented `ListBox` and `VirtualizingStackPanel` controls.
- Keep the existing persisted context settings for compatibility; they no
  longer limit the full-file navigation view.
- Do not add a view-model layer or a separate scrolling abstraction.
