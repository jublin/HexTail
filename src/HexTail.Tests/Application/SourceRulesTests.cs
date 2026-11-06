using HexTail.Application;
using HexTail.Domain;
using HexTail.Elastic;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;

namespace HexTail.Tests.Application;

public sealed class SourceRulesTests
{
    private static LogSourceService NewTailers() =>
        new(
            new TailerOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                UseFileSystemWatcher = false,
            }
        );

    private static ViewRule Source(string pattern, RuleAction action) =>
        new()
        {
            Pattern = pattern,
            Action = action,
            Scope = RuleScope.Source,
        };

    [Fact]
    public async Task SourceRulesAndLevels_PersistAndRestoreForAFile()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllLinesAsync(
            path,
            ["INFO a", "ERROR b"],
            TestContext.Current.CancellationToken
        );
        var persistence = new TestPersistence();
        try
        {
            await using (
                var state = new AppState(
                    NewTailers(),
                    persistence,
                    credentials: new InMemoryCredentialVault()
                )
            )
            {
                var tab = await state.OpenFileAsync(
                    path,
                    cancellationToken: TestContext.Current.CancellationToken
                );
                await state.SetSourceRulesAsync(
                    tab,
                    [Source("a", RuleAction.Hide)],
                    TestContext.Current.CancellationToken
                );
                await state.SetLevelsAsync(
                    tab,
                    LogLevels.Warn | LogLevels.Error,
                    TestContext.Current.CancellationToken
                );
                Assert.Equal(RuleScope.Source, Assert.Single(tab.Rules).Scope);
            }

            await using var restored = new AppState(
                NewTailers(),
                persistence,
                credentials: new InMemoryCredentialVault()
            );
            await restored.RestoreAsync(TestContext.Current.CancellationToken);
            var tab2 = Assert.Single(restored.Files);
            Assert.Equal("a", Assert.Single(tab2.Rules).Pattern);
            Assert.Equal(LogLevels.Warn | LogLevels.Error, tab2.Levels);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SourceRulesAndLevels_SurviveAJsonRoundTripAndLegacyConfigRestoresAll()
    {
        var path = Path.GetTempFileName();
        var persistence = new TestPersistence();
        try
        {
            await using (
                var state = new AppState(
                    NewTailers(),
                    persistence,
                    credentials: new InMemoryCredentialVault()
                )
            )
            {
                var tab = await state.OpenFileAsync(
                    path,
                    cancellationToken: TestContext.Current.CancellationToken
                );
                await state.SetSourceRulesAsync(
                    tab,
                    [Source("noise", RuleAction.Hide)],
                    TestContext.Current.CancellationToken
                );
                await state.SetLevelsAsync(
                    tab,
                    LogLevels.Info | LogLevels.Debug,
                    TestContext.Current.CancellationToken
                );
            }

            var json = AppConfigJson.Serialize(persistence.Config!);
            await using var restored = new AppState(
                NewTailers(),
                new TestPersistence(AppConfigJson.Deserialize(json)),
                credentials: new InMemoryCredentialVault()
            );
            await restored.RestoreAsync(TestContext.Current.CancellationToken);
            var tab2 = Assert.Single(restored.Files);
            Assert.Equal(RuleAction.Hide, Assert.Single(tab2.Rules).Action);
            Assert.Equal(LogLevels.Info | LogLevels.Debug, tab2.Levels);

            // A config written before levels existed has no level member and an explicit
            // None or unknown mask must never leave a tab with an empty view.
            Assert.Equal(LogLevels.All, new PersistedFileTab { Path = path }.Levels);
            Assert.Equal(LogLevels.All, AppState.NormalizeLevels(LogLevels.None));
            Assert.Equal(
                LogLevels.Error,
                AppState.NormalizeLevels(LogLevels.Error | (LogLevels)64)
            );
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SourceRulesAndLevels_PersistAndRestoreForAnElasticTab()
    {
        var connection = new ElasticConnectionSettings
        {
            Id = "elastic-1",
            Name = "ops",
            KibanaUrl = "https://kibana/",
            ElasticsearchUrl = "https://elastic/",
            DataViewId = "view",
            DataViewTitle = "logs-*",
            TimeFieldName = "@timestamp",
            ServerField = "server",
            NamespaceField = "namespace",
            OutputFields = ["message"],
            Sources = [new ElasticSourceSettings { Id = "source-1", ServerValue = "api" }],
        };
        var persistence = new TestPersistence();
        await using (
            var original = new AppState(
                NewTailers(),
                persistence,
                new AppSettings { ElasticConnections = [connection] },
                elastic: new FakeElasticApiClient(),
                credentials: new InMemoryCredentialVault()
            )
        )
        {
            var tab = await original.OpenElasticSourceAsync(
                "source-1",
                save: false,
                cancellationToken: TestContext.Current.CancellationToken
            );
            await original.SetSourceRulesAsync(
                tab,
                [Source("healthz", RuleAction.Hide)],
                TestContext.Current.CancellationToken
            );
            await original.SetLevelsAsync(
                tab,
                LogLevels.Error,
                TestContext.Current.CancellationToken
            );
        }

        var json = AppConfigJson.Serialize(persistence.Config!);
        await using var restored = new AppState(
            NewTailers(),
            new TestPersistence(AppConfigJson.Deserialize(json)),
            elastic: new FakeElasticApiClient(),
            credentials: new InMemoryCredentialVault()
        );
        await restored.RestoreAsync(TestContext.Current.CancellationToken);
        var restoredTab = restored.SelectedFile!;
        Assert.Equal("healthz", Assert.Single(restoredTab.Rules).Pattern);
        Assert.Equal(LogLevels.Error, restoredTab.Levels);
    }

    [Fact]
    public async Task SetLevels_StoresAllForNoneOrUnknownBits_AndRulesAreNormalized()
    {
        var path = Path.GetTempFileName();
        try
        {
            await using var state = new AppState(
                NewTailers(),
                new TestPersistence(),
                credentials: new InMemoryCredentialVault()
            );
            var tab = await state.OpenFileAsync(
                path,
                cancellationToken: TestContext.Current.CancellationToken
            );

            await state.SetLevelsAsync(tab, LogLevels.None, TestContext.Current.CancellationToken);
            Assert.Equal(LogLevels.All, tab.Levels);
            await state.SetLevelsAsync(tab, (LogLevels)0xFF, TestContext.Current.CancellationToken);
            Assert.Equal(LogLevels.All, tab.Levels);

            await state.SetSourceRulesAsync(
                tab,
                [
                    new ViewRule
                    {
                        Pattern = "  x  ",
                        Scope = RuleScope.Global,
                        Color = "bad",
                    },
                    new ViewRule { Pattern = "  " },
                ],
                TestContext.Current.CancellationToken
            );
            var rule = Assert.Single(tab.Rules);
            Assert.Equal(
                ("x", RuleScope.Source, "#f59e0b"),
                (rule.Pattern, rule.Scope, rule.Color)
            );
        }
        finally
        {
            File.Delete(path);
        }
    }
}
