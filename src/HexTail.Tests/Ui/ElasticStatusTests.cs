using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia.Headless.XUnit;
using HexTail.Application;
using HexTail.Elastic;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.Ui;

public sealed class ElasticStatusTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedConnectionInputsRejectLateTestResults(bool failure)
    {
        var response = new TaskCompletionSource<IReadOnlyList<ElasticDataViewSummary>>();
        var client = new FakeElasticApiClient { DataViewsHandler = () => response.Task };
        await using var owner = new MainWindowViewModel(
            new AppState(new LogSourceService(), new TestPersistence(), elastic: client),
            startPolling: false
        );
        owner.Settings.AddElasticConnectionCommand.Execute().Subscribe();
        var editor = Assert.Single(owner.Settings.ElasticConnections);
        editor.Name = "Ops";
        editor.KibanaUrl = "https://old-kibana.example/";
        editor.ElasticsearchUrl = "https://old-elastic.example/";
        var testing = editor.TestConnectionCommand.Execute().FirstAsync().ToTask();
        editor.ElasticsearchUrl = "https://new-elastic.example/";
        if (failure)
            response.SetException(new IOException("old endpoint unavailable"));
        else
            response.SetResult([new("old-view", "old-logs-*")]);
        await testing;
        Assert.Equal("Not tested", editor.Status);
        Assert.Null(editor.Error);
        Assert.Empty(editor.DataViews);
    }

    [AvaloniaFact]
    public async Task UntestedAndFailedServers_HaveNoSuccessState()
    {
        await using var state = new AppState(
            new LogSourceService(),
            new TestPersistence(),
            elastic: new FakeElasticApiClient
            {
                ElasticsearchError = new InvalidOperationException("Endpoint offline"),
            }
        );
        await using var owner = new MainWindowViewModel(
            state,
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        var editor = new ElasticConnectionEditorViewModel(owner.Settings, "server")
        {
            KibanaUrl = "https://kibana/",
            ElasticsearchUrl = "https://elastic/",
        };
        Assert.Equal("Not tested", editor.Status);
        Assert.False(editor.IsConnected);
        await editor.TestConnectionCommand.Execute().FirstAsync();
        Assert.False(editor.IsConnected);
        Assert.True(editor.IsFailed);
        Assert.Equal("Endpoint offline", editor.Error);
        Assert.Equal("Connection failed", editor.Status);
    }

    [AvaloniaFact]
    public async Task MissingCredential_RemainsVisibleAfterOpenCheckboxReverts()
    {
        var connection = new ElasticConnectionSettings
        {
            Id = "ops",
            Name = "Ops",
            AuthMode = ElasticAuthMode.ApiKey,
            KibanaUrl = "https://kibana/",
            ElasticsearchUrl = "https://elastic/",
            Views =
            [
                new ElasticViewSettings
                {
                    Id = "view",
                    Name = "Logs",
                    DataViewId = "logs",
                    DataViewTitle = "logs-*",
                    TimeFieldName = "@timestamp",
                    ServerField = "server",
                    OutputFields = ["message"],
                    Sources = [new ElasticSourceSettings { Id = "source", ServerValue = "api" }],
                },
            ],
        };
        await using var state = new AppState(
            new LogSourceService(),
            new TestPersistence(),
            new AppSettings { ElasticConnections = [connection] },
            credentials: new InMemoryCredentialVault()
        );
        await using var owner = new MainWindowViewModel(
            state,
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        var source = new ElasticSourceOptionViewModel(owner, "source", "Ops", "Ops / Logs");
        source.IsOpen = true;
        Assert.False(source.IsOpen);
        var message = source.OpenError;
        Assert.Contains("credential", message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
