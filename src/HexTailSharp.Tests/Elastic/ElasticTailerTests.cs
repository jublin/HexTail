using System.Text.Json;
using System.Threading.Channels;
using HexTailSharp.Domain;
using HexTailSharp.Elastic;
using HexTailSharp.Persistence;
using HexTailSharp.Tailing;
using HexTailSharp.Tests.Support;

namespace HexTailSharp.Tests.Elastic;

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

        await tailer.PollOnceAsync(CancellationToken.None);

        var lines = Assert.IsType<SourceLines>(await channel.Reader.ReadAsync());
        Assert.Equal(10_000, lines.Lines.Count);
        Assert.Equal("line-2", lines.Lines[0].Raw);
        Assert.Equal("line-10001", lines.Lines[^1].Raw);
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
