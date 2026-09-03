# Configurable log generator design

## Goal

Add a small .NET console application that produces append-only plaintext or JSONL log files for exercising HexTail's file tailing and parser behavior.

## Scope

Create `src/HexTail.LogGenerator/HexTail.LogGenerator.csproj` and add it to `src/HexTail.slnx`.

The executable has no package dependencies. It uses `System.Text.Json` for JSONL output and the base class library for argument parsing, file output, timing, and cancellation.

## Command-line interface

`dotnet run --project src/HexTail.LogGenerator -- [options]`

| Option | Default | Behavior |
| --- | --- | --- |
| `--format text|jsonl` | `text` | Selects plaintext or JSONL output. |
| `--output <path>` | `hextail.log` or `hextail.jsonl` | Destination file; the format-specific default is selected when omitted. |
| `--interval <ms>` | `1000` | Delay between emitted records; must be non-negative. |
| `--count <n>` | continuous | Emits exactly `n` records, then exits; must be positive. |
| `--levels <list>` | all levels | Comma-separated levels drawn from Trace, Debug, Information, Warning, Error, and Critical. The generator cycles through the configured values. |
| `--truncate` | off | Replaces an existing output file before the first record. |
| `--help` | off | Prints usage and exits successfully. |

Invalid or unknown options print an actionable error and exit non-zero. Ctrl+C stops continuous generation cleanly.

## Output

Each record has a UTC timestamp, severity, monotonically increasing event number, and human-readable message.

Text output is one line per event, suitable for the plaintext parser:

```text
2026-09-02T22:00:00.0000000Z [Warning] event=42 Generated Warning test event 42
```

JSONL output is one JSON object per line, suitable for the JSONL parser:

```json
{"timestamp":"2026-09-02T22:00:00.0000000Z","level":"Warning","eventId":42,"message":"Generated Warning test event 42"}
```

The process appends by default, which makes it usable with an already-open HexTail tab. `--truncate` is the explicit clean-start operation.

## Structure and verification

Keep the implementation in a single `Program.cs` with small private helpers; no logging abstractions, configuration files, or external CLI package are warranted.

Add focused tests under the existing test project for option validation, default output selection, severity cycling, and one record in each output format. Verify with the solution build and the focused test project.

## Non-goals

- Simulating every logging framework's schema.
- Multiple output files or rotation.
- Randomized payloads, external configuration, or a dependency-based command-line parser.
