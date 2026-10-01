# UI fix verification — October 1, 2026

All 17 findings from the [re-audit](../../2026-10-01-ui-reaudit.md) are implemented on `audit-ui`, one fix commit per finding after the approved plan.

## Checks

- Complete Release suite: **244 passed, 0 failed**. The isolated post-test dispatcher error seen during development did not recur in either complete run.
- Narrow settings: Labels, Appearance and Elastic checked at 720, 1280 and 1920 pixels, all three densities and 14/24px panel fonts. Forms stack and restore columns; actions remain inside the panel with vertical scrolling.
- Native modal input: focus enters Settings, forward/backward Tab stays inside, Control/Meta+F cannot move focus to the workspace, Escape returns focus. Draft dismissal via Escape, backdrop, button and binding retains the native dialog until Save/Discard/Keep editing.
- Draft save progress/failure, edits during save, nested field/credential changes, deletion cancellation and failed-delete rollback are covered by executable checks.
- Historical restoration verifies that the **first** request uses the saved fixed interval. Reopened sources reject queued and partial events from the old instance.
- Field discovery checks bounded blank-query choices, types/searchability, saved composition order and metadata refresh. Search placeholder/text bounds and native selected/accent states have regression coverage.
- Changed C# and XAML files pass CSharpier checks; `git diff --check` passes. An unchanged `HexTail.csproj` has a pre-existing whole-tree formatting discrepancy.

## Render evidence

19 Avalonia/Skia captures use synthetic metadata and log rows. The harness waits for the initial load before adding illustrative rows; their timestamps need not match the displayed interval. No credentials or real server were used. The temporary probe and Skia test configuration were removed after capture.

- [Empty workspace at720](empty-720.png)
- [Workspace at720](workspace-720.png), [1280](workspace-1280.png), [1920](workspace-1920.png)
- [Visible search placeholder](search-placeholder-720.png)
- Elastic and field controls in [Cyber Tail](fields-cyber-tail-1280.png), [Catppuccin](fields-catppuccin-mocha-1280.png), [Spotify](fields-spotify-1280.png)
- [Narrow Elastic form](elastic-720.png), [labels](labels-720.png), [appearance and zone guidance](appearance-720.png)
- [Draft confirmation](draft-confirmation-720.png), [native datetime picker draft](range-draft.png)

Native screen readers, operating-system scaling, Windows/macOS keyboard behavior, and a real Elastic cluster remain platform checks; headless evidence does not certify those environments.
