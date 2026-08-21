using HexTailSharp.Application;
using HexTailSharp.Domain;
using HexTailSharp.Elastic;
using HexTailSharp.Persistence;
using HexTailSharp.Tailing;
using HexTailSharp.Tests.Support;

namespace HexTailSharp.Tests.Application;

public sealed class AppStateTests
{
    [Fact]
    public async Task SaveElasticConnection_PreservesExistingApiKeyWhenSecretIsBlank()
    {
        var connection = ElasticConnection("Ops") with { AuthMode = ElasticAuthMode.ApiKey };
        var vault = new InMemoryCredentialVault();
        vault.Set(connection.Id, "saved-api-key");
        await using var state = new AppState(
            NewTailers(),
            new MemoryPersistence(),
            new AppSettings { ElasticConnections = [connection] },
            vault,
            new FakeElasticApiClient()
        );

        await state.SaveElasticConnectionAsync(connection with { Name = "Updated" }, null);

        Assert.Equal("saved-api-key", vault.Get(connection.Id));
        Assert.Equal("Updated", Assert.Single(state.Settings.ElasticConnections).Name);
    }

    [Fact]
    public async Task AppState_NormalizesLegacyElasticConnectionIntoView()
    {
        var connection = ElasticConnection("Ops");
        await using var state = new AppState(
            NewTailers(),
            new MemoryPersistence(),
            new AppSettings { ElasticConnections = [connection] },
            new InMemoryCredentialVault(),
            new FakeElasticApiClient()
        );

        var view = Assert.Single(Assert.Single(state.Settings.ElasticConnections).Views);

        Assert.Equal(connection.DataViewId, view.DataViewId);
        Assert.Equal(connection.DataViewTitle, view.DataViewTitle);
        Assert.Equal(connection.Sources, view.Sources);
    }

    [Fact]
    public async Task SaveElasticConnection_RestoresSecretAndSettingsWhenJsonSaveFails()
    {
        var old = ElasticConnection("Old name");
        var updated = old with { Name = "New name" };
        var persistence = new MemoryPersistence { SaveError = new IOException("disk full") };
        var vault = new InMemoryCredentialVault();
        vault.Set("elastic-1", "old-secret");
        await using var state = new AppState(
            NewTailers(),
            persistence,
            new AppSettings { ElasticConnections = [old] },
            vault,
            new FakeElasticApiClient()
        );

        await Assert.ThrowsAsync<IOException>(() =>
            state.SaveElasticConnectionAsync(updated, "new-secret").AsTask()
        );

        Assert.Equal("old-secret", vault.Get("elastic-1"));
        Assert.Equal("Old name", Assert.Single(state.Settings.ElasticConnections).Name);
        persistence.SaveError = null;
    }

    [Fact]
    public async Task OpenElasticSource_UsesOneTabPerStableSourceAndPersistsRemoteSelection()
    {
        var connection = ElasticConnection("ops") with
        {
            Sources =
            [
                new ElasticSourceSettings
                {
                    Id = "source-1",
                    ServerValue = "api",
                    NamespaceValue = "prod",
                },
            ],
        };
        var persistence = new MemoryPersistence();
        await using var state = new AppState(
            NewTailers(),
            persistence,
            new AppSettings { ElasticConnections = [connection] },
            new InMemoryCredentialVault(),
            new FakeElasticApiClient()
        );

        var first = await state.OpenElasticSourceAsync("source-1", save: false);
        var second = await state.OpenElasticSourceAsync("source-1", save: false);
        await state.SaveAsync();

        Assert.Same(first, second);
        Assert.Equal(LogSourceKind.Elastic, first.Source.Kind);
        Assert.Equal("api-prod", first.DisplayName);
        Assert.Empty(Assert.IsType<AppConfig>(persistence.Config).OpenFiles);
        Assert.Equal(
            "source-1",
            Assert.Single(Assert.IsType<AppConfig>(persistence.Config).OpenElasticTabs).SourceId
        );
        Assert.Equal(
            "source-1",
            Assert.IsType<AppConfig>(persistence.Config).SelectedElasticSourceId
        );
    }

    [Fact]
    public async Task OpenAndDrain_AppendsParsedLinesAndUpdatesSearches()
    {
        var path = CreateTempFile("level=info\n", ".logfmt");
        var persistence = new MemoryPersistence();
        await using var tailers = new LogSourceService(
            new TailerOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                UseFileSystemWatcher = false,
            }
        );
        await using var state = new AppState(tailers, persistence);

        var tab = await state.OpenFileAsync(path);
        await DrainUntilAsync(state, () => tab.Buffer.Count == 1);
        var search = state.AddSearch(
            tab,
            "info",
            MatchMode.Literal,
            caseSensitive: true,
            "#00ff00"
        );

        await File.AppendAllTextAsync(path, "level=error\n");
        await DrainUntilAsync(state, () => tab.Buffer.Count == 2);

        Assert.Equal("info", tab.Buffer[0].ParsedFields!["level"]);
        Assert.Equal([0], search.Results);
    }

    [Fact]
    public async Task SaveAndRestore_PreservesTabsSearchesAndViewSettings()
    {
        var path = CreateTempFile("error\n");
        var persistence = new MemoryPersistence();
        await using (var tailers = NewTailers())
        await using (var state = new AppState(tailers, persistence))
        {
            var tab = await state.OpenFileAsync(path);
            await DrainUntilAsync(state, () => tab.Buffer.Count == 1);
            tab.FollowAll = false;
            tab.ShowContext = true;
            tab.ContextAbove = 4;
            tab.ContextBelow = 8;
            state.AddSearch(tab, "error", MatchMode.Literal, caseSensitive: false, "#ff0000");
            await state.SaveAsync();
        }

        await using var restoredTailers = NewTailers();
        await using var restored = new AppState(restoredTailers, persistence);
        await restored.RestoreAsync();

        var restoredTab = Assert.Single(restored.Files);
        Assert.False(restoredTab.FollowAll);
        Assert.True(restoredTab.ShowContext);
        Assert.Equal(4, restoredTab.ContextAbove);
        Assert.Equal(8, restoredTab.ContextBelow);
        var search = Assert.Single(restoredTab.Searches);
        Assert.Equal("error", search.Query.Query);
        Assert.False(search.Query.CaseSensitive);
    }

    [Fact]
    public void AppConfigJson_LoadsOlderConfigWithMissingOptionalFields()
    {
        var config = AppConfigJson.Deserialize("{\"openFiles\":[{\"path\":\"/tmp/app.log\"}]}");

        var tab = Assert.Single(config.OpenFiles);
        Assert.Equal("/tmp/app.log", tab.Path);
        Assert.Empty(tab.Searches);
        Assert.Equal(3, tab.ContextAbove);
        Assert.Equal(10, tab.ContextBelow);
        Assert.Empty(config.OpenElasticTabs);
        Assert.Empty(config.Settings.ElasticConnections);
    }

    [Fact]
    public async Task UpdateSettings_PersistsNormalizedGlobalRules()
    {
        var persistence = new MemoryPersistence();
        await using var state = new AppState(NewTailers(), persistence);

        await state.UpdateSettingsAsync(
            new AppSettings
            {
                GlobalLabels =
                [
                    new GlobalLabel { Text = " Error ", Color = "#ff0000" },
                    new GlobalLabel { Text = "error", Color = "#00ff00" },
                ],
                GlobalExcludeLabels = [" Health ", "health", ""],
                Theme = "not-a-theme",
                SettingsMenuAlignment = SettingsMenuAlignment.Left,
            }
        );

        var settings = Assert.IsType<AppConfig>(persistence.Config).Settings;
        var label = Assert.Single(settings.GlobalLabels);
        Assert.Equal("Error", label.Text);
        Assert.Equal("#ff0000", label.Color);
        Assert.Equal(["Health"], settings.GlobalExcludeLabels);
        Assert.Equal("cyber-tail", settings.Theme);
        Assert.Equal(SettingsMenuAlignment.Right, settings.SettingsMenuAlignment);
    }

    [Fact]
    public void AppSettings_MatchesLabelsAndExclusionsCaseInsensitively()
    {
        var settings = new AppSettings
        {
            GlobalLabels = [new GlobalLabel { Text = @"warn\s+id", Color = "#f59e0b" }],
            GlobalExcludeLabels = [@"health\s+check"],
        };

        Assert.True(settings.Excludes("GET /HEALTH CHECK"));
        var highlight = settings.GetLabelHighlights("WARN ID: warn id").First();
        Assert.Equal(0, highlight.Start);
        Assert.Equal(7, highlight.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("system")]
    [InlineData("light")]
    [InlineData("material-wcag")]
    [InlineData("dark")]
    public void ThemeCatalog_NormalizesEveryLegacyValueToCyberTail(string? value)
    {
        Assert.Equal(["cyber-tail", "catppuccin-mocha", "spotify"], ThemeCatalog.Names);
        Assert.Equal(
            value is "catppuccin-mocha" or "spotify" ? value : "cyber-tail",
            ThemeCatalog.Normalize(value)
        );
    }

    [Fact]
    public async Task DrainTailerEvents_WithoutEventsDoesNotNotify()
    {
        await using var state = new AppState(NewTailers(), new MemoryPersistence());
        var notifications = 0;
        state.Changed += () => notifications++;

        Assert.False(state.DrainTailerEvents());
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task Files_ReturnsSnapshotWhileWorkspaceChanges()
    {
        var path = CreateTempFile(string.Empty);
        await using var state = new AppState(NewTailers(), new MemoryPersistence());

        var beforeOpen = state.Files;
        await state.OpenFileAsync(path, save: false);

        Assert.Empty(beforeOpen);
        Assert.Single(state.Files);
    }

    [Fact]
    public void LogParserSelector_UsesLogfmtOnlyForLogfmtExtension()
    {
        Assert.IsType<LogfmtParser>(LogParserSelector.ForPath("app.logfmt"));
        Assert.IsType<PlainTextParser>(LogParserSelector.ForPath("app.log"));
    }

    private static LogSourceService NewTailers() =>
        new(
            new TailerOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                UseFileSystemWatcher = false,
            }
        );

    private static async Task DrainUntilAsync(AppState state, Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            state.DrainTailerEvents();
            await Task.Delay(10, timeout.Token);
        }

        state.DrainTailerEvents();
    }

    private static string CreateTempFile(string contents, string extension = ".log")
    {
        var path = Path.Combine(Path.GetTempPath(), $"hextail-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, contents);
        return path;
    }

    private sealed class MemoryPersistence : IAppPersistence
    {
        public AppConfig? Config { get; private set; }
        public Exception? SaveError { get; set; }

        public ValueTask<AppConfig?> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Config);

        public ValueTask SaveAsync(AppConfig config, CancellationToken cancellationToken = default)
        {
            if (SaveError is not null)
                throw SaveError;
            Config = AppConfigJson.Deserialize(AppConfigJson.Serialize(config));
            return ValueTask.CompletedTask;
        }
    }

    private static ElasticConnectionSettings ElasticConnection(string name) =>
        new()
        {
            Id = "elastic-1",
            Name = name,
            KibanaUrl = "https://kibana/",
            ElasticsearchUrl = "https://elastic/",
            DataViewId = "view",
            DataViewTitle = "logs-*",
            TimeFieldName = "@timestamp",
            ServerField = "server",
            NamespaceField = "namespace",
            OutputFields = ["message"],
        };
}
