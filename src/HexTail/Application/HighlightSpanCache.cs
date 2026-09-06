using HexTail.Domain;
using HexTail.Persistence;

namespace HexTail.Application;

internal readonly record struct HighlightSpan(int Start, int Length, string Color);

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
    private GlobalLabel[] _labels = [];
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
        AppSettings settings
    )
    {
        // Search queries/colors and label properties are immutable. Snapshot identities
        // detect replacement, removal and reordering without hashing every pattern.
        if (!_searches.SequenceEqual(searches) || !_labels.SequenceEqual(settings.GlobalLabels))
        {
            _entries.Clear();
            _recent.Clear();
            _spanCount = 0;
            _searches = searches.ToArray();
            _labels = settings.GlobalLabels.ToArray();
        }
        if (_entries.TryGetValue(line, out var cached))
        {
            _recent.Remove(cached);
            _recent.AddLast(cached);
            return cached.Value.Spans;
        }

        var spans = Compute(line, searches, settings);
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
        AppSettings settings
    )
    {
        var candidates = searches
            .SelectMany(search =>
                search
                    .GetHighlights(line)
                    .Select(range => new HighlightSpan(range.Start, range.Length, search.Color))
            )
            .Concat(
                settings
                    .GetLabelHighlights(line.Raw, includeSearchTabs: false)
                    .Select(range => new HighlightSpan(range.Start, range.Length, range.Color))
            )
            .Where(span =>
                span.Start >= 0 && span.Length > 0 && span.Length <= line.Raw.Length - span.Start
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
