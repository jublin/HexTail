using HexTail.Application;
using HexTail.Domain;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;

namespace HexTail.Tests.Application;

public sealed class RuleCommandsTests
{
    private static AppState NewState(TestPersistence? persistence = null) =>
        new(
            new LogSourceService(
                new TailerOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(10),
                    UseFileSystemWatcher = false,
                }
            ),
            persistence ?? new TestPersistence(),
            credentials: new InMemoryCredentialVault()
        );

    private static async Task<(FileTabState Tab, string Path)> OpenTempFileAsync(AppState state)
    {
        var path = Path.GetTempFileName();
        await File.WriteAllLinesAsync(
            path,
            ["INFO a", "ERROR b"],
            TestContext.Current.CancellationToken
        );
        var tab = await state.OpenFileAsync(
            path,
            cancellationToken: TestContext.Current.CancellationToken
        );
        return (tab, path);
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
        var persistence = new TestPersistence();
        await using var state = NewState(persistence);
        var (tab, path) = await OpenTempFileAsync(state);
        try
        {
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
            Assert.Equal(
                ["noise"],
                persistence.Config!.OpenFiles.Single().Rules.Select(r => r.Pattern)
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
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CrossScopeReplaceWithoutATabChangesNothing()
    {
        await using var state = NewState();
        await state.AddRuleAsync(
            null,
            TestRules.Highlight("keep"),
            TestContext.Current.CancellationToken
        );
        var global = state.Settings.Rules[0];

        await state.ReplaceRuleAsync(
            null,
            global,
            global with
            {
                Scope = RuleScope.Source,
            },
            TestContext.Current.CancellationToken
        );
        Assert.Equal(["keep"], state.Settings.Rules.Select(r => r.Pattern));

        var source = TestRules.Source("src");
        await state.ReplaceRuleAsync(
            null,
            source,
            source with
            {
                Scope = RuleScope.Global,
            },
            TestContext.Current.CancellationToken
        );
        Assert.Equal(["keep"], state.Settings.Rules.Select(r => r.Pattern));
    }

    [Fact]
    public async Task GlobalReplaceAndRemoveOfAbsentRulesAreNoOps()
    {
        await using var state = NewState();
        await state.AddRuleAsync(
            null,
            TestRules.Highlight("keep"),
            TestContext.Current.CancellationToken
        );
        var absent = TestRules.Highlight("absent");

        await state.ReplaceRuleAsync(
            null,
            absent,
            absent with
            {
                Enabled = false,
            },
            TestContext.Current.CancellationToken
        );
        await state.RemoveRuleAsync(null, absent, TestContext.Current.CancellationToken);

        var rule = Assert.Single(state.Settings.Rules);
        Assert.Equal("keep", rule.Pattern);
        Assert.True(rule.Enabled);
    }
}
