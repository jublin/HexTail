using System.Threading.Channels;
using HexTailSharp.Domain;
using HexTailSharp.Tailing;

namespace HexTailSharp.Tests.Tailing;

public sealed class TailerServiceTests
{
    [Fact]
    public async Task StartTailer_EmitsInitialAndAppendedCompleteLines()
    {
        var path = CreateTempFile("first\n");
        await using var service = new LogSourceService(
            new TailerOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                UseFileSystemWatcher = false,
            }
        );
        await using var tailer = service.StartFile("file-1", path, new LogfmtParser());

        var initial = await ReadEventAsync<SourceLines>(
            service.Events,
            e => e.Lines.Select(line => line.Raw).SequenceEqual(["first"])
        );

        await File.AppendAllTextAsync(path, "partial");
        await AssertNoEventAsync(service.Events, TimeSpan.FromMilliseconds(50));

        await File.AppendAllTextAsync(path, " line\nsecond\r\n");
        var appended = await ReadEventAsync<SourceLines>(
            service.Events,
            e => e.Lines.Select(line => line.Raw).SequenceEqual(["partial line", "second"])
        );

        Assert.Equal("file-1", initial.SourceId);
        Assert.Equal("file-1", appended.SourceId);
    }

    [Fact]
    public async Task StartTailer_EmitsTruncated_WhenFileShrinks()
    {
        var path = CreateTempFile("old content\n");
        await using var service = new LogSourceService(
            new TailerOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                UseFileSystemWatcher = false,
            }
        );
        await using var tailer = service.StartFile("file-1", path, new PlainTextParser());

        await ReadEventAsync<SourceLines>(service.Events);
        await File.WriteAllTextAsync(path, "new\n");

        var truncated = await ReadEventAsync<SourceReset>(service.Events);
        var replacement = await ReadEventAsync<SourceLines>(
            service.Events,
            e => e.Lines.Select(line => line.Raw).SequenceEqual(["new"])
        );

        Assert.Equal("file-1", truncated.SourceId);
        Assert.Equal("file-1", replacement.SourceId);
    }

    [Fact]
    public async Task StartTailer_EmitsRotated_WhenFileIsRecreated()
    {
        var directory = Directory.CreateTempSubdirectory("hextail-");
        var path = Path.Combine(directory.FullName, "app.log");
        try
        {
            await File.WriteAllTextAsync(path, "before\n");
            await using var service = new LogSourceService(
                new TailerOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(10),
                    UseFileSystemWatcher = false,
                }
            );
            await using var tailer = service.StartFile("file-1", path, new PlainTextParser());

            await ReadEventAsync<SourceLines>(service.Events);
            File.Delete(path);
            await WaitUntilAsync(() => !File.Exists(path));
            await Task.Delay(50);
            await File.WriteAllTextAsync(path, "after\n");

            var rotated = await ReadEventAsync<SourceReset>(service.Events);
            var replacement = await ReadEventAsync<SourceLines>(
                service.Events,
                e => e.Lines.Select(line => line.Raw).SequenceEqual(["after"])
            );

            Assert.Equal("file-1", rotated.SourceId);
            Assert.Equal("file-1", replacement.SourceId);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DisposeAsync_StopsTailerAndCompletes()
    {
        var path = CreateTempFile("line\n");
        await using var service = new LogSourceService(
            new TailerOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                UseFileSystemWatcher = false,
            }
        );
        var tailer = service.StartFile("file-1", path, new PlainTextParser());

        await tailer.DisposeAsync();

        await tailer.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(tailer.Completion.IsCompletedSuccessfully);
    }

    private static async Task<T> ReadEventAsync<T>(
        ChannelReader<SourceEvent> reader,
        Func<T, bool>? predicate = null
    )
        where T : SourceEvent
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await foreach (var item in reader.ReadAllAsync(timeout.Token))
        {
            if (item is T typed && (predicate is null || predicate(typed)))
                return typed;
        }

        throw new TimeoutException($"Timed out waiting for {typeof(T).Name}.");
    }

    private static async Task AssertNoEventAsync(
        ChannelReader<SourceEvent> reader,
        TimeSpan duration
    )
    {
        await Task.Delay(duration);
        Assert.False(reader.TryRead(out _), "An event was emitted for an incomplete line.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static string CreateTempFile(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hextail-{Guid.NewGuid():N}.log");
        File.WriteAllText(path, contents);
        return path;
    }
}
