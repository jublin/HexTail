using System.Text.Json.Nodes;
using HexTail.Domain;

namespace HexTail.Persistence;

/// <summary>Rewrites a v1 session (labels/exclusions) as v2 (rules) at JSON level.</summary>
internal static class RuleMigration
{
    internal const int CurrentVersion = 2;
    private const string FallbackColor = "#f59e0b";

    internal static bool NeedsMigration(string json) => Apply(json).Migrated;

    /// <summary>True when the document was written by a newer build than this one understands.</summary>
    internal static bool IsFutureVersion(string json)
    {
        try
        {
            return JsonNode.Parse(json) is JsonObject root
                && root["version"] is JsonValue value
                && value.TryGetValue<int>(out var version)
                && version > CurrentVersion;
        }
        catch (Exception exception) when (IsUnreadableTree(exception))
        {
            return false;
        }
    }

    /// <summary>
    /// True when reading the JSON tree failed. <see cref="JsonObject"/> builds lazily, so a duplicate
    /// key surfaces as an <see cref="ArgumentException"/> at first access, not at parse time.
    /// </summary>
    internal static bool IsUnreadableTree(Exception exception) =>
        exception
            is System.Text.Json.JsonException
                or ArgumentException
                or InvalidOperationException;

    internal static (string Json, bool Migrated) Apply(string json)
    {
        try
        {
            return ApplyCore(json);
        }
        catch (Exception exception) when (IsUnreadableTree(exception))
        {
            return (json, false);
        }
    }

    private static (string Json, bool Migrated) ApplyCore(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject;
        if (root is null)
            return (json, false);
        if (
            root["version"] is JsonValue versionValue
            && versionValue.TryGetValue<int>(out var version)
            && version >= CurrentVersion
        )
            return (json, false);

        if (root["settings"] is JsonObject settings)
        {
            var existing = settings["rules"] as JsonArray;
            var rules = existing ?? [];
            var modes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (settings["globalExcludeModes"] is JsonObject modeObject)
                foreach (var (key, value) in modeObject)
                    if (TextOf(value) is { } modeText)
                        modes[key] = modeText;

            if (settings["globalLabels"] is JsonArray labels)
                foreach (var label in labels.OfType<JsonObject>())
                {
                    var text = TextOf(label["text"]);
                    if (string.IsNullOrWhiteSpace(text))
                        continue;
                    rules.Add(
                        Rule(
                            text.Trim(),
                            TextOf(label["mode"]),
                            TextOf(label["color"]),
                            "Highlight",
                            FlagOf(label["showInOpenFile"]) ?? true
                        )
                    );
                }

            if (settings["globalExcludeLabels"] is JsonArray exclusions)
                foreach (var exclusion in exclusions)
                {
                    var text = TextOf(exclusion);
                    if (string.IsNullOrWhiteSpace(text))
                        continue;
                    if (!modes.TryGetValue(text, out var mode))
                        modes.TryGetValue(text.Trim(), out mode);
                    rules.Add(Rule(text.Trim(), mode, null, "Hide", false));
                }

            settings.Remove("globalLabels");
            settings.Remove("globalExcludeLabels");
            settings.Remove("globalExcludeModes");
            if (existing is null)
                settings["rules"] = rules;
        }

        root["version"] = CurrentVersion;
        return (root.ToJsonString(), true);
    }

    private static JsonObject Rule(
        string pattern,
        string? mode,
        string? color,
        string action,
        bool pinned
    ) =>
        new()
        {
            ["pattern"] = pattern,
            ["mode"] = ParseMode(mode, pattern).ToString(),
            ["caseSensitive"] = false,
            ["color"] = IsHexColor(color) ? color : FallbackColor,
            ["action"] = action,
            ["scope"] = "Global",
            ["enabled"] = true,
            ["pinnedAsTab"] = pinned,
        };

    /// <summary>Reads a JSON string; any other JSON type is treated as absent.</summary>
    private static string? TextOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool? FlagOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static MatchMode ParseMode(string? mode, string pattern)
    {
        foreach (var name in Enum.GetNames<MatchMode>())
            if (string.Equals(name, mode?.Trim(), StringComparison.OrdinalIgnoreCase))
                return Enum.Parse<MatchMode>(name);
        return CompiledQuery.DetectMode(pattern);
    }

    private static bool IsHexColor(string? color) =>
        color is { Length: 4 or 7 } && color[0] == '#' && color[1..].All(Uri.IsHexDigit);
}
