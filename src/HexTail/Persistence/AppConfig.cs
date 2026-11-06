using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HexTail.Domain;

namespace HexTail.Persistence;

public sealed class AppConfig
{
    public int Version { get; init; } = RuleMigration.CurrentVersion;
    public List<PersistedFileTab> OpenFiles { get; init; } = [];
    public List<PersistedElasticTab> OpenElasticTabs { get; init; } = [];
    public string? SelectedFilePath { get; init; }
    public string? SelectedElasticSourceId { get; init; }
    public AppWindowState Window { get; init; } = new();
    public AppSettings Settings { get; init; } = new();
    public List<string> RecentFiles { get; init; } = [];
    public List<string> RecentElasticSourceIds { get; init; } = [];
}

public sealed class PersistedElasticTab
{
    public required string SourceId { get; init; }
    public string From { get; init; } = "now-5m";
    public string To { get; init; } = "now";
    public AppTimeZoneMode InputZone { get; init; } = AppTimeZoneMode.Utc;
    public List<PersistedSearch> Searches { get; init; } = [];
    public bool FollowAll { get; init; } = true;
    public List<bool> FollowSearches { get; init; } = [];
    public bool ShowContext { get; init; }
    public int? SelectedLine { get; init; }
    public int ContextAbove { get; init; } = 3;
    public int ContextBelow { get; init; } = 10;
    public List<ViewRule> Rules { get; init; } = [];
    public LogLevels Levels { get; init; } = LogLevels.All;
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
    public List<ViewRule> Rules { get; init; } = [];
    public LogLevels Levels { get; init; } = LogLevels.All;
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
    public bool SidebarCollapsed { get; init; }
    public double SidebarWidth { get; init; }
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
    public List<ViewRule> Rules { get; init; } = [];
    public string Theme { get; init; } = "cyber-tail";
    public UiDensity Density { get; init; } = UiDensity.Comfortable;
    public LogFontSize LogFontSize { get; init; } = LogFontSize.Medium;
    public SettingsMenuAlignment SettingsMenuAlignment { get; init; } = SettingsMenuAlignment.Right;
    public AppTimeZoneMode TimeZoneMode { get; init; } = AppTimeZoneMode.Local;
    public List<ElasticConnectionSettings> ElasticConnections { get; init; } = [];
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
        JsonSerializer.Deserialize<AppConfig>(Sanitize(RuleMigration.Apply(json).Json), Options)
        ?? new AppConfig();

    /// <summary>
    /// Drops individual rules that do not deserialize (an unknown action, scope or mode name written
    /// by another build) and treats an unusable <c>levels</c> value as absent, so one bad value costs
    /// that value, not the whole session.
    /// </summary>
    private static string Sanitize(string json)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return json; // The real deserializer reports the malformed document.
        }
        if (root is null)
            return json;

        if (root["settings"] is JsonObject settings)
            SanitizeRules(settings);
        foreach (var tabs in new[] { root["openFiles"], root["openElasticTabs"] })
            if (tabs is JsonArray array)
                foreach (var tab in array.OfType<JsonObject>())
                {
                    SanitizeRules(tab);
                    if (
                        tab.ContainsKey("levels")
                        && !(tab["levels"] is { } levels && Parses<LogLevels>(levels))
                    )
                        tab.Remove("levels");
                }
        return root.ToJsonString();
    }

    private static void SanitizeRules(JsonObject owner)
    {
        if (!owner.ContainsKey("rules"))
            return;
        if (owner["rules"] is not JsonArray rules)
        {
            owner.Remove("rules");
            return;
        }
        for (var index = rules.Count - 1; index >= 0; index--)
            if (rules[index] is not JsonObject rule || !Parses<ViewRule>(rule))
                rules.RemoveAt(index);
    }

    private static bool Parses<T>(JsonNode node)
    {
        try
        {
            return node.Deserialize<T>(Options) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
