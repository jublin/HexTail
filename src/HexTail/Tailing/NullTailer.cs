namespace HexTail.Tailing;

/// <summary>A tailer that never produces events: the handle behind tabs whose lines are fed in-process (streams).</summary>
internal sealed class NullTailer(string sourceId, string displayName) : ILogTailer
{
    public string SourceId { get; } = sourceId;
    public string DisplayName { get; } = displayName;
    public Task Completion => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
