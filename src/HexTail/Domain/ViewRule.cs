namespace HexTail.Domain;

public enum RuleAction
{
    Highlight,
    ShowOnly,
    Hide,
}

public enum RuleScope
{
    View,
    Source,
    Global,
}

/// <summary>
/// One filter rule. Immutable; identity is value equality.
/// <see cref="PinnedAsTab"/> only applies to Highlight/ShowOnly: the rule then also owns a view tab
/// (a <c>Search</c>), and <see cref="RuleSet"/> ignores it because that search supplies the matches.
/// </summary>
public sealed record ViewRule
{
    public string Pattern { get; init; } = string.Empty;
    public MatchMode Mode { get; init; } = MatchMode.Literal;
    public bool CaseSensitive { get; init; }
    public string Color { get; init; } = "#F59E0B";
    public RuleAction Action { get; init; } = RuleAction.Highlight;
    public RuleScope Scope { get; init; } = RuleScope.Global;
    public bool Enabled { get; init; } = true;
    public bool PinnedAsTab { get; init; }
}
