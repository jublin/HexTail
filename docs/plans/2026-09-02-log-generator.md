# Configurable Log Generator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a small CLI project that continuously appends configurable plaintext or JSONL test logs for HexTail.

**Architecture:** `HexTail.LogGenerator` is a dependency-free .NET console project. One `Program.cs` owns option parsing, record formatting, append-only output, delay handling, and cancellation; its small public surface is exercised from the existing test project.

**Tech Stack:** .NET 10, C#, `System.Text.Json`, xUnit v3.

**Spec:** `docs/superpowers/specs/2026-09-02-log-generator-design.md`

## Global Constraints

- Add `src/HexTail.LogGenerator/HexTail.LogGenerator.csproj` to `src/HexTail.slnx`.
- Do not add package dependencies or a command-line parsing library.
- Default to continuous append-only output; `--count` is the finite-run mode.
- Support only `text` and `jsonl` formats with UTC timestamps, severity, event id, and message.
- Preserve the existing solution's .NET 10 target and central build configuration.
- Use a conventional commit for each task; the plan commit is first.

---

### Task 1: Commit the implementation plan

**Files:**
- Create: `docs/plans/2026-09-02-log-generator.md`

**Interfaces:**
- Consumes: `docs/superpowers/specs/2026-09-02-log-generator-design.md`
- Produces: the approved task sequence used by implementation.

- [ ] **Step 1: Verify the plan matches the approved design**

Run: `rtk rg -n -- '--format|--output|--interval|--count|--levels|--truncate|--help' docs/superpowers/specs/2026-09-02-log-generator-design.md docs/plans/2026-09-02-log-generator.md`

Expected: each required option appears in both documents.

- [ ] **Step 2: Commit the plan**

```bash
rtk git add docs/plans/2026-09-02-log-generator.md
rtk git commit -m "docs: add log generator implementation plan" -- docs/plans/2026-09-02-log-generator.md
```

### Task 2: Add the generator project and testable CLI core

**Files:**
- Create: `src/HexTail.LogGenerator/HexTail.LogGenerator.csproj`
- Create: `src/HexTail.LogGenerator/Program.cs`
- Modify: `src/HexTail.slnx`
- Modify: `src/HexTail.Tests/HexTail.Tests.csproj`
- Create: `src/HexTail.Tests/LogGenerator/LogGeneratorTests.cs`

**Interfaces:**
- Consumes: the existing .NET 10 build settings and xUnit test project.
- Produces:
  - `public enum LogFormat { Text, Jsonl }`
  - `public sealed record LogGeneratorOptions(LogFormat Format, string OutputPath, TimeSpan Interval, int? Count, IReadOnlyList<LogLevel> Levels, bool Truncate)`
  - `public static bool LogGeneratorOptions.TryParse(string[] args, TextWriter error, out LogGeneratorOptions? options)`
  - `public static Task<int> LogGenerator.RunAsync(LogGeneratorOptions options, CancellationToken cancellationToken)`
  - `public static string LogGenerator.FormatRecord(LogFormat format, LogLevel level, long eventId, DateTimeOffset timestamp)`

- [ ] **Step 1: Add a failing project-reference test suite**

Add this project reference to `src/HexTail.Tests/HexTail.Tests.csproj`:

```xml
<ProjectReference Include="..\HexTail.LogGenerator\HexTail.LogGenerator.csproj" />
```

Create `src/HexTail.Tests/LogGenerator/LogGeneratorTests.cs` with tests that define the required behavior:

```csharp
using System.Text.Json;

namespace HexTail.Tests.LogGenerator;

public sealed class LogGeneratorTests
{
    [Fact]
    public void TryParse_JsonlWithoutOutput_UsesJsonlDefault()
    {
        var ok = LogGeneratorOptions.TryParse(["--format", "jsonl", "--count", "2"], TextWriter.Null, out var options);

        Assert.True(ok);
        Assert.NotNull(options);
        Assert.Equal(LogFormat.Jsonl, options.Format);
        Assert.Equal("hextail.jsonl", options.OutputPath);
        Assert.Equal(2, options.Count);
    }

    [Fact]
    public void TryParse_RejectsUnknownLevel()
    {
        using var error = new StringWriter();

        Assert.False(LogGeneratorOptions.TryParse(["--levels", "information,broken"], error, out _));
        Assert.Contains("broken", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatRecord_JsonlContainsStructuredFields()
    {
        var record = LogGenerator.FormatRecord(LogFormat.Jsonl, LogLevel.Warning, 42, new DateTimeOffset(2026, 9, 2, 22, 0, 0, TimeSpan.Zero));
        using var json = JsonDocument.Parse(record);

        Assert.Equal("Warning", json.RootElement.GetProperty("level").GetString());
        Assert.Equal(42, json.RootElement.GetProperty("eventId").GetInt64());
        Assert.True(json.RootElement.TryGetProperty("timestamp", out _));
        Assert.True(json.RootElement.TryGetProperty("message", out _));
    }
}
```

- [ ] **Step 2: Run the focused tests to verify they fail**

Run: `rtk dotnet test src/HexTail.Tests/HexTail.Tests.csproj --filter FullyQualifiedName~LogGeneratorTests`

Expected: FAIL because the referenced generator project and public types do not exist.

- [ ] **Step 3: Create the project and add it to the solution**

Create `src/HexTail.LogGenerator/HexTail.LogGenerator.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

Add this entry to `src/HexTail.slnx` beside the existing projects:

```xml
<Project Path="HexTail.LogGenerator/HexTail.LogGenerator.csproj" />
```

- [ ] **Step 4: Implement the minimal option parser, formatter, and writer**

In `Program.cs`, define `LogLevel` with `Trace`, `Debug`, `Information`, `Warning`, `Error`, and `Critical`. Implement `TryParse` with explicit handling for `--format`, `--output`, `--interval`, `--count`, `--levels`, `--truncate`, and `--help`; reject missing values, unknown options, negative intervals, non-positive counts, empty level lists, and unknown levels.

Use this core writer shape:

```csharp
public static async Task<int> RunAsync(LogGeneratorOptions options, CancellationToken cancellationToken)
{
    await using var stream = new FileStream(options.OutputPath, options.Truncate ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
    await using var writer = new StreamWriter(stream) { AutoFlush = true };

    for (long eventId = 1; !options.Count.HasValue || eventId <= options.Count.Value; eventId++)
    {
        var level = options.Levels[(int)((eventId - 1) % options.Levels.Count)];
        await writer.WriteLineAsync(FormatRecord(options.Format, level, eventId, DateTimeOffset.UtcNow));
        if (options.Interval > TimeSpan.Zero)
            await Task.Delay(options.Interval, cancellationToken);
    }

    return 0;
}
```

`FormatRecord` must emit `timestamp:O [Level] event=id Generated Level test event id` for text and serialize an anonymous object with lowercase `timestamp`, `level`, `eventId`, and `message` properties for JSONL. `Main` wires Ctrl+C to a `CancellationTokenSource`, prints usage for `--help`, and returns non-zero for invalid input or cancellation.

- [ ] **Step 5: Expand focused tests for text output and finite file generation**

Add these tests to `LogGeneratorTests`:

```csharp
[Fact]
public void FormatRecord_TextContainsLevelAndEventId()
{
    var record = LogGenerator.FormatRecord(LogFormat.Text, LogLevel.Error, 7, new DateTimeOffset(2026, 9, 2, 22, 0, 0, TimeSpan.Zero));

    Assert.Contains("[Error]", record);
    Assert.Contains("event=7", record);
}

[Fact]
public async Task RunAsync_CountWritesExactlyThatManyLines()
{
    var path = Path.Combine(Path.GetTempPath(), $"hextail-generator-{Guid.NewGuid():N}.jsonl");
    try
    {
        var options = new LogGeneratorOptions(LogFormat.Jsonl, path, TimeSpan.Zero, 3, [LogLevel.Information], true);

        Assert.Equal(0, await LogGenerator.RunAsync(options, TestContext.Current.CancellationToken));
        var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(3, lines.Length);
    }
    finally
    {
        File.Delete(path);
    }
}
```

- [ ] **Step 6: Run focused tests, then the full solution build**

Run: `rtk dotnet test src/HexTail.Tests/HexTail.Tests.csproj --filter FullyQualifiedName~LogGeneratorTests`

Expected: PASS.

Run: `rtk dotnet build src/HexTail.slnx -c Release`

Expected: PASS with no compiler errors.

- [ ] **Step 7: Commit the generator**

```bash
rtk git add src/HexTail.slnx src/HexTail.LogGenerator src/HexTail.Tests/HexTail.Tests.csproj src/HexTail.Tests/LogGenerator/LogGeneratorTests.cs
rtk git commit -m "feat: add configurable log generator"
```

### Task 3: Document usage and verify both formats end to end

**Files:**
- Modify: `README.md`

**Interfaces:**
- Consumes: `HexTail.LogGenerator` command-line options and output formats from Task 2.
- Produces: copyable generator commands for a tailing session.

- [ ] **Step 1: Add failing documentation examples by checking for both formats**

Run: `rtk rg -n 'HexTail.LogGenerator|--format jsonl|--interval|--count' README.md`

Expected: no matches before documentation is added.

- [ ] **Step 2: Add concise generator usage to the README**

Add a `## Generate test logs` section after the run instructions:

```bash
dotnet run --project src/HexTail.LogGenerator -- --output /tmp/hextail.log --interval 250 --truncate
dotnet run --project src/HexTail.LogGenerator -- --format jsonl --output /tmp/hextail.jsonl --count 100 --truncate
```

Explain that the first command emits continuously until Ctrl+C and the second produces exactly 100 JSONL records.

- [ ] **Step 3: Run both finite commands and validate the result**

Run: `rtk dotnet run --project src/HexTail.LogGenerator -- --output /tmp/hextail-text.log --count 2 --truncate`

Expected: exit code 0 and `/tmp/hextail-text.log` contains two plaintext lines.

Run: `rtk dotnet run --project src/HexTail.LogGenerator -- --format jsonl --output /tmp/hextail-jsonl.jsonl --count 2 --truncate`

Expected: exit code 0 and `/tmp/hextail-jsonl.jsonl` contains two JSON objects, one per line.

- [ ] **Step 4: Run the full test project and commit documentation**

Run: `rtk dotnet test src/HexTail.Tests/HexTail.Tests.csproj -c Release`

Expected: all unrelated existing failures are reported separately; no `LogGeneratorTests` failure is permitted.

```bash
rtk git add README.md
rtk git commit -m "docs: document log generator usage" -- README.md
```
