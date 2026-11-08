using HexTail.Domain;

namespace HexTail.Application;

public readonly record struct HighlightSpan(int Start, int Length, string Color);

/// <summary>UI-thread cache shared by all views of a file. Each rule generation owns one LRU.</summary>
internal sealed class HighlightSpanCache
{
    private readonly int _maxLines;
    private readonly int _maxSpans;
    private readonly Dictionary<Line, LinkedListNode<Entry>> _entries = new(
        ReferenceEqualityComparer.Instance
    );
    private readonly LinkedList<Entry> _recent = new();
    private Search[] _searches = [];
    private RuleSet? _rules;
    private int _spanCount;

    internal HighlightSpanCache(int maxLines = 512, int maxSpans = 16_384)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSpans);
        _maxLines = maxLines;
        _maxSpans = maxSpans;
    }

    internal IReadOnlyList<HighlightSpan> Get(
        Line line,
        IReadOnlyList<Search> searches,
        RuleSet rules
    )
    {
        // Search queries/colors are immutable and rule sets are replaced, never mutated. Snapshot
        // identities detect replacement, removal and reordering without hashing every pattern.
        if (!_searches.SequenceEqual(searches) || !ReferenceEquals(_rules, rules))
        {
            _entries.Clear();
            _recent.Clear();
            _spanCount = 0;
            _searches = searches.ToArray();
            _rules = rules;
        }
        if (_entries.TryGetValue(line, out var cached))
        {
            _recent.Remove(cached);
            _recent.AddLast(cached);
            return cached.Value.Spans;
        }

        var spans = Compute(line, searches, rules);
        // A single unusually dense line must not consume the entire cache budget.
        if (spans.Count > _maxSpans)
            return spans;
        while (_entries.Count >= _maxLines || _spanCount + spans.Count > _maxSpans)
        {
            var oldest = _recent.First!;
            _entries.Remove(oldest.Value.Line);
            _spanCount -= oldest.Value.Spans.Count;
            _recent.RemoveFirst();
        }
        _entries.Add(line, _recent.AddLast(new Entry(line, spans)));
        _spanCount += spans.Count;
        return spans;
    }

    private static IReadOnlyList<HighlightSpan> Compute(
        Line line,
        IReadOnlyList<Search> searches,
        RuleSet rules
    ) => ComputeForText(line.Raw, searches, rules);

    /// <summary>
    /// Search and rule highlights for one piece of text (a whole line or one cell), with overlaps
    /// resolved: earlier start wins, then the longer span, and a span starting inside a kept one is dropped.
    /// </summary>
    internal static IReadOnlyList<HighlightSpan> ComputeForText(
        string text,
        IReadOnlyList<Search> searches,
        RuleSet rules
    )
    {
        var candidates = searches
            .SelectMany(search =>
                search
                    .GetHighlights(text)
                    .Select(range => new HighlightSpan(range.Start, range.Length, search.Color))
            )
            .Concat(
                rules
                    .Highlights(text)
                    .Select(range => new HighlightSpan(range.Start, range.Length, range.Color))
            )
            .Where(span =>
                span.Start >= 0 && span.Length > 0 && span.Length <= text.Length - span.Start
            )
            .OrderBy(span => span.Start)
            .ThenByDescending(span => span.Length);
        var spans = new List<HighlightSpan>();
        var end = 0;
        foreach (var span in candidates)
        {
            if (span.Start < end)
                continue;
            spans.Add(span);
            end = span.Start + span.Length;
        }
        return spans.AsReadOnly();
    }

    private sealed record Entry(Line Line, IReadOnlyList<HighlightSpan> Spans);
}
