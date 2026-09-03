using Avalonia.Media;
using HexTail.ViewModels;

namespace HexTail.Tests.Ui;

public sealed class HighlightContrastTests
{
    [Fact]
    public void LightHighlightUsesDarkText()
    {
        Assert.Equal(Colors.Black, LogLineViewModel.ReadableHighlightColor("#F59E0B"));
    }

    [Fact]
    public void DarkHighlightUsesLightText()
    {
        Assert.Equal(Colors.White, LogLineViewModel.ReadableHighlightColor("#1E293B"));
    }
}
