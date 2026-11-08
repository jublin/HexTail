using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace HexTail.Domain;

public readonly record struct ColoredRange(int Start, int Length, string Color);

/// <summary>
/// Compiled, immutable evaluation of a rule list. Hide wins; several ShowOnly rules must all match;
/// ShowOnly and Highlight rules contribute highlight ranges. Disabled and pinned rules are ignored.
/// Rules whose regex does not compile are skipped and listed in <see cref="InvalidRules"/>.
/// </summary>
public sealed class RuleSet
{
    private const int QueryCacheLimit = 512;

    public static RuleSet Empty { get; } = new([]);

    private static readonly ConditionalWeakTable<IReadOnlyList<ViewRule>, RuleSet> ForCache = new();
    private static readonly ConcurrentDictionary<
        (string Pattern, MatchMode Mode, bool CaseSensitive),
        CompiledQuery?
    > Queries = new();

    private readonly Matcher[] _hide;
    private readonly Matcher[] _showOnly;
    private readonly Matcher[] _highlight;

    public RuleSet(IEnumerable<ViewRule> rules)
    {
        var hide = new List<Matcher>();
        var showOnly = new List<Matcher>();
        var highlight = new List<Matcher>();
        var invalid = new List<ViewRule>();
        foreach (var rule in rules)
        {
            if (!rule.Enabled || rule.PinnedAsTab || string.IsNullOrWhiteSpace(rule.Pattern))
                continue;
            var query = Compile(rule);
            if (query is null)
            {
                invalid.Add(rule);
                continue;
            }
            var matcher = new Matcher(query, rule.Color);
            switch (rule.Action)
            {
                case RuleAction.Hide:
                    hide.Add(matcher);
                    break;
                case RuleAction.ShowOnly:
                    showOnly.Add(matcher);
                    highlight.Add(matcher);
                    break;
                default:
                    highlight.Add(matcher);
                    break;
            }
        }
        _hide = [.. hide];
        _showOnly = [.. showOnly];
        _highlight = [.. highlight];
        InvalidRules = invalid;
    }

    public IReadOnlyList<ViewRule> InvalidRules { get; }
    public bool HasVisibilityRules => _hide.Length + _showOnly.Length > 0;

    /// <summary>Cached by list instance; callers replace lists instead of mutating them.</summary>
    public static RuleSet For(IReadOnlyList<ViewRule> rules) =>
        rules.Count == 0 ? Empty : ForCache.GetValue(rules, static list => new RuleSet(list));

    public static RuleSet Combine(IReadOnlyList<ViewRule> first, IReadOnlyList<ViewRule> second) =>
        second.Count == 0 ? For(first)
        : first.Count == 0 ? For(second)
        : new RuleSet(first.Concat(second));

    public bool IsVisible(Line line)
    {
        foreach (var matcher in _hide)
            if (matcher.Query.IsMatch(line.Raw))
                return false;
        foreach (var matcher in _showOnly)
            if (!matcher.Query.IsMatch(line.Raw))
                return false;
        return true;
    }

    public IReadOnlyList<ColoredRange> Highlights(Line line) => Highlights(line.Raw);

    /// <summary>Highlight ranges within <paramref name="text"/> (a whole line or one cell of it).</summary>
    public IReadOnlyList<ColoredRange> Highlights(string text)
    {
        if (_highlight.Length == 0)
            return [];
        List<ColoredRange>? ranges = null;
        foreach (var matcher in _highlight)
        foreach (var range in matcher.Query.GetHighlights(text))
            (ranges ??= []).Add(new ColoredRange(range.Start, range.Length, matcher.Color));
        return ranges ?? [];
    }

    private static CompiledQuery? Compile(ViewRule rule)
    {
        var key = (rule.Pattern, rule.Mode, rule.CaseSensitive);
        if (Queries.TryGetValue(key, out var cached))
            return cached;
        CompiledQuery? query;
        try
        {
            query = new CompiledQuery(rule.Pattern, rule.Mode, rule.CaseSensitive);
        }
        catch (ArgumentException)
        {
            query = null;
        }
        if (Queries.Count >= QueryCacheLimit)
            Queries.Clear();
        Queries[key] = query;
        return query;
    }

    private readonly record struct Matcher(CompiledQuery Query, string Color);
}
