using HexTail.Domain;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.ViewModels;

public sealed class RuleDraftTests
{
    [Fact]
    public void ToRuleCarriesEditsAndScope()
    {
        var draft = new RuleDraftViewModel(TestRules.Source("old"))
        {
            Pattern = "  new  ",
            Action = RuleAction.ShowOnly,
            AllSources = true,
            CaseSensitive = true,
        };
        var rule = draft.ToRule();
        Assert.Equal("new", rule.Pattern);
        Assert.Equal(RuleAction.ShowOnly, rule.Action);
        Assert.Equal(RuleScope.Global, rule.Scope);
        Assert.True(rule.CaseSensitive);
    }

    [Fact]
    public void PinningIsOnlyOfferedToGlobalNonHideRules()
    {
        var draft = new RuleDraftViewModel(TestRules.Source("x"));
        Assert.False(draft.CanPin);
        draft.AllSources = true;
        Assert.True(draft.CanPin);
        draft.PinnedAsTab = true;
        draft.Action = RuleAction.Hide;
        Assert.False(draft.CanPin);
        Assert.False(draft.ToRule().PinnedAsTab);
    }

    [Fact]
    public void InvalidRegexBlocksSaving()
    {
        var draft = new RuleDraftViewModel(TestRules.Source("x"))
        {
            MatchMode = MatchMode.Regex,
            Pattern = "[",
        };
        Assert.False(draft.CanSave);
        Assert.NotNull(draft.Error);
        Assert.True(draft.HasError);
        draft.Pattern = "a|b";
        Assert.True(draft.CanSave);
        Assert.Null(draft.Error);
        Assert.False(draft.HasError);
    }

    [Fact]
    public void BlankPatternBlocksSaving()
    {
        Assert.False(new RuleDraftViewModel(TestRules.Source("x")) { Pattern = "  " }.CanSave);
    }

    [Fact]
    public void PickingASwatchSetsTheColorAndKeepsOtherFields()
    {
        var original = TestRules.Source("x") with { Enabled = false };
        var draft = new RuleDraftViewModel(original);
        var swatch = draft.Swatches[1];
        draft.PickSwatchCommand.Execute(swatch).Subscribe();
        Assert.Equal(swatch, draft.Color);
        var rule = draft.ToRule();
        Assert.Equal(swatch, rule.Color);
        Assert.False(rule.Enabled);
    }

    [Fact]
    public void PickerColorRoundTripsThroughTheHexColor()
    {
        var draft = new RuleDraftViewModel(TestRules.Source("x") with { Color = "#102030" });
        Assert.Equal(Avalonia.Media.Color.Parse("#102030"), draft.PickerColor);
        draft.PickerColor = Avalonia.Media.Color.Parse("#AABBCC");
        Assert.Equal("#AABBCC", draft.Color);
    }
}
