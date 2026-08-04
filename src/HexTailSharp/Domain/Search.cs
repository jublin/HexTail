using System.Text.RegularExpressions;

namespace HexTailSharp.Domain;

public enum MatchMode
{
    Literal,
    Regex,
}

/// <summary>A highlighted span within a line's raw text, for rendering in the "All" view.</summary>
public readonly record struct HighlightRange(int Start, int Length);

/// <summary>
/// A compiled search query: the query string, match mode, and case
/// sensitivity. Exposes per-line highlight ranges. Construction throws
/// <see cref="ArgumentException"/> for an invalid regular expression.
/// </summary>
public sealed class CompiledQuery
{
    private readonly Regex? _regex;

    public CompiledQuery(string query, MatchMode mode, bool caseSensitive)
    {
        Query = query;
        Mode = mode;
        CaseSensitive = caseSensitive;

        if (mode is MatchMode.Regex)
        {
            var options = RegexOptions.Compiled;
            if (!caseSensitive)
                options |= RegexOptions.IgnoreCase;
            try
            {
                _regex = new Regex(query, options);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException(
                    $"Invalid regular expression: {ex.Message}",
                    nameof(query),
                    ex
                );
            }
        }
    }

    public string Query { get; }
    public MatchMode Mode { get; }
    public bool CaseSensitive { get; }

    /// <summary>
    /// Returns the highlight ranges for <paramref name="text"/>; an empty list
    /// means no match. An empty query matches nothing. Zero-length regex
    /// matches are omitted since they cannot be rendered.
    /// </summary>
    public IReadOnlyList<HighlightRange> GetHighlights(string text)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(Query))
            return [];

        return Mode is MatchMode.Literal ? LiteralHighlights(text) : RegexHighlights(text);
    }

    public bool IsMatch(string text) => GetHighlights(text).Count > 0;

    private List<HighlightRange> LiteralHighlights(string text)
    {
        var comparison = CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var ranges = new List<HighlightRange>();
        var start = 0;
        while (start <= text.Length - Query.Length)
        {
            var found = text.IndexOf(Query, start, comparison);
            if (found < 0)
                break;
            ranges.Add(new HighlightRange(found, Query.Length));
            start = found + Query.Length;
        }

        return ranges;
    }

    private List<HighlightRange> RegexHighlights(string text)
    {
        var ranges = new List<HighlightRange>();
        foreach (Match match in _regex!.Matches(text))
        {
            if (match.Length > 0)
                ranges.Add(new HighlightRange(match.Index, match.Length));
        }

        return ranges;
    }
}

/// <summary>
/// An active search: a <see cref="CompiledQuery"/>, a highlight color, and
/// the indices of matching lines in the parent <see cref="FileBuffer"/>.
/// Scans the entire buffer on creation; afterwards the buffer feeds it only
/// newly appended lines via <see cref="FileBuffer.Append"/>.
///
/// Result indices are buffer-relative. When the buffer rolls out old lines,
/// it rebases each search: indices pointing at removed lines are dropped and
/// the rest are shifted down, so results always reference current buffer
/// positions.
/// </summary>
public sealed class Search
{
    private readonly List<int> _results = [];

    public Search(CompiledQuery query, string color, FileBuffer buffer)
    {
        Query = query;
        Color = color;
        ScanAppended(buffer, 0);
    }

    public CompiledQuery Query { get; }
    public string Color { get; }

    /// <summary>Indices of matching lines in the parent buffer, ascending.</summary>
    public IReadOnlyList<int> Results => _results;

    /// <summary>Highlight ranges for one line, for the "All" view. Empty when the line does not match.</summary>
    public IReadOnlyList<HighlightRange> GetHighlights(Line line) => Query.GetHighlights(line.Raw);

    internal void ScanAppended(FileBuffer buffer, int startIndex)
    {
        for (var i = startIndex; i < buffer.Count; i++)
        {
            if (Query.IsMatch(buffer[i].Raw))
                _results.Add(i);
        }
    }

    internal void Rebase(int rolledOut)
    {
        var write = 0;
        for (var read = 0; read < _results.Count; read++)
        {
            var rebased = _results[read] - rolledOut;
            if (rebased >= 0)
                _results[write++] = rebased;
        }

        _results.RemoveRange(write, _results.Count - write);
    }

    internal void ClearResults() => _results.Clear();
}
