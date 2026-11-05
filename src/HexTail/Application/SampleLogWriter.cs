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

    internal const string FileName = "hextail-sample.log";

    private readonly Random _random = new(1337);
    private readonly CancellationTokenSource _stop = new();
    private readonly DirectoryInfo _directory;
    private readonly FileStream _stream;
    private Task _loop = Task.CompletedTask;
    private int _disposed;

    private SampleLogWriter(DirectoryInfo directory, FileStream stream)
    {
        _directory = directory;
        _stream = stream;
        Path = stream.Name;
    }

    public string Path { get; }

    /// <summary>
    /// Creates a private, randomly named directory under the temp folder (0700 on Unix) and starts a
    /// sample log in it. The directory and file are removed on dispose.
    /// </summary>
    public static SampleLogWriter Start(TimeSpan interval, int backlog = 200) =>
        Start(Directory.CreateTempSubdirectory("hextail-"), interval, backlog);

    /// <summary>
    /// Starts a sample log in <paramref name="directory"/>, which the writer takes ownership of: the
    /// file is created exclusively (an existing file or link at the path is refused) and held open
    /// through one handle for the backlog and every append, so the path is never reopened. On dispose
    /// only the file the writer created and then the (by then empty) directory are removed.
    /// </summary>
    internal static SampleLogWriter Start(DirectoryInfo directory, TimeSpan interval, int backlog)
    {
        FileStream? stream = null;
        SampleLogWriter? writer = null;
        try
        {
            stream = new FileStream(
                System.IO.Path.Combine(directory.FullName, FileName),
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                }
            );
            writer = new SampleLogWriter(directory, stream);
            var start = DateTimeOffset.UtcNow - interval * backlog;
            var text = string.Concat(
                Enumerable
                    .Range(0, backlog)
                    .Select(index =>
                        writer.NextLine(start + interval * index) + Environment.NewLine
                    )
            );
            stream.Write(System.Text.Encoding.UTF8.GetBytes(text));
            stream.Flush();
            writer._loop = writer.RunAsync(interval);
            return writer;
        }
        catch
        {
            writer?._stop.Dispose();
            stream?.Dispose();
            // Only remove what this call created: the file exists only if CreateNew succeeded, and a
            // non-empty directory is never deleted.
            if (stream is not null)
                TryDelete(stream.Name);
            TryDeleteDirectory(directory);
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
            {
                // No token on the write itself: a cancelled write must not leave half a line behind.
                var bytes = System.Text.Encoding.UTF8.GetBytes(
                    NextLine(DateTimeOffset.UtcNow) + Environment.NewLine
                );
                await _stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                await _stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            await _loop.ConfigureAwait(false);
        }
        finally
        {
            _stop.Dispose();
            await _stream.DisposeAsync().ConfigureAwait(false);
            TryDelete(Path);
            TryDeleteDirectory(_directory);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(DirectoryInfo directory)
    {
        try
        {
            // Not recursive: only an empty directory goes, so nothing foreign is ever removed.
            directory.Delete(recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
