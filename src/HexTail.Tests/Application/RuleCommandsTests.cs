using HexTail.Application;
using HexTail.Domain;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;

namespace HexTail.Tests.Application;

public sealed class RuleCommandsTests
{
    private static AppState NewState() =>
        new(
            new LogSourceService(
                new TailerOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(10),
                    UseFileSystemWatcher = false,
                }
            ),
            new TestPersistence(),
            credentials: new InMemoryCredentialVault()
        );

    private static async Task<FileTabState> OpenTempFileAsync(AppState state)
    {
        var path = Path.GetTempFileName();
        await File.WriteAllLinesAsync(
            path,
            ["INFO a", "ERROR b"],
            TestContext.Current.CancellationToken
        );
        return await state.OpenFileAsync(
            path,
            cancellationToken: TestContext.Current.CancellationToken
        );
    }

    [Fact]
    public async Task GlobalRulesRoundTripThroughAddReplaceRemove()
    {
        await using var state = NewState();
        var rule = TestRules.Highlight("boom");
        await state.AddRuleAsync(null, rule, TestContext.Current.CancellationToken);
        Assert.Equal(["boom"], state.Settings.Rules.Select(r => r.Pattern));
        var stored = state.Settings.Rules[0];
        await state.ReplaceRuleAsync(
            null,
            stored,
            stored with
            {
                Enabled = false,
            },
            TestContext.Current.CancellationToken
        );
        Assert.False(state.Settings.Rules[0].Enabled);
        await state.RemoveRuleAsync(
            null,
            state.Settings.Rules[0],
            TestContext.Current.CancellationToken
        );
        Assert.Empty(state.Settings.Rules);
    }

    [Fact]
    public async Task SourceRulesNeedATabAndMoveBetweenScopes()
    {
        await using var state = NewState();
        var tab = await OpenTempFileAsync(state);
        await state.AddRuleAsync(
            null,
            TestRules.Source("ignored"),
            TestContext.Current.CancellationToken
        );
        Assert.Empty(tab.Rules);

        await state.AddRuleAsync(
            tab,
            TestRules.Source("noise", RuleAction.Hide),
            TestContext.Current.CancellationToken
        );
        var stored = tab.Rules[0];
        await state.ReplaceRuleAsync(
            tab,
            stored,
            stored with
            {
                Scope = RuleScope.Global,
            },
            TestContext.Current.CancellationToken
        );
        Assert.Empty(tab.Rules);
        Assert.Equal(["noise"], state.Settings.Rules.Select(r => r.Pattern));
        Assert.Equal(RuleAction.Hide, state.Settings.Rules[0].Action);
    }

    [Fact]
    public void Validate_ReportsBadRegexOnly()
    {
        Assert.Null(RuleValidation.Validate("[", MatchMode.Literal));
        Assert.NotNull(RuleValidation.Validate("[", MatchMode.Regex));
        Assert.Null(RuleValidation.Validate("a|b", MatchMode.Regex));
    }
}
