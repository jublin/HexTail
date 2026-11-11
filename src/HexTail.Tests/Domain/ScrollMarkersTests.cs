using HexTail.Domain;

namespace HexTail.Tests.Domain;

public sealed class ScrollMarkersTests
{
    private static Line L(string raw) => new(raw, null);

    [Fact]
    public void MarksRuleMatchesAndErrors_InRowOrder()
    {
        var rows = new[]
        {
            L("INFO quiet"),
            L("ERROR boom"),
            L("INFO token here"),
            L("ERROR token too"),
        };
        var markers = ScrollMarkers.Build(
            rows,
            line => line.Raw.Contains("token", StringComparison.Ordinal) ? "#123456" : null
        );
        Assert.Equal(
            [
                new ScrollMarker(1, null, true),
                new ScrollMarker(2, "#123456", false),
                new ScrollMarker(3, "#123456", true),
            ],
            markers
        );
    }

    [Fact]
    public void NoMatches_GivesNoMarkers() =>
        Assert.Empty(ScrollMarkers.Build([L("INFO a"), L("WARN b")], _ => null));

    [Fact]
    public void FirstHighlightColor_IsTheFirstMatchingHighlightRule()
    {
        var rules = new RuleSet([
            new ViewRule { Pattern = "zzz", Color = "#000001" },
            new ViewRule { Pattern = "boom", Color = "#000002" },
            new ViewRule { Pattern = "bo", Color = "#000003" },
            new ViewRule
            {
                Pattern = "boom",
                Action = RuleAction.Hide,
                Color = "#000004",
            },
            new ViewRule
            {
                Pattern = "boom",
                Enabled = false,
                Color = "#000005",
            },
        ]);
        Assert.Equal("#000002", rules.FirstHighlightColor(L("ERROR boom")));
        Assert.Null(rules.FirstHighlightColor(L("INFO calm")));
    }
}
