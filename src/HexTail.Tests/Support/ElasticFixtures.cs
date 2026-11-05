using System.Reactive.Concurrency;
using HexTail.Application;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.ViewModels;

namespace HexTail.Tests.Support;

internal static class ElasticFixtures
{
    public static ElasticConnectionSettings Connection(
        string id,
        string name,
        params string[] values
    ) =>
        new()
        {
            Id = id,
            Name = name,
            KibanaUrl = "https://kibana/",
            ElasticsearchUrl = "https://elastic/",
            Views =
            [
                new ElasticViewSettings
                {
                    Id = $"{id}-view",
                    Name = "Logs",
                    DataViewTitle = "logs-*",
                    TimeFieldName = "@timestamp",
                    ServerField = "service.name",
                    OutputFields = ["message"],
                    Sources =
                    [
                        .. values.Select(value => new ElasticSourceSettings
                        {
                            Id = $"{id}-{value}",
                            ServerValue = value,
                        }),
                    ],
                },
            ],
        };

    public static TestPersistence Persistence(params ElasticConnectionSettings[] connections) =>
        new(new AppConfig { Settings = new AppSettings { ElasticConnections = [.. connections] } });

    public static MainWindowViewModel Workspace(params ElasticConnectionSettings[] connections) =>
        Workspace(Persistence(connections));

    public static MainWindowViewModel Workspace(TestPersistence persistence) =>
        new(
            new AppState(
                new LogSourceService(),
                persistence,
                credentials: new InMemoryCredentialVault(),
                elastic: new FakeElasticApiClient()
            ),
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
}
