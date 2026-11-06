using System.Text.Json;
using System.Threading.Channels;
using HexTail.Domain;
using HexTail.Elastic;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;

namespace HexTail.Tests.Elastic;

public sealed class ElasticTailerTests
{
    [Fact]
    public async Task PollOnce_UsesFiveMinuteLookbackAndClosesNewestPit()
    {
        var client = new FakeElasticApiClient();
        using var sortDocument = JsonDocument.Parse("[1]");
        client.Pages.Enqueue(
            new ElasticSearchPage(
                "pit-2",
                [Hit("a", "2026-08-20T10:00:00Z", [sortDocument.RootElement[0].Clone()])]
            )
        );
        var channel = Channel.CreateUnbounded<SourceEvent>();
        var connection = Connection();
        var now = new DateTimeOffset(2026, 8, 20, 10, 5, 0, TimeSpan.Zero);
        await using var tailer = new ElasticTailer(
            connection,
            connection.Views[0],
            connection.Views[0].Sources[0],
            "secret",
            client,
            channel.Writer,
            () => now,
            (_, _) => Task.CompletedTask
        );

        await tailer.PollOnceAsync(CancellationToken.None);

        Assert.Equal(now.AddMinutes(-5), client.Searches[0].FromInclusive);
        Assert.Equal("pit-2", Assert.Single(client.ClosedPitIds));
        var lines = Assert.IsType<SourceLines>(await channel.Reader.ReadAsync());
        Assert.Equal("ready", lines.Lines[0].Raw);
    }

    [Fact]
    public async Task PollOnce_LimitsInitialBatchToNewestTenThousandLines()
    {
        using var sortDocument = JsonDocument.Parse("[1]");
        var baseTime = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);
        var hits = Enumerable
            .Range(1, 10_001)
            .Reverse()
            .Select(index => new ElasticHit(
                $"id-{index}",
                baseTime.AddSeconds(index),
                new Line($"line-{index}"),
                [sortDocument.RootElement[0].Clone()]
            ));
        var client = new FakeElasticApiClient();
        foreach (var page in hits.Chunk(ElasticTailer.PageSize))
            client.Pages.Enqueue(new ElasticSearchPage("pit-1", page));

        var channel = Channel.CreateUnbounded<SourceEvent>();
        var connection = Connection();
        await using var tailer = new ElasticTailer(
            connection,
            connection.Views[0],
            connection.Views[0].Sources[0],
            "secret",
            client,
            channel.Writer,
            () => new DateTimeOffset(2026, 8, 20, 10, 5, 0, TimeSpan.Zero)
        );

        var loaded = await tailer.PollOnceAsync(CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(10_000, loaded.InitialLimitReached);
        var lines = Assert.IsType<SourceLines>(await channel.Reader.ReadAsync());
        Assert.Equal(10_000, lines.Lines.Count);
        Assert.Equal("line-2", lines.Lines[0].Raw);
        Assert.Equal("line-10001", lines.Lines[^1].Raw);
    }

    [Fact]
    public async Task PollOnce_EmitsOnlyNewHitsFromInclusiveCursor()
    {
        using var sortDocument = JsonDocument.Parse("[1]");
        var baseTime = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);
        var client = new FakeElasticApiClient();
        client.Pages.Enqueue(
            new ElasticSearchPage(
                "pit-1",
                [
                    new ElasticHit(
                        "cursor",
                        baseTime.AddMinutes(1),
                        new Line("cursor"),
                        [sortDocument.RootElement[0].Clone()]
                    ),
                    new ElasticHit(
                        "old",
                        baseTime,
                        new Line("old"),
                        [sortDocument.RootElement[0].Clone()]
                    ),
                ]
            )
        );
        client.Pages.Enqueue(
            new ElasticSearchPage(
                "pit-1",
                [
                    new ElasticHit(
                        "old",
                        baseTime,
                        new Line("old"),
                        [sortDocument.RootElement[0].Clone()]
                    ),
                    new ElasticHit(
                        "cursor",
                        baseTime.AddMinutes(1),
                        new Line("cursor"),
                        [sortDocument.RootElement[0].Clone()]
                    ),
                    new ElasticHit(
                        "new",
                        baseTime.AddMinutes(2),
                        new Line("new"),
                        [sortDocument.RootElement[0].Clone()]
                    ),
                ]
            )
        );

        var channel = Channel.CreateUnbounded<SourceEvent>();
        var connection = Connection();
        await using var tailer = new ElasticTailer(
            connection,
            connection.Views[0],
            connection.Views[0].Sources[0],
            "secret",
            client,
            channel.Writer,
            () => baseTime.AddMinutes(5)
        );

        await tailer.PollOnceAsync(CancellationToken.None);
        Assert.Equal(
            ["old", "cursor"],
            Assert
                .IsType<SourceLines>(await channel.Reader.ReadAsync())
                .Lines.Select(line => line.Raw)
        );

        await tailer.PollOnceAsync(CancellationToken.None);

        var incremental = Assert.IsType<SourceLines>(await channel.Reader.ReadAsync());
        Assert.Equal(["new"], incremental.Lines.Select(line => line.Raw));
    }

    [Fact]
    public async Task PollOnce_UsesViewDataViewTitle()
    {
        var client = new FakeElasticApiClient();
        client.Pages.Enqueue(new ElasticSearchPage("pit-1", []));
        var channel = Channel.CreateUnbounded<SourceEvent>();
        var connection = Connection() with { DataViewTitle = null };
        await using var tailer = new ElasticTailer(
            connection,
            connection.Views[0],
            connection.Views[0].Sources[0],
            "secret",
            client,
            channel.Writer,
            () => new DateTimeOffset(2026, 8, 20, 10, 5, 0, TimeSpan.Zero),
            (_, _) => Task.CompletedTask
        );

        await tailer.PollOnceAsync(CancellationToken.None);

        Assert.Equal("logs-*", client.Searches[0].DataViewTitle);
    }

    [Fact]
    public async Task IncrementalPoll_StreamsBoundedPagesAndDoesNotReplayCursor()
    {
        var client = new FakeElasticApiClient();
        var time = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);
        client.Pages.Enqueue(
            new ElasticSearchPage(
                "pit-1",
                [new ElasticHit("initial", time, new Line("initial"), [])]
            )
        );
        var connection = Connection();
        var channel = Channel.CreateUnbounded<SourceEvent>();
        await using var tailer = new ElasticTailer(
            connection,
            connection.Views[0],
            connection.Views[0].Sources[0],
            "secret",
            client,
            channel.Writer,
            () => time.AddMinutes(5)
        );
        await tailer.PollOnceAsync(TestContext.Current.CancellationToken);
        Assert.True(channel.Reader.TryRead(out _));
        var hits = Enumerable
            .Range(1, 2_501)
            .Select(i => new ElasticHit(
                $"id-{i}",
                time.AddMilliseconds(i),
                new Line($"line-{i}"),
                []
            ))
            .ToArray();
        foreach (var page in hits.Chunk(ElasticTailer.PageSize))
            client.Pages.Enqueue(new ElasticSearchPage("pit-1", page));
        await tailer.PollOnceAsync(TestContext.Current.CancellationToken);
        var actual = new List<Line>();
        while (channel.Reader.TryRead(out var item))
        {
            var batch = Assert.IsType<SourceLines>(item);
            Assert.InRange(batch.Lines.Count, 1, ElasticTailer.PageSize);
            actual.AddRange(batch.Lines);
        }
        Assert.Equal(hits.Select(hit => hit.Line), actual);
        client.Pages.Enqueue(new ElasticSearchPage("pit-1", [hits[^1]]));
        await tailer.PollOnceAsync(TestContext.Current.CancellationToken);
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task IncrementalPoll_FailureAfterDeliveredPageResumesWithoutDuplicates()
    {
        var client = new FakeElasticApiClient();
        var time = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);
        var initial = new ElasticHit("initial", time, new Line("initial"), []);
        client.Pages.Enqueue(new ElasticSearchPage("pit-1", [initial]));
        var connection = Connection();
        var channel = Channel.CreateUnbounded<SourceEvent>();
        await using var tailer = new ElasticTailer(
            connection,
            connection.Views[0],
            connection.Views[0].Sources[0],
            "secret",
            client,
            channel.Writer,
            () => time.AddMinutes(5)
        );
        await tailer.PollOnceAsync(TestContext.Current.CancellationToken);
        Assert.True(channel.Reader.TryRead(out _));
        // Same-timestamp hits exercise inclusive cursor deduplication across pages.
        var hits = Enumerable
            .Range(1, ElasticTailer.PageSize)
            .Select(i => new ElasticHit($"id-{i}", time.AddSeconds(1), new Line($"line-{i}"), []))
            .ToArray();
        client.Pages.Enqueue(new ElasticSearchPage("pit-1", hits));
        // The fake throws when the next page is missing.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tailer.PollOnceAsync(TestContext.Current.CancellationToken)
        );
        Assert.Equal(
            hits.Length,
            Assert
                .IsType<SourceLines>(
                    await channel.Reader.ReadAsync(TestContext.Current.CancellationToken)
                )
                .Lines.Count
        );
        var last = new ElasticHit("last", time.AddSeconds(1), new Line("last"), []);
        client.Pages.Enqueue(new ElasticSearchPage("pit-1", hits));
        client.Pages.Enqueue(new ElasticSearchPage("pit-1", [last]));
        await tailer.PollOnceAsync(TestContext.Current.CancellationToken);
        Assert.Same(
            last.Line,
            Assert.Single(
                Assert
                    .IsType<SourceLines>(
                        await channel.Reader.ReadAsync(TestContext.Current.CancellationToken)
                    )
                    .Lines
            )
        );
        Assert.False(channel.Reader.TryRead(out _));
        Assert.Equal(3, client.ClosedPitIds.Count);
    }

    [Fact]
    public async Task RangeChange_DiscardsInFlightRowsAndOldCursor()
    {
        var response = new TaskCompletionSource<ElasticSearchPage>();
        var client = new FakeElasticApiClient { SearchHandler = _ => response.Task };
        var connection = Connection();
        var channel = Channel.CreateUnbounded<SourceEvent>();
        var now = new DateTimeOffset(2026, 8, 20, 10, 5, 0, TimeSpan.Zero);
        await using var tailer = new ElasticTailer(
            connection,
            connection.Views[0],
            connection.Views[0].Sources[0],
            "secret",
            client,
            channel.Writer,
            () => now
        );
        var oldPoll = tailer.PollOnceAsync(TestContext.Current.CancellationToken);
        tailer.SetTimeRange("now-2m", "now");
        response.SetResult(
            new ElasticSearchPage(
                "pit-old",
                [new ElasticHit("old", now.AddMinutes(-4), new Line("old"), [])]
            )
        );
        await oldPoll;
        Assert.False(channel.Reader.TryRead(out _));
        client.SearchHandler = null;
        client.Pages.Enqueue(new ElasticSearchPage("pit-new", []));
        await tailer.PollOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(now.AddMinutes(-2), client.Searches[^1].FromInclusive);
        Assert.True(client.Searches[^1].SortDescending);
        Assert.Contains("pit-old", client.ClosedPitIds);
    }

    [Fact]
    public async Task RelativeRange_UsesOneClockReadingForBothEndpoints()
    {
        var client = new FakeElasticApiClient();
        client.Pages.Enqueue(new ElasticSearchPage("pit", []));
        var connection = Connection();
        var now = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        await using var tailer = new ElasticTailer(
            connection,
            connection.Views[0],
            connection.Views[0].Sources[0],
            "",
            client,
            Channel.CreateUnbounded<SourceEvent>().Writer,
            () => now
        );
        tailer.SetTimeRange("now-5m", "now-1m");
        await tailer.PollOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(now.AddMinutes(-5), client.Searches[0].FromInclusive);
        Assert.Equal(now.AddMinutes(-1), client.Searches[0].ToInclusive);
    }

    [Theory]
    [InlineData("now", "now-1m")]
    [InlineData("now-1e100d", "now")]
    [InlineData("now-NaNd", "now")]
    [InlineData("now--5m", "now")]
    [InlineData("invalid", "now")]
    public async Task InvalidRange_IsRejectedWithoutChangingCurrentRange(string from, string to)
    {
        var client = new FakeElasticApiClient();
        client.Pages.Enqueue(new ElasticSearchPage("pit", []));
        var connection = Connection();
        var now = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        await using var tailer = new ElasticTailer(
            connection,
            connection.Views[0],
            connection.Views[0].Sources[0],
            "",
            client,
            Channel.CreateUnbounded<SourceEvent>().Writer,
            () => now
        );
        Assert.Throws<ArgumentException>(() => tailer.SetTimeRange(from, to));
        await tailer.PollOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(now.AddMinutes(-5), client.Searches[0].FromInclusive);
    }

    [Fact]
    public async Task HistoricalRange_ReportsEmptyLoadAndDoesNotKeepRequesting()
    {
        var client = new FakeElasticApiClient
        {
            SearchHandler = _ => Task.FromResult(new ElasticSearchPage("pit", [])),
        };
        var connection = Connection();
        var channel = Channel.CreateUnbounded<SourceEvent>();
        var firstDelay = new TaskCompletionSource();
        var secondDelay = new TaskCompletionSource();
        var resume = new TaskCompletionSource();
        var resumeSecond = new TaskCompletionSource();
        var thirdDelay = new TaskCompletionSource();
        var delays = 0;
        await using var tailer = new ElasticTailer(
            connection,
            connection.Views[0],
            connection.Views[0].Sources[0],
            "",
            client,
            channel.Writer,
            delay: async (_, token) =>
            {
                if (Interlocked.Increment(ref delays) == 1)
                {
                    firstDelay.SetResult();
                    await resume.Task.WaitAsync(token);
                }
                else if (delays == 2)
                {
                    secondDelay.TrySetResult();
                    await resumeSecond.Task.WaitAsync(token);
                }
                else
                {
                    thirdDelay.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
            }
        );
        tailer.SetTimeRange("2026-08-20T10:00:00Z", "2026-08-20T11:00:00Z");
        tailer.Start();
        await firstDelay.Task.WaitAsync(TestContext.Current.CancellationToken);
        resume.SetResult();
        await secondDelay.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Single(client.Searches);
        Assert.True(channel.Reader.TryRead(out var completed));
        Assert.NotNull(completed);
        Assert.Equal("SourceRangeLoaded", completed.GetType().Name);
        tailer.SetTimeRange("2026-08-20T09:00:00Z", "2026-08-20T10:00:00Z");
        resumeSecond.SetResult();
        await thirdDelay.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, client.Searches.Count);
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-20T09:00:00Z"),
            client.Searches[^1].FromInclusive
        );
    }

    private static ElasticHit Hit(string id, string timestamp, IReadOnlyList<JsonElement> sort) =>
        new(id, DateTimeOffset.Parse(timestamp), new Line("ready"), sort);

    private static ElasticConnectionSettings Connection() =>
        new()
        {
            Id = "c1",
            Name = "ops",
            KibanaUrl = "https://kibana/",
            ElasticsearchUrl = "https://elastic/",
            DataViewTitle = "logs-*",
            TimeFieldName = "@timestamp",
            ServerField = "server",
            NamespaceField = "namespace",
            OutputFields = ["message"],
            Views =
            [
                new ElasticViewSettings
                {
                    Id = "v1",
                    DataViewTitle = "logs-*",
                    TimeFieldName = "@timestamp",
                    ServerField = "server",
                    NamespaceField = "namespace",
                    OutputFields = ["message"],
                    Sources =
                    [
                        new ElasticSourceSettings
                        {
                            Id = "s1",
                            ServerValue = "api",
                            NamespaceValue = "prod",
                        },
                    ],
                },
            ],
        };
}
