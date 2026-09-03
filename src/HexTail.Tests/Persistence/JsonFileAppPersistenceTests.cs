using HexTail.Domain;
using HexTail.Persistence;

namespace HexTail.Tests.Persistence;

public sealed class JsonFileAppPersistenceTests
{
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
            },
            Settings = new AppSettings { Theme = "light", GlobalExcludeLabels = ["health"] },
        };

        await persistence.SaveAsync(config);
        var restored = await persistence.LoadAsync();

        Assert.NotNull(restored);
        Assert.Equal("/tmp/app.log", restored.SelectedFilePath);
        Assert.Equal(900, restored.Window.Width);
        Assert.Equal(34, restored.Window.Y);
        Assert.Equal("light", restored.Settings.Theme);
        Assert.Equal("error", Assert.Single(Assert.Single(restored.OpenFiles).Searches).Query);
        Assert.True(File.Exists(path));
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

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory() =>
            Path = Directory.CreateTempSubdirectory("hextail-persistence-").FullName;

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
