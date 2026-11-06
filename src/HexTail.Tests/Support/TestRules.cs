using HexTail.Domain;

namespace HexTail.Tests.Support;

internal static class TestRules
{
    public static ViewRule Highlight(
        string pattern,
        string color = "#F59E0B",
        bool pinned = false
    ) =>
        new()
        {
            Pattern = pattern,
            Mode = CompiledQuery.DetectMode(pattern),
            Color = color,
            Action = RuleAction.Highlight,
            Scope = RuleScope.Global,
            PinnedAsTab = pinned,
        };

    public static ViewRule Hide(string pattern) =>
        new()
        {
            Pattern = pattern,
            Mode = CompiledQuery.DetectMode(pattern),
            Action = RuleAction.Hide,
            Scope = RuleScope.Global,
        };
}
