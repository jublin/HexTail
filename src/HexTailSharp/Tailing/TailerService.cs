using System.Threading.Channels;

namespace HexTailSharp.Tailing;

/// <summary>
/// Entry point and owner of the tailer layer. Creates one background tailer per file;
/// all tailers push immutable <see cref="TailerEvent"/>s into a single unbounded channel
/// that the state layer drains. Dispose to stop all tailers and complete the channel.
/// </summary>
public sealed class TailerService : IAsyncDisposable
{
    private readonly Channel<TailerEvent> _channel = Channel.CreateUnbounded<TailerEvent>();
    private readonly TailerOptions _options;
    private readonly List<FileTailer> _tailers = [];
    private readonly Lock _gate = new();

    public TailerService(TailerOptions? options = null)
    {
        _options = options ?? TailerOptions.Default;
    }

    /// <summary>Stream of events from all started tailers. Drained by the state layer each frame.</summary>
    public ChannelReader<TailerEvent> Events => _channel.Reader;

    /// <summary>
    /// Starts a background tailer for <paramref name="path"/>. The tailer reads the current
    /// file contents and emits an initial <see cref="NewLines"/> batch, then watches for
    /// appended data, truncation, and rotation.
    /// </summary>
    /// <param name="fileId">Caller-assigned identifier carried by every event from this file.</param>
    /// <param name="path">Path of the file to tail. The file does not need to exist yet.</param>
    public IFileTailer StartTailer(string fileId, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var tailer = new FileTailer(fileId, path, _channel.Writer, _options);
        lock (_gate)
        {
            _tailers.Add(tailer);
        }

        tailer.Start();
        return tailer;
    }

    /// <summary>Stops all tailers and completes the event channel.</summary>
    public async ValueTask DisposeAsync()
    {
        List<FileTailer> tailers;
        lock (_gate)
        {
            tailers = [.. _tailers];
            _tailers.Clear();
        }

        foreach (var tailer in tailers)
        {
            await tailer.DisposeAsync();
        }

        _channel.Writer.TryComplete();
    }
}
