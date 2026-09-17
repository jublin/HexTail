using HexTail.Elastic;
using HexTail.Persistence;
using HexTail.Tests.Support;

namespace HexTail.Tests.Elastic;

public sealed class ElasticHealthMonitorTests
{
    [Fact]
    public async Task CheckOnce_PublishesConnectedAndUnauthorizedStatuses()
    {
        var client = new FakeElasticApiClient();
        var connection = Connection();
        await using var monitor = new ElasticHealthMonitor(client, new InMemoryCredentialVault());

        await monitor.CheckOnceAsync(new AppSettings { ElasticConnections = [connection] });
        Assert.Equal(ElasticConnectionStatus.Connected, monitor.Statuses["s1"].Status);

        client.DataViewError = new ElasticUnauthorizedException(401, "unauthorized");
        await monitor.CheckOnceAsync(new AppSettings { ElasticConnections = [connection] });
        Assert.Equal(ElasticConnectionStatus.Unauthorized, monitor.Statuses["s1"].Status);
    }

    [Fact]
    public async Task CheckOnce_MarksIncompleteConnectionsMisconfigured()
    {
        var connection = Connection() with
        {
            Views = [Connection().Views[0] with { DataViewId = null }],
        };
        await using var monitor = new ElasticHealthMonitor(
            new FakeElasticApiClient(),
            new InMemoryCredentialVault()
        );

        await monitor.CheckOnceAsync(new AppSettings { ElasticConnections = [connection] });

        Assert.Equal(ElasticConnectionStatus.Misconfigured, monitor.Statuses["s1"].Status);
    }

    private static ElasticConnectionSettings Connection() =>
        new()
        {
            Id = "c1",
            Name = "ops",
            KibanaUrl = "https://kibana/",
            ElasticsearchUrl = "https://elastic/",
            Views =
            [
                new ElasticViewSettings
                {
                    Id = "v1",
                    Name = "Logs",
                    DataViewId = "view",
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
