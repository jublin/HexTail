using HexTail.Domain;

namespace HexTail.Tests.Domain;

public sealed class RuleValidationTests
{
    [Fact]
    public void Validate_ReportsBadRegexOnly()
    {
        Assert.Null(RuleValidation.Validate("[", MatchMode.Literal));
        Assert.NotNull(RuleValidation.Validate("[", MatchMode.Regex));
        Assert.Null(RuleValidation.Validate("a|b", MatchMode.Regex));
    }
}
