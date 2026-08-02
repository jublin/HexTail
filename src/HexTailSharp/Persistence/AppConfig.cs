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

public sealed class AppSettings
{
    public int MaxLines { get; init; } = FileBuffer.DefaultMaxLines;
    public int ContextAbove { get; init; } = 3;
    public int ContextBelow { get; init; } = 10;
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
