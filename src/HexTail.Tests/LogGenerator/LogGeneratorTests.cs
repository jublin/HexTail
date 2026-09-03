using System.Text.Json;
using global::HexTail.LogGenerator;
using Generator = global::HexTail.LogGenerator.LogGenerator;

namespace HexTail.Tests.LogGenerator;

public sealed class LogGeneratorTests
{
    [Fact]
    public void TryParse_JsonlWithoutOutput_UsesJsonlDefault()
    {
        var ok = LogGeneratorOptions.TryParse(
            ["--format", "jsonl", "--count", "2"],
            TextWriter.Null,
            out var options
        );

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

        Assert.False(
            LogGeneratorOptions.TryParse(["--levels", "information,broken"], error, out _)
        );
        Assert.Contains("broken", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatRecord_JsonlContainsStructuredFields()
    {
        var record = Generator.FormatRecord(
            LogFormat.Jsonl,
            LogLevel.Warning,
            42,
            new DateTimeOffset(2026, 9, 2, 22, 0, 0, TimeSpan.Zero)
        );
        using var json = JsonDocument.Parse(record);

        Assert.Equal("Warning", json.RootElement.GetProperty("level").GetString());
        Assert.Equal(42, json.RootElement.GetProperty("eventId").GetInt64());
        Assert.True(json.RootElement.TryGetProperty("timestamp", out _));
        Assert.True(json.RootElement.TryGetProperty("message", out _));
    }

    [Fact]
    public void FormatRecord_TextContainsLevelAndEventId()
    {
        var record = Generator.FormatRecord(
            LogFormat.Text,
            LogLevel.Error,
            7,
            new DateTimeOffset(2026, 9, 2, 22, 0, 0, TimeSpan.Zero)
        );

        Assert.Contains("[Error]", record);
        Assert.Contains("event=7", record);
    }

    [Fact]
    public async Task RunAsync_CountWritesExactlyThatManyLines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hextail-generator-{Guid.NewGuid():N}.jsonl");
        try
        {
            var options = new LogGeneratorOptions(
                LogFormat.Jsonl,
                path,
                TimeSpan.Zero,
                3,
                [LogLevel.Information],
                true
            );

            Assert.Equal(
                0,
                await Generator.RunAsync(options, TestContext.Current.CancellationToken)
            );
            var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(3, lines.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
