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
    public void SavedFilters_DropUnusableEntriesAndKeepTheRestWithTabTolerance()
    {
        var config = AppConfigJson.Deserialize(
            """
            { "version": 2,
              "savedFilters": [
                { "name": "ok", "rules": [{ "pattern": "a" }], "levels": "Error" },
                { "rules": [] },
                "junk",
                { "name": "bad", "levels": "Nope",
                  "rules": [{ "pattern": "b", "action": "Bogus" }] }
              ] }
            """
        );

        Assert.Equal(["ok", "bad"], config.SavedFilters.Select(filter => filter.Name));
        var ok = config.SavedFilters[0];
        Assert.Equal("a", Assert.Single(ok.Rules).Pattern);
        Assert.Equal(LogLevels.Error, ok.Levels);
        var bad = config.SavedFilters[1];
        Assert.Equal(LogLevels.All, bad.Levels);
        Assert.Empty(bad.Rules);
    }

    [Fact]
    public void SavedFilters_AMalformedValueOrBlankNameCostsOnlyThatValue()
    {
        Assert.Empty(
            AppConfigJson.Deserialize("""{ "version": 2, "savedFilters": "oops" }""").SavedFilters
        );
        var config = AppConfigJson.Deserialize(
            """
            { "version": 2,
              "savedFilters": [{ "name": "  " }, { "name": 5 }, { "name": "n", "rules": "x" }] }
            """
        );
        var filter = Assert.Single(config.SavedFilters);
        Assert.Equal("n", filter.Name);
        Assert.Empty(filter.Rules);
    }

    [Fact]
    public void SessionsWithoutSavedFilters_LoadWithAnEmptyList()
    {
        Assert.Empty(AppConfigJson.Deserialize("""{ "version": 2 }""").SavedFilters);
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

    private const string DuplicateKeys = """
        { "version": 2,
          "recentFiles": ["/tmp/keep.log"],
          "settings": { "maxLines": 5000,
            "rules": [{ "pattern": "first", "action": "Hide" }],
            "rules": [{ "pattern": "last", "action": "Hide" }] } }
        """;

    [Fact]
    public void DuplicateJsonKeys_LoadWithTheLastValueWinning()
    {
        var config = AppConfigJson.Deserialize(DuplicateKeys);

        Assert.Equal("last", Assert.Single(config.Settings.Rules).Pattern);
        Assert.Equal(5000, config.Settings.MaxLines);
        Assert.False(RuleMigration.NeedsMigration(DuplicateKeys));
        Assert.False(RuleMigration.IsFutureVersion(DuplicateKeys));
    }

    [Fact]
    public async Task DuplicateJsonKeys_LoadThroughPersistenceWithoutLosingSettingsOrBackingUp()
    {
        using var directory = new Dir();
        await File.WriteAllTextAsync(
            directory.Session,
            DuplicateKeys,
            TestContext.Current.CancellationToken
        );

        var loaded = await new JsonFileAppPersistence(directory.Session).LoadAsync(
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(loaded);
        Assert.Equal("last", Assert.Single(loaded.Settings.Rules).Pattern);
        Assert.Equal(5000, loaded.Settings.MaxLines);
        Assert.Equal("/tmp/keep.log", Assert.Single(loaded.RecentFiles));
        Assert.False(File.Exists(directory.Backup));
    }

    [Fact]
    public async Task DuplicateKeysInAFileThatCannotBeDeserialized_AreBackedUpAndReportedUnreadable()
    {
        using var directory = new Dir();
        const string broken =
            """{ "version": 2, "settings": { "rules": [], "rules": [] }, "openFiles": 7 }""";
        await File.WriteAllTextAsync(
            directory.Session,
            broken,
            TestContext.Current.CancellationToken
        );

        var loaded = await new JsonFileAppPersistence(directory.Session).LoadAsync(
            TestContext.Current.CancellationToken
        );

        Assert.Null(loaded);
        Assert.Equal(
            broken,
            await File.ReadAllTextAsync(directory.Backup, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task ExistingBackupWithDifferentContent_IsKeptAndTheNewFileGetsASecondName()
    {
        using var directory = new Dir();
        await File.WriteAllTextAsync(
            directory.Backup,
            "old incident",
            TestContext.Current.CancellationToken
        );
        const string garbage = "{ \"version\": 2, \"settings\": ";
        await File.WriteAllTextAsync(
            directory.Session,
            garbage,
            TestContext.Current.CancellationToken
        );
        var persistence = new JsonFileAppPersistence(directory.Session);

        await persistence.LoadAsync(TestContext.Current.CancellationToken);
        await persistence.LoadAsync(TestContext.Current.CancellationToken); // same content: no duplicate

        Assert.Equal(
            "old incident",
            await File.ReadAllTextAsync(directory.Backup, TestContext.Current.CancellationToken)
        );
        var extra = Assert.Single(directory.Backups, path => path != directory.Backup);
        Assert.Equal(
            garbage,
            await File.ReadAllTextAsync(extra, TestContext.Current.CancellationToken)
        );
        Assert.Equal(2, directory.Backups.Length);
    }

    [Fact]
    public async Task ExistingBackupWithTheSameContentUnderAnEarlierTimestamp_IsNotDuplicated()
    {
        using var directory = new Dir();
        const string garbage = "{ \"version\": 2, \"settings\": ";
        await File.WriteAllTextAsync(
            directory.Backup,
            "old incident",
            TestContext.Current.CancellationToken
        );
        var earlier = Path.Combine(
            Path.GetDirectoryName(directory.Backup)!,
            "session.unreadable.20000101000000.bak"
        );
        await File.WriteAllTextAsync(earlier, garbage, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            directory.Session,
            garbage,
            TestContext.Current.CancellationToken
        );

        await new JsonFileAppPersistence(directory.Session).LoadAsync(
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, directory.Backups.Length);
    }

    private sealed class Dir : IDisposable
    {
        private readonly string _path = Directory
            .CreateTempSubdirectory("hextail-tolerance-")
            .FullName;

        public string Session => Path.Combine(_path, JsonFileAppPersistence.FileName);

        public string[] Backups => Directory.GetFiles(_path, "session.unreadable*.bak");

        public string Backup => Path.Combine(_path, "session.unreadable.bak");

        public void Dispose() => Directory.Delete(_path, recursive: true);
    }

    [Fact]
    public void Streams_BlankNamesDuplicatesAndMalformedEntriesCostOnlyThatValue()
    {
        var config = AppConfigJson.Deserialize(
            """
            { "version": 2,
              "streams": [
                { "id": "a", "name": "  ",
                  "members": [
                    { "key": "file:/x.log", "color": "#111111" },
                    { "key": "file:/x.log", "color": "#222222" },
                    { "key": "elastic:gone" },
                    { "key": 5 }, "junk", null, {}
                  ] },
                { "id": "b", "name": 7, "members": "oops" },
                { "id": "a", "name": "duplicate id", "members": [] },
                { "name": "no id" },
                "junk", null,
                { "id": "c", "name": "single", "members": [{ "key": "file:/y.log", "color": "#333333" }] }
              ],
              "openStreams": [
                { "streamId": "a", "levels": "Nope", "rules": "oops", "hiddenSources": ["file:/x.log", 4, null],
                  "from": 5, "live": "yes", "followSearches": [true, "x"],
                  "searches": [{ "query": "ok" }, { "mode": "Fuzzy" }, 3],
                  "columns": [{ "key": "@time", "width": 90 }, 5], "timeDisplay": "Sideways" },
                { "streamId": 3 }, { "nothing": true }, "junk"
              ],
              "selectedStreamId": 12 }
            """
        );

        Assert.Equal(["a", "b", "c"], config.Streams.Select(stream => stream.Id));
        Assert.Equal("Stream", config.Streams[0].Name);
        Assert.Equal(["file:/x.log", "elastic:gone"], config.Streams[0].Members.Select(m => m.Key));
        Assert.Equal("#111111", config.Streams[0].Members[0].Color);
        Assert.Equal(StreamPalette.ColorAt(1), config.Streams[0].Members[1].Color);
        Assert.Equal("Stream", config.Streams[1].Name);
        Assert.Empty(config.Streams[1].Members);
        Assert.Single(config.Streams[2].Members);
        var tab = Assert.Single(config.OpenStreams);
        Assert.Equal(LogLevels.All, tab.Levels);
        Assert.Empty(tab.Rules);
        Assert.Equal(["file:/x.log"], tab.HiddenSources);
        Assert.Equal("now-15m", tab.From);
        Assert.True(tab.Live);
        Assert.Equal([true], tab.FollowSearches);
        Assert.Equal("ok", Assert.Single(tab.Searches).Query);
        Assert.Equal("@time", Assert.Single(tab.Columns!).Key);
        Assert.Equal(TimeDisplay.Absolute, tab.TimeDisplay);
        Assert.Null(config.SelectedStreamId);
    }

    [Fact]
    public void Streams_AMalformedContainerIsDropped()
    {
        var config = AppConfigJson.Deserialize(
            """
            { "version": 2, "streams": "oops", "openStreams": 7, "selectedStreamId": null,
              "openFiles": [{ "path": "/tmp/a.log" }] }
            """
        );

        Assert.Empty(config.Streams);
        Assert.Empty(config.OpenStreams);
        Assert.Equal("/tmp/a.log", Assert.Single(config.OpenFiles).Path);
        Assert.Empty(
            AppConfigJson.Deserialize("""{ "streams": null, "openStreams": null }""").Streams
        );
    }
}
