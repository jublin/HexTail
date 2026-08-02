# Offline installation

Build the tool on a machine with the .NET 10 SDK:

```sh
dotnet pack src/HexTailSharp.Tool/HexTailSharp.Tool.csproj -c Release -o artifacts/nupkg
```

Copy `HexTailSharp.Tool.0.1.0-alpha.nupkg` to the offline machine, then install from that directory:

```sh
dotnet tool install --global --add-source <package-directory> HexTailSharp.Tool
hextail
```

Use `hextail --no-browser` to suppress browser launch or `hextail --port <1-65535>` to select another loopback port. The browser must be Chrome or Edge for active file tailing because it uses the File System Access API. After the first load, the installed PWA shell is available offline.
