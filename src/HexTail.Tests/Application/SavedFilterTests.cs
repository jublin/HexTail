using HexTail.Application;
using HexTail.Domain;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;

namespace HexTail.Tests.Application;

public sealed class SavedFilterTests
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
    public async Task SavingCapturesSourceRulesAndLevelsAndOverwritesBySameName()
    {
        await using var state = NewState();
        await state.SaveFilterAsync(
            "Errors",
            [TestRules.Source("boom")],
            LogLevels.Error | LogLevels.Warn,
            TestContext.Current.CancellationToken
        );
        await state.SaveFilterAsync(
            "errors ",
            [TestRules.Highlight("other")],
            LogLevels.All,
            TestContext.Current.CancellationToken
        );
        var filter = Assert.Single(state.SavedFilters);
        Assert.Equal("errors", filter.Name);
        Assert.Equal(["other"], filter.Rules.Select(rule => rule.Pattern));
        Assert.All(filter.Rules, rule => Assert.Equal(RuleScope.Source, rule.Scope));
        Assert.Equal(LogLevels.All, filter.Levels);
    }

    [Fact]
    public async Task ApplyMergesRulesWithoutDuplicatesAndSetsLevelsOnlyWhenNotAll()
    {
        await using var state = NewState();
        var tab = await OpenTempFileAsync(state);
        await state.SetSourceRulesAsync(
            tab,
            [TestRules.Source("keep")],
            TestContext.Current.CancellationToken
        );
        await state.SaveFilterAsync(
            "mix",
            [TestRules.Source("keep"), TestRules.Source("noise", RuleAction.Hide)],
            LogLevels.Error,
            TestContext.Current.CancellationToken
        );
        await state.ApplySavedFilterAsync(tab, "mix", TestContext.Current.CancellationToken);
        Assert.Equal(["keep", "noise"], tab.Rules.Select(rule => rule.Pattern));
        Assert.Equal(LogLevels.Error, tab.Levels);

        await state.SaveFilterAsync(
            "rules-only",
            [TestRules.Source("x")],
            LogLevels.All,
            TestContext.Current.CancellationToken
        );
        await state.ApplySavedFilterAsync(tab, "rules-only", TestContext.Current.CancellationToken);
        Assert.Equal(LogLevels.Error, tab.Levels);
        Assert.Contains(tab.Rules, rule => rule.Pattern == "x");
    }

    [Fact]
    public async Task DeleteAndUnknownNamesAreSafe()
    {
        await using var state = NewState();
        var tab = await OpenTempFileAsync(state);
        await state.SaveFilterAsync(
            "a",
            [TestRules.Source("x")],
            LogLevels.All,
            TestContext.Current.CancellationToken
        );
        await state.ApplySavedFilterAsync(tab, "missing", TestContext.Current.CancellationToken);
        Assert.Empty(tab.Rules);
        await state.DeleteSavedFilterAsync("A", TestContext.Current.CancellationToken);
        Assert.Empty(state.SavedFilters);
    }

    [Fact]
    public async Task SavedFiltersSurviveARestart()
    {
        var persistence = new TestPersistence();
        await using (var first = NewState(persistence))
            await first.SaveFilterAsync(
                "keep",
                [TestRules.Source("boom")],
                LogLevels.Warn,
                TestContext.Current.CancellationToken
            );
        await using var second = NewState(persistence);
        await second.RestoreAsync(TestContext.Current.CancellationToken);
        var filter = Assert.Single(second.SavedFilters);
        Assert.Equal("keep", filter.Name);
        Assert.Equal(LogLevels.Warn, filter.Levels);
        Assert.Equal(["boom"], filter.Rules.Select(rule => rule.Pattern));
    }

    [Fact]
    public async Task RestoreNormalizesNamesAndKeepsTheLaterOfCaseInsensitiveDuplicates()
    {
        var persistence = new TestPersistence(
            new AppConfig
            {
                SavedFilters =
                [
                    new SavedFilter { Name = " Dup ", Rules = [TestRules.Source("first")] },
                    new SavedFilter { Name = "  " },
                    new SavedFilter
                    {
                        Name = "dup",
                        Rules = [TestRules.Source("second")],
                        Levels = LogLevels.None,
                    },
                ],
            }
        );
        await using var state = NewState(persistence);
        await state.RestoreAsync(TestContext.Current.CancellationToken);
        var filter = Assert.Single(state.SavedFilters);
        Assert.Equal("dup", filter.Name);
        Assert.Equal(["second"], filter.Rules.Select(rule => rule.Pattern));
        Assert.Equal(LogLevels.All, filter.Levels);
    }
}
