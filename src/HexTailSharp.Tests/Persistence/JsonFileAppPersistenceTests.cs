using HexTailSharp.Domain;
using HexTailSharp.Persistence;

namespace HexTailSharp.Tests.Persistence;

public sealed class JsonFileAppPersistenceTests
{
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
            Window = new AppWindowState { Width = 900, Height = 600, X = 12, Y = 34 },
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
        public TempDirectory() => Path = Directory.CreateTempSubdirectory("hextail-persistence-").FullName;

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
