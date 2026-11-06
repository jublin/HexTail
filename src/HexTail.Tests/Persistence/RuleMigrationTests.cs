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
        Assert.True(warn.PinnedAsTab); // the old ShowInOpenFile default was true

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
    public void LabelWithoutShowInOpenFile_MigratesPinned_ExplicitFalseDoesNot()
    {
        var rules = AppConfigJson
            .Deserialize(
                """
                { "settings": { "globalLabels": [
                  { "text": "a" },
                  { "text": "b", "showInOpenFile": false },
                  { "text": "c", "showInOpenFile": true }
                ] } }
                """
            )
            .Settings.Rules;

        Assert.Equal([true, false, true], rules.Select(rule => rule.PinnedAsTab));
    }

    [Fact]
    public void WrongTypedValuesAndCaseCollidingModeKeys_DoNotThrowAndKeepUsableEntries()
    {
        var json = """
            { "settings": {
              "globalLabels": [
                { "text": 5 }, 42, null,
                { "text": "ok", "color": 7, "mode": 3, "showInOpenFile": "yes" },
                { "text": "fine", "mode": "literal", "showInOpenFile": false }
              ],
              "globalExcludeLabels": [1, null, "keep", "Other"],
              "globalExcludeModes": { "keep": "literal", "KEEP": "Regex", "Other": 9 }
            } }
            """;

        var (migratedJson, migrated) = RuleMigration.Apply(json);
        var rules = AppConfigJson.Deserialize(json).Settings.Rules;

        Assert.True(migrated);
        Assert.NotEmpty(migratedJson);
        Assert.Equal(["ok", "fine", "keep", "Other"], rules.Select(rule => rule.Pattern));
        Assert.Equal("#f59e0b", rules[0].Color);
        Assert.True(rules[0].PinnedAsTab); // unusable flag: the legacy default (true) applies
        Assert.Equal(MatchMode.Literal, rules[1].Mode);
        Assert.False(rules[1].PinnedAsTab);
        Assert.Equal(MatchMode.Regex, rules[2].Mode); // colliding keys: last one wins
        Assert.Equal(RuleAction.Hide, rules[2].Action);
        Assert.Equal(MatchMode.Literal, rules[3].Mode); // numeric mode: detected from the text
    }

    [Theory]
    [InlineData("literal", MatchMode.Literal)]
    [InlineData("REGEX", MatchMode.Regex)]
    [InlineData("Regex", MatchMode.Regex)]
    public void ModeNames_AreMatchedCaseInsensitively(string mode, MatchMode expected)
    {
        var rule = Assert.Single(
            AppConfigJson
                .Deserialize(
                    $$"""{ "settings": { "globalLabels": [{ "text": "a.b", "mode": "{{mode}}" }] } }"""
                )
                .Settings.Rules
        );

        Assert.Equal(expected, rule.Mode);
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
