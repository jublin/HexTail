# Native desktop build

HexTail targets .NET 10, Avalonia 12.1.1, and AtomUI 6.1.2. It is a native desktop executable; running it does not start a browser or listen on a port.

The interactive shell uses AtomUI's Ant Design controls and theme manager. ReactiveUI view models own state projection, commands, persistence orchestration, and tail refresh. `MainWindow` only adapts platform events (storage picker, drag/drop, window geometry, and keyboard input). The log and context surfaces intentionally retain Avalonia's virtualized `ListBox` because they render the hot 100,000-line path.

AtomUI is consumed as published LGPL-3.0 binaries; its source is not modified or vendored.

## Build and run

```bash
dotnet build src/HexTailSharp.slnx
dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj
dotnet run --project src/HexTailSharp/HexTailSharp.csproj -- /path/to/app.log /path/to/other.log
```

The **Pick and tail log files** button uses the platform file picker. Files can also be dropped onto the workspace.

## Publish

Choose a runtime identifier for the target desktop platform:

```bash
dotnet publish src/HexTailSharp/HexTailSharp.csproj \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained false
```

Replace `linux-x64` with the required RID, such as `win-x64` or `osx-arm64`. The publish directory is under `build/HexTailSharp/Release/net10.0/<rid>/publish`.

## Formatting and commit hooks

The repository pins Husky.Net and CSharpier as local tools. Restore them and
install the Git hook with:

```bash
dotnet tool restore
dotnet husky install
```

The `pre-commit` hook formats staged `*.cs` files with CSharpier and re-stages
the formatter output. Run it manually with `dotnet husky run --group
pre-commit`. Set `HUSKY=0` for CI or another environment where hooks should be
skipped.

## Session data

Session state is stored as `HexTailSharp/session.json` in the OS application-data directory. It includes open paths, searches, settings, window geometry, and context-pane size. Delete that file to start with a clean session.
