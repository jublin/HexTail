using HexTail.ViewModels;

namespace HexTail.Tests.Ui;

public sealed class SearchColorTests
{
    [Fact]
    public void NextSearchColorSkipsActiveAndGlobalColors()
    {
        var color = MainWindowViewModel.NextSearchColor(["#F59E0B", "#22D3EE"], ["#A78BFA"]);

        Assert.Equal("#34D399", color);
    }
}
