using System.Reactive.Concurrency;
using System.Reactive.Linq;
using HexTailSharp.Application;
using HexTailSharp.Persistence;
using HexTailSharp.Tailing;
using HexTailSharp.Tests.Support;
using HexTailSharp.ViewModels;
using ReactiveUI.Reactive.Builder;

namespace HexTailSharp.Tests.ViewModels;

public sealed class ElasticSettingsViewModelTests
{
    [Fact]
    public async Task ChangingAuthModeNotifiesCredentialVisibility()
    {
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
        var state = new AppState(new LogSourceService(), new TestPersistence());
        await using var owner = new MainWindowViewModel(
            state,
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        owner.Settings.AddElasticConnectionCommand.Execute().Subscribe();
        var editor = Assert.Single(owner.Settings.ElasticConnections);
        var changed = new List<string>();
        editor.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);

        editor.AuthMode = ElasticAuthMode.Basic;

        Assert.Contains(nameof(editor.IsBasic), changed);
        Assert.Contains(nameof(editor.IsAuthenticated), changed);
    }

    [Fact]
    public async Task TestConnection_PopulatesDataViewsAndReportsConnected()
    {
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
        var client = new FakeElasticApiClient { DataViews = [new("view-1", "logs-*")] };
        var state = new AppState(new LogSourceService(), new TestPersistence(), elastic: client);
        await using var owner = new MainWindowViewModel(
            state,
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        owner.Settings.AddElasticConnectionCommand.Execute().Subscribe();
        var editor = Assert.Single(owner.Settings.ElasticConnections);
        editor.AddViewCommand.Execute().Subscribe();
        var view = Assert.Single(editor.Views);

        await editor.TestConnectionCommand.Execute().FirstAsync();

        Assert.Equal("Connected (1 data view)", editor.Status);
        Assert.Equal("logs-*", Assert.Single(view.DataViews).Title);
    }

    [Fact]
    public async Task TestConnection_FailsWhenElasticsearchUrlIsInvalid()
    {
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
        var client = new FakeElasticApiClient
        {
            DataViews = [new("view-1", "logs-*")],
            ElasticsearchError = new UriFormatException("Invalid Elasticsearch URL."),
        };
        var state = new AppState(new LogSourceService(), new TestPersistence(), elastic: client);
        await using var owner = new MainWindowViewModel(
            state,
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        owner.Settings.AddElasticConnectionCommand.Execute().Subscribe();
        var editor = Assert.Single(owner.Settings.ElasticConnections);
        editor.ElasticsearchUrl = "not-a-url";

        await editor.TestConnectionCommand.Execute().FirstAsync();

        Assert.Equal("Connection failed", editor.Status);
    }

    [Fact]
    public async Task TestConnection_PreservesConfiguredViewSettings()
    {
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
        var connection = new ElasticConnectionSettings
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
                    DataViewId = "view-1",
                    DataViewTitle = "logs-*",
                    TimeFieldName = "@timestamp",
                    ServerField = "server.name",
                    NamespaceField = "service.name",
                    OutputFields = ["message"],
                },
            ],
        };
        var client = new FakeElasticApiClient
        {
            DataViews = [new("view-1", "logs-*"), new("view-2", "other-*")],
        };
        var state = new AppState(
            new LogSourceService(),
            new TestPersistence(),
            new AppSettings { ElasticConnections = [connection] },
            new InMemoryCredentialVault(),
            client
        );
        await using var owner = new MainWindowViewModel(
            state,
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        owner.Settings.Sync(state.Settings);
        var editor = Assert.Single(owner.Settings.ElasticConnections);
        var view = Assert.Single(editor.Views);

        await editor.TestConnectionCommand.Execute().FirstAsync();

        Assert.Equal("view-1", view.SelectedDataViewId);
        Assert.Equal("server.name", view.ServerField);
        Assert.Equal("service.name", view.NamespaceField);
        Assert.Equal(["message"], view.ToSettings().OutputFields);
    }

    [Fact]
    public async Task TestConnection_DoesNotRefreshViewsWhenServerUrlsAreUnchanged()
    {
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
        var connection = new ElasticConnectionSettings
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
                    DataViewId = "view-1",
                    DataViewTitle = "logs-*",
                    TimeFieldName = "@timestamp",
                    ServerField = "ident",
                    OutputFields = ["message"],
                },
            ],
        };
        var client = new FakeElasticApiClient { DataViews = [new("view-2", "other-*")] };
        var state = new AppState(
            new LogSourceService(),
            new TestPersistence(),
            new AppSettings { ElasticConnections = [connection] },
            new InMemoryCredentialVault(),
            client
        );
        await using var owner = new MainWindowViewModel(
            state,
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        owner.Settings.Sync(state.Settings);
        var editor = Assert.Single(owner.Settings.ElasticConnections);
        var view = Assert.Single(editor.Views);

        editor.Name = "renamed";
        editor.AuthMode = ElasticAuthMode.ApiKey;
        editor.Secret = "typed-api-key";
        await editor.TestConnectionCommand.Execute().FirstAsync();

        Assert.Equal(["view-1"], editor.DataViews.Select(dataView => dataView.Id));
        Assert.Equal("view-1", view.SelectedDataViewId);
        Assert.Equal("ident", view.ServerField);
        Assert.Equal(["message"], view.ToSettings().OutputFields);
    }

    [Fact]
    public async Task ServerTestConnection_UsesSavedApiKeyForNewView()
    {
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
        var connection = new ElasticConnectionSettings
        {
            Id = "c1",
            Name = "ops",
            KibanaUrl = "https://kibana/",
            ElasticsearchUrl = "https://elastic/",
            AuthMode = ElasticAuthMode.ApiKey,
            Views =
            [
                new ElasticViewSettings
                {
                    Id = "v1",
                    Name = "Logs",
                    DataViewId = "view-1",
                    DataViewTitle = "logs-*",
                    TimeFieldName = "@timestamp",
                    ServerField = "server",
                    NamespaceField = "namespace",
                    OutputFields = ["message"],
                },
            ],
        };
        var client = new FakeElasticApiClient { DataViews = [new("view-1", "logs-*")] };
        var vault = new InMemoryCredentialVault();
        vault.Set(connection.Id, "saved-api-key");
        var state = new AppState(
            new LogSourceService(),
            new TestPersistence(),
            new AppSettings { ElasticConnections = [connection] },
            vault,
            client
        );
        await using var owner = new MainWindowViewModel(
            state,
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        owner.Settings.Sync(state.Settings);
        var editor = Assert.Single(owner.Settings.ElasticConnections);
        editor.AddViewCommand.Execute().Subscribe();

        await editor.TestConnectionCommand.Execute().FirstAsync();

        Assert.Equal("saved-api-key", Assert.Single(client.DataViewSecrets));
        Assert.Equal("logs-*", Assert.Single(editor.Views[^1].DataViews).Title);
    }

    [Fact]
    public async Task FieldSearch_UsesLatestQuery()
    {
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
        var state = new AppState(new LogSourceService(), new TestPersistence());
        await using var owner = new MainWindowViewModel(
            state,
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        owner.Settings.AddElasticConnectionCommand.Execute().Subscribe();
        var editor = Assert.Single(owner.Settings.ElasticConnections);
        editor.AddViewCommand.Execute().Subscribe();
        var view = Assert.Single(editor.Views);
        view.Sync(
            new ElasticViewSettings
            {
                Id = view.Id,
                Name = "Logs",
                OutputFields = ["service.name", "message", "status.code"],
            }
        );

        view.OutputFieldQuery = "status";

        var fields = view.Fields.Select(field => (field, field.Name, field.IsOutput)).ToArray();
        Assert.Equal(
            "status.code",
            Assert
                .Single(ElasticViewEditorViewModel.FilterFields(fields, view.OutputFieldQuery))
                .Name
        );
    }

    [Fact]
    public async Task StateSync_DoesNotClearUnsavedElasticEditorValues()
    {
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
        var state = new AppState(new LogSourceService(), new TestPersistence());
        await using var owner = new MainWindowViewModel(
            state,
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        var connection = new ElasticConnectionSettings
        {
            Id = "c1",
            Name = "ops",
            KibanaUrl = "https://kibana/",
            ElasticsearchUrl = "https://elastic/",
            AuthMode = ElasticAuthMode.ApiKey,
            Views =
            [
                new ElasticViewSettings
                {
                    Id = "v1",
                    Name = "Logs",
                    DataViewId = "view-1",
                    DataViewTitle = "logs-*",
                    ServerField = "ident",
                    NamespaceField = "service.name",
                },
            ],
        };
        owner.Settings.Sync(new AppSettings { ElasticConnections = [connection] });
        var editor = Assert.Single(owner.Settings.ElasticConnections);
        var view = Assert.Single(editor.Views);
        editor.Secret = "typed-api-key";
        view.ServerField = "updated.ident";

        owner.Settings.Sync(new AppSettings { ElasticConnections = [connection] });

        Assert.Equal(ElasticAuthMode.ApiKey, editor.AuthMode);
        Assert.Equal("typed-api-key", editor.Secret);
        Assert.Equal("updated.ident", view.ServerField);
        Assert.Equal("view-1", view.SelectedDataViewId);
        Assert.Equal("logs-*", Assert.Single(view.DataViews).Title);
    }

    [Fact]
    public async Task StateSync_PreservesUnsavedElasticConnectionDraft()
    {
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
        var state = new AppState(new LogSourceService(), new TestPersistence());
        await using var owner = new MainWindowViewModel(
            state,
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        owner.Settings.AddElasticConnectionCommand.Execute().Subscribe();
        var editor = Assert.Single(owner.Settings.ElasticConnections);
        editor.AddViewCommand.Execute().Subscribe();

        owner.Settings.Sync(new AppSettings());

        Assert.Same(editor, Assert.Single(owner.Settings.ElasticConnections));
        Assert.Single(editor.Views);
    }

    [Fact]
    public async Task DataViewReload_PreservesSelectedOutputFields()
    {
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
        var connection = new ElasticConnectionSettings
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
                    DataViewId = "view-1",
                    DataViewTitle = "logs-*",
                    TimeFieldName = "@timestamp",
                    OutputFields = ["message"],
                },
            ],
        };
        var client = new FakeElasticApiClient { DataViewFields = [new("message", "text", true)] };
        var state = new AppState(
            new LogSourceService(),
            new TestPersistence(),
            new AppSettings { ElasticConnections = [connection] },
            new InMemoryCredentialVault(),
            client
        );
        await using var owner = new MainWindowViewModel(
            state,
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        owner.Settings.Sync(state.Settings);
        var view = Assert.Single(Assert.Single(owner.Settings.ElasticConnections).Views);

        view.SelectedDataViewId = null;
        view.SelectedDataViewId = "view-1";

        Assert.True(Assert.Single(view.Fields).IsOutput);
        Assert.Equal(["message"], view.ToSettings().OutputFields);
    }

    [Fact]
    public async Task Settings_ExposesElasticSectionAndManualSourceEditors()
    {
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
        var state = new AppState(new LogSourceService(), new TestPersistence());
        await using var owner = new MainWindowViewModel(
            state,
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );

        owner.Settings.SectionIndex = 2;
        owner.Settings.AddElasticConnectionCommand.Execute().Subscribe();
        var editor = Assert.Single(owner.Settings.ElasticConnections);
        editor.AddViewCommand.Execute().Subscribe();

        Assert.Equal("elastic", owner.Settings.Section);
        Assert.Matches("^[0-9a-f]{32}$", Assert.Single(Assert.Single(editor.Views).Sources).Id);
    }
}
