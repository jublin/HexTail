using Avalonia;
using Avalonia.Media;
using HexTailSharp.Persistence;

namespace HexTailSharp;

internal static class ThemeManager
{
    private static readonly IReadOnlyDictionary<string, string> CyberTail = new Dictionary<
        string,
        string
    >
    {
        ["SurfaceBrush"] = "#090D12",
        ["RaisedBrush"] = "#101722",
        ["ToolbarBrush"] = "#0D141E",
        ["BorderBrush"] = "#263449",
        ["TextBrush"] = "#E6EDF7",
        ["MutedBrush"] = "#91A0B5",
        ["AccentBrush"] = "#28D7FE",
        ["SuccessBrush"] = "#39E58C",
        ["ErrorBrush"] = "#FF667A",
        ["ErrorBackgroundBrush"] = "#2A1118",
        ["ErrorTextBrush"] = "#FFD5DC",
    };

    private static readonly IReadOnlyDictionary<string, string> CatppuccinMocha = new Dictionary<
        string,
        string
    >
    {
        ["SurfaceBrush"] = "#1E1E2E",
        ["RaisedBrush"] = "#313244",
        ["ToolbarBrush"] = "#181825",
        ["BorderBrush"] = "#45475A",
        ["TextBrush"] = "#CDD6F4",
        ["MutedBrush"] = "#A6ADC8",
        ["AccentBrush"] = "#CBA6F7",
        ["SuccessBrush"] = "#A6E3A1",
        ["ErrorBrush"] = "#F38BA8",
        ["ErrorBackgroundBrush"] = "#33202A",
        ["ErrorTextBrush"] = "#F5C2E7",
    };

    private static readonly IReadOnlyDictionary<string, string> Spotify = new Dictionary<
        string,
        string
    >
    {
        ["SurfaceBrush"] = "#121212",
        ["RaisedBrush"] = "#181818",
        ["ToolbarBrush"] = "#1F1F1F",
        ["BorderBrush"] = "#2A2A2A",
        ["TextBrush"] = "#FFFFFF",
        ["MutedBrush"] = "#B3B3B3",
        ["AccentBrush"] = "#1ED760",
        ["SuccessBrush"] = "#1ED760",
        ["ErrorBrush"] = "#F3727F",
        ["ErrorBackgroundBrush"] = "#2A171A",
        ["ErrorTextBrush"] = "#FFD7DC",
    };

    public static void Apply(string? theme)
    {
        if (Avalonia.Application.Current is null)
            return;
        var palette = ThemeCatalog.Normalize(theme) switch
        {
            "catppuccin-mocha" => CatppuccinMocha,
            "spotify" => Spotify,
            _ => CyberTail,
        };
        foreach (var (key, value) in palette)
            Avalonia.Application.Current.Resources[key] = new SolidColorBrush(Color.Parse(value));
    }

    internal static IBrush Brush(string key) =>
        Avalonia.Application.Current?.Resources[key] as IBrush ?? Brushes.Transparent;
}
