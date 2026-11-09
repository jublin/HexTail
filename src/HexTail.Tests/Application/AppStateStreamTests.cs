using HexTail.Application;
using HexTail.Domain;
using HexTail.Elastic;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;

namespace HexTail.Tests.Application;

public sealed class AppStateStreamTests
{
    private static readonly string[] FirstLines =
    [
        "2026-01-01 10:00:01 INFO a1",
        "2026-01-01 10:00:03 INFO a3",
    ];
    private static readonly string[] SecondLines =
    [
        "2026-01-01 10:00:02 INFO b2",
        "2026-01-01 10:00:04 INFO b4",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory = Directory
            .CreateTempSubdirectory("hextail-streams-")
            .FullName;
        public DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public TestPersistence Persistence { get; } = new();
        public AppState State { get; }
        public LogSourceService Tailers { get; private set; } = null!;

        private readonly IElasticApiClient? _elastic;

        public Harness(TestPersistence? persistence = null, IElasticApiClient? elastic = null)
        {
            _elastic = elastic;
            Persistence = persistence ?? new TestPersistence();
            State = NewState(Persistence);
            State.StreamClock = () => Now;
        }

        public AppState NewState(TestPersistence persistence)
        {
            Tailers = new LogSourceService(
                new TailerOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(10),
                    UseFileSystemWatcher = false,
                }
            );
            var state = new AppState(
                Tailers,
                persistence,
                elastic: _elastic,
                credentials: new InMemoryCredentialVault()
            );
            return state;
        }

        public string Write(string name, params string[] lines)
        {
            var path = Path.Combine(_directory, name);
            File.WriteAllLines(path, lines);
            return path;
        }

        public async Task<FileTabState> OpenLoaded(string path, int expectedLines)
        {
            var tab = await State.OpenFileAsync(path, cancellationToken: Ct);
            await Until(State, () => tab.Buffer.Count >= expectedLines);
            return tab;
        }

        public async ValueTask DisposeAsync()
        {
            await State.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static async Task Until(AppState state, Func<bool> condition, int timeoutMs = 10_000)
    {
        var timeout = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < timeout)
        {
            state.DrainTailerEvents();
            await Task.Delay(10, Ct);
        }
        Assert.True(condition());
    }

    private static string[] Raws(FileTabState tab) => [.. tab.Buffer.Lines.Select(l => l.Raw)];

    private static string[] Words(FileTabState tab) =>
        [.. tab.Buffer.Lines.Select(l => l.Raw[^2..])];

    [Fact]
    public async Task CreateStream_SavesTheDefinitionWithKeysAndTwoPaletteColours()
    {
        await using var h = new Harness();
        var a = await h.OpenLoaded(h.Write("a.log", FirstLines), 2);
        var b = await h.OpenLoaded(h.Write("b.log", SecondLines), 2);

        var stream = await h.State.CreateStreamAsync("  Both  ", [a, b], Ct);

        Assert.Equal("Both", stream.Name);
        Assert.Equal(
            [AppState.MemberKey(a), AppState.MemberKey(b)],
            stream.Members.Select(m => m.Key)
        );
        Assert.StartsWith("file:", stream.Members[0].Key);
        Assert.All(stream.Members, m => Assert.Contains(m.Color, StreamPalette.Colors));
        Assert.NotEqual(stream.Members[0].Color, stream.Members[1].Color);
        Assert.Equal(stream, Assert.Single(h.State.Streams));
        Assert.Equal(stream, Assert.Single(h.Persistence.Config!.Streams));
        Assert.Empty(h.Persistence.Config.OpenStreams);
        Assert.DoesNotContain(h.State.Files, f => f.Source.Kind == LogSourceKind.Stream);
    }

    [Fact]
    public async Task CreateStream_BlankNameBecomesStream_AndDuplicatesAreCollapsed()
    {
        await using var h = new Harness();
        var a = await h.OpenLoaded(h.Write("a.log", FirstLines), 2);
        var b = await h.OpenLoaded(h.Write("b.log", SecondLines), 2);

        var stream = await h.State.CreateStreamAsync(" ", [a, b, a], Ct);

        Assert.Equal("Stream", stream.Name);
        Assert.Equal(2, stream.Members.Count);
    }

    [Fact]
    public async Task CreateStream_RejectsFewerThanTwoSourcesAndStreamsAsMembers()
    {
        await using var h = new Harness();
        var a = await h.OpenLoaded(h.Write("a.log", FirstLines), 2);
        var b = await h.OpenLoaded(h.Write("b.log", SecondLines), 2);
        var made = await h.State.CreateStreamAsync("S", [a, b], Ct);
        var open = (await h.State.OpenStreamAsync(made.Id, Ct))!;

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await h.State.CreateStreamAsync("One", [a], Ct)
        );
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await h.State.CreateStreamAsync("Same", [a, a], Ct)
        );
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await h.State.CreateStreamAsync("Nested", [a, open], Ct)
        );
        Assert.Single(h.State.Streams);
    }

    [Fact]
    public async Task OpenStream_ShowsBothFilesInTimestampOrder_AndSelectsTheStreamTab()
    {
        await using var h = new Harness();
        var a = await h.OpenLoaded(h.Write("a.log", FirstLines), 2);
        var b = await h.OpenLoaded(h.Write("b.log", SecondLines), 2);
        var made = await h.State.CreateStreamAsync("Both", [a, b], Ct);

        var tab = await h.State.OpenStreamAsync(made.Id, Ct);

        Assert.NotNull(tab);
        Assert.Same(tab, h.State.SelectedFile);
        Assert.Contains(tab, h.State.Files);
        Assert.Equal(LogSourceKind.Stream, tab.Source.Kind);
        Assert.Same(tab.Stream, tab.Stream);
        Assert.Equal([a.Id, b.Id], tab.Stream!.MemberIds);
        Assert.Empty(tab.Stream.MissingMembers);
        Assert.Equal(["a1", "b2", "a3", "b4"], Words(tab));
        Assert.Equal([a.Id, b.Id, a.Id, b.Id], tab.Buffer.Lines.Select(l => l.SourceId));
        Assert.Equal("Both", Assert.Single(h.Persistence.Config!.OpenStreams.Select(_ => "Both")));
        Assert.Equal(made.Id, h.Persistence.Config.SelectedStreamId);
        Assert.Null(h.Persistence.Config.SelectedFilePath);
    }

    [Fact]
    public async Task OpeningTheSameStreamTwice_ReturnsTheExistingTab()
    {
        await using var h = new Harness();
        var a = await h.OpenLoaded(h.Write("a.log", FirstLines), 2);
        var b = await h.OpenLoaded(h.Write("b.log", SecondLines), 2);
        var made = await h.State.CreateStreamAsync("Both", [a, b], Ct);

        var first = await h.State.OpenStreamAsync(made.Id, Ct);
        h.State.SelectFile(a);
        var second = await h.State.OpenStreamAsync(made.Id, Ct);

        Assert.Same(first, second);
        Assert.Same(first, h.State.SelectedFile);
        Assert.Equal(1, h.State.Files.Count(f => f.Source.Kind == LogSourceKind.Stream));
        Assert.Null(await h.State.OpenStreamAsync("missing-id", Ct));
    }

    [Fact]
    public async Task OpenStream_OpensMembersThatAreClosed()
    {
        await using var h = new Harness();
        var pathA = h.Write("a.log", FirstLines);
        var pathB = h.Write("b.log", SecondLines);
        var a = await h.OpenLoaded(pathA, 2);
        var b = await h.OpenLoaded(pathB, 2);
        var made = await h.State.CreateStreamAsync("Both", [a, b], Ct);
        await h.State.CloseFileAsync(a, Ct);
        await h.State.CloseFileAsync(b, Ct);

        var tab = await h.State.OpenStreamAsync(made.Id, Ct);

        Assert.Equal(3, h.State.Files.Count);
        Assert.Equal(2, tab!.Stream!.MemberIds.Count);
        // The reopened members load asynchronously; each one's first lines re-merge the stream from the member buffers, so nothing is late or waiting for the hold.
        await Until(
            h.State,
            () =>
                h
                    .State.Files.Where(f => f.Source.Kind == LogSourceKind.File)
                    .All(f => f.Buffer.Count == 2)
        );
        await Until(h.State, () => tab.Buffer.Count == 4);
        Assert.Equal(["a1", "b2", "a3", "b4"], Words(tab));
        Assert.DoesNotContain(tab.Buffer.Lines, line => tab.Stream.IsLate(line));
    }

    [Fact]
    public async Task AppendedMemberLines_MergeBehindTheWatermark_AndTheHoldReleasesTheRest()
    {
        await using var h = new Harness();
        var pathA = h.Write("a.log", FirstLines);
        var pathB = h.Write("b.log", SecondLines);
        var a = await h.OpenLoaded(pathA, 2);
        var b = await h.OpenLoaded(pathB, 2);
        var made = await h.State.CreateStreamAsync("Both", [a, b], Ct);
        var tab = (await h.State.OpenStreamAsync(made.Id, Ct))!;

        // A moves ahead; B is still at 10:00:04, so the watermark holds A's new line back.
        await File.AppendAllLinesAsync(pathA, ["2026-01-01 10:00:10 INFO a10"], Ct);
        await Until(h.State, () => a.Buffer.Count == 3);
        h.State.DrainTailerEvents();
        Assert.Equal(4, tab.Buffer.Count);

        // B catches up past it: A's line is now at or below the watermark and is released in order.
        await File.AppendAllLinesAsync(pathB, ["2026-01-01 10:00:11 INFO b11"], Ct);
        await Until(h.State, () => b.Buffer.Count == 3);
        await Until(h.State, () => tab.Buffer.Count == 5);
        Assert.Equal(["a1", "b2", "a3", "b4", "10"], Words(tab));

        // B's newest line waits for the hold, then goes out on an idle drain (no events).
        h.State.DrainTailerEvents();
        Assert.Equal(5, tab.Buffer.Count);
        h.Now += TimeSpan.FromSeconds(6);
        Assert.True(h.State.DrainTailerEvents());
        Assert.Equal(6, tab.Buffer.Count);
        Assert.Equal("11", Words(tab)[^1]);
    }

    [Fact]
    public async Task MemberTruncation_RebuildsTheStreamFromTheMemberBuffers()
    {
        await using var h = new Harness();
        var pathA = h.Write("a.log", FirstLines);
        var a = await h.OpenLoaded(pathA, 2);
        var b = await h.OpenLoaded(h.Write("b.log", SecondLines), 2);
        var made = await h.State.CreateStreamAsync("Both", [a, b], Ct);
        var tab = (await h.State.OpenStreamAsync(made.Id, Ct))!;
        Assert.Equal(4, tab.Buffer.Count);

        await File.WriteAllLinesAsync(pathA, ["2026-01-01 10:00:05 INFO a5"], Ct);

        await Until(h.State, () => Words(tab) is ["b2", "b4", "a5"]);
        Assert.Equal(["a5"], a.Buffer.Lines.Select(l => l.Raw[^2..]));
    }

    [Fact]
    public async Task ClearedMemberBufferWithResetEvent_Rebuilds_AndMemberClearsFlagStreamsOnce()
    {
        await using var h = new Harness();
        var pathA = h.Write("a.log", FirstLines);
        var a = await h.OpenLoaded(pathA, 2);
        var b = await h.OpenLoaded(h.Write("b.log", SecondLines), 2);
        var made = await h.State.CreateStreamAsync("Both", [a, b], Ct);
        var tab = (await h.State.OpenStreamAsync(made.Id, Ct))!;

        // Rotation: the file is replaced by a different one.
        File.Delete(pathA);
        await File.WriteAllLinesAsync(pathA, ["2026-01-01 10:00:06 INFO a6"], Ct);

        await Until(h.State, () => Words(tab) is ["b2", "b4", "a6"]);
    }

    private static Line Stamped(
        FileTabState tab,
        string time,
        string word,
        TimeSpan offset = default
    ) =>
        new($"2026-01-01 {time} INFO {word}")
        {
            Timestamp = new DateTimeOffset(DateTime.Parse($"2026-01-01T{time}"), offset),
            SourceId = tab.Id,
        };

    private static void AssertSortedAndOnTime(FileTabState stream, params string[] words)
    {
        Assert.Equal(words, Words(stream));
        Assert.DoesNotContain(stream.Buffer.Lines, line => stream.Stream!.IsLate(line));
        Assert.Equal(
            stream.Buffer.Lines.OrderBy(line => line.Timestamp).Select(line => line.Raw),
            stream.Buffer.Lines.Select(line => line.Raw)
        );
    }

    [Fact]
    public async Task MemberReset_DrainedAlone_ThenReloadedLinesOlderThanTheOthers_StayInOrderAndOnTime()
    {
        await using var h = new Harness();
        var a = await h.OpenLoaded(h.Write("a.log", FirstLines), 2);
        var b = await h.OpenLoaded(h.Write("b.log", SecondLines), 2);
        // Events are supplied by hand from here on, so nothing races the drains below.
        await a.Tailer.DisposeAsync();
        await b.Tailer.DisposeAsync();
        var made = await h.State.CreateStreamAsync("Both", [a, b], Ct);
        var tab = (await h.State.OpenStreamAsync(made.Id, Ct))!;
        AssertSortedAndOnTime(tab, "a1", "b2", "a3", "b4");
        // File lines are read in the session's naive zone; hand-made lines use the same offset.
        var offset = b.Buffer[0].Timestamp!.Value.Offset;

        // The member is reset and that is drained on its own: the stream is rebuilt while A is empty.
        Assert.True(h.Tailers.Publish(new SourceReset(a.Id)));
        h.State.DrainTailerEvents();
        Assert.Empty(a.Buffer.Lines);
        Assert.Equal(["b2", "b4"], Words(tab));

        // A's reloaded lines are older than B's newest line.
        Assert.True(
            h.Tailers.Publish(
                new SourceLines(
                    a.Id,
                    [Stamped(a, "10:00:01", "a1", offset), Stamped(a, "10:00:03", "a3", offset)]
                )
            )
        );
        h.State.DrainTailerEvents();

        AssertSortedAndOnTime(tab, "a1", "b2", "a3", "b4");

        // The re-arm is consumed by that first delivery: later lines merge incrementally again.
        Assert.True(
            h.Tailers.Publish(new SourceLines(a.Id, [Stamped(a, "10:00:20", "a20", offset)]))
        );
        Assert.True(
            h.Tailers.Publish(new SourceLines(b.Id, [Stamped(b, "10:00:21", "b21", offset)]))
        );
        h.State.DrainTailerEvents();
        AssertSortedAndOnTime(tab, "a1", "b2", "a3", "b4", "20");
        Assert.False(tab.Stream!.NeedsRebuild);
    }

    [Fact]
    public async Task ElasticMemberRangeChangedToAnEarlierWindow_LeavesTheStreamSortedWithNoLateRows()
    {
        var client = new FakeElasticApiClient
        {
            SearchHandler = _ => Task.FromResult(new ElasticSearchPage("pit", [])),
        };
        await using var h = new Harness(elastic: client);
        await h.State.UpdateSettingsAsync(
            new AppSettings
            {
                ElasticConnections =
                [
                    new ElasticConnectionSettings
                    {
                        Id = "elastic-1",
                        Name = "Ops",
                        KibanaUrl = "https://kibana/",
                        ElasticsearchUrl = "https://elastic/",
                        DataViewId = "view",
                        DataViewTitle = "logs-*",
                        TimeFieldName = "@timestamp",
                        ServerField = "server",
                        OutputFields = ["message"],
                        Views =
                        [
                            new ElasticViewSettings
                            {
                                Id = "v1",
                                DataViewTitle = "logs-*",
                                TimeFieldName = "@timestamp",
                                ServerField = "server",
                                Sources =
                                [
                                    new ElasticSourceSettings { Id = "s1", ServerValue = "api" },
                                ],
                            },
                        ],
                    },
                ],
            },
            Ct
        );
        var file = await h.OpenLoaded(h.Write("b.log", SecondLines), 2);
        var elastic = await h.State.OpenElasticSourceAsync(
            "s1",
            save: false,
            cancellationToken: Ct
        );
        await file.Tailer.DisposeAsync();
        await elastic.Tailer.DisposeAsync();
        var tailer = (ElasticTailer)elastic.Tailer;
        // File lines are read in the session's naive zone; give the Elastic rows the same offset.
        var offset = file.Buffer[0].Timestamp!.Value.Offset;
        Line Row(string time, string word) => Stamped(elastic, time, word, offset);
        SourceLines Page(params Line[] lines) =>
            new(elastic.Id, lines)
            {
                Generation = tailer.Generation,
                InstanceId = tailer.InstanceId,
            };
        Assert.True(h.Tailers.Publish(Page(Row("10:00:05", "e5"), Row("10:00:06", "e6"))));
        h.State.DrainTailerEvents();
        var made = await h.State.CreateStreamAsync("Mixed", [file, elastic], Ct);
        var tab = (await h.State.OpenStreamAsync(made.Id, Ct))!;
        AssertSortedAndOnTime(tab, "b2", "b4", "e5", "e6");

        // The range moves to an earlier window; the clear is drained before any of the new rows arrive.
        h.State.SetElasticTimeRange(elastic, "now-2h", "now-1h");
        h.State.DrainTailerEvents();
        Assert.Equal(["b2", "b4"], Words(tab));
        Assert.True(h.Tailers.Publish(Page(Row("10:00:01", "e1"), Row("10:00:03", "e3"))));
        h.State.DrainTailerEvents();

        AssertSortedAndOnTime(tab, "e1", "b2", "e3", "b4");
    }

    [Fact]
    public async Task OpeningAnOpenStream_DoesNotReopenMembersTheUserClosed()
    {
        await using var h = new Harness();
        var a = await h.OpenLoaded(h.Write("a.log", FirstLines), 2);
        var b = await h.OpenLoaded(h.Write("b.log", SecondLines), 2);
        var made = await h.State.CreateStreamAsync("Both", [a, b], Ct);
        var tab = (await h.State.OpenStreamAsync(made.Id, Ct))!;
        await h.State.CloseFileAsync(a, Ct);
        h.State.SelectFile(b);

        var again = await h.State.OpenStreamAsync(made.Id, Ct);

        Assert.Same(tab, again);
        Assert.Same(tab, h.State.SelectedFile);
        Assert.DoesNotContain(
            h.State.Files,
            f => f.Source.Kind == LogSourceKind.File && f.Path == a.Path
        );
        Assert.Equal([AppState.MemberKey(a)], tab.Stream!.MissingMembers);
    }

    [Fact]
    public async Task SelectedLine_OfAStreamTab_IsSavedAndRestored()
    {
        var persistence = new TestPersistence();
        await using var h = new Harness(persistence);
        var pathA = h.Write("a.log", FirstLines);
        var pathB = h.Write("b.log", SecondLines);
        {
            var a = await h.OpenLoaded(pathA, 2);
            var b = await h.OpenLoaded(pathB, 2);
            var made = await h.State.CreateStreamAsync("Both", [a, b], Ct);
            var tab = (await h.State.OpenStreamAsync(made.Id, Ct))!;
            h.State.SelectLine(tab, tab.Buffer[2]);
            await h.State.SaveAsync(Ct);
        }
        Assert.Equal(2, Assert.Single(persistence.Config!.OpenStreams).SelectedLine);

        var restored = h.NewState(persistence);
        restored.StreamClock = () => h.Now;
        try
        {
            await restored.RestoreAsync(Ct);

            Assert.Equal(2, restored.SelectedFile!.SelectedLine);
        }
        finally
        {
            await restored.DisposeAsync();
        }
    }

    [Fact]
    public async Task StoredStreamMembersThatDifferOnlyInCase_AreOneMember()
    {
        var dir = Directory.CreateTempSubdirectory("hextail-streams-").FullName;
        try
        {
            var a = Path.Combine(dir, "a.log");
            var b = Path.Combine(dir, "b.log");
            await File.WriteAllLinesAsync(a, FirstLines, Ct);
            await File.WriteAllLinesAsync(b, SecondLines, Ct);
            var persistence = new TestPersistence(
                new AppConfig
                {
                    Streams =
                    [
                        new StreamSettings(
                            "s1",
                            "Cased",
                            [
                                new StreamMember("file:" + a, "#111111"),
                                new StreamMember("file:" + a.ToUpperInvariant(), "#222222"),
                                new StreamMember("file:" + b, "#333333"),
                            ]
                        ),
                    ],
                    OpenStreams = [new PersistedStreamTab { StreamId = "s1" }],
                }
            );
            await using var state = new AppState(
                new LogSourceService(
                    new TailerOptions
                    {
                        PollInterval = TimeSpan.FromMilliseconds(10),
                        UseFileSystemWatcher = false,
                    }
                ),
                persistence,
                credentials: new InMemoryCredentialVault()
            );

            await state.RestoreAsync(Ct);

            var tab = state.SelectedFile!;
            Assert.Equal(2, Assert.Single(state.Streams).Members.Count);
            Assert.Equal(2, tab.Stream!.MemberIds.Count);
            Assert.Empty(tab.Stream.MissingMembers);
            await Until(state, () => tab.Buffer.Count == 4);
            Assert.Equal(["a1", "b2", "a3", "b4"], Words(tab));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ClosingAMember_MarksItMissing_KeepsTheStreamAndItsRows()
    {
        await using var h = new Harness();
        var pathA = h.Write("a.log", FirstLines);
        var pathB = h.Write("b.log", SecondLines);
        var a = await h.OpenLoaded(pathA, 2);
        var b = await h.OpenLoaded(pathB, 2);
        var made = await h.State.CreateStreamAsync("Both", [a, b], Ct);
        var tab = (await h.State.OpenStreamAsync(made.Id, Ct))!;

        await h.State.CloseFileAsync(a, Ct);

        Assert.Contains(tab, h.State.Files);
        Assert.Equal([b.Id], tab.Stream!.MemberIds);
        Assert.Equal([AppState.MemberKey(a)], tab.Stream.MissingMembers);
        Assert.Equal(["a1", "b2", "a3", "b4"], Words(tab));
        Assert.Equal(made.Members[0].Color, tab.Stream.ColorFor(a.Id));
        Assert.Equal(made.Members[1].Color, tab.Stream.ColorFor(b.Id));

        // The remaining member keeps flowing once the closed one no longer counts as recent.
        await File.AppendAllLinesAsync(pathB, ["2026-01-01 10:00:20 INFO b20"], Ct);
        await Until(h.State, () => b.Buffer.Count == 3);
        h.Now += TimeSpan.FromSeconds(6);
        await Until(h.State, () => tab.Buffer.Count == 5);
        Assert.Equal("20", Words(tab)[^1]);
        Assert.Contains(b, h.State.Files);
    }

    [Fact]
    public async Task ReopeningAClosedMember_RejoinsTheOpenStream()
    {
        await using var h = new Harness();
        var pathA = h.Write("a.log", FirstLines);
        var a = await h.OpenLoaded(pathA, 2);
        var b = await h.OpenLoaded(h.Write("b.log", SecondLines), 2);
        var made = await h.State.CreateStreamAsync("Both", [a, b], Ct);
        var tab = (await h.State.OpenStreamAsync(made.Id, Ct))!;
        await h.State.CloseFileAsync(a, Ct);

        var reopened = await h.State.OpenFileAsync(pathA, cancellationToken: Ct);

        Assert.Equal(2, tab.Stream!.MemberIds.Count);
        Assert.Contains(reopened.Id, tab.Stream.MemberIds);
        Assert.Empty(tab.Stream.MissingMembers);
        await Until(
            h.State,
            () =>
                Words(tab) is ["a1", "b2", "a3", "b4"]
                && tab.Buffer.Lines.Where(l => l.Raw.EndsWith('1') || l.Raw.EndsWith('3'))
                    .All(l => l.SourceId == reopened.Id)
        );
    }

    [Fact]
    public async Task ClosingTheStream_KeepsMembersOpenAndTheDefinition()
    {
        await using var h = new Harness();
        var a = await h.OpenLoaded(h.Write("a.log", FirstLines), 2);
        var b = await h.OpenLoaded(h.Write("b.log", SecondLines), 2);
        var made = await h.State.CreateStreamAsync("Both", [a, b], Ct);
        var tab = (await h.State.OpenStreamAsync(made.Id, Ct))!;

        await h.State.CloseFileAsync(tab, Ct);

        Assert.DoesNotContain(tab, h.State.Files);
        Assert.Contains(a, h.State.Files);
        Assert.Contains(b, h.State.Files);
        Assert.Equal(made, Assert.Single(h.State.Streams));
        Assert.NotNull(h.State.SelectedFile);
        Assert.NotEqual(LogSourceKind.Stream, h.State.SelectedFile.Source.Kind);
        Assert.Empty(h.Persistence.Config!.OpenStreams);
        Assert.Null(h.Persistence.Config.SelectedStreamId);
        // The drain no longer touches the closed stream's merger.
        h.State.DrainTailerEvents();
    }

    [Fact]
    public async Task DeletingAStream_ClosesItsTabAndRemovesTheDefinition_NotTheMembers()
    {
        await using var h = new Harness();
        var a = await h.OpenLoaded(h.Write("a.log", FirstLines), 2);
        var b = await h.OpenLoaded(h.Write("b.log", SecondLines), 2);
        var made = await h.State.CreateStreamAsync("Both", [a, b], Ct);
        var tab = (await h.State.OpenStreamAsync(made.Id, Ct))!;

        await h.State.DeleteStreamAsync(made.Id, Ct);

        Assert.Empty(h.State.Streams);
        Assert.DoesNotContain(tab, h.State.Files);
        Assert.Equal(2, h.State.Files.Count);
        Assert.Empty(h.Persistence.Config!.Streams);
        Assert.Empty(h.Persistence.Config.OpenStreams);
        await h.State.DeleteStreamAsync(made.Id, Ct);
    }

    [Fact]
    public async Task DeletingAClosedStream_JustRemovesTheDefinition()
    {
        await using var h = new Harness();
        var a = await h.OpenLoaded(h.Write("a.log", FirstLines), 2);
        var b = await h.OpenLoaded(h.Write("b.log", SecondLines), 2);
        var made = await h.State.CreateStreamAsync("Both", [a, b], Ct);

        await h.State.DeleteStreamAsync(made.Id, Ct);

        Assert.Empty(h.State.Streams);
        Assert.Equal(2, h.State.Files.Count);
    }

    [Fact]
    public async Task UpdateStream_AddingAMemberRebuildsWithItsLines_RemovingOneDropsThem()
    {
        await using var h = new Harness();
        var a = await h.OpenLoaded(h.Write("a.log", FirstLines), 2);
        var b = await h.OpenLoaded(h.Write("b.log", SecondLines), 2);
        var c = await h.OpenLoaded(h.Write("c.log", "2026-01-01 10:00:02 INFO c2"), 1);
        var made = await h.State.CreateStreamAsync("Both", [a, b], Ct);
        var tab = (await h.State.OpenStreamAsync(made.Id, Ct))!;
        var colorOfA = made.Members[0].Color;

        await h.State.UpdateStreamAsync(made.Id, "Three", [a, b, c], Ct);

        Assert.Equal("Three", tab.DisplayName);
        Assert.Equal(3, tab.Stream!.MemberIds.Count);
        Assert.Equal(["a1", "b2", "c2", "a3", "b4"], Words(tab));
        Assert.Equal(colorOfA, h.State.Streams[0].Members[0].Color);
        Assert.Equal(3, h.State.Streams[0].Members.Select(m => m.Color).Distinct().Count());

        await h.State.UpdateStreamAsync(made.Id, "Two", [b, c], Ct);

        Assert.Equal(["b2", "c2", "b4"], Words(tab));
        Assert.Equal([b.Id, c.Id], tab.Stream.MemberIds);
        Assert.Equal("Two", h.Persistence.Config!.Streams[0].Name);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await h.State.UpdateStreamAsync(made.Id, "Bad", [b], Ct)
        );
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await h.State.UpdateStreamAsync("nope", "Bad", [b, c], Ct)
        );
    }

    [Fact]
    public async Task UpdateStream_OnAClosedStream_OnlyChangesTheDefinition()
    {
        await using var h = new Harness();
        var a = await h.OpenLoaded(h.Write("a.log", FirstLines), 2);
        var b = await h.OpenLoaded(h.Write("b.log", SecondLines), 2);
        var c = await h.OpenLoaded(h.Write("c.log", "2026-01-01 10:00:02 INFO c2"), 1);
        var made = await h.State.CreateStreamAsync("Both", [a, b], Ct);

        await h.State.UpdateStreamAsync(made.Id, "Renamed", [a, b, c], Ct);

        Assert.Equal("Renamed", h.State.Streams[0].Name);
        Assert.Equal(3, h.State.Streams[0].Members.Count);
        Assert.Equal(3, h.State.Files.Count);
    }

    [Fact]
    public async Task SetHiddenSources_StoresMemberKeysForTheSession_AndExposesTabIds()
    {
        await using var h = new Harness();
        var a = await h.OpenLoaded(h.Write("a.log", FirstLines), 2);
        var b = await h.OpenLoaded(h.Write("b.log", SecondLines), 2);
        var made = await h.State.CreateStreamAsync("Both", [a, b], Ct);
        var tab = (await h.State.OpenStreamAsync(made.Id, Ct))!;

        await h.State.SetHiddenSourcesAsync(tab, new HashSet<string> { b.Id, "bogus" }, Ct);

        Assert.Equal([b.Id], tab.Stream!.HiddenSources);
        Assert.Equal(
            [AppState.MemberKey(b)],
            Assert.Single(h.Persistence.Config!.OpenStreams).HiddenSources
        );
        await h.State.SetHiddenSourcesAsync(tab, new HashSet<string>(), Ct);
        Assert.Empty(tab.Stream.HiddenSources);
    }

    [Fact]
    public async Task StreamIsStructured_EvenWhenNoMemberIs()
    {
        await using var h = new Harness();
        var plain = await h.OpenLoaded(h.Write("a.log", FirstLines), 2);
        var plain2 = await h.OpenLoaded(h.Write("b.log", SecondLines), 2);
        var json = await h.OpenLoaded(
            h.Write("c.jsonl", """{"time":"2026-01-01T10:00:02Z","msg":"x"}"""),
            1
        );
        var textual = await h.State.CreateStreamAsync("Text", [plain, plain2], Ct);
        var mixed = await h.State.CreateStreamAsync("Mixed", [plain, json], Ct);

        var textTab = (await h.State.OpenStreamAsync(textual.Id, Ct))!;
        var mixedTab = (await h.State.OpenStreamAsync(mixed.Id, Ct))!;

        // A stream always uses columns; plain members only differ in AnyMemberStructured.
        Assert.True(textTab.IsStructured);
        Assert.False(textTab.Stream!.AnyMemberStructured);
        Assert.True(mixedTab.IsStructured);
        Assert.True(mixedTab.Stream!.AnyMemberStructured);
        Assert.False(textTab.ElasticLoading);
    }

    [Fact]
    public async Task SavedSession_RestoresStreamsAfterTheirMembers_AndTheStreamSelection()
    {
        var persistence = new TestPersistence();
        await using var h = new Harness(persistence);
        var pathA = h.Write("a.log", FirstLines);
        var pathB = h.Write("b.log", SecondLines);
        string streamId;
        {
            var a = await h.OpenLoaded(pathA, 2);
            var b = await h.OpenLoaded(pathB, 2);
            var made = await h.State.CreateStreamAsync("Both", [a, b], Ct);
            streamId = made.Id;
            var tab = (await h.State.OpenStreamAsync(made.Id, Ct))!;
            await h.State.SetLevelsAsync(tab, LogLevels.Warn | LogLevels.Error, Ct);
            await h.State.SetHiddenSourcesAsync(tab, new HashSet<string> { b.Id }, Ct);
            await h.State.SaveAsync(Ct);
        }

        var restored = h.NewState(persistence);
        restored.StreamClock = () => h.Now;
        try
        {
            await restored.RestoreAsync(Ct);

            Assert.Equal(3, restored.Files.Count);
            Assert.Equal(
                [LogSourceKind.File, LogSourceKind.File, LogSourceKind.Stream],
                restored.Files.Select(f => f.Source.Kind)
            );
            var tab = restored.SelectedFile!;
            Assert.Equal(LogSourceKind.Stream, tab.Source.Kind);
            Assert.Equal(streamId, tab.Stream!.Definition.Id);
            Assert.Equal(LogLevels.Warn | LogLevels.Error, tab.Levels);
            Assert.Equal(2, tab.Stream.MemberIds.Count);
            Assert.Equal([restored.Files[1].Id], tab.Stream.HiddenSources);
            await Until(restored, () => restored.Files.Take(2).All(f => f.Buffer.Count == 2));
            await Until(restored, () => tab.Buffer.Count == 4);
            Assert.Equal(["a1", "b2", "a3", "b4"], Words(tab));
        }
        finally
        {
            await restored.DisposeAsync();
        }
    }

    [Fact]
    public async Task StreamWithAMissingFileOrUnknownElasticSource_OpensWithThoseMembersMissing()
    {
        var dir = Directory.CreateTempSubdirectory("hextail-streams-").FullName;
        try
        {
            var existing = Path.Combine(dir, "a.log");
            await File.WriteAllLinesAsync(existing, FirstLines, Ct);
            var missingPath = Path.Combine(dir, "gone.log");
            var definition = new StreamSettings(
                "s1",
                "Mixed",
                [
                    new StreamMember("file:" + existing, "#111111"),
                    new StreamMember("file:" + missingPath, "#222222"),
                    new StreamMember("elastic:not-configured", "#333333"),
                    new StreamMember("weird:thing", "#444444"),
                ]
            );
            var persistence = new TestPersistence(
                new AppConfig
                {
                    Streams = [definition],
                    OpenStreams = [new PersistedStreamTab { StreamId = "s1" }],
                    SelectedStreamId = "s1",
                }
            );
            await using var state = new AppState(
                new LogSourceService(
                    new TailerOptions
                    {
                        PollInterval = TimeSpan.FromMilliseconds(10),
                        UseFileSystemWatcher = false,
                    }
                ),
                persistence,
                credentials: new InMemoryCredentialVault()
            );

            await state.RestoreAsync(Ct);

            var tab = state.SelectedFile!;
            Assert.Equal(LogSourceKind.Stream, tab.Source.Kind);
            Assert.Single(tab.Stream!.MemberIds);
            Assert.Equal(
                ["file:" + missingPath, "elastic:not-configured", "weird:thing"],
                tab.Stream.MissingMembers
            );
            Assert.Equal(2, state.Files.Count);
            Assert.False(File.Exists(missingPath));
            await Until(state, () => tab.Buffer.Count == 2);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task MalformedStoredStreams_NeverFailTheRestore()
    {
        var persistence = new TestPersistence(
            new AppConfig
            {
                Streams =
                [
                    new StreamSettings("", "No id", []),
                    new StreamSettings("solo", " ", [new StreamMember("file:/nope/x.log", "")]),
                ],
                OpenStreams =
                [
                    new PersistedStreamTab { StreamId = "unknown" },
                    new PersistedStreamTab { StreamId = "solo" },
                ],
                SelectedStreamId = "unknown",
            }
        );
        await using var state = new AppState(
            new LogSourceService(),
            persistence,
            credentials: new InMemoryCredentialVault()
        );

        await state.RestoreAsync(Ct);

        var definition = Assert.Single(state.Streams);
        Assert.Equal("Stream", definition.Name);
        Assert.Equal(StreamPalette.ColorAt(0), definition.Members[0].Color);
        var tab = Assert.Single(state.Files);
        Assert.Equal(["file:/nope/x.log"], tab.Stream!.MissingMembers);
        Assert.Same(tab, state.SelectedFile);
    }

    [Fact]
    public async Task SessionsWrittenBeforeStreamsExisted_StillRestore()
    {
        var config = AppConfigJson.Deserialize("""{ "version": 2, "openFiles": [] }""");
        var persistence = new TestPersistence(config);
        await using var state = new AppState(
            new LogSourceService(),
            persistence,
            credentials: new InMemoryCredentialVault()
        );

        await state.RestoreAsync(Ct);

        Assert.Empty(state.Streams);
        Assert.Empty(state.Files);
    }

    [Fact]
    public async Task IdleDrains_WithNoStreams_AndWithStreams_DoNotReportChangesWhenNothingIsReleased()
    {
        await using var h = new Harness();
        var a = await h.OpenLoaded(h.Write("a.log", FirstLines), 2);
        var b = await h.OpenLoaded(h.Write("b.log", SecondLines), 2);
        var made = await h.State.CreateStreamAsync("Both", [a, b], Ct);
        await h.State.OpenStreamAsync(made.Id, Ct);

        Assert.False(h.State.DrainTailerEvents());
    }
}
