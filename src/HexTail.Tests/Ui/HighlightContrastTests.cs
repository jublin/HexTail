using Avalonia.Media;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class HighlightContrastTests
{
    [Fact]
    public void LightHighlightUsesDarkText()
    {
        Assert.Equal(
            Colors.Black,
            HighlightedTextBlock.ReadableHighlightColor(Color.Parse("#F59E0B"))
        );
    }

    [Fact]
    public void DarkHighlightUsesLightText()
    {
        Assert.Equal(
            Colors.White,
            HighlightedTextBlock.ReadableHighlightColor(Color.Parse("#1E293B"))
        );
    }
}
