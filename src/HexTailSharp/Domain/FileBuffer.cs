namespace HexTailSharp.Domain;

/// <summary>Describes what changed in a <see cref="FileBuffer"/> mutation.</summary>
/// <param name="AppendedCount">Lines appended in this mutation.</param>
/// <param name="RolledOutCount">Oldest lines removed because the cap was exceeded.</param>
/// <param name="Cleared">Whether the buffer was cleared (truncation/rotation).</param>
public readonly record struct BufferChange(int AppendedCount, int RolledOutCount, bool Cleared);

/// <summary>
/// Owns the line list for one tailed file, capped at <see cref="MaxLines"/>.
/// Appending past the cap rolls the oldest lines out. Registered
/// <see cref="Search"/>es are kept consistent automatically: they scan only
/// newly appended lines, and on rollover their result indices are rebased
/// (shifted down, indices of removed lines dropped). <see cref="Changed"/>
/// fires after every mutation.
/// </summary>
public sealed class FileBuffer
{
    public const int DefaultMaxLines = 100_000;

    private readonly List<Line> _lines = [];
    private readonly List<Search> _searches = [];

    public FileBuffer(int maxLines = DefaultMaxLines)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLines);
        MaxLines = maxLines;
    }

    public int MaxLines { get; }
    public int Count => _lines.Count;
    public IReadOnlyList<Line> Lines => _lines;
    public Line this[int index] => _lines[index];
    public IReadOnlyList<Search> Searches => _searches;

    public event Action<BufferChange>? Changed;

    /// <summary>Registers an active search. The search must already have been created against this buffer.</summary>
    public void AddSearch(Search search) => _searches.Add(search);

    public bool RemoveSearch(Search search) => _searches.Remove(search);

    public void Append(Line line) => Append([line]);

    public void Append(IEnumerable<Line> lines)
    {
        var startIndex = _lines.Count;
        _lines.AddRange(lines);
        var appended = _lines.Count - startIndex;
        if (appended == 0) return;

        var rolledOut = 0;
        var excess = _lines.Count - MaxLines;
        if (excess > 0)
        {
            _lines.RemoveRange(0, excess);
            rolledOut = excess;
        }

        // Index of the first appended line after rollover; clamped in case a
        // single batch exceeds the cap entirely.
        var newStart = Math.Max(0, startIndex - rolledOut);
        foreach (var search in _searches)
        {
            if (rolledOut > 0) search.Rebase(rolledOut);
            search.ScanAppended(this, newStart);
        }

        Changed?.Invoke(new BufferChange(appended, rolledOut, Cleared: false));
    }

    /// <summary>Empties the buffer and all search results (file truncation/rotation).</summary>
    public void Clear()
    {
        if (_lines.Count == 0) return;
        _lines.Clear();
        foreach (var search in _searches) search.ClearResults();
        Changed?.Invoke(new BufferChange(0, 0, Cleared: true));
    }

    /// <summary>
    /// Returns the line at <paramref name="index"/> plus up to
    /// <paramref name="above"/> lines before and <paramref name="below"/>
    /// lines after it, clamped to the buffer bounds.
    /// </summary>
    public IReadOnlyList<Line> GetContextWindow(int index, int above, int below)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _lines.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(above);
        ArgumentOutOfRangeException.ThrowIfNegative(below);

        var start = Math.Max(0, index - above);
        var end = Math.Min(_lines.Count - 1, index + below);
        return _lines.GetRange(start, end - start + 1);
    }
}
