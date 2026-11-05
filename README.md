# HexTail

![HexTail](assets/hextail-readme-image.png)

HexTail is a native cross-platform desktop application for watching live log
files. It opens plaintext, `.logfmt`, and `.jsonl` files, keeps multiple files and
searches in separate tabs, and renders a bounded, virtualized log view without
a browser, WebView, JavaScript runtime, or local HTTP server.

Elastic sources use separate Kibana and Elasticsearch URLs, generic
server/namespace mappings, ordered output fields, a five-minute initial
lookback, and native OS credential storage for authenticated connections.

![HexTail desktop UI](assets/hextail-ui.png)

## Features

- Live tailing with append, truncate, rotation, and missing-file recovery.
- Multiple files listed in a source sidebar, opened from the native picker,
  drag-and-drop, or the command line.
- Literal and regular-expression searches with optional case sensitivity and a
  separate highlight color for each search.
- Independent follow-tail toggles for the All view and each search view.
- Optional inline context view with its own scroll position.
- Global case-insensitive labels and exclusions from the settings pane.
- Display density, log font size, and theme settings.
- Session persistence for open files, searches, settings, window geometry, and
  context-pane size.

## Quick start

### Install from NuGet

Use the .NET 10 SDK to install HexTail as a global tool. Release packages
include the .NET runtime for Windows x64, Linux x64, and macOS x64/arm64.

```bash
dotnet tool install --global HexTail
hextail /path/to/application.log
```

### Portable downloads

GitHub releases also include self-contained ZIPs for `linux-x64`, `win-x64`,
`osx-x64`, and `osx-arm64`. Extract the ZIP and launch HexTail; no .NET
installation is required.

### Run from source

Requires the .NET 10 SDK.

```bash
dotnet restore src/HexTail.slnx
dotnet run --project src/HexTail/HexTail.csproj -- /path/to/application.log
```

You can also start without a path and use **Open**, drag files onto the window,
or pass multiple paths:

```bash
dotnet run --project src/HexTail/HexTail.csproj -- \
  /var/log/application.log /var/log/worker.log
```

## Generate test logs for development

Generate a continuously appended plaintext log for a live tailing session:

```bash
dotnet run --project src/HexTail.LogGenerator -- --output /tmp/hextail.log --interval 250 --truncate
```

Press `Ctrl+C` to stop it. Generate exactly 100 JSONL records instead:

```bash
dotnet run --project src/HexTail.LogGenerator -- --format jsonl --output /tmp/hextail.jsonl --count 100 --truncate
```

## Using HexTail

1. Open one or more files with the folder button, `Ctrl+O`/`Cmd+O`, or
   drag-and-drop. With nothing open, the empty state also offers recent files
   and Elastic sources, **Connect to Elastic**, and **Try a sample log**, a
   generated log that is kept out of the saved session and the recents list.
2. Pick a source in the sidebar, which lists open files and Elastic sources.
   Each source has an **All** view with the complete buffered tail. Toggle the
   sidebar with the toolbar button or `Ctrl+B`; below 1000 px wide it becomes an
   overlay that closes after a selection.
3. Enter a query in **Search this file** and press Enter or **Add Search**.
   Search tabs show matching lines; the All view highlights the same matches.
4. **Follow** and **Context** are on each view's toolbar. Follow keeps a view at
   the newest line or match; scrolling away shows a **↓ N new lines** pill that
   jumps back and resumes following. **Context** shows the selected line with
   its surrounding lines. **Pause** on the top toolbar freezes row updates in
   the view while logs keep buffering.
5. The status bar shows source and row counts and the display time zone
   (**Local** or **Utc**). For an Elastic source, the clock pill in the top
   toolbar (for example **Last 5 min · live**) opens the time range: quick
   presets, or typed **From** and **To** times.
6. Press `Ctrl+K` for the command palette. **Settings** manages global labels
   and exclusions, Elastic servers, and display preferences.

### Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| `Ctrl+K` / `Cmd+K` (or `Ctrl+Shift+P`) | Open the command palette: commands, views, sources |
| `Ctrl+O` / `Cmd+O` | Open files |
| `Ctrl+F` / `Cmd+F`, or `/` | Focus the search box |
| `Ctrl+B` / `Cmd+B` | Toggle the source sidebar |
| `Ctrl+W` / `Cmd+W` | Close the selected search view, or the source when **All** is selected |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next / previous view |
| `Ctrl+1`…`Ctrl+9` | Select a view |
| `F` | Toggle follow for the selected view |
| `Space` | Pause / resume view updates (logs keep buffering) |
| `g` / `Shift+G` | Jump to the first line / the newest line (and follow) |
| `Alt+Up` / `Alt+Down` | Move the focused file up or down in the sidebar |
| `Alt+Left` / `Alt+Right` | Move the focused view tab left or right |
| `Ctrl+S` / `Cmd+S` | Save the session now (it also saves automatically) |
| `Esc` | Close Settings or the command palette |

The single-key shortcuts (`/`, `F`, `Space`, `g`, `Shift+G`) do not fire while a
text box has focus. While the palette or Settings is open, the other shortcuts
are paused; `Esc` closes the palette.

### Input formats

Files with a `.logfmt` extension use the built-in `key=value` parser. Files with
a `.jsonl` extension parse JSON object fields, flattening nested objects with
dot-separated keys. All log lines remain displayed as raw text; malformed
logfmt/JSONL lines and non-object JSONL values are treated as plain text.

The default in-memory limit is 100,000 lines per file. It can be changed in
`AppSettings.MaxLines` in the application code.

### Session data

HexTail saves session state as `HexTailSharp/session.json` under the operating
system application-data directory. The file is written atomically and stores
open paths, searches, settings, window geometry, and context-pane size. Remove
that file when a clean first-launch session is needed.

## Development

Restore, build, and test the solution with:

```bash
dotnet restore src/HexTail.slnx
dotnet build src/HexTail.slnx -c Release --no-restore
dotnet test src/HexTail.Tests/HexTail.Tests.csproj -c Release
```

Publish a desktop build for a runtime identifier:

```bash
dotnet publish src/HexTail/HexTail.csproj -c Release \
  -r linux-x64 --self-contained false
```

Supported publish examples are `linux-x64`, `win-x64`, `osx-x64`, and
`osx-arm64`. See [the native build guide](docs/native-build.md) for the full
matrix, hooks, manual smoke pass, and session details.

## Project documentation

- [Architecture](docs/architecture.md) — current runtime boundary, data flow,
  component map, and limits.
- [Native build guide](docs/native-build.md) — restore, test, publish, hooks,
  and manual verification.
- [Native desktop design](docs/specs/2026-07-22-log-tail-app-design.md) —
  implemented behavior and design rationale.

Historical plans and specs may describe an earlier browser-based design. The
current implementation is the native Avalonia desktop application documented
above.

## Contributing

Keep changes scoped to the native desktop boundary and add or update tests for
behavioral changes. Before committing, restore the local tools and install the
formatting hook:

```bash
dotnet tool restore
dotnet husky install
```

The pre-commit hook formats staged C# files with CSharpier. Use the existing
Conventional Commit format, for example `fix(search): handle invalid regex`.

## License

See [LICENSE](LICENSE).
