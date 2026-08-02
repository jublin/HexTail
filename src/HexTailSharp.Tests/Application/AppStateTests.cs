using HexTailSharp.Application;
using HexTailSharp.Domain;
using HexTailSharp.Persistence;
using HexTailSharp.Tailing;

namespace HexTailSharp.Tests.Application;

public sealed class AppStateTests
{
    [Fact]
    public async Task OpenAndDrain_AppendsParsedLinesAndUpdatesSearches()
    {
        var path = CreateTempFile("level=info\n", ".logfmt");
        var persistence = new MemoryPersistence();
        await using var tailers = new TailerService(new TailerOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            UseFileSystemWatcher = false,
        });
        await using var state = new AppState(tailers, persistence);

        var tab = await state.OpenFileAsync(path);
        await DrainUntilAsync(state, () => tab.Buffer.Count == 1);
        var search = state.AddSearch(tab, "info", MatchMode.Literal, caseSensitive: true, "#00ff00");

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
    }

    [Fact]
    public void LogParserSelector_UsesLogfmtOnlyForLogfmtExtension()
    {
        Assert.IsType<LogfmtParser>(LogParserSelector.ForPath("app.logfmt"));
        Assert.IsType<PlainTextParser>(LogParserSelector.ForPath("app.log"));
    }

    private static TailerService NewTailers() => new(new TailerOptions
    {
        PollInterval = TimeSpan.FromMilliseconds(10),
        UseFileSystemWatcher = false,
    });

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

        public ValueTask<AppConfig?> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Config);

        public ValueTask SaveAsync(AppConfig config, CancellationToken cancellationToken = default)
        {
            Config = AppConfigJson.Deserialize(AppConfigJson.Serialize(config));
            return ValueTask.CompletedTask;
        }
    }
}
