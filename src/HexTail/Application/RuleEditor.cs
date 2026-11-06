using HexTail.Domain;

namespace HexTail.Application;

internal static class RuleEditor
{
    public static List<ViewRule> Add(IEnumerable<ViewRule> rules, ViewRule rule) =>
        [.. rules, rule];

    public static List<ViewRule> Replace(
        IEnumerable<ViewRule> rules,
        ViewRule old,
        ViewRule updated
    )
    {
        var list = rules.ToList();
        var index = list.IndexOf(old);
        if (index >= 0)
            list[index] = updated;
        return list;
    }

    public static List<ViewRule> Remove(IEnumerable<ViewRule> rules, ViewRule rule)
    {
        var list = rules.ToList();
        var index = list.IndexOf(rule);
        if (index >= 0)
            list.RemoveAt(index);
        return list;
    }
}
