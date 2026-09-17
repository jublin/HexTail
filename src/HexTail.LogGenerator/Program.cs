using System.Text.Json;

namespace HexTail.LogGenerator;

public enum LogFormat
{
    Text,
    Jsonl,
}

public enum LogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
}

public sealed record LogGeneratorOptions(
    LogFormat Format,
    string OutputPath,
    TimeSpan Interval,
    int? Count,
    IReadOnlyList<LogLevel> Levels,
    bool Truncate
)
{
    private static readonly LogLevel[] DefaultLevels = Enum.GetValues<LogLevel>();

    public static bool TryParse(string[] args, TextWriter error, out LogGeneratorOptions? options)
    {
        var format = LogFormat.Text;
        string? outputPath = null;
        var interval = TimeSpan.FromSeconds(1);
        int? count = null;
        IReadOnlyList<LogLevel> levels = DefaultLevels;
        var truncate = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--format":
                    if (
                        !TryReadValue(args, ref index, error, out var formatValue)
                        || !Enum.TryParse<LogFormat>(formatValue, true, out format)
                    )
                    {
                        error.WriteLine("--format must be text or jsonl.");
                        options = null;
                        return false;
                    }
                    break;

                case "--output":
                    if (
                        !TryReadValue(args, ref index, error, out outputPath)
                        || string.IsNullOrWhiteSpace(outputPath)
                    )
                    {
                        error.WriteLine("--output must be a file path.");
                        options = null;
                        return false;
                    }
                    break;

                case "--interval":
                    if (
                        !TryReadValue(args, ref index, error, out var intervalValue)
                        || !int.TryParse(intervalValue, out var milliseconds)
                        || milliseconds < 0
                    )
                    {
                        error.WriteLine(
                            "--interval must be a non-negative number of milliseconds."
                        );
                        options = null;
                        return false;
                    }
                    interval = TimeSpan.FromMilliseconds(milliseconds);
                    break;

                case "--count":
                    if (
                        !TryReadValue(args, ref index, error, out var countValue)
                        || !int.TryParse(countValue, out var parsedCount)
                        || parsedCount <= 0
                    )
                    {
                        error.WriteLine("--count must be a positive integer.");
                        options = null;
                        return false;
                    }
                    count = parsedCount;
                    break;

                case "--levels":
                    if (
                        !TryReadValue(args, ref index, error, out var levelsValue)
                        || !TryParseLevels(levelsValue, out levels)
                    )
                    {
                        error.WriteLine($"--levels contains an unknown level: {levelsValue}.");
                        options = null;
                        return false;
                    }
                    break;

                case "--truncate":
                    truncate = true;
                    break;

                default:
                    error.WriteLine($"Unknown option: {args[index]}.");
                    options = null;
                    return false;
            }
        }

        options = new LogGeneratorOptions(
            format,
            outputPath ?? (format == LogFormat.Jsonl ? "hextail.jsonl" : "hextail.log"),
            interval,
            count,
            levels,
            truncate
        );
        return true;
    }

    private static bool TryReadValue(
        string[] args,
        ref int index,
        TextWriter error,
        out string value
    )
    {
        if (index + 1 < args.Length)
        {
            value = args[++index];
            return true;
        }

        error.WriteLine($"{args[index]} requires a value.");
        value = string.Empty;
        return false;
    }

    private static bool TryParseLevels(string value, out IReadOnlyList<LogLevel> levels)
    {
        var parsedLevels = new List<LogLevel>();
        foreach (
            var level in value.Split(
                ',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
            )
        )
        {
            if (!Enum.TryParse<LogLevel>(level, true, out var parsedLevel))
            {
                levels = [];
                return false;
            }

            parsedLevels.Add(parsedLevel);
        }

        levels = parsedLevels;
        return parsedLevels.Count > 0;
    }
}

public static class LogGenerator
{
    public static async Task<int> RunAsync(
        LogGeneratorOptions options,
        CancellationToken cancellationToken
    )
    {
        await using var stream = new FileStream(
            options.OutputPath,
            options.Truncate ? FileMode.Create : FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite
        );
        await using var writer = new StreamWriter(stream) { AutoFlush = true };

        for (long eventId = 1; !options.Count.HasValue || eventId <= options.Count.Value; eventId++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var level = options.Levels[(int)((eventId - 1) % options.Levels.Count)];
            await writer.WriteLineAsync(
                FormatRecord(options.Format, level, eventId, DateTimeOffset.UtcNow)
            );

            if (
                options.Interval > TimeSpan.Zero
                && (!options.Count.HasValue || eventId < options.Count.Value)
            )
            {
                await Task.Delay(options.Interval, cancellationToken);
            }
        }

        return 0;
    }

    public static string FormatRecord(
        LogFormat format,
        LogLevel level,
        long eventId,
        DateTimeOffset timestamp
    )
    {
        var message = $"Generated {level} test event {eventId}";
        return format == LogFormat.Jsonl
            ? JsonSerializer.Serialize(
                new
                {
                    timestamp = timestamp.ToUniversalTime().ToString("O"),
                    level = level.ToString(),
                    eventId,
                    message,
                }
            )
            : $"{timestamp:O} [{level}] event={eventId} {message}";
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--help")
        {
            Console.WriteLine(
                "Usage: HexTail.LogGenerator [--format text|jsonl] [--output <path>] [--interval <ms>] [--count <n>] [--levels <list>] [--truncate]"
            );
            return 0;
        }

        if (!LogGeneratorOptions.TryParse(args, Console.Error, out var options))
        {
            return 1;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            return await LogGenerator.RunAsync(options!, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 130;
        }
    }
}
