using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using HexTail.Persistence;

namespace HexTail.Tests.Ui;

public sealed class AppThemeTests
{
    private static readonly string[] ShadeKeys =
    [
        "SurfaceAltBrush",
        "RaisedAltBrush",
        "BorderStrongBrush",
        "FaintTextBrush",
        "AccentMutedBrush",
        "AccentStrongBrush",
    ];

    [AvaloniaFact]
    public void AppLoadsCyberTailSemanticResources()
    {
        var app = Assert.IsType<App>(Avalonia.Application.Current);
        Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
        Assert.True(app.TryGetResource("AccentBrush", ThemeVariant.Dark, out _));
        Assert.True(app.TryGetResource("SurfaceBrush", ThemeVariant.Dark, out _));
        Assert.True(app.TryGetResource("SelectedTabBrush", ThemeVariant.Dark, out _));
    }

    [AvaloniaFact]
    public void ThemeManagerSwapsSemanticPalette()
    {
        var app = Assert.IsType<App>(Avalonia.Application.Current);
        ThemeManager.Apply("spotify");
        var spotify = Assert.IsType<SolidColorBrush>(app.Resources["AccentBrush"]);
        Assert.Equal("#FF14D760", spotify.Color.ToString().ToUpperInvariant());
        Assert.Equal(
            "#FF269750",
            Assert
                .IsType<SolidColorBrush>(app.Resources["SelectedTabBrush"])
                .Color.ToString()
                .ToUpperInvariant()
        );

        ThemeManager.Apply("catppuccin-mocha");
        var mocha = Assert.IsType<SolidColorBrush>(app.Resources["AccentBrush"]);
        Assert.Equal("#FFCBA6F7", mocha.Color.ToString().ToUpperInvariant());
        Assert.Equal(
            "#FF3552E7",
            Assert
                .IsType<SolidColorBrush>(app.Resources["SelectedTabBrush"])
                .Color.ToString()
                .ToUpperInvariant()
        );

        ThemeManager.Apply("cyber-tail");
        Assert.Equal(
            "#FF28D7FE",
            Assert
                .IsType<SolidColorBrush>(app.Resources["AccentBrush"])
                .Color.ToString()
                .ToUpperInvariant()
        );
        Assert.Equal(
            "#FF4822FE",
            Assert
                .IsType<SolidColorBrush>(app.Resources["SelectedTabBrush"])
                .Color.ToString()
                .ToUpperInvariant()
        );
        Assert.Contains("cyber-tail", ThemeCatalog.Names);
    }

    [AvaloniaFact]
    public void EveryThemeProvidesShadeLevels()
    {
        var app = Assert.IsType<App>(Avalonia.Application.Current);

        foreach (var theme in ThemeCatalog.Names)
        {
            ThemeManager.Apply(theme);
            foreach (var key in ShadeKeys)
                Assert.IsType<SolidColorBrush>(app.Resources[key]);
        }
    }
}
