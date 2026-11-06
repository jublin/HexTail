using HexTail.Domain;

namespace HexTail.Tests.Domain;

public sealed class RuleSetTests
{
    private static ViewRule Rule(
        string pattern,
        RuleAction action = RuleAction.Highlight,
        bool enabled = true,
        bool pinned = false,
        MatchMode mode = MatchMode.Literal,
        bool caseSensitive = false,
        string color = "#112233"
    ) =>
        new()
        {
            Pattern = pattern,
            Action = action,
            Enabled = enabled,
            PinnedAsTab = pinned,
            Mode = mode,
            CaseSensitive = caseSensitive,
            Color = color,
            Scope = RuleScope.Global,
        };

    private static Line L(string raw) => new(raw);

    [Fact]
    public void Hide_RemovesMatchingLinesIgnoringCaseByDefault()
    {
        var rules = new RuleSet([Rule("healthz", RuleAction.Hide)]);
        Assert.False(rules.IsVisible(L("GET /HEALTHZ 200")));
        Assert.True(rules.IsVisible(L("GET /orders 200")));
        Assert.True(rules.HasVisibilityRules);
    }

    [Fact]
    public void CaseSensitiveRule_OnlyMatchesExactCase()
    {
        var rules = new RuleSet([Rule("Error", RuleAction.Hide, caseSensitive: true)]);
        Assert.False(rules.IsVisible(L("an Error here")));
        Assert.True(rules.IsVisible(L("an error here")));
    }

    [Fact]
    public void ShowOnly_RulesCombineWithAnd_AndHideWins()
    {
        var rules = new RuleSet([
            Rule("checkout", RuleAction.ShowOnly),
            Rule("timeout", RuleAction.ShowOnly),
            Rule("retry", RuleAction.Hide),
        ]);
        Assert.True(rules.IsVisible(L("checkout timeout")));
        Assert.False(rules.IsVisible(L("checkout ok")));
        Assert.False(rules.IsVisible(L("checkout timeout retry")));
    }

    [Fact]
    public void DisabledAndPinnedRules_AreIgnored()
    {
        var rules = new RuleSet([
            Rule("a", RuleAction.Hide, enabled: false),
            Rule("b", RuleAction.Hide, pinned: true),
            Rule("c", RuleAction.Highlight, pinned: true),
        ]);
        Assert.False(rules.HasVisibilityRules);
        Assert.True(rules.IsVisible(L("a b c")));
        Assert.Empty(rules.Highlights(L("a b c")));
    }

    [Fact]
    public void Highlights_CoverHighlightAndShowOnlyRulesWithTheirColors()
    {
        var rules = new RuleSet([
            Rule("warn", RuleAction.Highlight, color: "#aa0000"),
            Rule("db", RuleAction.ShowOnly, color: "#00aa00"),
            Rule("noise", RuleAction.Hide, color: "#0000aa"),
        ]);
        var ranges = rules.Highlights(L("warn from db and noise"));
        Assert.Contains(new ColoredRange(0, 4, "#aa0000"), ranges);
        Assert.Contains(new ColoredRange(10, 2, "#00aa00"), ranges);
        Assert.DoesNotContain(ranges, range => range.Color == "#0000aa");
    }

    [Fact]
    public void InvalidRegex_IsReportedAndSkippedInsteadOfThrowing()
    {
        var bad = Rule("([", RuleAction.Hide, mode: MatchMode.Regex);
        var rules = new RuleSet([bad, Rule("ok", RuleAction.Hide)]);
        Assert.Equal([bad], rules.InvalidRules);
        Assert.False(rules.IsVisible(L("ok")));
        Assert.True(rules.IsVisible(L("([")));
    }

    [Fact]
    public void For_ReturnsTheSameInstanceForTheSameList_AndCombineKeepsBothSides()
    {
        IReadOnlyList<ViewRule> list = [Rule("x", RuleAction.Hide)];
        Assert.Same(RuleSet.For(list), RuleSet.For(list));
        Assert.Same(RuleSet.Empty, RuleSet.For([]));

        var combined = RuleSet.Combine(list, [Rule("y", RuleAction.Hide)]);
        Assert.False(combined.IsVisible(L("x")));
        Assert.False(combined.IsVisible(L("y")));
        Assert.Same(RuleSet.For(list), RuleSet.Combine(list, []));
    }
}
