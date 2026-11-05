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
}
