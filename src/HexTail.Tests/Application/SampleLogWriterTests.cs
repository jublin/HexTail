using HexTail.Application;

namespace HexTail.Tests.Application;

public sealed class SampleLogWriterTests
{
    [Fact]
    public async Task WritesBacklogThenAppendsTimestampedLeveledLines()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log");
        try
        {
            await using (
                var writer = SampleLogWriter.Start(path, TimeSpan.FromMilliseconds(20), backlog: 50)
            )
            {
                Assert.Equal(path, writer.Path);
                Assert.Equal(50, File.ReadAllLines(path).Length);
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (File.ReadAllLines(path).Length < 52 && DateTime.UtcNow < deadline)
                    await Task.Delay(20);
            }
            var lines = File.ReadAllLines(path);
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
        finally
        {
            File.Delete(path);
        }
    }
}
