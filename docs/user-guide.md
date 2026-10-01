# HexTail user guide

## Start tailing

Choose **Open file** to tail a local log, or **Set up Elastic** in the empty workspace to open **Settings → Elastic**. The toolbar also provides file opening, Settings, and the Elastic source menu.

## Set up Elastic

1. Select **Add server**, give it a name, and enter separate **Kibana URL** and **Elasticsearch URL** values. Kibana supplies data-view metadata; Elasticsearch supplies log documents.
2. Choose **Login method**: Anonymous, Basic, or API key. Basic needs a username and password; API key needs its key. Enter a replacement secret only when changing the saved credential.
3. Select **Test connection** to check both endpoints and refresh available data views. Testing does not save edits. Fix any error before continuing.
4. Select **Add view**, name it, and choose a **Data view**. Wait for metadata to load. The timestamp field is supplied by that data view and is shown beneath the selectors; missing timestamp metadata prevents saving.
5. Choose a searchable **Filter field** and enter each **Source filter value**. The field/value pair filters the remote query using Elasticsearch phrase matching. Saved namespace fields and values are shown for reference and preserved; they are not additional query constraints.
6. Search and check **output fields**. The initial list shows up to 50 matching candidates plus selected fields. Field details show type and searchability; **Refresh fields** loads the current catalog when opening an older saved configuration. Output fields need not be searchable, but a known unsearchable filter field cannot be saved.
7. Check the selected-field order and row preview. Values are joined with spaces in selection order; blank or missing values are omitted. Unchecking and checking a field again moves it to the end. Filtering the choices does not remove checked fields.
8. Select **Save** on the server card. The card shows **Unsaved**, **Saving…**, **Saved**, or **Save failed**. Save is unavailable while prerequisites are missing. Then open its source from the toolbar's **Elastic** menu.

Changing an open source's saved configuration reloads its logs with the new settings while preserving its searches, follow state, and applied interval. Source and tab names include filter values so sources under the same view can be distinguished.

Closing Settings with Elastic drafts offers **Save changes**, **Discard changes**, or **Keep editing**, including when closing with Escape or the backdrop. Failed saves keep the draft available. Deleting a saved server asks for confirmation and lists affected sources; confirming removes the configuration and closes its open source tabs.

Passwords and API keys are stored in the operating-system credential vault, never in `session.json`. Authenticated configuration fails closed when the vault is unavailable. Linux needs an active Secret Service session and `libsecret`. See [native build and platform setup](native-build.md).

## Set an Elastic log interval

A newly opened source initially loads the previous five minutes. Select its tab, then **Time range…**:

- Choose **Last 5 minutes**, **Last 15 minutes**, **Last hour**, or **Last 24 hours**, or use **Custom interval** with the native From date and time pickers.
- Choose **Input and interval display time zone**: UTC or this computer's Local zone. This choice belongs to the source tab and is saved with its interval. Changing the display zone preserves the represented instants. Ambiguous or missing local times during daylight-saving changes require UTC.
- Keep **To now (live logs)** checked to append newly arriving logs, normally every two seconds. Uncheck it and select a To date and time to load a fixed historical interval once. Both endpoints are inclusive.
- Select **Apply**. Editing a picker or preset shows **Unapplied changes** until applied. Apply clears the previous results and loads the requested interval; the applied summary and status show what is actually loading or loaded. From must be at or before To.

Preset offsets are relative to the load's clock. Live polling appends from the loaded cursor rather than repeatedly replacing the view with a sliding window. When an open tab is restored after restart, a saved relative preset resolves again; fixed date/time endpoints are restored before the first request. Searches and the range's zone are restored too.

**Appearance → Source clock time zone** selects the UTC or local clock offset used for source requests and status times. Both clocks represent the same current instant. The range picker's input/display zone is a separate setting. Neither setting rewrites timestamps embedded in log messages.

## Read source status and results

Connection and source health describe reachability and authentication: an untested or failed connection is not shown as connected. Opening progress and open errors appear beside the source. A reachable server can still have a failed log query or an interval with no logs; read the selected tab's load status and result message as well.

Each view distinguishes loading, query failure, no returned logs, no search matches, and logs hidden by global exclusions. Counts show **visible** rows and rows **loaded from source**, not the server's total matching count.

The initial fetch is bounded (10,000 newest logs by default). If the initial limit is reached, the view says that the interval may contain more logs. Narrow the interval to inspect earlier history. The application also keeps a bounded log buffer, so a long-running session does not retain every row indefinitely.

## Search, labels, and exclusions

Enter a search in the toolbar, choose **Literal** or **Regex**, and press Enter to add a search tab. Literal treats punctuation as ordinary text; Regex uses a regular expression and reports invalid patterns. The case-sensitive control applies to that search.

In **Settings → Labels & Exclusions**, labels highlight case-insensitive matches and can create search tabs. Exclusions hide matching rows from log and context views while keeping them buffered. Each rule has an explicit Literal/Regex choice; new rules default to Literal. For example, Literal `[ERROR]` matches those brackets exactly. Invalid Regex drafts show an error and do not replace the last saved valid rule. Existing rules retain their earlier matching behavior.

## Inspect rows and navigate tabs

Double-click a log row, or press Enter with its row focused, to expand selectable full text and parsed fields. Right-click, the Menu key, or Shift+F10 opens actions to toggle details, **Copy raw line**, or **Copy fields**. Ctrl+C (Command+C on macOS) copies the focused row's full raw text; add Shift to copy fields. Selecting text in expanded details keeps normal text copying available. For Elastic rows, raw text means the composed output-field row, not the original JSON document.

Long source names have full tooltips. Use **Tabs** to choose any open source and its adjacent close action to close the selected tab. Alt+Left or Alt+Right on a tab header reorders it while preserving selection. Search tabs can also be reordered; **All** stays first. Auto-scroll follows new rows, and the context controls expose neighboring buffered rows around a selected match.
