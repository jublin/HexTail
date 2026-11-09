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
    public List<SavedFilter> SavedFilters { get; init; } = [];

    /// <summary>Named merged streams (definitions persist while closed). Absent in older sessions.</summary>
    public List<StreamSettings> Streams { get; init; } = [];

    /// <summary>Stream tabs open when the session was saved. Absent in older sessions.</summary>
    public List<PersistedStreamTab> OpenStreams { get; init; } = [];
    public string? SelectedStreamId { get; init; }
}

/// <summary>One member of a stream. <paramref name="Key"/> is <c>file:{full path}</c> or <c>elastic:{source id}</c>.</summary>
public sealed record StreamMember(string Key, string Color);

/// <summary>A named stream: a set of sources merged by timestamp into one tab.</summary>
public sealed record StreamSettings(string Id, string Name, List<StreamMember> Members)
{
    public const string DefaultName = "Stream";

    /// <summary>
    /// Tolerant clean-up shared by session loading and AppState: blank names become
    /// <see cref="DefaultName"/>, members without a key and duplicate members are dropped, a missing
    /// colour is replaced from the palette, and entries without an id or with a repeated id are
    /// dropped. A stream left with fewer than two members is kept (it still loads).
    /// </summary>
    public static List<StreamSettings> Normalize(IEnumerable<StreamSettings?>? streams)
    {
        var result = new List<StreamSettings>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stream in streams ?? [])
        {
            if (stream is null || string.IsNullOrWhiteSpace(stream.Id) || !ids.Add(stream.Id))
                continue;
            var members = new List<StreamMember>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in stream.Members ?? [])
            {
                if (
                    member is null
                    || string.IsNullOrWhiteSpace(member.Key)
                    || !keys.Add(member.Key)
                )
                    continue;
                var color = string.IsNullOrWhiteSpace(member.Color)
                    ? StreamPalette.ColorAt(members.Count)
                    : member.Color;
                members.Add(new StreamMember(member.Key, color));
            }
            result.Add(
                new StreamSettings(
                    stream.Id,
                    string.IsNullOrWhiteSpace(stream.Name) ? DefaultName : stream.Name.Trim(),
                    members
                )
            );
        }
        return result;
    }
}

/// <summary>The fixed colours members get, by member order, when a stream is created.</summary>
public static class StreamPalette
{
    public static readonly IReadOnlyList<string> Colors =
    [
        "#38bdf8",
        "#f59e0b",
        "#a78bfa",
        "#34d399",
        "#f472b6",
        "#f87171",
        "#facc15",
        "#94a3b8",
    ];

    public static string ColorAt(int index) =>
        Colors[((index % Colors.Count) + Colors.Count) % Colors.Count];
}

public sealed class PersistedStreamTab
{
    public required string StreamId { get; init; }
    public List<PersistedSearch> Searches { get; init; } = [];
    public bool FollowAll { get; init; } = true;
    public List<bool> FollowSearches { get; init; } = [];
    public List<ViewRule> Rules { get; init; } = [];
    public LogLevels Levels { get; init; } = LogLevels.All;
    public List<PersistedColumn>? Columns { get; init; }
    public bool ComposedRows { get; init; }
    public TimeDisplay TimeDisplay { get; init; } = TimeDisplay.Absolute;

    /// <summary>Member keys (not tab ids, which change between runs) whose rows are hidden.</summary>
    public List<string> HiddenSources { get; init; } = [];
    public string From { get; init; } = "now-15m";
    public string To { get; init; } = "now";
    public bool Live { get; init; } = true;
}

/// <summary>A named bundle of source rules and a level mask that can be merged into any source.</summary>
public sealed record SavedFilter
{
    public string Name { get; init; } = string.Empty;
    public List<ViewRule> Rules { get; init; } = [];
    public LogLevels Levels { get; init; } = LogLevels.All;
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
    public int? SelectedLine { get; init; }
    public List<ViewRule> Rules { get; init; } = [];
    public LogLevels Levels { get; init; } = LogLevels.All;
    public List<PersistedColumn>? Columns { get; init; }
    public bool ComposedRows { get; init; }
    public TimeDisplay TimeDisplay { get; init; } = TimeDisplay.Absolute;
}

public sealed class PersistedFileTab
{
    public required string Path { get; init; }
    public List<PersistedSearch> Searches { get; init; } = [];
    public bool FollowAll { get; init; } = true;
    public List<bool> FollowSearches { get; init; } = [];
    public int? SelectedLine { get; init; }
    public List<ViewRule> Rules { get; init; } = [];
    public LogLevels Levels { get; init; } = LogLevels.All;
    public List<PersistedColumn>? Columns { get; init; }
    public bool ComposedRows { get; init; }
    public TimeDisplay TimeDisplay { get; init; } = TimeDisplay.Absolute;
}

public enum TimeDisplay
{
    Absolute,
    Relative,
}

public sealed class PersistedColumn
{
    public string Key { get; init; } = string.Empty;
    public double Width { get; init; }
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
    public bool InspectorOpen { get; init; }
    public double InspectorWidth { get; init; } = 360;
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
    public int ElasticInitialLines { get; init; } = 10_000;
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

    public static AppConfig Deserialize(string json)
    {
        var config =
            JsonSerializer.Deserialize<AppConfig>(Sanitize(RuleMigration.Apply(json).Json), Options)
            ?? new AppConfig();
        var streams = StreamSettings.Normalize(config.Streams);
        config.Streams.Clear();
        config.Streams.AddRange(streams);
        return config;
    }

    /// <summary>
    /// Drops individual rules that do not deserialize (an unknown action, scope or mode name written
    /// by another build) and treats an unusable <c>levels</c> value as absent, so one bad value costs
    /// that value, not the whole session.
    /// </summary>
    private static string Sanitize(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
                return json;

            if (root["settings"] is JsonObject settings)
                SanitizeRules(settings);
            foreach (var tabs in new[] { root["openFiles"], root["openElasticTabs"] })
                if (tabs is JsonArray array)
                    foreach (var tab in array.OfType<JsonObject>())
                    {
                        SanitizeRulesAndLevels(tab);
                        SanitizeColumns(tab);
                    }
            SanitizeSavedFilters(root);
            SanitizeStreams(root);
            return root.ToJsonString();
        }
        catch (Exception exception) when (RuleMigration.IsUnreadableTree(exception))
        {
            // The real deserializer reports a malformed document (and accepts duplicate keys).
            return json;
        }
    }

    private static void SanitizeRulesAndLevels(JsonObject owner)
    {
        SanitizeRules(owner);
        if (
            owner.ContainsKey("levels")
            && !(owner["levels"] is { } levels && Parses<LogLevels>(levels))
        )
            owner.Remove("levels");
    }

    /// <summary>Column settings are optional: bad entries are dropped and an unknown time display falls back to Absolute.</summary>
    private static void SanitizeColumns(JsonObject tab)
    {
        if (tab.ContainsKey("columns"))
        {
            if (tab["columns"] is not JsonArray columns)
                tab.Remove("columns");
            else
                for (var index = columns.Count - 1; index >= 0; index--)
                    if (columns[index] is not JsonObject column || !Parses<PersistedColumn>(column))
                        columns.RemoveAt(index);
        }
        if (
            tab.ContainsKey("composedRows")
            && !(tab["composedRows"] is JsonValue flag && flag.TryGetValue<bool>(out _))
        )
            tab.Remove("composedRows");
        if (
            tab.ContainsKey("timeDisplay")
            && !(tab["timeDisplay"] is { } display && Parses<TimeDisplay>(display))
        )
            tab.Remove("timeDisplay");
    }

    /// <summary>
    /// Streams are optional and written by newer builds: anything malformed is dropped (a stream
    /// entry, a member, an open-stream entry, a bad field) rather than failing the load.
    /// </summary>
    private static void SanitizeStreams(JsonObject root)
    {
        if (root.ContainsKey("streams"))
        {
            if (root["streams"] is not JsonArray streams)
                root.Remove("streams");
            else
                for (var index = streams.Count - 1; index >= 0; index--)
                {
                    if (
                        streams[index] is not JsonObject stream
                        || !IsText(stream["id"], allowBlank: false)
                    )
                    {
                        streams.RemoveAt(index);
                        continue;
                    }
                    if (!IsText(stream["name"], allowBlank: true))
                        stream.Remove("name");
                    if (stream["members"] is not JsonArray members)
                    {
                        stream.Remove("members");
                        continue;
                    }
                    for (var member = members.Count - 1; member >= 0; member--)
                    {
                        if (
                            members[member] is not JsonObject item
                            || !IsText(item["key"], allowBlank: false)
                        )
                            members.RemoveAt(member);
                        else if (!IsText(item["color"], allowBlank: true))
                            item.Remove("color");
                    }
                }
        }

        if (root.ContainsKey("openStreams"))
        {
            if (root["openStreams"] is not JsonArray tabs)
                root.Remove("openStreams");
            else
                for (var index = tabs.Count - 1; index >= 0; index--)
                {
                    if (
                        tabs[index] is not JsonObject tab
                        || !IsText(tab["streamId"], allowBlank: false)
                    )
                    {
                        tabs.RemoveAt(index);
                        continue;
                    }
                    SanitizeRulesAndLevels(tab);
                    SanitizeColumns(tab);
                    SanitizeStreamTabFields(tab);
                }
        }

        if (root.ContainsKey("selectedStreamId") && !IsText(root["selectedStreamId"], true))
            root.Remove("selectedStreamId");
    }

    private static void SanitizeStreamTabFields(JsonObject tab)
    {
        foreach (var name in new[] { "from", "to" })
            if (tab.ContainsKey(name) && !IsText(tab[name], allowBlank: false))
                tab.Remove(name);
        foreach (var name in new[] { "live", "followAll" })
            if (
                tab.ContainsKey(name)
                && !(tab[name] is JsonValue flag && flag.TryGetValue<bool>(out _))
            )
                tab.Remove(name);
        if (tab.ContainsKey("hiddenSources"))
        {
            if (tab["hiddenSources"] is not JsonArray hidden)
                tab.Remove("hiddenSources");
            else
                for (var index = hidden.Count - 1; index >= 0; index--)
                    if (!IsText(hidden[index], allowBlank: false))
                        hidden.RemoveAt(index);
        }
        if (tab.ContainsKey("followSearches"))
        {
            if (tab["followSearches"] is not JsonArray follows)
                tab.Remove("followSearches");
            else
                for (var index = follows.Count - 1; index >= 0; index--)
                    if (!(follows[index] is JsonValue flag && flag.TryGetValue<bool>(out _)))
                        follows.RemoveAt(index);
        }
        if (tab.ContainsKey("searches"))
        {
            if (tab["searches"] is not JsonArray searches)
                tab.Remove("searches");
            else
                for (var index = searches.Count - 1; index >= 0; index--)
                    if (
                        searches[index] is not JsonObject search
                        || !Parses<PersistedSearch>(search)
                    )
                        searches.RemoveAt(index);
        }
    }

    private static bool IsText(JsonNode? node, bool allowBlank) =>
        node is JsonValue value
        && value.TryGetValue<string>(out var text)
        && (allowBlank || !string.IsNullOrWhiteSpace(text));

    /// <summary>Saved filters are optional: a malformed list is dropped, as are entries without a name.</summary>
    private static void SanitizeSavedFilters(JsonObject root)
    {
        if (!root.ContainsKey("savedFilters"))
            return;
        if (root["savedFilters"] is not JsonArray filters)
        {
            root.Remove("savedFilters");
            return;
        }
        for (var index = filters.Count - 1; index >= 0; index--)
            if (
                filters[index] is not JsonObject filter
                || filter["name"] is not JsonValue name
                || !name.TryGetValue<string>(out var text)
                || string.IsNullOrWhiteSpace(text)
            )
                filters.RemoveAt(index);
            else
                SanitizeRulesAndLevels(filter);
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
