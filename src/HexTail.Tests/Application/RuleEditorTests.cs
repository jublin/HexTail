using HexTail.Application;
using HexTail.Domain;
using HexTail.Tests.Support;

namespace HexTail.Tests.Application;

public sealed class RuleEditorTests
{
    [Fact]
    public void Add_AppendsWithoutTouchingTheInput()
    {
        var rules = new List<ViewRule> { TestRules.Source("a") };
        var result = RuleEditor.Add(rules, TestRules.Source("b"));
        Assert.Equal(["a", "b"], result.Select(rule => rule.Pattern));
        Assert.Single(rules);
    }

    [Fact]
    public void Replace_ActsOnTheFirstValueEqualRuleOnly()
    {
        var twin = TestRules.Source("x");
        var result = RuleEditor.Replace(
            [twin, TestRules.Source("y"), twin],
            twin,
            twin with
            {
                Enabled = false,
            }
        );
        Assert.Equal([false, true, true], result.Select(rule => rule.Enabled));
    }

    [Fact]
    public void Remove_DropsOneMatchAndIgnoresAbsentRules()
    {
        var rule = TestRules.Source("x");
        Assert.Equal(["x"], RuleEditor.Remove([rule, rule], rule).Select(r => r.Pattern));
        Assert.Single(RuleEditor.Remove([rule], TestRules.Source("other")));
    }
}
