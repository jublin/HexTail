using System.Text.Json;
using System.Text.Json.Serialization;
using HexTailSharp.Domain;

namespace HexTailSharp.Persistence;

public sealed class AppConfig
{
    public List<PersistedFileTab> OpenFiles { get; init; } = [];
    public string? SelectedFilePath { get; init; }
    public AppWindowState Window { get; init; } = new();
    public AppSettings Settings { get; init; } = new();
}

public sealed class PersistedFileTab
{
    public required string Path { get; init; }
    public List<PersistedSearch> Searches { get; init; } = [];
    public bool FollowAll { get; init; } = true;
    public List<bool> FollowSearches { get; init; } = [];
    public bool ShowContext { get; init; }
    public int? SelectedLine { get; init; }
    public int ContextAbove { get; init; } = 3;
    public int ContextBelow { get; init; } = 10;
}

public sealed class PersistedSearch
{
    public required string Query { get; init; }
    public MatchMode Mode { get; init; }
    public bool CaseSensitive { get; init; }
    public string Color { get; init; } = "#f59e0b";
}

public sealed class AppWindowState
{
    public bool VerticalFileTabs { get; init; }
    public int ContextPaneSize { get; init; } = 300;
}

public sealed record AppSettings
{
    public int MaxLines { get; init; } = FileBuffer.DefaultMaxLines;
    public int ContextAbove { get; init; } = 3;
    public int ContextBelow { get; init; } = 10;
    public List<GlobalLabel> GlobalLabels { get; init; } = [];
    public List<string> GlobalExcludeLabels { get; init; } = [];
    public string Theme { get; init; } = "material-dark";
    public UiDensity Density { get; init; } = UiDensity.Comfortable;
    public LogFontSize LogFontSize { get; init; } = LogFontSize.Medium;
    public SettingsMenuAlignment SettingsMenuAlignment { get; init; } = SettingsMenuAlignment.Right;

    public bool Excludes(string text) =>
        GlobalExcludeLabels.Any(label => text.Contains(label, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<LabelHighlight> GetLabelHighlights(string text)
    {
        foreach (var label in GlobalLabels)
        {
            for (var start = 0; (start = text.IndexOf(label.Text, start, StringComparison.OrdinalIgnoreCase)) >= 0; start += label.Text.Length)
                yield return new LabelHighlight(start, label.Text.Length, label.Color);
        }
    }
}

public static class ThemeCatalog
{
    public static readonly string[] Names =
    [
        "dark", "dark-wcag",
        "default", "default-wcag",
        "humanistic", "humanistic-dark", "humanistic-dark-wcag", "humanistic-wcag",
        "material", "material-dark", "material-dark-wcag", "material-wcag",
        "software", "software-dark", "software-dark-wcag", "software-wcag",
        "standard", "standard-dark", "standard-dark-wcag", "standard-wcag",
    ];

    public static bool Contains(string theme) => Names.Contains(theme, StringComparer.Ordinal);

    public static bool IsWcag(string theme) => theme.EndsWith("-wcag", StringComparison.Ordinal);

    public static string BaseName(string theme) => IsWcag(theme) ? theme[..^5] : theme;
}

public sealed class GlobalLabel
{
    public string Text { get; init; } = string.Empty;
    public string Color { get; init; } = "#f59e0b";
}

public readonly record struct LabelHighlight(int Start, int Length, string Color);

public enum UiDensity
{
    Comfortable,
    Cozy,
    Compact,
}

public enum LogFontSize
{
    Small,
    Medium,
    Large,
    ExtraLarge,
}

public enum SettingsMenuAlignment
{
    Left,
    Right,
}

public static class AppConfigJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(AppConfig config) => JsonSerializer.Serialize(config, Options);

    public static AppConfig Deserialize(string json) =>
        JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();
}
