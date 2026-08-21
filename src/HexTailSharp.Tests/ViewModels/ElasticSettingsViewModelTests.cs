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

        await editor.TestConnectionCommand.Execute().FirstAsync();

        Assert.Equal("Connected (1 data view)", editor.Status);
        Assert.Equal("logs-*", Assert.Single(editor.DataViews).Title);
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
            ServerField = "ident",
            NamespaceField = "service.name",
        };
        owner.Settings.Sync(new AppSettings { ElasticConnections = [connection] });
        var editor = Assert.Single(owner.Settings.ElasticConnections);
        editor.Secret = "typed-api-key";
        editor.ServerField = "updated.ident";

        owner.Settings.Sync(new AppSettings { ElasticConnections = [connection] });

        Assert.Equal(ElasticAuthMode.ApiKey, editor.AuthMode);
        Assert.Equal("typed-api-key", editor.Secret);
        Assert.Equal("updated.ident", editor.ServerField);
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

        owner.Settings.SectionIndex = 4;
        owner.Settings.AddElasticConnectionCommand.Execute().Subscribe();
        var editor = Assert.Single(owner.Settings.ElasticConnections);
        editor.AddSourceCommand.Execute().Subscribe();

        Assert.Equal("elastic", owner.Settings.Section);
        Assert.Matches("^[0-9a-f]{32}$", Assert.Single(editor.Sources).Id);
    }
}
