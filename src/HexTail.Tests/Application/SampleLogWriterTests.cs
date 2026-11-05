using HexTail.Application;

namespace HexTail.Tests.Application;

public sealed class SampleLogWriterTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task WritesBacklogThenAppendsTimestampedLeveledLines()
    {
        string path;
        string[] lines;
        await using (var writer = SampleLogWriter.Start(Fast, backlog: 50))
        {
            path = writer.Path;
            Assert.Equal("hextail-sample.log", Path.GetFileName(path));
            Assert.Equal(50, ReadLines(path).Length);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (ReadLines(path).Length < 52 && DateTime.UtcNow < deadline)
                await Task.Delay(20, TestContext.Current.CancellationToken);
            lines = ReadLines(path);
        }
        Assert.True(lines.Length >= 52);
        Assert.All(
            lines,
            line =>
                Assert.Matches(
                    @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z (DEBUG|INFO |WARN |ERROR) \S",
                    line
                )
        );
    }

    [Fact]
    public async Task StartsInAPrivateRandomDirectoryAndDisposeRemovesFileAndDirectory()
    {
        var writer = SampleLogWriter.Start(Fast, backlog: 5);
        var directory = Path.GetDirectoryName(writer.Path)!;
        Assert.StartsWith("hextail-", Path.GetFileName(directory));
        Assert.Equal(
            Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetDirectoryName(directory)
        );
        if (!OperatingSystem.IsWindows())
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(directory)
            );
        Assert.True(File.Exists(writer.Path));

        await writer.DisposeAsync();
        Assert.False(File.Exists(writer.Path));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task TwoWritersNeverShareADirectory()
    {
        await using var first = SampleLogWriter.Start(Fast, backlog: 1);
        await using var second = SampleLogWriter.Start(Fast, backlog: 1);
        Assert.NotEqual(first.Path, second.Path);
    }

    [Fact]
    public async Task DisposeIsIdempotent()
    {
        var writer = SampleLogWriter.Start(Fast, backlog: 5);
        await writer.DisposeAsync();
        await writer.DisposeAsync();
    }

    [Fact]
    public void RefusesAnExistingFileAndLeavesItUntouched()
    {
        var directory = Directory.CreateTempSubdirectory("hextail-test-");
        var path = Path.Combine(directory.FullName, SampleLogWriter.FileName);
        File.WriteAllText(path, "precious");
        try
        {
            Assert.ThrowsAny<IOException>(() => SampleLogWriter.Start(directory, Fast, backlog: 5));
            Assert.Equal("precious", File.ReadAllText(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void RefusesALinkPlantedAtThePathAndNeverWritesThroughIt()
    {
        var victimDirectory = Directory.CreateTempSubdirectory("hextail-victim-");
        var victim = Path.Combine(victimDirectory.FullName, "victim.txt");
        File.WriteAllText(victim, "untouched");
        var directory = Directory.CreateTempSubdirectory("hextail-test-");
        var link = Path.Combine(directory.FullName, SampleLogWriter.FileName);
        try
        {
            try
            {
                File.CreateSymbolicLink(link, victim);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return; // Linking is not permitted here (unprivileged Windows).
            }
            Assert.ThrowsAny<IOException>(() => SampleLogWriter.Start(directory, Fast, backlog: 5));
            Assert.Equal("untouched", File.ReadAllText(victim));
            Assert.NotNull(new FileInfo(link).LinkTarget);

            // A dangling link is refused as well, and nothing appears at its target.
            File.Delete(link);
            var missing = Path.Combine(victimDirectory.FullName, "missing.txt");
            File.CreateSymbolicLink(link, missing);
            Assert.ThrowsAny<IOException>(() => SampleLogWriter.Start(directory, Fast, backlog: 5));
            Assert.False(File.Exists(missing));
        }
        finally
        {
            directory.Delete(recursive: true);
            victimDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CleanupRemovesOnlyWhatTheWriterCreated()
    {
        var directory = Directory.CreateTempSubdirectory("hextail-test-");
        var foreign = Path.Combine(directory.FullName, "other.txt");
        File.WriteAllText(foreign, "keep");
        try
        {
            var writer = SampleLogWriter.Start(directory, Fast, backlog: 5);
            await writer.DisposeAsync();
            Assert.False(File.Exists(writer.Path));
            Assert.True(File.Exists(foreign));
            Assert.True(Directory.Exists(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string[] ReadLines(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete
        );
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }
}
