namespace HexTailSharp.Tailing;

/// <summary>
/// Tuning knobs for <see cref="TailerService"/> and its per-file tailers.
/// </summary>
public sealed record TailerOptions
{
    /// <summary>Default options: 250 ms poll interval, watcher enabled, 3 exponential-backoff retries.</summary>
    public static TailerOptions Default { get; } = new();

    /// <summary>
    /// Interval between polls. When a <see cref="FileSystemWatcher"/> is available it only
    /// wakes the loop early; otherwise this is the sole detection cadence.
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Whether to attempt using a <see cref="FileSystemWatcher"/>. Watcher creation failures
    /// (e.g. unsupported in browser WASM) silently fall back to polling regardless.
    /// </summary>
    public bool UseFileSystemWatcher { get; init; } = true;

    /// <summary>Maximum number of retries for a failed read before giving up until the next poll cycle.</summary>
    public int MaxRetryAttempts { get; init; } = 3;

    /// <summary>Initial delay for exponential backoff between read retries.</summary>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Upper bound for the exponential backoff delay.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(5);
}
