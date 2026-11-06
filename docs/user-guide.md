# HexTail user guide

## Start tailing

Choose **Open file** (or press `Ctrl+O`) to tail a local log, or drop files onto the window. Open sources appear in the **sidebar**: local files under **Files** and Elastic servers with their sources under **Elastic**. The **+** next to each heading opens a file or **Settings → Elastic**. Select a source to show it; each source has an **All** view plus one view per search.

With nothing open, the workspace shows:

- **Open file…**, **Connect to Elastic** (opens **Settings → Elastic**), and **Try a sample log**.
- **Recent files** and **Recent Elastic sources**, up to 10 each. A recent file that no longer exists is listed but disabled. Recents are remembered across restarts.
- A reminder that `Ctrl+K` opens the command palette.

**Try a sample log** starts a generated log that grows about twice a second, so you can try Follow, Pause, and search without a real log. It lives in a per-process temporary directory, is removed when you close it or quit, and is never saved with the session or added to recents.

The sidebar can be hidden with its toolbar button or `Ctrl+B`, and a wide window remembers that choice. Drag its right edge to resize it (the width is saved). Below 1000 px wide the sidebar is an overlay instead: it opens on top of the workspace and closes after you select a source. Each file row shows a status dot, its line count, and a close button; hovering shows the full path. The toolbar also has the command palette button, **Pause**, and **Settings**.

The **command palette** (`Ctrl+K` or `Ctrl+Shift+P`) lists commands, the views of the selected source, open sources, and Elastic sources that are not open yet. Type to filter, use Up and Down to move, press Enter to run the highlighted item, and press `Esc` (or click outside) to close it; focus returns to where it was. Pressing `Ctrl+K` again does not close it. When an Elastic source is selected the palette also offers quick time ranges.

The status bar at the bottom shows how many sources are open, the selected view's row counts, and the **display time zone** picker. When **Pause** is on it also says **Paused — rows are buffering**.

## Settings

**Settings** (toolbar button or command palette) is a compact modal with a section list on the left: **Appearance**, **Elastic** and **Limits**. **Appearance** holds display density, log font size and theme. **Elastic** is described below. **Limits** holds:

- **Maximum lines per source** (1,000 to 1,000,000; default 100,000): the buffer cap for each source. It applies to sources opened afterwards.
- **Context lines before** and **after** (0 to 50; defaults 3 and 10): how many neighbouring rows the inspector's **Show context** lists.
- **Initial Elastic fetch** (100 to 100,000; default 10,000): how many rows an Elastic view loads first. It applies to Elastic views opened afterwards.

## Set up Elastic

1. Select **Add server**, give it a name, and enter separate **Kibana URL** and **Elasticsearch URL** values. Kibana supplies data-view metadata; Elasticsearch supplies log documents.
2. Choose **Login method**: Anonymous, Basic, or API key. Basic needs a username and password; API key needs its key. Enter a replacement secret only when changing the saved credential.
3. Select **Test connection** to check both endpoints and refresh available data views. Testing does not save edits. Fix any error before continuing.
4. Select **Add view**, name it, and choose a **Data view**. Wait for metadata to load. The timestamp field is supplied by that data view and is shown beneath the selectors; missing timestamp metadata prevents saving.
5. Choose a searchable **Filter field** and enter each **Source filter value**. The field/value pair filters the remote query using Elasticsearch phrase matching. Saved namespace fields and values are shown for reference and preserved; they are not additional query constraints.
6. Search and check **output fields**. The initial list shows up to 50 matching candidates plus selected fields. Field details show type and searchability; **Refresh fields** loads the current catalog when opening an older saved configuration. Output fields need not be searchable, but a known unsearchable filter field cannot be saved.
7. Check the selected-field order and row preview. Values are joined with spaces in selection order; blank or missing values are omitted. Unchecking and checking a field again moves it to the end. Filtering the choices does not remove checked fields.
8. Select **Save** on the server card. The card shows **Unsaved**, **Saving…**, **Saved**, or **Save failed**. Save is unavailable while prerequisites are missing. Then open its source from the sidebar's **Elastic** section, which groups each server's sources and shows their connection status, or from the command palette.

Changing an open source's saved configuration reloads its logs with the new settings while preserving its searches, follow state, and applied interval. Source and tab names include filter values so sources under the same view can be distinguished.

Closing Settings with Elastic drafts offers **Save changes**, **Discard changes**, or **Keep editing**, including when closing with Escape or the backdrop. Failed saves keep the draft available. Deleting a saved server asks for confirmation and lists affected sources; confirming removes the configuration and closes its open source tabs.

Passwords and API keys are stored in the operating-system credential vault, never in `session.json`. Authenticated configuration fails closed when the vault is unavailable. Linux needs an active Secret Service session and `libsecret`. See [native build and platform setup](native-build.md).

**Upgrading the session file.** The first launch of this version migrates `session.json` (global labels and exclusions become global filter rules, shown as dashed chips) and keeps the original as `session.v1.json.bak` in the same folder. Going back to an older build afterwards loses labels and exclusions; copy the backup over `session.json` to recover them. If `session.json` cannot be read, or was written by a newer build, HexTail copies it to `session.unreadable.bak` before saving anything new. An existing backup is never overwritten; a later, different file is kept under a timestamped name such as `session.unreadable.20260101120000.bak`.

## Set an Elastic log interval

A newly opened source initially loads the previous five minutes. Select an Elastic source and click the clock pill in the top toolbar. It shows the applied range and state, for example **Last 5 min · live**, and its tooltip shows the exact applied instants in the display zone. The pill opens the time range:

- **Quick select** presets apply immediately, with no separate Apply step: **5 min**, **15 min**, **1 hour**, **4 hours**, **24 hours**, and **7 days**. Each is a live range ending now.
- **From** and **To** are text fields. Accepted formats are:
  - `now`, and relative times such as `now-15m`, `-2h`, `-30s`, `-1d`, `-1w`, with units s, m, h, d, and w. Decimals such as `-1.5h` work.
  - A time of day such as `14:00` or `14:00:30`, meaning today.
  - `today` or `yesterday`, optionally followed by a time, such as `yesterday 09:00`.
  - A date and time such as `2026-11-05 14:00`, `2026-11-05 14:00:30`, or `2026-11-05T14:00`, or a date alone such as `2026-11-05`.
  - A full ISO timestamp with an offset or `Z`, such as `2026-11-05T14:00:00Z` or `2026-11-05T14:00:00+02:00`.
- A line under each field previews what the text resolves to, in the display zone, or explains why it cannot be read. If a field cannot be read, Apply shows an error such as **From: …** or **To: …** and the previously applied range stays in effect.
- **Live** next to **To** keeps the range open-ended so newly arriving logs keep appending, normally every two seconds. Clear it and enter a **To** time to load a fixed historical range once. Both endpoints are inclusive, and From must be at or before To.
- Press Enter in either field, or choose **Apply**. Until then the flyout shows **Unapplied — press Enter or Apply**. Applying clears the previous results and loads the requested range; the pill and status bar show what is actually loading or loaded.

The **display time zone** picker in the status bar (**Local** or **Utc**) is the single time-zone setting. It sets how typed times without an offset are read, and the zone the previews, the range pill, and the applied range are shown in. Times with an explicit offset or `Z` are never changed. Switching the zone does not change the instant a loaded range represents. A local wall-clock time that is missing or ambiguous because of a daylight-saving change is rejected; switch to Utc and type the time again. The zone never rewrites timestamps inside log messages.

Preset offsets are relative to the load's clock. Live polling appends from the loaded cursor rather than repeatedly replacing the view with a sliding window. When an open tab is restored after restart, a saved relative range resolves again; fixed endpoints are restored before the first request. Searches are restored too.

## Read source status and results

Connection and source health describe reachability and authentication: an untested or failed connection is not shown as connected. Open errors appear under the source in the sidebar, and hovering a source shows whether its logs are closed, loading, live, historical, or failed. A reachable server can still have a failed log query or an interval with no logs; read the selected tab's load status and result message as well.

Each view distinguishes loading, query failure, no returned logs, no search matches, and logs hidden by filter rules. Counts show **visible** rows and rows **loaded from source**, not the server's total matching count.

The initial fetch is bounded (10,000 newest logs by default). If the initial limit is reached, the view says that the interval may contain more logs. Narrow the interval to inspect earlier history. The application also keeps a bounded log buffer, so a long-running session does not retain every row indefinitely.

## Filters

Every source has a **filter bar** under its view tabs. It holds the filter input, the level control and a row of **chips**, one per active rule.

**Add a rule.** Type text in the input and press:

- `Enter` to **highlight** matches in the All view and in every search view.
- `Shift+Enter` to **show only** matching rows. Rows that match no show-only rule are hidden.
- `Alt+Enter` to **hide** matching rows. They stay buffered and are not deleted.

The input is cleared and a chip appears. New rules apply to the selected source and get the next color automatically. **Literal** or **Regex** picks the match mode (Literal treats punctuation as ordinary text, so Literal `[ERROR]` matches those brackets exactly); an invalid Regex shows an error under the input and adds nothing. **Aa** makes the rule case-sensitive. Rules are case-insensitive by default.

The bookmark toggle, **Pin as tab**, changes what a plain `Enter` does: instead of a highlight rule it creates a search view tab for the text, as earlier versions did. Pin as tab does not affect `Shift+Enter` or `Alt+Enter`.

**Chips.** A chip shows the rule's color (hide rules have none), an icon for hide, show-only and tab rules, and the text, shortened when long. Hover it for the action, scope and match mode.

- A **dashed** outline marks a global rule (applies to every source); a **solid** outline marks a rule for this source only.
- A chip with a bookmark icon is a pinned search tab of this source. Click it to select its tab and use its **x** to remove the tab. It has no editor.
- A disabled rule is dimmed with struck-through text and has no effect until enabled again.
- **Click** a chip to edit it in a popover: pattern, an **Enabled** checkbox, Literal/Regex, **Aa**, action (Highlight, Show only, Hide), an **All sources** switch, a color (five swatches or a custom color; hide rules have no color) and, for a global rule that is not Hide, **Show as a tab in every file**. **Apply** (or `Enter`) saves, **Cancel** (or `Esc`) discards, and **Remove** deletes the rule. An invalid pattern disables **Apply** and shows why.
- **Middle-click** a chip to disable it, and again to enable it. The popover's **Enabled** checkbox does the same from the keyboard: it takes effect when you **Apply**.
- The **x** on a chip removes the rule.
- **All sources** in the popover turns a source rule into a global rule, and back. Global rules are listed on the bar of every open source, so they can only be edited while a source is open.

**Level control.** The **T D I W E** buttons (Trace, Debug, Info, Warn, Error) choose which log levels the selected source shows. Click a button to turn that level off or on; the last level that is still on cannot be turned off. When some levels are off the control is marked as filtering. The level comes from a parsed field such as `level` or `severity` (logfmt and JSONL), otherwise from an UPPERCASE word such as `ERROR` or `WARN` near the start of the line. Lines with no recognizable level always stay visible.

**Saved filters.** When the source has rules or a level filter, a save button appears at the right of the bar. Enter a name and choose **Save** (or press `Enter`; `Esc` cancels). A saved filter stores this source's rules and its level selection; global rules are not included. Saving under an existing name, ignoring case, replaces that filter. Saved filters are listed under **Saved filters** in the sidebar and in the command palette as **Apply filter: name**. Applying one **merges** it into the selected source: its rules are added (rules already present are skipped), and the level selection is replaced unless the saved filter shows every level. It never removes rules the source already has. Use the **x** next to a saved filter in the sidebar to delete it. Saved filters are kept in `session.json`.

## Inspect a row

The inspector is a panel on the right of the log that shows the selected row in detail. Open it with `Ctrl+I` (which also closes it), by pressing Enter on a focused row, by double-clicking a row, or with **Inspect** in the row menu; the last three open it on that row. Drag the handle on its left edge to resize it (260 to 640 px). Whether it is open and how wide it is are saved with the session.

- **Raw** shows the row's full, selectable text.
- **Fields** lists the parsed fields. Each field has a **…** menu: **Show only this value**, **Hide value** and **Highlight** add a source rule for that field's value (literal and case-sensitive), and **Copy value** copies it. These rules match the value as literal text anywhere in the raw line, so a very short value such as `1` also matches unrelated rows.
- **Copy raw** copies the raw text. **Copy JSON** copies the fields as JSON and is disabled for plain-text rows.
- **Show context** toggles a list of the rows around the selected one, taken from the source's buffer regardless of filters. The numbers of rows before and after come from **Settings → Limits**. Click a context row to select it. The toggle is not saved.
- **Bookmark** marks the row. Bookmarked rows show a bookmark glyph. `m` toggles the bookmark on the selected row even when the inspector is closed, and `]` and `[` jump to the next and previous bookmarked row that is visible. Bookmarks last for the session and are not saved. The command palette has **Toggle inspector** and **Toggle bookmark on selected row**.

## Navigate rows and tabs

Select a row to work with it; right-click, the Menu key, or Shift+F10 opens actions for the row: **Inspect**, **Copy raw line**, **Toggle bookmark**, and **Copy fields**. Ctrl+C (Command+C on macOS) copies the focused row's full raw text; add Shift to copy fields. For Elastic rows, raw text means the composed output-field row, not the original JSON document.

Long source names have full tooltips. Select any open source in the **sidebar**, and use the **x** on its row to close it. Within a source, view tabs hold **All** plus one tab per search; **All** stays first. Alt+Up or Alt+Down on a focused sidebar file moves it up or down, and Alt+Left or Alt+Right on a view tab header reorders search tabs, in both cases preserving the selection.

Keyboard shortcuts for navigating views and rows (they do not fire while a text box has focus, except the Ctrl combinations):

- `Ctrl+Tab` and `Ctrl+Shift+Tab` select the next and previous view; `Ctrl+1` to `Ctrl+9` select a view by position.
- `Ctrl+W` closes the selected search view, or the source when **All** is selected.
- `F` toggles **Follow**, `g` jumps to the first line, and `Shift+G` jumps to the newest line and follows. `/` or `Ctrl+F` focuses the filter input.
- `Ctrl+I` toggles the inspector, and works while a text box has focus. `m` toggles a bookmark on the selected row, and `]` and `[` jump to the next and previous bookmarked row. `]` and `[` behave like the other row keys and stop following; `m` does not move the selection.
- `j` and `k` select the next and previous row in the current view. `n` and `N` select the next and previous row that has at least one highlight in the current view (a match of a highlight rule or search). They stop at the ends of the list rather than wrapping, and `j`, `k`, `n` and `N` do nothing while a text box has focus.

**Follow** is a toggle on each view's toolbar. Follow keeps the newest row (or match) in view; scrolling up, pressing `g`, turning Follow off, or using a row key such as `j`, `k`, `[` or `]` stops it. While a view is not following and new lines arrive, a **↓ N new lines** pill appears at the bottom of the list; click it to jump to the newest line and resume following.

**Pause** (toolbar button or `Space`) freezes the rows shown while the sources keep buffering. The status bar says **Paused — rows are buffering**, the **visible** count stops changing while the **loaded** count keeps rising, and the new-lines pill counts what has arrived since. Press `Space` or the button again to resume and catch up, or click the pill, which resumes and follows. Pause applies to the whole window, not one view.
