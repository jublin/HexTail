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

Each view distinguishes loading, query failure, no returned logs, no search matches, and logs hidden by global exclusions. Counts show **visible** rows and rows **loaded from source**, not the server's total matching count.

The initial fetch is bounded (10,000 newest logs by default). If the initial limit is reached, the view says that the interval may contain more logs. Narrow the interval to inspect earlier history. The application also keeps a bounded log buffer, so a long-running session does not retain every row indefinitely.

## Search, labels, and exclusions

Enter a search in the search bar under the top bar, choose **Literal** or **Regex**, and press Enter to add a search tab. Literal treats punctuation as ordinary text; Regex uses a regular expression and reports invalid patterns. The case-sensitive control applies to that search.

In **Settings → Labels & Exclusions**, labels highlight case-insensitive matches and can create search tabs. Exclusions hide matching rows from log and context views while keeping them buffered. Each rule has an explicit Literal/Regex choice; new rules default to Literal. For example, Literal `[ERROR]` matches those brackets exactly. Invalid Regex drafts show an error and do not replace the last saved valid rule. Existing rules retain their earlier matching behavior.

## Inspect rows and navigate tabs

Double-click a log row, or press Enter with its row focused, to expand selectable full text and parsed fields. Right-click, the Menu key, or Shift+F10 opens actions to toggle details, **Copy raw line**, or **Copy fields**. Ctrl+C (Command+C on macOS) copies the focused row's full raw text; add Shift to copy fields. Selecting text in expanded details keeps normal text copying available. For Elastic rows, raw text means the composed output-field row, not the original JSON document.

Long source names have full tooltips. Select any open source in the **sidebar**, and use the **x** on its row to close it. Within a source, view tabs hold **All** plus one tab per search; **All** stays first. Alt+Up or Alt+Down on a focused sidebar file moves it up or down, and Alt+Left or Alt+Right on a view tab header reorders search tabs, in both cases preserving the selection.

Keyboard shortcuts for navigating views (they do not fire while a text box has focus, except the Ctrl combinations):

- `Ctrl+Tab` and `Ctrl+Shift+Tab` select the next and previous view; `Ctrl+1` to `Ctrl+9` select a view by position.
- `Ctrl+W` closes the selected search view, or the source when **All** is selected.
- `F` toggles **Follow**, `g` jumps to the first line, and `Shift+G` jumps to the newest line and follows. `/` or `Ctrl+F` focuses the search box.

**Follow** and **Context** are toggles on each view's toolbar. Follow keeps the newest row (or match) in view; scrolling up, pressing `g`, or turning Follow off stops it. While a view is not following and new lines arrive, a **↓ N new lines** pill appears at the bottom of the list; click it to jump to the newest line and resume following. **Context** shows the neighboring buffered rows around the selected row.

**Pause** (toolbar button or `Space`) freezes the rows shown while the sources keep buffering. The status bar says **Paused — rows are buffering**, the **visible** count stops changing while the **loaded** count keeps rising, and the new-lines pill counts what has arrived since. Press `Space` or the button again to resume and catch up, or click the pill, which resumes and follows. Pause applies to the whole window, not one view.
