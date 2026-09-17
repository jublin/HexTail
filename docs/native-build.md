# Native desktop build

See the [README](../README.md) for the user-facing quick start and feature
overview. This page covers local development, publishing, and verification.

HexTail is a native .NET 10/Avalonia 12.1.1 desktop executable. It does not
start a browser or listen on a port. The UI is native Avalonia Fluent with a
dark-only Cyber Tail theme. ReactiveUI.Avalonia.Reactive supplies the
System.Reactive-compatible commands and schedulers.

The shell uses native picker and drop interactions and a responsive settings
inspector. The log and context surfaces retain Avalonia's virtualized
lists for the bounded live-tail path.

## Build and test

```bash
dotnet restore src/HexTail.slnx
dotnet build src/HexTail.slnx -c Release --no-restore
dotnet test src/HexTail.Tests/HexTail.Tests.csproj -c Release
dotnet run --project src/HexTail/HexTail.csproj -- /path/to/app.log
```

## Publish

Publish the desktop app for each supported runtime identifier as needed:

```bash
dotnet publish src/HexTail/HexTail.csproj -c Release -r win-x64 --self-contained false
dotnet publish src/HexTail/HexTail.csproj -c Release -r linux-x64 --self-contained false
dotnet publish src/HexTail/HexTail.csproj -c Release -r osx-x64 --self-contained false
dotnet publish src/HexTail/HexTail.csproj -c Release -r osx-arm64 --self-contained false
```

The publish directory is under
`build/HexTail/Release/net10.0/<rid>/publish`.

## Manual smoke pass

Run the app with a representative sample log and verify: picker, drop, CLI
path, tabs, every combo box, search button/Enter, regex error, settings
add/edit/remove, settings error, follow/scroll-away, truncate, rotation,
session restore, and keyboard shortcuts.

CI supplies compiled/headless coverage on the other desktop operating systems;
do not claim native manual coverage that was not performed locally.

Whole-file random access and global search are deliberately deferred to the
next engine phase.

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

Session state is stored as `HexTailSharp/session.json` in the OS application-
data directory. It includes open paths, searches, settings, window geometry,
and context-pane size. Delete that file to start with a clean session.
