using HexTail.ViewModels;

namespace HexTail.Tests.ViewModels;

public sealed class CommandPaletteViewModelTests
{
    [Theory]
    [InlineData("api.log", "", 1)]
    [InlineData("api.log", "api", 300)]
    [InlineData("Toggle sidebar", "side", 200)]
    [InlineData("worker-api.log", "api", 200)]
    [InlineData("checkout", "kout", 100)]
    [InlineData("Open files", "ofs", 50)]
    [InlineData("Open files", "zzz", 0)]
    public void ScoreRanksPrefixThenWordThenContainsThenSubsequence(
        string title,
        string query,
        int expected
    ) => Assert.Equal(expected, CommandPaletteViewModel.Score(title, query));

    [Fact]
    public async Task OpenFiltersMovesAndExecutes()
    {
        var executed = new List<string>();
        PaletteItem Item(string title) =>
            new(
                title,
                "Test",
                null,
                () =>
                {
                    executed.Add(title);
                    return Task.CompletedTask;
                }
            );
        var palette = new CommandPaletteViewModel(() =>
            [Item("Open files…"), Item("api.log"), Item("worker-api.log")]
        );

        palette.Open();
        Assert.True(palette.IsOpen);
        Assert.Equal(3, palette.Results.Count);
        palette.Query = "api";
        Assert.Equal(["api.log", "worker-api.log"], palette.Results.Select(item => item.Title));
        Assert.Equal(0, palette.SelectedIndex);
        palette.Move(1);
        Assert.Equal(1, palette.SelectedIndex);
        palette.Move(1);
        Assert.Equal(0, palette.SelectedIndex);

        await palette.ExecuteSelectedAsync();
        Assert.Equal(["api.log"], executed);
        Assert.False(palette.IsOpen);
    }
}
