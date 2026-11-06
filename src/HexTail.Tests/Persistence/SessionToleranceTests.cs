using HexTail.Domain;
using HexTail.Persistence;

namespace HexTail.Tests.Persistence;

public sealed class SessionToleranceTests
{
    private const string SessionWithUnknownValues = """
        { "version": 2,
          "settings": { "rules": [
            { "pattern": "ok", "action": "Hide", "scope": "Global" },
            { "pattern": "bad-action", "action": "Pin", "scope": "Global" },
            { "pattern": "bad-scope", "action": "Hide", "scope": "Saved" },
            { "pattern": "bad-mode", "action": "Hide", "mode": "Fuzzy" },
            { "pattern": 5 }, 7, null
          ] },
          "openFiles": [
            { "path": "/tmp/a.log",
              "levels": "Warn, Error",
              "rules": [
                { "pattern": "keep", "scope": "Source" },
                { "pattern": "drop", "action": "Nope", "scope": "Source" }
              ] },
            { "path": "/tmp/b.log", "levels": "Warn, Bogus" },
            { "path": "/tmp/c.log", "levels": null, "rules": "oops" }
          ],
          "openElasticTabs": [
            { "sourceId": "s1", "levels": "Loud",
              "rules": [{ "pattern": "e", "scope": "Mystery" }, { "pattern": "f" }] }
          ] }
        """;

    [Fact]
    public void UnknownEnumNames_DropOnlyTheAffectedRuleAndEverythingElseLoads()
    {
        var config = AppConfigJson.Deserialize(SessionWithUnknownValues);

        Assert.Equal(["ok"], config.Settings.Rules.Select(rule => rule.Pattern));
        Assert.Equal(
            ["/tmp/a.log", "/tmp/b.log", "/tmp/c.log"],
            config.OpenFiles.Select(t => t.Path)
        );
        Assert.Equal("keep", Assert.Single(config.OpenFiles[0].Rules).Pattern);
        Assert.Equal("f", Assert.Single(Assert.Single(config.OpenElasticTabs).Rules).Pattern);
    }

    [Fact]
    public void UnusableLevels_BecomeAll_AndValidLevelsSurvive()
    {
        var config = AppConfigJson.Deserialize(SessionWithUnknownValues);

        Assert.Equal(LogLevels.Warn | LogLevels.Error, config.OpenFiles[0].Levels);
        Assert.Equal(LogLevels.All, config.OpenFiles[1].Levels);
        Assert.Equal(LogLevels.All, config.OpenFiles[2].Levels);
        Assert.Empty(config.OpenFiles[2].Rules);
        Assert.Equal(LogLevels.All, config.OpenElasticTabs[0].Levels);
    }

    [Fact]
    public void TabsWithoutLevelsOrRules_LoadWithAllAndNoRules()
    {
        var config = AppConfigJson.Deserialize(
            """
            { "version": 2,
              "openFiles": [{ "path": "/tmp/a.log" }],
              "openElasticTabs": [{ "sourceId": "s1" }] }
            """
        );

        var file = Assert.Single(config.OpenFiles);
        Assert.Equal(LogLevels.All, file.Levels);
        Assert.Empty(file.Rules);
        var elastic = Assert.Single(config.OpenElasticTabs);
        Assert.Equal(LogLevels.All, elastic.Levels);
        Assert.Empty(elastic.Rules);
    }

    [Fact]
    public void ExclusionModeLookup_AlsoTriesTheTrimmedText()
    {
        var rule = Assert.Single(
            AppConfigJson
                .Deserialize(
                    """
                    { "settings": {
                      "globalExcludeLabels": ["  [a-z]+ping  "],
                      "globalExcludeModes": { "[a-z]+ping": "Literal" }
                    } }
                    """
                )
                .Settings.Rules
        );

        Assert.Equal(("[a-z]+ping", MatchMode.Literal), (rule.Pattern, rule.Mode));
    }

    [Fact]
    public async Task UnparseableFile_IsKeptAsAnUnreadableBackupAndLeftUntouched()
    {
        using var directory = new Dir();
        const string garbage = "{ \"version\": 2, \"settings\": ";
        await File.WriteAllTextAsync(
            directory.Session,
            garbage,
            TestContext.Current.CancellationToken
        );

        var loaded = await new JsonFileAppPersistence(directory.Session).LoadAsync(
            TestContext.Current.CancellationToken
        );

        Assert.Null(loaded);
        Assert.Equal(
            garbage,
            await File.ReadAllTextAsync(directory.Backup, TestContext.Current.CancellationToken)
        );
        Assert.Equal(
            garbage,
            await File.ReadAllTextAsync(directory.Session, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task FutureVersionFile_IsBackedUpOnceAndStillLoads()
    {
        using var directory = new Dir();
        const string future = """{ "version": 3, "recentFiles": ["/tmp/a.log"] }""";
        await File.WriteAllTextAsync(
            directory.Session,
            future,
            TestContext.Current.CancellationToken
        );
        var persistence = new JsonFileAppPersistence(directory.Session);

        var loaded = await persistence.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("/tmp/a.log", Assert.Single(loaded!.RecentFiles));
        Assert.Equal(
            future,
            await File.ReadAllTextAsync(directory.Backup, TestContext.Current.CancellationToken)
        );
        Assert.Equal(
            future,
            await File.ReadAllTextAsync(directory.Session, TestContext.Current.CancellationToken)
        );

        // An existing backup is never overwritten.
        await File.WriteAllTextAsync(
            directory.Session,
            future + " ",
            TestContext.Current.CancellationToken
        );
        await persistence.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            future,
            await File.ReadAllTextAsync(directory.Backup, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task FailedBackupCopy_DoesNotStopTheLoad()
    {
        using var directory = new Dir();
        const string future = """{ "version": 3, "recentFiles": ["/tmp/a.log"] }""";
        await File.WriteAllTextAsync(
            directory.Session,
            future,
            TestContext.Current.CancellationToken
        );
        // A directory squatting on the backup name makes File.Exists false and the copy fail.
        Directory.CreateDirectory(directory.Backup);

        var loaded = await new JsonFileAppPersistence(directory.Session).LoadAsync(
            TestContext.Current.CancellationToken
        );

        Assert.Equal("/tmp/a.log", Assert.Single(loaded!.RecentFiles));
    }

    private sealed class Dir : IDisposable
    {
        private readonly string _path = Directory
            .CreateTempSubdirectory("hextail-tolerance-")
            .FullName;

        public string Session => Path.Combine(_path, JsonFileAppPersistence.FileName);

        public string Backup => Path.Combine(_path, "session.unreadable.bak");

        public void Dispose() => Directory.Delete(_path, recursive: true);
    }
}
