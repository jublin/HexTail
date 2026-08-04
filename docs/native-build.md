# Native desktop build

HexTail targets .NET 10 and Avalonia 12.1.1. It is a native desktop executable; running it does not start a browser or listen on a port.

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

## Session data

Session state is stored as `HexTailSharp/session.json` in the OS application-data directory. It includes open paths, searches, settings, window geometry, and context-pane size. Delete that file to start with a clean session.
