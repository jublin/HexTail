using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;

namespace HexTailSharp.Tests.Ui;

public sealed class AppThemeTests
{
    [AvaloniaFact]
    public void AppLoadsCyberTailDarkResources()
    {
        var app = Assert.IsType<App>(Avalonia.Application.Current);
        Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
        Assert.True(app.TryGetResource("CyberCyanBrush", ThemeVariant.Dark, out _));
        Assert.True(app.TryGetResource("CyberSurfaceBrush", ThemeVariant.Dark, out _));
    }
}
