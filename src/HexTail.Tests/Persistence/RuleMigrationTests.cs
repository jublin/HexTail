using HexTail.Domain;
using HexTail.Persistence;

namespace HexTail.Tests.Persistence;

public sealed class RuleMigrationTests
{
    private const string LegacyJson = """
        { "settings": {
          "globalLabels": [
            { "text": "[ERROR]", "color": "#ff0000", "mode": "Literal", "showInOpenFile": true },
            { "text": "warn.*", "color": "#00ff00" }
          ],
          "globalExcludeLabels": ["healthz", "[a-z]+ping"],
          "globalExcludeModes": { "healthz": "Literal" }
        } }
        """;

    [Fact]
    public void LegacyLabelsAndExclusions_BecomeGlobalRulesWithModesAndPinning()
    {
        var config = AppConfigJson.Deserialize(LegacyJson);
        var rules = config.Settings.Rules;

        Assert.Equal(4, rules.Count);
        Assert.All(rules, rule => Assert.Equal(RuleScope.Global, rule.Scope));

        var error = rules[0];
        Assert.Equal(
            ("[ERROR]", MatchMode.Literal, "#ff0000"),
            (error.Pattern, error.Mode, error.Color)
        );
        Assert.Equal(RuleAction.Highlight, error.Action);
        Assert.True(error.PinnedAsTab);

        var warn = rules[1];
        Assert.Equal(MatchMode.Regex, warn.Mode); // legacy rule with no mode: detected
        Assert.False(warn.PinnedAsTab);

        Assert.Equal(
            ("healthz", MatchMode.Literal, RuleAction.Hide),
            (rules[2].Pattern, rules[2].Mode, rules[2].Action)
        );
        Assert.Equal(MatchMode.Regex, rules[3].Mode);
        Assert.Equal(RuleAction.Hide, rules[3].Action);
        Assert.False(rules[2].PinnedAsTab);
    }

    [Fact]
    public void Migration_PreservesBehavior_ForLiteralBracketPatterns()
    {
        var set = RuleSet.For(AppConfigJson.Deserialize(LegacyJson).Settings.Rules);
        Assert.True(set.IsVisible(new Line("READY")));
        Assert.False(set.IsVisible(new Line("GET /healthz")));
    }

    [Fact]
    public void RoundTrip_WritesVersionTwoAndNoLegacyFields()
    {
        var config = AppConfigJson.Deserialize(LegacyJson);
        var json = AppConfigJson.Serialize(config);

        Assert.DoesNotContain("globalLabels", json);
        Assert.DoesNotContain("globalExcludeLabels", json);
        Assert.Contains("\"version\": 2", json);
        Assert.Equal(config.Settings.Rules, AppConfigJson.Deserialize(json).Settings.Rules);
    }

    [Fact]
    public void CurrentFiles_AreNotRewritten()
    {
        var json = AppConfigJson.Serialize(new AppConfig());
        var (rewritten, migrated) = RuleMigration.Apply(json);
        Assert.False(migrated);
        Assert.Equal(json, rewritten);
    }

    [Fact]
    public void MissingSettingsAndGarbageAreTolerated()
    {
        Assert.Empty(AppConfigJson.Deserialize("{}").Settings.Rules);
        Assert.False(RuleMigration.Apply("[1,2]").Migrated);
    }

    [Fact]
    public async Task FirstMigratingLoad_KeepsAOneTimeBackup()
    {
        var directory = Directory.CreateTempSubdirectory("hextail-migration-");
        try
        {
            var path = Path.Combine(directory.FullName, JsonFileAppPersistence.FileName);
            await File.WriteAllTextAsync(path, LegacyJson, TestContext.Current.CancellationToken);
            var persistence = new JsonFileAppPersistence(path);

            var loaded = await persistence.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Equal(4, loaded!.Settings.Rules.Count);
            var backup = Path.Combine(directory.FullName, "session.v1.json.bak");
            Assert.Equal(
                LegacyJson,
                await File.ReadAllTextAsync(backup, TestContext.Current.CancellationToken)
            );

            await File.WriteAllTextAsync(
                path,
                LegacyJson + " ",
                TestContext.Current.CancellationToken
            );
            await persistence.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(
                LegacyJson,
                await File.ReadAllTextAsync(backup, TestContext.Current.CancellationToken)
            );
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
