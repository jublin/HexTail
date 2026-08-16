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
        ["SurfaceAltBrush"] = "#0C1219",
        ["RaisedBrush"] = "#101722",
        ["RaisedAltBrush"] = "#151F2D",
        ["ToolbarBrush"] = "#0D141E",
        ["BorderBrush"] = "#263449",
        ["BorderStrongBrush"] = "#354761",
        ["TextBrush"] = "#E6EDF7",
        ["MutedBrush"] = "#91A0B5",
        ["FaintTextBrush"] = "#65758D",
        ["AccentBrush"] = "#28D7FE",
        ["AccentMutedBrush"] = "#157B94",
        ["AccentStrongBrush"] = "#7AEAFF",
        ["SelectedTabBrush"] = "#4822FE",
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
        ["SurfaceAltBrush"] = "#242438",
        ["RaisedBrush"] = "#313244",
        ["RaisedAltBrush"] = "#3B3D50",
        ["ToolbarBrush"] = "#181825",
        ["BorderBrush"] = "#45475A",
        ["BorderStrongBrush"] = "#585B70",
        ["TextBrush"] = "#CDD6F4",
        ["MutedBrush"] = "#A6ADC8",
        ["FaintTextBrush"] = "#7F849C",
        ["AccentBrush"] = "#CBA6F7",
        ["AccentMutedBrush"] = "#8F6DB4",
        ["AccentStrongBrush"] = "#E7C6FF",
        ["SelectedTabBrush"] = "#3552E7",
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
        ["SurfaceAltBrush"] = "#151515",
        ["RaisedBrush"] = "#181818",
        ["RaisedAltBrush"] = "#222222",
        ["ToolbarBrush"] = "#1F1F1F",
        ["BorderBrush"] = "#2A2A2A",
        ["BorderStrongBrush"] = "#3E3E3E",
        ["TextBrush"] = "#FFFFFF",
        ["MutedBrush"] = "#B3B3B3",
        ["FaintTextBrush"] = "#737373",
        ["AccentBrush"] = "#14D760",
        ["AccentMutedBrush"] = "#0B873D",
        ["AccentStrongBrush"] = "#62F18F",
        ["SelectedTabBrush"] = "#269750",
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
