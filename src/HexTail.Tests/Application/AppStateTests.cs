using HexTail.Application;
using HexTail.Domain;
using HexTail.Elastic;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;

namespace HexTail.Tests.Application;

public sealed class AppStateTests
{
    [Fact]
    public async Task RemovingConfiguredSource_SavesOnceAndClosesItsTab()
    {
        var persistence = new MemoryPersistence { FailOnSaveNumber = 2 };
        var connection = ElasticConnection("Ops") with
        {
            Views =
            [
                new ElasticViewSettings
                {
                    Id = "view",
                    Name = "Logs",
                    DataViewId = "view",
                    DataViewTitle = "logs-*",
                    TimeFieldName = "@timestamp",
                    ServerField = "server",
                    OutputFields = ["message"],
                    Sources = [new ElasticSourceSettings { Id = "source-1", ServerValue = "api" }],
                },
            ],
        };
        var client = new FakeElasticApiClient
        {
            SearchHandler = _ => Task.FromResult(new ElasticSearchPage("pit", [])),
        };
        await using var state = new AppState(
            new LogSourceService(),
            persistence,
            new AppSettings { ElasticConnections = [connection] },
            elastic: client
        );
        try
        {
            await state.OpenElasticSourceAsync("source-1", save: false);
            await state.SaveElasticConnectionAsync(connection with { Views = [] }, null);
            Assert.Empty(state.Files);
            Assert.Equal(1, persistence.SaveCount);
            Assert.Empty(persistence.Config!.OpenElasticTabs);
            Assert.Null(persistence.Config.SelectedElasticSourceId);
        }
        finally
        {
            persistence.FailOnSaveNumber = null;
        }
    }

    [Fact]
    public async Task ElasticSourceIdentity_DistinguishesFiltersAndRefreshesAfterSave()
    {
        var connection = ElasticConnection("ops") with
        {
            Sources =
            [
                new ElasticSourceSettings
                {
                    Id = "api",
                    ServerValue = "api",
                    NamespaceValue = "prod",
                },
                new ElasticSourceSettings
                {
                    Id = "worker",
                    ServerValue = "worker",
                    NamespaceValue = "stage",
                },
            ],
        };
        await using var state = new AppState(
            NewTailers(),
            new MemoryPersistence(),
            new AppSettings { ElasticConnections = [connection] },
            elastic: new FakeElasticApiClient
            {
                SearchHandler = _ => Task.FromResult(new ElasticSearchPage("pit", [])),
            }
        );
        var api = await state.OpenElasticSourceAsync(
            "api",
            false,
            TestContext.Current.CancellationToken
        );
        var worker = await state.OpenElasticSourceAsync(
            "worker",
            false,
            TestContext.Current.CancellationToken
        );
        Assert.NotEqual(api.DisplayName, worker.DisplayName);
        Assert.Contains("api", api.Source.ToolTip);
        var saved = state.Settings.ElasticConnections[0];
        var view = saved.Views[0];
        await state.SaveElasticConnectionAsync(
            saved with
            {
                Name = "Renamed",
                Views =
                [
                    view with
                    {
                        Sources =
                        [
                            view.Sources[0] with
                            {
                                ServerValue = "changed-api",
                            },
                            view.Sources[1],
                        ],
                    },
                ],
            },
            null,
            TestContext.Current.CancellationToken
        );
        Assert.Contains("Renamed", api.DisplayName);
        Assert.Contains("changed-api", api.DisplayName);
        Assert.Contains("prod", api.Source.ToolTip);
    }

    [Fact]
    public async Task RestoreElasticHistory_UsesSavedIntervalOnFirstRequest()
    {
        var persistence = new MemoryPersistence();
        var connection = ElasticConnection("ops") with
        {
            Sources = [new ElasticSourceSettings { Id = "source-1", ServerValue = "api" }],
        };
        await using (
            var original = new AppState(
                NewTailers(),
                persistence,
                new AppSettings { ElasticConnections = [connection] },
                elastic: new FakeElasticApiClient()
            )
        )
        {
            var tab = await original.OpenElasticSourceAsync(
                "source-1",
                save: false,
                cancellationToken: TestContext.Current.CancellationToken
            );
            original.SetElasticTimeRange(tab, "2026-08-20T10:00:00Z", "2026-08-20T11:00:00Z");
            tab.ElasticInputZone = AppTimeZoneMode.Local;
            await original.SaveAsync(TestContext.Current.CancellationToken);
        }
        var requested = new TaskCompletionSource<ElasticSearchRequest>();
        var client = new FakeElasticApiClient
        {
            SearchHandler = request =>
            {
                requested.TrySetResult(request);
                return Task.FromResult(new ElasticSearchPage("pit", []));
            },
        };
        await using var restored = new AppState(NewTailers(), persistence, elastic: client);
        await restored.RestoreAsync(TestContext.Current.CancellationToken);
        var first = await requested.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(DateTimeOffset.Parse("2026-08-20T10:00:00Z"), first.FromInclusive);
        Assert.Equal(DateTimeOffset.Parse("2026-08-20T11:00:00Z"), first.ToInclusive);
        Assert.Equal("2026-08-20T11:00:00Z", restored.SelectedFile!.ElasticTo);
        Assert.Equal(AppTimeZoneMode.Local, restored.SelectedFile.ElasticInputZone);
    }

    [Fact]
    public async Task SavingOpenElasticSettings_ReloadsTheSavedFilterAndPreservesInvestigation()
    {
        var connection = ElasticConnection("ops") with
        {
            Sources = [new ElasticSourceSettings { Id = "source-1", ServerValue = "api" }],
        };
        var changedRequested = new TaskCompletionSource<ElasticSearchRequest>();
        var client = new FakeElasticApiClient
        {
            SearchHandler = request =>
            {
                if (request.FilterValue == "changed-api")
                    changedRequested.TrySetResult(request);
                return Task.FromResult(new ElasticSearchPage("pit", []));
            },
        };
        await using var state = new AppState(
            NewTailers(),
            new MemoryPersistence(),
            new AppSettings { ElasticConnections = [connection] },
            elastic: client
        );
        var tab = await state.OpenElasticSourceAsync(
            "source-1",
            save: false,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var oldTailer = tab.Tailer;
        await oldTailer.DisposeAsync();
        var from = "2026-08-20T10:00:00Z";
        var to = "2026-08-20T11:00:00Z";
        state.SetElasticTimeRange(tab, from, to);
        tab.Buffer.Append(new Line("old results"));
        tab.AddSearch(
            new Search(
                new CompiledQuery("message", MatchMode.Literal, false),
                "#ff0000",
                tab.Buffer
            )
        );
        tab.FollowAll = false;
        var saved = state.Settings.ElasticConnections[0];
        var view = saved.Views[0];
        var updated = saved with
        {
            Views =
            [
                view with
                {
                    Sources = [view.Sources[0] with { ServerValue = "changed-api" }],
                },
            ],
        };

        await state.SaveElasticConnectionAsync(
            updated,
            null,
            TestContext.Current.CancellationToken
        );

        Assert.Same(tab, state.SelectedFile);
        Assert.NotSame(oldTailer, tab.Tailer);
        Assert.Empty(tab.Buffer.Lines);
        Assert.Single(tab.Searches);
        Assert.False(tab.FollowAll);
        Assert.Equal(from, tab.ElasticFrom);
        Assert.Equal(to, tab.ElasticTo);
        var request = await changedRequested.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(DateTimeOffset.Parse(from), request.FromInclusive);
        Assert.Equal(DateTimeOffset.Parse(to), request.ToInclusive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReopeningElasticSource_RejectsQueuedEventsFromItsPreviousOpening(bool partial)
    {
        var connection = ElasticConnection("ops") with
        {
            Sources = [new ElasticSourceSettings { Id = "source-1", ServerValue = "api" }],
        };
        var payload = "old opening";
        var client = new FakeElasticApiClient
        {
            SearchHandler = _ =>
                Task.FromResult(
                    new ElasticSearchPage(
                        "pit",
                        [
                            new ElasticHit(
                                Guid.NewGuid().ToString(),
                                DateTimeOffset.UtcNow,
                                new Line(payload),
                                []
                            ),
                        ]
                    )
                ),
        };
        await using var service = NewTailers();
        await using var state = new AppState(
            service,
            new MemoryPersistence(),
            new AppSettings { ElasticConnections = [connection] },
            elastic: client
        );
        var old = await state.OpenElasticSourceAsync(
            "source-1",
            save: false,
            cancellationToken: TestContext.Current.CancellationToken
        );
        await old.Tailer.DisposeAsync();
        await ((ElasticTailer)old.Tailer).PollOnceAsync(TestContext.Current.CancellationToken);
        SourceLines? oldRows = null;
        while (service.Events.TryRead(out var sourceEvent))
            if (sourceEvent is SourceLines rows)
                oldRows = rows;
        Assert.NotNull(oldRows);
        var queued = oldRows with
        {
            Lines = Enumerable.Repeat(new Line("old opening"), partial ? 20_000 : 1).ToArray(),
        };
        Assert.True(service.Publish(queued));
        if (partial)
        {
            state.DrainTailerEvents();
            Assert.NotEmpty(old.Buffer.Lines);
        }
        await state.CloseFileAsync(old, TestContext.Current.CancellationToken);
        payload = "new opening";
        var current = await state.OpenElasticSourceAsync(
            "source-1",
            save: false,
            cancellationToken: TestContext.Current.CancellationToken
        );
        await current.Tailer.DisposeAsync();
        await ((ElasticTailer)current.Tailer).PollOnceAsync(TestContext.Current.CancellationToken);
        while (state.DrainTailerEvents()) { }
        Assert.DoesNotContain(current.Buffer.Lines, line => line.Raw == "old opening");
        Assert.Contains(current.Buffer.Lines, line => line.Raw == "new opening");
    }

    [Fact]
    public async Task SaveElasticConnection_PreservesExistingApiKeyWhenSecretIsBlank()
    {
        var connection = ElasticConnection("Ops") with { AuthMode = ElasticAuthMode.ApiKey };
        var vault = new InMemoryCredentialVault();
        vault.Set(connection.Id, "saved-api-key");
        await using var state = new AppState(
            NewTailers(),
            new MemoryPersistence(),
            new AppSettings { ElasticConnections = [connection] },
            vault,
            new FakeElasticApiClient()
        );

        await state.SaveElasticConnectionAsync(connection with { Name = "Updated" }, null);

        Assert.Equal("saved-api-key", vault.Get(connection.Id));
        Assert.Equal("Updated", Assert.Single(state.Settings.ElasticConnections).Name);
    }

    [Fact]
    public async Task AppState_NormalizesLegacyElasticConnectionIntoView()
    {
        var connection = ElasticConnection("Ops");
        await using var state = new AppState(
            NewTailers(),
            new MemoryPersistence(),
            new AppSettings { ElasticConnections = [connection] },
            new InMemoryCredentialVault(),
            new FakeElasticApiClient()
        );

        var view = Assert.Single(Assert.Single(state.Settings.ElasticConnections).Views);

        Assert.Equal(connection.DataViewId, view.DataViewId);
        Assert.Equal(connection.DataViewTitle, view.DataViewTitle);
        Assert.Equal(connection.Sources, view.Sources);
    }

    [Fact]
    public async Task SaveElasticConnection_RestoresSecretAndSettingsWhenJsonSaveFails()
    {
        var old = ElasticConnection("Old name");
        var updated = old with { Name = "New name" };
        var persistence = new MemoryPersistence { SaveError = new IOException("disk full") };
        var vault = new InMemoryCredentialVault();
        vault.Set("elastic-1", "old-secret");
        await using var state = new AppState(
            NewTailers(),
            persistence,
            new AppSettings { ElasticConnections = [old] },
            vault,
            new FakeElasticApiClient()
        );

        await Assert.ThrowsAsync<IOException>(() =>
            state.SaveElasticConnectionAsync(updated, "new-secret").AsTask()
        );

        Assert.Equal("old-secret", vault.Get("elastic-1"));
        Assert.Equal("Old name", Assert.Single(state.Settings.ElasticConnections).Name);
        persistence.SaveError = null;
    }

    [Fact]
    public async Task OpenElasticSource_UsesOneTabPerStableSourceAndPersistsRemoteSelection()
    {
        var connection = ElasticConnection("ops") with
        {
            Sources =
            [
                new ElasticSourceSettings
                {
                    Id = "source-1",
                    ServerValue = "api",
                    NamespaceValue = "prod",
                },
            ],
        };
        var persistence = new MemoryPersistence();
        await using var state = new AppState(
            NewTailers(),
            persistence,
            new AppSettings { ElasticConnections = [connection] },
            new InMemoryCredentialVault(),
            new FakeElasticApiClient()
        );

        var first = await state.OpenElasticSourceAsync("source-1", save: false);
        var second = await state.OpenElasticSourceAsync("source-1", save: false);
        await state.SaveAsync();

        Assert.Same(first, second);
        Assert.Equal(LogSourceKind.Elastic, first.Source.Kind);
        Assert.Equal("ops / logs-* · api", first.DisplayName);
        Assert.Empty(Assert.IsType<AppConfig>(persistence.Config).OpenFiles);
        Assert.Equal(
            "source-1",
            Assert.Single(Assert.IsType<AppConfig>(persistence.Config).OpenElasticTabs).SourceId
        );
        Assert.Equal(
            "source-1",
            Assert.IsType<AppConfig>(persistence.Config).SelectedElasticSourceId
        );
    }

    [Fact]
    public async Task OpenAndDrain_AppendsParsedLinesAndUpdatesSearches()
    {
        var path = CreateTempFile("level=info\n", ".logfmt");
        var persistence = new MemoryPersistence();
        await using var tailers = new LogSourceService(
            new TailerOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                UseFileSystemWatcher = false,
            }
        );
        await using var state = new AppState(tailers, persistence);

        var tab = await state.OpenFileAsync(path);
        await DrainUntilAsync(state, () => tab.Buffer.Count == 1);
        var search = state.AddSearch(
            tab,
            "info",
            MatchMode.Literal,
            caseSensitive: true,
            "#00ff00"
        );

        await File.AppendAllTextAsync(path, "level=error\n");
        await DrainUntilAsync(state, () => tab.Buffer.Count == 2);

        Assert.Equal("info", tab.Buffer[0].ParsedFields!["level"]);
        Assert.Equal([0], search.Results);
    }

    [Fact]
    public async Task SaveAndRestore_PreservesTabsSearchesAndViewSettings()
    {
        var path = CreateTempFile("error\n");
        var persistence = new MemoryPersistence();
        await using (var tailers = NewTailers())
        await using (var state = new AppState(tailers, persistence))
        {
            var tab = await state.OpenFileAsync(path);
            await DrainUntilAsync(state, () => tab.Buffer.Count == 1);
            tab.FollowAll = false;
            tab.ShowContext = true;
            tab.ContextAbove = 4;
            tab.ContextBelow = 8;
            state.AddSearch(tab, "error", MatchMode.Literal, caseSensitive: false, "#ff0000");
            await state.SaveAsync();
        }

        await using var restoredTailers = NewTailers();
        await using var restored = new AppState(restoredTailers, persistence);
        await restored.RestoreAsync();

        var restoredTab = Assert.Single(restored.Files);
        Assert.False(restoredTab.FollowAll);
        Assert.True(restoredTab.ShowContext);
        Assert.Equal(4, restoredTab.ContextAbove);
        Assert.Equal(8, restoredTab.ContextBelow);
        var search = Assert.Single(restoredTab.Searches);
        Assert.Equal("error", search.Query.Query);
        Assert.False(search.Query.CaseSensitive);
    }

    [Fact]
    public void AppConfigJson_LoadsOlderConfigWithMissingOptionalFields()
    {
        var config = AppConfigJson.Deserialize("{\"openFiles\":[{\"path\":\"/tmp/app.log\"}]}");

        var tab = Assert.Single(config.OpenFiles);
        Assert.Equal("/tmp/app.log", tab.Path);
        Assert.Empty(tab.Searches);
        Assert.Equal(3, tab.ContextAbove);
        Assert.Equal(10, tab.ContextBelow);
        Assert.Empty(config.OpenElasticTabs);
        Assert.Empty(config.Settings.ElasticConnections);
    }

    [Fact]
    public async Task UpdateSettings_PersistsNormalizedGlobalRules()
    {
        var persistence = new MemoryPersistence();
        await using var state = new AppState(NewTailers(), persistence);

        await state.UpdateSettingsAsync(
            new AppSettings
            {
                GlobalLabels =
                [
                    new GlobalLabel { Text = " Error ", Color = "#ff0000" },
                    new GlobalLabel { Text = "error", Color = "#00ff00" },
                ],
                GlobalExcludeLabels = [" Health ", "health", ""],
                Theme = "not-a-theme",
                SettingsMenuAlignment = SettingsMenuAlignment.Left,
            }
        );

        var settings = Assert.IsType<AppConfig>(persistence.Config).Settings;
        var label = Assert.Single(settings.GlobalLabels);
        Assert.Equal("Error", label.Text);
        Assert.Equal("#ff0000", label.Color);
        Assert.Equal(["Health"], settings.GlobalExcludeLabels);
        Assert.Equal("cyber-tail", settings.Theme);
        Assert.Equal(SettingsMenuAlignment.Right, settings.SettingsMenuAlignment);
    }

    [Fact]
    public async Task UpdateSettings_ShowsGlobalLabelsAsSearchTabsInOpenViews()
    {
        var path = CreateTempFile("error\n");
        await using var state = new AppState(NewTailers(), new MemoryPersistence());
        try
        {
            var tab = await state.OpenFileAsync(path, save: false);

            await state.UpdateSettingsAsync(
                new AppSettings
                {
                    GlobalLabels = [new GlobalLabel { Text = "error", Color = "#ff0000" }],
                }
            );

            var search = Assert.Single(tab.Searches);
            Assert.Equal("error", search.Query.Query);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AppSettings_MatchesLabelsAndExclusionsCaseInsensitively()
    {
        var settings = new AppSettings
        {
            GlobalLabels = [new GlobalLabel { Text = @"warn\s+id", Color = "#f59e0b" }],
            GlobalExcludeLabels = [@"health\s+check"],
        };

        Assert.True(settings.Excludes("GET /HEALTH CHECK"));
        var highlight = settings.GetLabelHighlights("WARN ID: warn id").First();
        Assert.Equal(0, highlight.Start);
        Assert.Equal(7, highlight.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("system")]
    [InlineData("light")]
    [InlineData("material-wcag")]
    [InlineData("dark")]
    public void ThemeCatalog_NormalizesEveryLegacyValueToCyberTail(string? value)
    {
        Assert.Equal(["cyber-tail", "catppuccin-mocha", "spotify"], ThemeCatalog.Names);
        Assert.Equal(
            value is "catppuccin-mocha" or "spotify" ? value : "cyber-tail",
            ThemeCatalog.Normalize(value)
        );
    }

    [Fact]
    public async Task DrainTailerEvents_WithoutEventsDoesNotNotify()
    {
        await using var state = new AppState(NewTailers(), new MemoryPersistence());
        var notifications = 0;
        state.Changed += () => notifications++;

        Assert.False(state.DrainTailerEvents());
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task DrainTailerEvents_CoalescesLineBatchesPerSource()
    {
        var path = CreateTempFile(string.Empty);
        await using var tailers = NewTailers();
        await using var state = new AppState(tailers, new MemoryPersistence());
        var tab = await state.OpenFileAsync(path, save: false);
        var bufferChanges = 0;
        tab.Buffer.Changed += _ => bufferChanges++;
        var notifications = 0;
        state.Changed += () => notifications++;

        Assert.True(tailers.Publish(new SourceLines(tab.Id, [new Line("one")])));
        Assert.True(tailers.Publish(new SourceLines(tab.Id, [new Line("two")])));

        Assert.True(state.DrainTailerEvents());
        Assert.Equal(1, bufferChanges);
        Assert.Equal(1, notifications);
        Assert.Equal(["one", "two"], tab.Buffer.Lines.Select(line => line.Raw));
    }

    [Fact]
    public async Task DrainTailerEvents_YieldsWithBacklogAndPreservesResetOrder()
    {
        var path = CreateTempFile(string.Empty);
        try
        {
            await using var tailers = NewTailers();
            await using var state = new AppState(tailers, new MemoryPersistence());
            var tab = await state.OpenFileAsync(path, save: false);
            var lines = Enumerable.Range(0, 20_000).Select(i => new Line(i.ToString())).ToArray();
            Assert.True(tailers.Publish(new SourceLines(tab.Id, lines)));
            Assert.True(tailers.Publish(new SourceReset(tab.Id)));
            Assert.True(tailers.Publish(new SourceLines(tab.Id, [new Line("after reset")])));
            state.DrainTailerEvents();
            Assert.InRange(tab.Buffer.Count, 1, AppState.MaxLinesPerDrain);
            Assert.Equal("0", tab.Buffer[0].Raw);
            while (state.DrainTailerEvents()) { }
            Assert.Equal("after reset", Assert.Single(tab.Buffer.Lines).Raw);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Files_ReturnsSnapshotWhileWorkspaceChanges()
    {
        var path = CreateTempFile(string.Empty);
        await using var state = new AppState(NewTailers(), new MemoryPersistence());

        var beforeOpen = state.Files;
        await state.OpenFileAsync(path, save: false);

        Assert.Empty(beforeOpen);
        Assert.Single(state.Files);
    }

    [Fact]
    public void LogParserSelector_UsesFormatSpecificParsers()
    {
        Assert.IsType<LogfmtParser>(LogParserSelector.ForPath("app.logfmt"));
        Assert.IsType<JsonlParser>(LogParserSelector.ForPath("events.JSONL"));
        Assert.IsType<PlainTextParser>(LogParserSelector.ForPath("app.log"));
    }

    [Fact]
    public void LogParserSelector_ParsesJsonlObjectFields()
    {
        const string raw =
            "{\"message\":\"ready\",\"service\":{\"name\":\"api\"},\"tags\":[\"a\",\"b\"],\"count\":42}";

        var line = LogParserSelector.ForPath("events.jsonl").Parse(raw);

        Assert.Equal(raw, line.Raw);
        var fields = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            line.ParsedFields
        );
        Assert.Equal("ready", fields["message"]);
        Assert.Equal("api", fields["service.name"]);
        Assert.Equal("[\"a\",\"b\"]", fields["tags"]);
        Assert.Equal("42", fields["count"]);
    }

    [Fact]
    public async Task ElasticRangeChange_ClearsRowsAndRejectsQueuedAndPartialOldBatches()
    {
        var client = new HexTail.Tests.Support.FakeElasticApiClient
        {
            SearchHandler = _ => Task.FromResult(new HexTail.Elastic.ElasticSearchPage("pit", [])),
        };
        await using var tailers = NewTailers();
        await using var state = new AppState(tailers, new MemoryPersistence(), elastic: client);
        var connection = ElasticConnection("ops") with
        {
            Views =
            [
                new ElasticViewSettings
                {
                    Id = "v1",
                    DataViewTitle = "logs-*",
                    TimeFieldName = "@timestamp",
                    ServerField = "server",
                    Sources = [new ElasticSourceSettings { Id = "s1", ServerValue = "api" }],
                },
            ],
        };
        await state.UpdateSettingsAsync(new AppSettings { ElasticConnections = [connection] });
        var tab = await state.OpenElasticSourceAsync("s1", save: false);
        // This test supplies events directly; stop the producer to avoid injected-event races.
        await tab.Tailer.DisposeAsync();
        tab.AddSearch(
            new Search(
                new CompiledQuery("old", CompiledQuery.DetectMode("old"), false),
                "#ff0000",
                tab.Buffer
            )
        );
        var oldBatch = new SourceLines(
            tab.Id,
            Enumerable.Range(0, 20_000).Select(_ => new Line("old")).ToArray()
        )
        {
            Generation = 0,
            InstanceId = ((ElasticTailer)tab.Tailer).InstanceId,
        };
        Assert.True(tailers.Publish(oldBatch));
        state.DrainTailerEvents();
        Assert.NotEmpty(tab.Buffer.Lines);
        tab.SelectedLine = 0;
        tab.ExpandedLine = 0;
        state.SetElasticTimeRange(tab, "now-2m", "now");
        Assert.Empty(tab.Buffer.Lines);
        Assert.Single(tab.Searches);
        Assert.Null(tab.SelectedLine);
        Assert.Null(tab.ExpandedLine);
        Assert.True(
            tailers.Publish(
                new SourceError(tab.Id, "old failure")
                {
                    Generation = 0,
                    InstanceId = ((ElasticTailer)tab.Tailer).InstanceId,
                }
            )
        );
        Assert.True(
            tailers.Publish(
                new SourceLines(tab.Id, [new Line("new")])
                {
                    Generation = 1,
                    InstanceId = ((ElasticTailer)tab.Tailer).InstanceId,
                }
            )
        );
        while (state.DrainTailerEvents()) { }
        Assert.Equal("new", Assert.Single(tab.Buffer.Lines).Raw);
        Assert.Null(tab.Error);
        Assert.True(tab.ElasticLoading);
        var from = DateTimeOffset.Parse("2026-08-20T10:00:00Z");
        var to = from.AddHours(1);
        Assert.True(
            tailers.Publish(
                new SourceRangeLoaded(tab.Id, from, to, true, true)
                {
                    Generation = 1,
                    InstanceId = ((ElasticTailer)tab.Tailer).InstanceId,
                }
            )
        );
        while (state.DrainTailerEvents()) { }
        Assert.False(tab.ElasticLoading);
        Assert.Equal(from, tab.ElasticResolvedFrom);
        Assert.Equal(to, tab.ElasticResolvedTo);
        Assert.True(
            tailers.Publish(
                new SourceRangeLoaded(tab.Id, from.AddMinutes(1), to.AddMinutes(1), false, true)
                {
                    Generation = 1,
                    InstanceId = ((ElasticTailer)tab.Tailer).InstanceId,
                }
            )
        );
        while (state.DrainTailerEvents()) { }
        Assert.Equal(from, tab.ElasticResolvedFrom);
        Assert.Equal(to.AddMinutes(1), tab.ElasticResolvedTo);
    }

    private static LogSourceService NewTailers() =>
        new(
            new TailerOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                UseFileSystemWatcher = false,
            }
        );

    private static async Task DrainUntilAsync(AppState state, Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            state.DrainTailerEvents();
            await Task.Delay(10, timeout.Token);
        }

        state.DrainTailerEvents();
    }

    private static string CreateTempFile(string contents, string extension = ".log")
    {
        var path = Path.Combine(Path.GetTempPath(), $"hextail-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, contents);
        return path;
    }

    private sealed class MemoryPersistence : IAppPersistence
    {
        public int SaveCount { get; private set; }
        public int? FailOnSaveNumber { get; set; }

        public AppConfig? Config { get; private set; }
        public Exception? SaveError { get; set; }

        public ValueTask<AppConfig?> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Config);

        public ValueTask SaveAsync(AppConfig config, CancellationToken cancellationToken = default)
        {
            if (++SaveCount == FailOnSaveNumber)
                throw new IOException("second write failed");
            if (SaveError is not null)
                throw SaveError;
            Config = AppConfigJson.Deserialize(AppConfigJson.Serialize(config));
            return ValueTask.CompletedTask;
        }
    }

    private static ElasticConnectionSettings ElasticConnection(string name) =>
        new()
        {
            Id = "elastic-1",
            Name = name,
            KibanaUrl = "https://kibana/",
            ElasticsearchUrl = "https://elastic/",
            DataViewId = "view",
            DataViewTitle = "logs-*",
            TimeFieldName = "@timestamp",
            ServerField = "server",
            NamespaceField = "namespace",
            OutputFields = ["message"],
        };
}
