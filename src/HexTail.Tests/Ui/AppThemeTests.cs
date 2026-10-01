using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
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
            "#FF14D760",
            Assert
                .IsType<SolidColorBrush>(app.Resources["SelectedTabBrush"])
                .Color.ToString()
                .ToUpperInvariant()
        );

        ThemeManager.Apply("catppuccin-mocha");
        var mocha = Assert.IsType<SolidColorBrush>(app.Resources["AccentBrush"]);
        Assert.Equal("#FFCBA6F7", mocha.Color.ToString().ToUpperInvariant());
        Assert.Equal(
            "#FFCBA6F7",
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
            "#FF28D7FE",
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

    [AvaloniaFact]
    public void SelectedOutlineAndPlaceholderHaveReadableContrastInEveryTheme()
    {
        var app = Assert.IsType<App>(Avalonia.Application.Current);
        try
        {
            foreach (var theme in ThemeCatalog.Names)
            {
                ThemeManager.Apply(theme);
                var selected = ResourceColor("SelectedTabBrush");
                foreach (
                    var key in new[]
                    {
                        "SurfaceBrush",
                        "SurfaceAltBrush",
                        "RaisedBrush",
                        "RaisedAltBrush",
                        "ToolbarBrush",
                        "BorderBrush",
                        "BorderStrongBrush",
                    }
                )
                    Assert.True(Contrast(selected, ResourceColor(key)) >= 3, $"{theme}: {key}");

                foreach (var key in new[] { "SurfaceBrush", "RaisedBrush", "RaisedAltBrush" })
                    Assert.True(
                        Contrast(ResourceColor("MutedBrush"), ResourceColor(key)) >= 4.5,
                        $"{theme}: placeholder against {key}"
                    );
            }
        }
        finally
        {
            ThemeManager.Apply("cyber-tail");
        }

        Color ResourceColor(string key) => Assert.IsType<SolidColorBrush>(app.Resources[key]).Color;
    }

    [AvaloniaFact]
    public void NativeAccentAndVisiblePlaceholderFollowThemeChanges()
    {
        var app = Assert.IsType<App>(Avalonia.Application.Current);
        var textBox = new TextBox { PlaceholderText = "Search logs", Width = 300 };
        var checkBox = new CheckBox { Content = "Include field", IsChecked = true };
        var selectedTab = new Border { Classes = { "file-tab", "selected" } };
        var window = new Window
        {
            Content = new StackPanel { Children = { textBox, checkBox, selectedTab } },
        };
        window.Show();
        try
        {
            foreach (var theme in ThemeCatalog.Names)
            {
                ThemeManager.Apply(theme);
                var accent = Assert.IsType<SolidColorBrush>(app.Resources["AccentBrush"]).Color;
                var fluent = Assert.Single(app.Styles.OfType<FluentTheme>());
                Assert.Equal(accent, fluent.Palettes[ThemeVariant.Dark].Accent);
                Assert.True(
                    app.TryGetResource("SystemAccentColor", ThemeVariant.Dark, out var nativeAccent)
                );
                Assert.Equal(accent, Assert.IsType<Color>(nativeAccent));
                Assert.Equal(new Avalonia.Thickness(2), selectedTab.BorderThickness);
                Assert.Equal(accent, Assert.IsType<SolidColorBrush>(selectedTab.BorderBrush).Color);

                Assert.True(
                    app.TryGetResource(
                        "CheckBoxCheckBackgroundFillChecked",
                        ThemeVariant.Dark,
                        out var checkedBrush
                    )
                );
                var checkedBox = checkBox
                    .GetVisualDescendants()
                    .OfType<Border>()
                    .Single(border => border.Name == "NormalRectangle");
                Assert.Equal(
                    Assert.IsType<SolidColorBrush>(checkedBrush).Color,
                    Assert.IsType<SolidColorBrush>(checkedBox.Background).Color
                );
                Assert.True(textBox.Focus());
                Assert.True(
                    app.TryGetResource(
                        "TextControlBorderBrushFocused",
                        ThemeVariant.Dark,
                        out var focusBrush
                    )
                );
                var focusedBorder = textBox
                    .GetVisualDescendants()
                    .OfType<Border>()
                    .Single(border => border.Name == "PART_BorderElement");
                Assert.Equal(
                    Assert.IsType<SolidColorBrush>(focusBrush).Color,
                    Assert.IsType<SolidColorBrush>(focusedBorder.BorderBrush).Color
                );

                var placeholder = textBox
                    .GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Single(block => block.Name == "PART_Placeholder");
                Assert.True(placeholder.IsVisible);
                Assert.Equal(1, placeholder.Opacity);
                Assert.Equal(
                    Assert.IsType<SolidColorBrush>(app.Resources["MutedBrush"]).Color,
                    Assert.IsType<SolidColorBrush>(placeholder.Foreground).Color
                );
                Assert.Equal(
                    Avalonia.Layout.VerticalAlignment.Center,
                    placeholder.VerticalAlignment
                );
            }
        }
        finally
        {
            window.Close();
            ThemeManager.Apply("cyber-tail");
        }
    }

    private static double Contrast(Color first, Color second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(Color color)
    {
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
    }
}
