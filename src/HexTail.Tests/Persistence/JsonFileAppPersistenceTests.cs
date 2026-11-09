using HexTail.Domain;
using HexTail.Persistence;
using HexTail.Tests.Support;

namespace HexTail.Tests.Persistence;

public sealed class JsonFileAppPersistenceTests
{
    [Fact]
    public void ExplicitLiteralGlobalRules_DoNotInterpretRegexCharacters()
    {
        var config = AppConfigJson.Deserialize(
            """
            { "settings": {
              "globalLabels": [{ "text": "[ERROR]", "color": "#ff0000", "mode": "Literal", "showInOpenFile": false }],
              "globalExcludeLabels": ["[ERROR]"],
              "globalExcludeModes": { "[ERROR]": "Literal" }
            } }
            """
        );
        var rules = RuleSet.For(config.Settings.Rules);
        Assert.True(rules.IsVisible(new Line("READY")));
        Assert.Empty(rules.Highlights(new Line("READY")));
        Assert.False(rules.IsVisible(new Line("[ERROR] failed")));
        Assert.Single(rules.Highlights(new Line("[ERROR] failed")));
        var restored = AppConfigJson.Deserialize(AppConfigJson.Serialize(config));
        Assert.True(RuleSet.For(restored.Settings.Rules).IsVisible(new Line("READY")));
    }

    [Fact]
    public void GlobalModes_PreserveLegacyRegexIntentAndSeparateEqualTextCaches()
    {
        var legacy = AppConfigJson.Deserialize(
            """
            { "settings": {
              "globalLabels": [{ "text": "ERROR|WARN", "showInOpenFile": false }],
              "globalExcludeLabels": ["ERROR|WARN"]
            } }
            """
        );
        Assert.Equal(2, legacy.Settings.Rules.Count);
        Assert.All(legacy.Settings.Rules, rule => Assert.Equal(MatchMode.Regex, rule.Mode));
        var text = legacy.Settings.Rules[0].Pattern;
        var regexRules = RuleSet.For(legacy.Settings.Rules);
        Assert.False(regexRules.IsVisible(new Line("WARN")));
        Assert.Single(regexRules.Highlights(new Line("WARN")));
        var literal = legacy.Settings with
        {
            Rules =
            [
                .. legacy.Settings.Rules.Select(rule => rule with { Mode = MatchMode.Literal }),
            ],
        };
        var literalRules = RuleSet.For(literal.Rules);
        Assert.True(literalRules.IsVisible(new Line("WARN")));
        Assert.Empty(literalRules.Highlights(new Line("WARN")));
        Assert.False(literalRules.IsVisible(new Line(text)));
        Assert.False(regexRules.IsVisible(new Line("WARN")));
        var restored = AppConfigJson.Deserialize(
            AppConfigJson.Serialize(new AppConfig { Settings = literal })
        );
        Assert.Equal(2, restored.Settings.Rules.Count);
        Assert.All(restored.Settings.Rules, rule => Assert.Equal(MatchMode.Literal, rule.Mode));
        Assert.Equal(
            [RuleAction.Highlight, RuleAction.Hide],
            restored.Settings.Rules.Select(rule => rule.Action)
        );
    }

    [Fact]
    public void GlobalExclusions_ReuseCompiledQueriesDuringRefresh()
    {
        var rules = RuleSet.For([TestRules.Hide(@"health\s+check")]);
        var completed = new Line("request completed");
        Assert.False(rules.IsVisible(new Line("HEALTH CHECK")));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 1_000; i++)
            Assert.True(rules.IsVisible(completed));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"1,000 exclusion checks: {allocated:N0} bytes, {timer.Elapsed.TotalMilliseconds:N3} ms"
        );
        Assert.True(
            allocated < 1_000_000,
            $"1,000 exclusion checks allocated {allocated:N0} bytes in {timer.Elapsed.TotalMilliseconds:N1} ms."
        );
    }

    [Fact]
    public void AppConfigJson_RoundTripsElasticSettingsWithoutSecretMaterial()
    {
        var connection = new ElasticConnectionSettings
        {
            Id = "elastic-1",
            Name = "Production",
            KibanaUrl = "https://kibana.example/space/default/",
            ElasticsearchUrl = "https://elastic.example/",
            AuthMode = ElasticAuthMode.Basic,
            Username = "reader",
            DataViewId = "logs-view",
            DataViewTitle = "logs-*",
            TimeFieldName = "@timestamp",
            ServerField = "service.name.keyword",
            NamespaceField = "labels.namespace.keyword",
            OutputFields = ["@timestamp", "message"],
            Sources =
            [
                new ElasticSourceSettings
                {
                    Id = "source-1",
                    ServerValue = "Mystack1",
                    NamespaceValue = "RhubarbPi",
                },
            ],
        };
        var json = AppConfigJson.Serialize(
            new AppConfig
            {
                Settings = new AppSettings { ElasticConnections = [connection] },
                OpenElasticTabs = [new PersistedElasticTab { SourceId = "source-1" }],
                SelectedElasticSourceId = "source-1",
            }
        );

        var restored = AppConfigJson.Deserialize(json);

        Assert.Equal(
            ["@timestamp", "message"],
            restored.Settings.ElasticConnections[0].OutputFields
        );
        Assert.Equal("source-1", Assert.Single(restored.OpenElasticTabs).SourceId);
        Assert.Equal("source-1", restored.SelectedElasticSourceId);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api-key", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppConfigJson_RoundTripsNestedElasticViewAndLocalTimeDefault()
    {
        var server = new ElasticConnectionSettings
        {
            Id = "server-1",
            Name = "Production",
            KibanaUrl = "https://kibana.example/",
            ElasticsearchUrl = "https://elastic.example/",
            Views =
            [
                new ElasticViewSettings
                {
                    Id = "view-1",
                    Name = "Application logs",
                    DataViewId = "logs-view",
                    DataViewTitle = "logs-*",
                    TimeFieldName = "@timestamp",
                    ServerField = "ident",
                    NamespaceField = "service.name",
                    OutputFields = ["message"],
                },
            ],
        };

        var restored = AppConfigJson.Deserialize(
            AppConfigJson.Serialize(
                new AppConfig { Settings = new AppSettings { ElasticConnections = [server] } }
            )
        );

        var view = Assert.Single(Assert.Single(restored.Settings.ElasticConnections).Views);
        Assert.Equal("Application logs", view.Name);
        Assert.Equal("logs-view", view.DataViewId);
        Assert.Equal(AppTimeZoneMode.Local, restored.Settings.TimeZoneMode);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsConfigAndCreatesParentDirectory()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "nested", "session.json");
        var persistence = new JsonFileAppPersistence(path);
        var config = new AppConfig
        {
            OpenFiles =
            [
                new PersistedFileTab
                {
                    Path = "/tmp/app.log",
                    Searches = [new PersistedSearch { Query = "error", Color = "#ff0000" }],
                    FollowAll = false,
                },
            ],
            SelectedFilePath = "/tmp/app.log",
            Window = new AppWindowState
            {
                Width = 900,
                Height = 600,
                X = 12,
                Y = 34,
                InspectorOpen = true,
                InspectorWidth = 400,
            },
            Settings = new AppSettings { Theme = "light", Rules = [TestRules.Hide("health")] },
        };

        await persistence.SaveAsync(config);
        var restored = await persistence.LoadAsync();

        Assert.NotNull(restored);
        Assert.Equal("/tmp/app.log", restored.SelectedFilePath);
        Assert.Equal(900, restored.Window.Width);
        Assert.Equal(34, restored.Window.Y);
        Assert.True(restored.Window.InspectorOpen);
        Assert.Equal(400, restored.Window.InspectorWidth);
        Assert.Equal("light", restored.Settings.Theme);
        Assert.Equal(TestRules.Hide("health"), Assert.Single(restored.Settings.Rules));
        Assert.Equal("error", Assert.Single(Assert.Single(restored.OpenFiles).Searches).Query);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Load_SessionWithRemovedContextPaneMembers_IgnoresThemAndKeepsTheFileOpen()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "session.json");
        await File.WriteAllTextAsync(
            path,
            """
            { "version": 2,
              "window": { "width": 1000, "height": 700, "contextPaneSize": 300 },
              "openFiles": [
                { "path": "/tmp/a.log", "showContext": true, "contextAbove": 5, "contextBelow": 5 }
              ] }
            """,
            TestContext.Current.CancellationToken
        );

        var result = await new JsonFileAppPersistence(path).LoadAsync(
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(result);
        Assert.Equal("/tmp/a.log", Assert.Single(result.OpenFiles).Path);
        Assert.Equal(1000, result.Window.Width);
    }

    [Fact]
    public async Task Load_MalformedJson_ReturnsNull()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "session.json");
        await File.WriteAllTextAsync(path, "{ definitely not json");

        var result = await new JsonFileAppPersistence(path).LoadAsync();

        Assert.Null(result);
    }

    [Fact]
    public void ColumnSettings_RoundTripForFileAndElasticTabs()
    {
        PersistedColumn[] columns =
        [
            new() { Key = "@time", Width = 120 },
            new() { Key = "service", Width = 200 },
        ];
        var config = new AppConfig
        {
            OpenFiles =
            [
                new PersistedFileTab
                {
                    Path = "/tmp/a.log",
                    Columns = [.. columns],
                    ComposedRows = true,
                    TimeDisplay = TimeDisplay.Relative,
                },
            ],
            OpenElasticTabs =
            [
                new PersistedElasticTab
                {
                    SourceId = "s1",
                    Columns = [.. columns],
                    ComposedRows = true,
                    TimeDisplay = TimeDisplay.Relative,
                },
            ],
        };

        var restored = AppConfigJson.Deserialize(AppConfigJson.Serialize(config));

        foreach (
            var (list, composed, display) in new[]
            {
                (
                    restored.OpenFiles[0].Columns,
                    restored.OpenFiles[0].ComposedRows,
                    restored.OpenFiles[0].TimeDisplay
                ),
                (
                    restored.OpenElasticTabs[0].Columns,
                    restored.OpenElasticTabs[0].ComposedRows,
                    restored.OpenElasticTabs[0].TimeDisplay
                ),
            }
        )
        {
            Assert.NotNull(list);
            Assert.Equal(["@time", "service"], list.Select(c => c.Key));
            Assert.Equal([120d, 200d], list.Select(c => c.Width));
            Assert.True(composed);
            Assert.Equal(TimeDisplay.Relative, display);
        }
    }

    [Fact]
    public void OldSessionWithoutColumnSettings_LoadsWithNullColumns()
    {
        var config = AppConfigJson.Deserialize(
            """
            { "version": 2,
              "openFiles": [{ "path": "/tmp/a.log" }],
              "openElasticTabs": [{ "sourceId": "s1" }] }
            """
        );

        Assert.Equal(2, config.Version);
        var file = Assert.Single(config.OpenFiles);
        Assert.Null(file.Columns);
        Assert.False(file.ComposedRows);
        Assert.Equal(TimeDisplay.Absolute, file.TimeDisplay);
        var elastic = Assert.Single(config.OpenElasticTabs);
        Assert.Null(elastic.Columns);
        Assert.Equal(TimeDisplay.Absolute, elastic.TimeDisplay);
    }

    [Fact]
    public void MalformedColumnSettings_DropOnlyTheBadValues()
    {
        var config = AppConfigJson.Deserialize(
            """
            { "version": 2,
              "openFiles": [
                { "path": "/tmp/a.log", "timeDisplay": "Sideways", "composedRows": "yes",
                  "columns": [{ "key": "@time", "width": 90 }, 5, { "key": "x", "width": "wide" }, null] },
                { "path": "/tmp/b.log", "columns": "oops", "timeDisplay": "Relative" }
              ] }
            """
        );

        var first = config.OpenFiles[0];
        Assert.Equal(TimeDisplay.Absolute, first.TimeDisplay);
        Assert.False(first.ComposedRows);
        Assert.Equal("@time", Assert.Single(first.Columns!).Key);
        var second = config.OpenFiles[1];
        Assert.Null(second.Columns);
        Assert.Equal(TimeDisplay.Relative, second.TimeDisplay);
    }

    [Fact]
    public void Streams_RoundTripThroughTheSessionFile()
    {
        var config = new AppConfig
        {
            Streams =
            [
                new StreamSettings(
                    "s1",
                    "Prod + staging",
                    [
                        new StreamMember("file:/var/log/a.log", "#38bdf8"),
                        new StreamMember("elastic:src-1", "#f59e0b"),
                    ]
                ),
            ],
            OpenStreams =
            [
                new PersistedStreamTab
                {
                    StreamId = "s1",
                    Levels = LogLevels.Warn,
                    ComposedRows = true,
                    TimeDisplay = TimeDisplay.Relative,
                    HiddenSources = ["elastic:src-1"],
                    From = "now-1h",
                    To = "now-5m",
                    Live = false,
                    FollowAll = false,
                    Searches = [new PersistedSearch { Query = "boom" }],
                    FollowSearches = [true],
                },
            ],
            SelectedStreamId = "s1",
        };

        var restored = AppConfigJson.Deserialize(AppConfigJson.Serialize(config));

        Assert.Equal(2, restored.Version);
        var stream = Assert.Single(restored.Streams);
        Assert.Equal("s1", stream.Id);
        Assert.Equal("Prod + staging", stream.Name);
        Assert.Equal(config.Streams[0].Members, stream.Members);
        var tab = Assert.Single(restored.OpenStreams);
        Assert.Equal("s1", tab.StreamId);
        Assert.Equal(LogLevels.Warn, tab.Levels);
        Assert.True(tab.ComposedRows);
        Assert.Equal(TimeDisplay.Relative, tab.TimeDisplay);
        Assert.Equal(["elastic:src-1"], tab.HiddenSources);
        Assert.Equal(("now-1h", "now-5m", false), (tab.From, tab.To, tab.Live));
        Assert.False(tab.FollowAll);
        Assert.Equal("boom", Assert.Single(tab.Searches).Query);
        Assert.Equal([true], tab.FollowSearches);
        Assert.Equal("s1", restored.SelectedStreamId);
    }

    [Fact]
    public void SessionsWithoutStreams_LoadWithNone()
    {
        var config = AppConfigJson.Deserialize(
            """{ "version": 2, "openFiles": [{ "path": "/tmp/a.log" }] }"""
        );

        Assert.Empty(config.Streams);
        Assert.Empty(config.OpenStreams);
        Assert.Null(config.SelectedStreamId);
        Assert.Equal(2, config.Version);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory() =>
            Path = Directory.CreateTempSubdirectory("hextail-persistence-").FullName;

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
