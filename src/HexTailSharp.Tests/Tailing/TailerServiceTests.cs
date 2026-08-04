using System.Threading.Channels;
using HexTailSharp.Tailing;

namespace HexTailSharp.Tests.Tailing;

public sealed class TailerServiceTests
{
    [Fact]
    public async Task StartTailer_EmitsInitialAndAppendedCompleteLines()
    {
        var path = CreateTempFile("first\n");
        await using var service = new TailerService(
            new TailerOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                UseFileSystemWatcher = false,
            }
        );
        await using var tailer = service.StartTailer("file-1", path);

        var initial = await ReadEventAsync<NewLines>(
            service.Events,
            e => e.Lines.SequenceEqual(["first"])
        );

        await File.AppendAllTextAsync(path, "partial");
        await AssertNoEventAsync(service.Events, TimeSpan.FromMilliseconds(50));

        await File.AppendAllTextAsync(path, " line\nsecond\r\n");
        var appended = await ReadEventAsync<NewLines>(
            service.Events,
            e => e.Lines.SequenceEqual(["partial line", "second"])
        );

        Assert.Equal("file-1", initial.FileId);
        Assert.Equal("file-1", appended.FileId);
    }

    [Fact]
    public async Task StartTailer_EmitsTruncated_WhenFileShrinks()
    {
        var path = CreateTempFile("old content\n");
        await using var service = new TailerService(
            new TailerOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                UseFileSystemWatcher = false,
            }
        );
        await using var tailer = service.StartTailer("file-1", path);

        await ReadEventAsync<NewLines>(service.Events);
        await File.WriteAllTextAsync(path, "new\n");

        var truncated = await ReadEventAsync<FileTruncated>(service.Events);
        var replacement = await ReadEventAsync<NewLines>(
            service.Events,
            e => e.Lines.SequenceEqual(["new"])
        );

        Assert.Equal("file-1", truncated.FileId);
        Assert.Equal("file-1", replacement.FileId);
    }

    [Fact]
    public async Task StartTailer_EmitsRotated_WhenFileIsRecreated()
    {
        var directory = Directory.CreateTempSubdirectory("hextail-");
        var path = Path.Combine(directory.FullName, "app.log");
        try
        {
            await File.WriteAllTextAsync(path, "before\n");
            await using var service = new TailerService(
                new TailerOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(10),
                    UseFileSystemWatcher = false,
                }
            );
            await using var tailer = service.StartTailer("file-1", path);

            await ReadEventAsync<NewLines>(service.Events);
            File.Delete(path);
            await WaitUntilAsync(() => !File.Exists(path));
            await Task.Delay(50);
            await File.WriteAllTextAsync(path, "after\n");

            var rotated = await ReadEventAsync<FileRotated>(service.Events);
            var replacement = await ReadEventAsync<NewLines>(
                service.Events,
                e => e.Lines.SequenceEqual(["after"])
            );

            Assert.Equal("file-1", rotated.FileId);
            Assert.Equal("file-1", replacement.FileId);
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
        await using var service = new TailerService(
            new TailerOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                UseFileSystemWatcher = false,
            }
        );
        var tailer = service.StartTailer("file-1", path);

        await tailer.DisposeAsync();

        await tailer.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(tailer.Completion.IsCompletedSuccessfully);
    }

    private static async Task<T> ReadEventAsync<T>(
        ChannelReader<TailerEvent> reader,
        Func<T, bool>? predicate = null
    )
        where T : TailerEvent
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
        ChannelReader<TailerEvent> reader,
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
