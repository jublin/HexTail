using System.Globalization;

namespace HexTail.Application;

/// <summary>Writes a small demo log and keeps appending to it so new users can watch live tailing.</summary>
internal sealed class SampleLogWriter : IAsyncDisposable
{
    private static readonly (string Level, string Message)[] Events =
    [
        ("INFO", "api request method=GET path=/orders status=200 duration_ms={0}"),
        ("INFO", "api request method=POST path=/orders status=201 duration_ms={0}"),
        ("INFO", "worker job=charge order_id={0} attempt=1"),
        ("INFO", "session resumed user=u-{0}"),
        ("INFO", "healthcheck GET /healthz status=200"),
        ("DEBUG", "cache lookup key=order:{0} hit=true"),
        ("DEBUG", "queue depth={0} lag_ms=12"),
        ("WARN", "upstream retry route=/payments attempt=2 order_id={0}"),
        ("WARN", "slow query table=orders duration_ms={0}"),
        ("ERROR", "payment gateway timeout after 5000ms order_id={0}"),
    ];

    private readonly Random _random = new(1337);
    private readonly CancellationTokenSource _stop = new();
    private readonly bool _deleteOnDispose;
    private Task _loop = Task.CompletedTask;
    private int _disposed;

    private SampleLogWriter(string path, bool deleteOnDispose)
    {
        Path = path;
        _deleteOnDispose = deleteOnDispose;
    }

    public string Path { get; }

    /// <param name="deleteOnDispose">Remove the file, and its directory once empty, on dispose.</param>
    public static SampleLogWriter Start(
        string path,
        TimeSpan interval,
        int backlog = 200,
        bool deleteOnDispose = false
    )
    {
        // The writer only ever touches this one file; refuse to follow a link planted at the path
        // or at its directory.
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            if (new DirectoryInfo(directory).LinkTarget is not null)
                throw new IOException($"The sample log directory is a link: {directory}");
            Directory.CreateDirectory(directory);
        }
        if (new FileInfo(path).LinkTarget is not null)
            throw new IOException($"The sample log path is a link: {path}");

        var writer = new SampleLogWriter(path, deleteOnDispose);
        try
        {
            var start = DateTimeOffset.UtcNow - interval * backlog;
            File.WriteAllLines(
                path,
                Enumerable
                    .Range(0, backlog)
                    .Select(index => writer.NextLine(start + interval * index))
            );
            writer._loop = writer.RunAsync(interval);
            return writer;
        }
        catch
        {
            writer._stop.Dispose();
            throw;
        }
    }

    internal string NextLine(DateTimeOffset timestamp)
    {
        var (level, message) = Events[_random.Next(Events.Length)];
        var text = string.Format(CultureInfo.InvariantCulture, message, _random.Next(1, 9999));
        return $"{timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture)}Z {level, -5} {text}";
    }

    private async Task RunAsync(TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                await File.AppendAllTextAsync(
                        Path,
                        NextLine(DateTimeOffset.UtcNow) + Environment.NewLine,
                        _stop.Token
                    )
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
        if (_deleteOnDispose)
            Cleanup();
    }

    private void Cleanup()
    {
        try
        {
            File.Delete(Path);
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (
                !string.IsNullOrEmpty(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any()
            )
                Directory.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
