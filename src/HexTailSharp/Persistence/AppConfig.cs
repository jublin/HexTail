using System.Text.Json;
using System.Text.Json.Serialization;
using HexTailSharp.Domain;

namespace HexTailSharp.Persistence;

public sealed class AppConfig
{
    public List<PersistedFileTab> OpenFiles { get; init; } = [];
    public List<PersistedElasticTab> OpenElasticTabs { get; init; } = [];
    public string? SelectedFilePath { get; init; }
    public string? SelectedElasticSourceId { get; init; }
    public AppWindowState Window { get; init; } = new();
    public AppSettings Settings { get; init; } = new();
}

public sealed class PersistedElasticTab
{
    public required string SourceId { get; init; }
    public List<PersistedSearch> Searches { get; init; } = [];
    public bool FollowAll { get; init; } = true;
    public List<bool> FollowSearches { get; init; } = [];
    public bool ShowContext { get; init; }
    public int? SelectedLine { get; init; }
    public int ContextAbove { get; init; } = 3;
    public int ContextBelow { get; init; } = 10;
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
    public double Width { get; init; } = 1280;
    public double Height { get; init; } = 800;
    public int? X { get; init; }
    public int? Y { get; init; }
}

public sealed record AppSettings
{
    public int MaxLines { get; init; } = FileBuffer.DefaultMaxLines;
    public int ContextAbove { get; init; } = 3;
    public int ContextBelow { get; init; } = 10;
    public List<GlobalLabel> GlobalLabels { get; init; } = [];
    public List<string> GlobalExcludeLabels { get; init; } = [];
    public string Theme { get; init; } = "cyber-tail";
    public UiDensity Density { get; init; } = UiDensity.Comfortable;
    public LogFontSize LogFontSize { get; init; } = LogFontSize.Medium;
    public SettingsMenuAlignment SettingsMenuAlignment { get; init; } = SettingsMenuAlignment.Right;
    public AppTimeZoneMode TimeZoneMode { get; init; } = AppTimeZoneMode.Local;
    public List<ElasticConnectionSettings> ElasticConnections { get; init; } = [];

    public bool Excludes(string text) =>
        GlobalExcludeLabels.Any(label => CreateGlobalQuery(label)?.IsMatch(text) is true);

    public IEnumerable<LabelHighlight> GetLabelHighlights(string text)
    {
        foreach (var label in GlobalLabels)
        {
            var query = CreateGlobalQuery(label.Text);
            if (query is null)
                continue;

            foreach (var range in query.GetHighlights(text))
                yield return new LabelHighlight(range.Start, range.Length, label.Color);
        }
    }

    private static CompiledQuery? CreateGlobalQuery(string query)
    {
        try
        {
            return new CompiledQuery(query, CompiledQuery.DetectMode(query), caseSensitive: false);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

public static class ThemeCatalog
{
    public static readonly string[] Names = ["cyber-tail", "catppuccin-mocha", "spotify"];

    public static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<
        string,
        string
    >(StringComparer.Ordinal)
    {
        ["cyber-tail"] = "Cyber Tail",
        ["catppuccin-mocha"] = "Catppuccin Mocha",
        ["spotify"] = "Open Design · Spotify",
    };

    public static bool Contains(string? theme) =>
        theme is "cyber-tail" or "catppuccin-mocha" or "spotify";

    public static string Normalize(string? theme) => Contains(theme) ? theme! : "cyber-tail";
}

public sealed record ThemeOption(string Id, string DisplayName);

public sealed class GlobalLabel
{
    public string Text { get; init; } = string.Empty;
    public string Color { get; init; } = "#f59e0b";
    public bool ShowInOpenFile { get; init; } = true;
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
