using HexTail.Application;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;

namespace HexTail.Tests.Application;

public sealed class RecentSourcesTests
{
    [Fact]
    public async Task OpeningFilesRecordsMostRecentFirstWithoutDuplicates()
    {
        var paths = Enumerable.Range(0, 3).Select(_ => Path.GetTempFileName()).ToArray();
        var persistence = new TestPersistence();
        try
        {
            await using (var state = new AppState(new LogSourceService(), persistence))
            {
                foreach (var path in paths)
                    await state.OpenFileAsync(path);
                await state.OpenFileAsync(paths[0]);
                Assert.Equal([paths[0], paths[2], paths[1]], state.RecentFiles);
            }
            Assert.Equal([paths[0], paths[2], paths[1]], persistence.Config!.RecentFiles);
        }
        finally
        {
            foreach (var path in paths)
                File.Delete(path);
        }
    }

    [Fact]
    public async Task RecentListIsCappedAndRestoreDoesNotReorderIt()
    {
        var paths = Enumerable.Range(0, 12).Select(_ => Path.GetTempFileName()).ToArray();
        try
        {
            var saved = new AppConfig
            {
                RecentFiles = [.. paths.Take(10)],
                OpenFiles = [new PersistedFileTab { Path = paths[5] }],
            };
            await using var state = new AppState(
                new LogSourceService(),
                new TestPersistence(saved)
            );
            await state.RestoreAsync();
            Assert.Equal(paths.Take(10), state.RecentFiles);

            await state.OpenFileAsync(paths[11]);
            Assert.Equal(AppState.MaxRecent, state.RecentFiles.Count);
            Assert.Equal(paths[11], state.RecentFiles[0]);

            await state.OpenFileAsync(paths[10], remember: false);
            Assert.DoesNotContain(paths[10], state.RecentFiles);
        }
        finally
        {
            foreach (var path in paths)
                File.Delete(path);
        }
    }

    [Fact]
    public async Task EphemeralTabIsNeitherPersistedNorSelectedNorRestored()
    {
        var kept = Path.GetTempFileName();
        var sample = Path.GetTempFileName();
        var persistence = new TestPersistence();
        try
        {
            await using (var state = new AppState(new LogSourceService(), persistence))
            {
                await state.OpenFileAsync(kept);
                var tab = await state.OpenFileAsync(sample, remember: false, ephemeral: true);
                Assert.True(tab.Ephemeral);
                Assert.Same(tab, state.SelectedFile);
                await state.SaveAsync();
            }
            var saved = persistence.Config!;
            Assert.Equal([Path.GetFullPath(kept)], saved.OpenFiles.Select(file => file.Path));
            Assert.DoesNotContain(Path.GetFullPath(sample), saved.RecentFiles);
            Assert.NotEqual(Path.GetFullPath(sample), saved.SelectedFilePath);

            await using var restored = new AppState(new LogSourceService(), persistence);
            await restored.RestoreAsync();
            var only = Assert.Single(restored.Files);
            Assert.Equal(Path.GetFullPath(kept), only.Path);
            Assert.Null(only.Error);
            Assert.Same(only, restored.SelectedFile);
        }
        finally
        {
            File.Delete(kept);
            File.Delete(sample);
        }
    }

    [Fact]
    public async Task SaveDropsRecentElasticSourcesThatNoLongerExist()
    {
        var persistence = new TestPersistence(
            new AppConfig
            {
                Settings = new AppSettings
                {
                    ElasticConnections = [ElasticFixtures.Connection("c1", "One", "a")],
                },
                RecentElasticSourceIds = ["gone-1", "c1-a", "gone-2"],
            }
        );
        await using var state = new AppState(
            new LogSourceService(),
            persistence,
            credentials: new InMemoryCredentialVault(),
            elastic: new FakeElasticApiClient()
        );
        await state.RestoreAsync();
        await state.SaveAsync();
        Assert.Equal(["c1-a"], state.RecentElasticSourceIds);
        Assert.Equal(["c1-a"], persistence.Config!.RecentElasticSourceIds);
    }
}
