namespace HexTailSharp.Tailing;

/// <summary>
/// Handle for a running per-file tailer. Events are delivered through the shared
/// <see cref="TailerService.Events"/> channel, not through this handle.
/// </summary>
public interface IFileTailer : IAsyncDisposable
{
    /// <summary>Identifier assigned by the caller of <see cref="TailerService.StartTailer"/>.</summary>
    string FileId { get; }

    /// <summary>Full path of the tailed file.</summary>
    string Path { get; }

    /// <summary>Completes when the tailer's background loop stops (after disposal).</summary>
    Task Completion { get; }
}
