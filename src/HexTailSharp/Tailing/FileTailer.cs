using System.Text;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Polly;
using Polly.Retry;

namespace HexTailSharp.Tailing;

internal sealed class FileTailer : IFileTailer
{
    private readonly ChannelWriter<TailerEvent> _events;
    private readonly TailerOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly ResiliencePipeline _retryPipeline;
    private readonly List<byte> _pendingBytes = [];
    private readonly object _wakeGate = new();
    private TaskCompletionSource<bool> _wake = NewWakeSource();
    private FileSystemWatcher? _watcher;
    private Task? _completion;
    private int _disposed;
    private long _offset;
    private bool _hasObservedFile;
    private bool _missing;
    private bool _rotationHint;

    public FileTailer(string fileId, string path, ChannelWriter<TailerEvent> events, TailerOptions options)
    {
        FileId = fileId;
        Path = System.IO.Path.GetFullPath(path);
        _events = events;
        _options = options;
        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = Math.Max(0, options.MaxRetryAttempts),
                Delay = options.InitialRetryDelay,
                MaxDelay = options.MaxRetryDelay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder().Handle<IOException>().Handle<UnauthorizedAccessException>(),
            })
            .Build();
    }

    public string FileId { get; }
    public string Path { get; }
    public Task Completion => _completion ?? Task.CompletedTask;

    public void Start()
    {
        if (_completion is not null)
            throw new InvalidOperationException("A file tailer can only be started once.");

        TryCreateWatcher();
        _completion = Task.Run(RunAsync);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _stop.Cancel();
        SignalWake();
        _watcher?.Dispose();

        if (_completion is not null)
        {
            try
            {
                await _completion.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
        }

        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await _retryPipeline.ExecuteAsync(
                        cancellationToken => PollOnceAsync(cancellationToken),
                        _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException)
                {
                    // The next poll retries after a transient read failure.
                }
                catch (UnauthorizedAccessException)
                {
                    // The next poll retries after a transient access failure.
                }
                catch (PlatformNotSupportedException)
                {
                    // Standalone browser WASM cannot access arbitrary OS paths.
                }
                catch (NotSupportedException)
                {
                    // Standalone browser WASM cannot access arbitrary OS paths.
                }

                await WaitForWakeOrPollAsync(_stop.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _watcher?.Dispose();
        }
    }

    private async ValueTask PollOnceAsync(CancellationToken cancellationToken)
    {
        var info = new FileInfo(Path);
        if (!info.Exists)
        {
            if (_hasObservedFile)
                _missing = true;

            return;
        }

        if (_missing || (_rotationHint && _hasObservedFile))
        {
            ResetReadState();
            _missing = false;
            _rotationHint = false;
            Write(new FileRotated(FileId));
        }

        if (info.Length < _offset)
        {
            ResetReadState();
            Write(new FileTruncated(FileId));
        }

        _hasObservedFile = true;
        await ReadAvailableBytesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ReadAvailableBytesAsync(CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        await using var stream = new FileStream(
            Path,
            new FileStreamOptions
            {
                Access = FileAccess.Read,
                Mode = FileMode.Open,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.SequentialScan,
                BufferSize = 81920,
            });

        if (stream.Length < _offset)
        {
            ResetReadState();
            Write(new FileTruncated(FileId));
        }

        stream.Position = _offset;
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            _offset += read;
            AppendCompleteLines(buffer.AsSpan(0, read), lines);
        }

        if (lines.Count > 0)
            Write(new NewLines(FileId, lines));
    }

    private void AppendCompleteLines(ReadOnlySpan<byte> bytes, List<string> lines)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            _pendingBytes.Add(bytes[i]);
            if (bytes[i] != (byte)'\n')
                continue;

            var length = _pendingBytes.Count - 1;
            if (length > 0 && _pendingBytes[length - 1] == (byte)'\r')
                length--;

            lines.Add(Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(_pendingBytes)[..length]));
            _pendingBytes.Clear();
        }
    }

    private void ResetReadState()
    {
        _offset = 0;
        _pendingBytes.Clear();
    }

    private void Write(TailerEvent tailerEvent) => _events.TryWrite(tailerEvent);

    private async Task WaitForWakeOrPollAsync(CancellationToken cancellationToken)
    {
        Task wakeTask;
        lock (_wakeGate)
        {
            wakeTask = _wake.Task;
        }

        var delayTask = Task.Delay(_options.PollInterval, cancellationToken);
        await Task.WhenAny(delayTask, wakeTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (wakeTask.IsCompleted)
        {
            lock (_wakeGate)
            {
                if (ReferenceEquals(_wake.Task, wakeTask))
                    _wake = NewWakeSource();
            }
        }
    }

    private void SignalWake()
    {
        lock (_wakeGate)
        {
            _wake.TrySetResult(true);
        }
    }

    private void TryCreateWatcher()
    {
        if (!_options.UseFileSystemWatcher || OperatingSystem.IsBrowser())
            return;

        try
        {
            var fullPath = System.IO.Path.GetFullPath(Path);
            var directory = System.IO.Path.GetDirectoryName(fullPath);
            var fileName = System.IO.Path.GetFileName(fullPath);
            if (directory is null || fileName.Length == 0 || !Directory.Exists(directory))
                return;

            _watcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.Size,
            };
            _watcher.Created += OnCreated;
            _watcher.Renamed += OnRenamed;
            _watcher.Deleted += OnDeleted;
            _watcher.Error += OnWatcherError;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void OnCreated(object sender, FileSystemEventArgs args)
    {
        _rotationHint = true;
        SignalWake();
    }

    private void OnRenamed(object sender, RenamedEventArgs args)
    {
        _rotationHint = true;
        SignalWake();
    }

    private void OnDeleted(object sender, FileSystemEventArgs args)
    {
        _missing = true;
        SignalWake();
    }

    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        // Polling remains active when the watcher buffer overflows or is unsupported.
        SignalWake();
    }

    private static TaskCompletionSource<bool> NewWakeSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
