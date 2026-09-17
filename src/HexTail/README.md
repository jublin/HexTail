# HexTail

HexTail is a cross-platform desktop application for watching and searching live
log files, including plaintext, logfmt, and JSONL. It can also tail configured
Elasticsearch sources.

## Install

Install the global tool with the .NET 10 SDK. The .NET 10 runtime is required
to run it:

```sh
dotnet tool install --global HexTail
```

Open a log file:

```sh
hextail /path/to/application.log
```

Run `hextail` without a path to open the application and choose files there.

## Supported platforms

NuGet provides packages for Windows x64, Linux x64, macOS x64, and macOS ARM64.

For documentation, issue reports, and releases, visit the
[HexTail GitHub repository](https://github.com/jublin/HexTail).
