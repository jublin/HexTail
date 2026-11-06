using System.Text.Json.Nodes;
using HexTail.Domain;

namespace HexTail.Persistence;

/// <summary>Rewrites a v1 session (labels/exclusions) as v2 (rules) at JSON level.</summary>
internal static class RuleMigration
{
    internal const int CurrentVersion = 2;
    private const string FallbackColor = "#f59e0b";

    internal static bool NeedsMigration(string json) => Apply(json).Migrated;

    internal static (string Json, bool Migrated) Apply(string json)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return (json, false);
        }
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
            var rules = settings["rules"] as JsonArray ?? [];
            var modes = (settings["globalExcludeModes"] as JsonObject)?.ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.GetValue<string>(),
                StringComparer.OrdinalIgnoreCase
            );

            if (settings["globalLabels"] is JsonArray labels)
                foreach (var label in labels.OfType<JsonObject>())
                {
                    var text = label["text"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(text))
                        continue;
                    rules.Add(
                        Rule(
                            text.Trim(),
                            label["mode"]?.GetValue<string>(),
                            label["color"]?.GetValue<string>(),
                            "Highlight",
                            label["showInOpenFile"]?.GetValue<bool>() ?? false
                        )
                    );
                }

            if (settings["globalExcludeLabels"] is JsonArray exclusions)
                foreach (var exclusion in exclusions)
                {
                    var text = exclusion?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(text))
                        continue;
                    string? mode = null;
                    modes?.TryGetValue(text, out mode);
                    rules.Add(Rule(text.Trim(), mode, null, "Hide", false));
                }

            settings.Remove("globalLabels");
            settings.Remove("globalExcludeLabels");
            settings.Remove("globalExcludeModes");
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
            ["mode"] = mode is "Literal" or "Regex"
                ? mode
                : CompiledQuery.DetectMode(pattern).ToString(),
            ["caseSensitive"] = false,
            ["color"] = IsHexColor(color) ? color : FallbackColor,
            ["action"] = action,
            ["scope"] = "Global",
            ["enabled"] = true,
            ["pinnedAsTab"] = pinned,
        };

    private static bool IsHexColor(string? color) =>
        color is { Length: 4 or 7 } && color[0] == '#' && color[1..].All(Uri.IsHexDigit);
}
