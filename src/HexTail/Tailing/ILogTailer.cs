namespace HexTail.Tailing;

/// <summary>
/// Handle for a running per-file tailer. Events are delivered through the shared
/// <see cref="TailerService.Events"/> channel, not through this handle.
/// </summary>
public interface ILogTailer : IAsyncDisposable
{
    string SourceId { get; }
    string DisplayName { get; }
    Task Completion { get; }
}
