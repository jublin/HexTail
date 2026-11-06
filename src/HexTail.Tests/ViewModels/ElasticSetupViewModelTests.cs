using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia.Headless.XUnit;
using HexTail.Application;
using HexTail.Elastic;
using HexTail.Persistence;
using HexTail.Security;
using HexTail.Tailing;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.ViewModels;

public sealed class ElasticSetupViewModelTests
{
    private sealed class Harness(
        MainWindowViewModel owner,
        FakeElasticApiClient client,
        IAppPersistence persistence
    ) : IAsyncDisposable
    {
        public MainWindowViewModel Owner => owner;
        public ElasticSetupViewModel Setup => owner.ElasticSetup;
        public FakeElasticApiClient Client => client;
        public TestPersistence Persistence => (TestPersistence)persistence;

        public ElasticConnectionSettings SavedConnection =>
            Persistence.Config!.Settings.ElasticConnections.Single();

        public async Task ConnectAsync()
        {
            Setup.Connection!.Name = "Ops";
            Setup.Connection.KibanaUrl = "https://kibana.example/";
            Setup.Connection.ElasticsearchUrl = "https://elastic.example/";
            await Setup.Connection.TestConnectionCommand.Execute();
        }

        public async Task NextAsync() => await Setup.NextCommand.Execute();

        public async Task ResolveDataViewAsync()
        {
            Setup.View!.SelectedDataViewId = "logs";
            await WaitForAsync(() => Setup.View.IsSelectionResolved);
        }

        public void FillFilter()
        {
            Setup.View!.ServerField = "service";
            Setup.View.AddFilterValue("api");
            Setup.View.AddFilterValue("web");
        }

        public void ChooseColumns()
        {
            Setup.View!.Fields.Single(field => field.Name == "message").IsOutput = true;
            Setup.View.Fields.Single(field => field.Name == "@timestamp").IsOutput = true;
        }

        public async Task WalkToFinishAsync()
        {
            Setup.StartNew();
            await ConnectAsync();
            await NextAsync();
            await ResolveDataViewAsync();
            await NextAsync();
            FillFilter();
            await NextAsync();
            ChooseColumns();
            await NextAsync();
        }

        public ValueTask DisposeAsync() => owner.DisposeAsync();
    }

    private static Task<Harness> CreateAsync(
        TestPersistence? persistence = null,
        params ElasticConnectionSettings[] saved
    ) => BuildAsync(persistence, new InMemoryCredentialVault(), saved);

    private static async Task<Harness> BuildAsync(
        TestPersistence? persistence,
        ICredentialVault vault,
        params ElasticConnectionSettings[] saved
    )
    {
        var client = new FakeElasticApiClient
        {
            DataViews = [new("logs", "logs-*"), new("metrics", "metrics-*")],
            DataViewFields =
            [
                new("@timestamp", "date", true),
                new("message", "text", true),
                new("service", "keyword", true),
                new("bytes", "long", false),
            ],
        };
        client.DataViewHandler = id =>
            Task.FromResult(
                new ElasticDataView(
                    id,
                    id == "logs" ? "logs-*" : "metrics-*",
                    "@timestamp",
                    client.DataViewFields
                )
            );
        persistence ??= ElasticFixtures.Persistence(saved);
        var owner = new MainWindowViewModel(
            new AppState(new LogSourceService(), persistence, credentials: vault, elastic: client),
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        await owner.InitializeAsync();
        return new Harness(owner, client, persistence);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 300 && !condition(); attempt++)
            await Task.Delay(10);
    }

    [AvaloniaFact]
    public async Task StartNew_OpensAtConnectionAndWaitsForAPassingTest()
    {
        await using var harness = await CreateAsync();
        var setup = harness.Setup;

        setup.StartNew();

        Assert.True(setup.IsOpen);
        Assert.Equal(ElasticSetupStep.Connection, setup.Step);
        Assert.Same(setup.Connection, Assert.Single(harness.Owner.Settings.ElasticConnections));
        Assert.Null(setup.View);
        Assert.Equal(5, setup.Steps.Count);
        Assert.True(setup.Steps[0].IsCurrent);
        Assert.All(setup.Steps.Skip(1), step => Assert.False(step.IsReachable));
        Assert.Equal(
            ["Connection", "Data view", "Filter", "Columns", "Name & open"],
            setup.Steps.Select(step => step.Title)
        );
        Assert.Equal([1, 2, 3, 4, 5], setup.Steps.Select(step => step.Number));
        Assert.False(setup.CanGoBack);
        Assert.False(setup.CanGoNext);
        Assert.False(await setup.NextCommand.CanExecute.FirstAsync());
    }

    [AvaloniaFact]
    public async Task Connection_NextSavesTheConnectionWithoutViewsAndAddsAFreshView()
    {
        await using var harness = await CreateAsync();
        harness.Setup.StartNew();

        await harness.ConnectAsync();

        Assert.True(harness.Setup.CanGoNext);
        Assert.True(await harness.Setup.NextCommand.CanExecute.FirstAsync());

        await harness.NextAsync();

        Assert.Empty(harness.SavedConnection.Views);
        Assert.Equal(ElasticSetupStep.DataView, harness.Setup.Step);
        Assert.NotNull(harness.Setup.View);
        Assert.Contains(harness.Setup.View, harness.Setup.Connection!.Views);
        Assert.Equal(string.Empty, harness.Setup.View.Name);
        Assert.True(harness.Setup.Steps[0].IsDone);
        Assert.True(harness.Setup.Steps[1].IsCurrent);
        Assert.True(harness.Setup.CanGoBack);
        Assert.False(harness.Setup.CanGoNext);
    }

    [AvaloniaFact]
    public async Task Connection_EditingAfterAPassingTestBlocksNextAgain()
    {
        await using var harness = await CreateAsync();
        harness.Setup.StartNew();
        await harness.ConnectAsync();
        Assert.True(harness.Setup.CanGoNext);

        harness.Setup.Connection!.ElasticsearchUrl = "https://other.example/";

        Assert.False(harness.Setup.CanGoNext);
        Assert.False(await harness.Setup.NextCommand.CanExecute.FirstAsync());
    }

    [AvaloniaFact]
    public async Task Steps_GateNextOnTheirOwnPrerequisites()
    {
        await using var harness = await CreateAsync();
        harness.Setup.StartNew();
        await harness.ConnectAsync();
        await harness.NextAsync();
        var setup = harness.Setup;

        // Data view: needs a resolved selection.
        Assert.False(setup.CanGoNext);
        await harness.ResolveDataViewAsync();
        Assert.True(setup.CanGoNext);
        await harness.NextAsync();

        // Filter: field and every chip valid.
        Assert.Equal(ElasticSetupStep.Filter, setup.Step);
        Assert.False(setup.CanGoNext);
        setup.View!.ServerField = "service";
        Assert.False(setup.CanGoNext);
        setup.View.AddFilterValue("api");
        Assert.True(setup.CanGoNext);
        setup.View.ServerField = null;
        Assert.False(setup.CanGoNext);
        setup.View.ServerField = "service";
        Assert.True(setup.CanGoNext);
        await harness.NextAsync();

        // Columns: at least one output field.
        Assert.Equal(ElasticSetupStep.Columns, setup.Step);
        Assert.False(setup.CanGoNext);
        var message = setup.View.Fields.Single(field => field.Name == "message");
        message.IsOutput = true;
        Assert.True(setup.CanGoNext);
        message.IsOutput = false;
        Assert.False(setup.CanGoNext);
        message.IsOutput = true;
        await harness.NextAsync();

        // Finish: the view can be saved, with the name suggested.
        Assert.Equal(ElasticSetupStep.Finish, setup.Step);
        Assert.Equal("logs-* · api", setup.View.Name);
        Assert.True(setup.CanGoNext);
        setup.View.Name = " ";
        Assert.False(setup.CanGoNext);
    }

    [AvaloniaFact]
    public async Task Finish_SavesTheFullConnectionAndOpensEverySource()
    {
        await using var harness = await CreateAsync();
        await harness.WalkToFinishAsync();
        var setup = harness.Setup;
        Assert.Equal(ElasticSetupStep.Finish, setup.Step);
        Assert.True(setup.OpenNow);
        Assert.Equal("Save & open", setup.NextLabel);

        await harness.NextAsync();

        var view = Assert.Single(harness.SavedConnection.Views);
        Assert.Equal("logs-* · api", view.Name);
        Assert.Equal("logs-*", view.DataViewTitle);
        Assert.Equal("service", view.ServerField);
        Assert.Equal(["api", "web"], view.Sources.Select(source => source.ServerValue));
        Assert.Equal(["message", "@timestamp"], view.OutputFields);
        Assert.Equal(
            view.Sources.Select(source => source.Id).Order(),
            harness.Owner.State.Files.Select(file => file.Id).Order()
        );
        Assert.False(setup.IsOpen);
        Assert.False(harness.Owner.Settings.CloseConfirmationVisible);
        Assert.Null(setup.Error);
    }

    [AvaloniaFact]
    public async Task Finish_KeepsTheChosenColumnOrder()
    {
        await using var harness = await CreateAsync();
        harness.Setup.StartNew();
        await harness.ConnectAsync();
        await harness.NextAsync();
        await harness.ResolveDataViewAsync();
        await harness.NextAsync();
        harness.FillFilter();
        await harness.NextAsync();
        harness.Setup.View!.Fields.Single(field => field.Name == "service").IsOutput = true;
        harness.Setup.View.Fields.Single(field => field.Name == "message").IsOutput = true;
        harness.Setup.View.Fields.Single(field => field.Name == "@timestamp").IsOutput = true;
        harness.Setup.View.MoveOutputField(2, 0);
        var expected = harness.Setup.View.SelectedOutputFields.ToArray();
        await harness.NextAsync();

        await harness.NextAsync();

        Assert.Equal(expected, harness.SavedConnection.Views.Single().OutputFields);
    }

    [AvaloniaFact]
    public async Task Finish_WithOpenNowOffSavesWithoutOpeningTabs()
    {
        await using var harness = await CreateAsync();
        await harness.WalkToFinishAsync();
        harness.Setup.OpenNow = false;
        Assert.Equal("Save", harness.Setup.NextLabel);

        await harness.NextAsync();

        Assert.Single(harness.SavedConnection.Views);
        Assert.Empty(harness.Owner.State.Files);
        Assert.False(harness.Setup.IsOpen);
    }

    [AvaloniaFact]
    public async Task NextLabel_IsNextUntilTheLastStep()
    {
        await using var harness = await CreateAsync();
        harness.Setup.StartNew();
        Assert.Equal("Next", harness.Setup.NextLabel);
        await harness.ConnectAsync();
        await harness.NextAsync();
        Assert.Equal("Next", harness.Setup.NextLabel);
        await harness.ResolveDataViewAsync();
        await harness.NextAsync();
        harness.FillFilter();
        await harness.NextAsync();
        harness.ChooseColumns();
        Assert.Equal("Next", harness.Setup.NextLabel);
        await harness.NextAsync();

        Assert.Equal("Save & open", harness.Setup.NextLabel);
        harness.Setup.OpenNow = false;
        Assert.Equal("Save", harness.Setup.NextLabel);
        harness.Setup.OpenNow = true;
        Assert.Equal("Save & open", harness.Setup.NextLabel);
    }

    [AvaloniaFact]
    public async Task Back_KeepsEnteredStateAndGoToStepOnlyJumpsToReachableSteps()
    {
        await using var harness = await CreateAsync();
        harness.Setup.StartNew();
        await harness.ConnectAsync();
        await harness.NextAsync();
        await harness.ResolveDataViewAsync();
        await harness.NextAsync();
        harness.FillFilter();
        var setup = harness.Setup;
        var view = setup.View;
        Assert.Equal(ElasticSetupStep.Filter, setup.Step);

        // Unreachable: Finish was never visited and is not the next step.
        Assert.False(setup.Steps[4].IsReachable);
        await setup.GoToStepCommand.Execute(setup.Steps[4]);
        Assert.Equal(ElasticSetupStep.Filter, setup.Step);

        await setup.BackCommand.Execute();
        Assert.Equal(ElasticSetupStep.DataView, setup.Step);
        Assert.Same(view, setup.View);
        Assert.Equal("logs", setup.View!.SelectedDataViewId);
        Assert.Equal("service", setup.View.ServerField);
        Assert.Equal(2, setup.View.Sources.Count);

        // Filter was visited, so it stays reachable and is valid again.
        Assert.True(setup.Steps[2].IsReachable);
        await setup.GoToStepCommand.Execute(setup.Steps[2]);
        Assert.Equal(ElasticSetupStep.Filter, setup.Step);

        await setup.GoToStepCommand.Execute(setup.Steps[0]);
        Assert.Equal(ElasticSetupStep.Connection, setup.Step);
        Assert.False(setup.CanGoBack);
        Assert.True(setup.CanGoNext);
    }

    [AvaloniaFact]
    public async Task Close_MidFlowKeepsTheSavedConnectionAndDropsTheUnfinishedView()
    {
        await using var harness = await CreateAsync();
        harness.Setup.StartNew();
        await harness.ConnectAsync();
        await harness.NextAsync();
        var connection = harness.Setup.Connection!;
        Assert.Single(connection.Views);

        harness.Setup.Close();

        Assert.False(harness.Setup.IsOpen);
        Assert.Null(harness.Setup.Error);
        Assert.False(harness.Owner.Settings.CloseConfirmationVisible);
        Assert.Empty(connection.Views);
        Assert.Contains(connection, harness.Owner.Settings.ElasticConnections);
        Assert.Equal("Ops", harness.SavedConnection.Name);
        Assert.Empty(harness.SavedConnection.Views);
    }

    [AvaloniaFact]
    public async Task Close_BeforeTheFirstSaveRemovesTheNewConnectionEditor()
    {
        await using var harness = await CreateAsync();
        harness.Setup.StartNew();
        harness.Setup.Connection!.Name = "Draft";

        harness.Setup.Close();

        Assert.False(harness.Setup.IsOpen);
        Assert.Empty(harness.Owner.Settings.ElasticConnections);
        Assert.False(harness.Owner.Settings.HasElasticDrafts);
        Assert.Empty(harness.Persistence.Config!.Settings.ElasticConnections);
    }

    [AvaloniaFact]
    public async Task EditView_StartsAtFilterBoundToTheSavedView()
    {
        var saved = ElasticFixtures.Connection("c1", "Ops", "api");
        await using var harness = await CreateAsync(null, saved);

        harness.Setup.EditView("c1", "c1-view");

        Assert.True(harness.Setup.IsOpen);
        Assert.Equal(ElasticSetupStep.Filter, harness.Setup.Step);
        Assert.Equal("c1-view", harness.Setup.View!.Id);
        Assert.Same(
            Assert.Single(harness.Owner.Settings.ElasticConnections),
            harness.Setup.Connection
        );
        Assert.Same(harness.Setup.Connection!.Views.Single(), harness.Setup.View);
        Assert.True(harness.Setup.CanGoNext);
        Assert.True(harness.Setup.Steps[1].IsReachable);
        Assert.True(harness.Setup.Steps[2].IsCurrent);
    }

    [AvaloniaFact]
    public async Task EditConnection_StartsAtConnectionUntilRetested()
    {
        var saved = ElasticFixtures.Connection("c1", "Ops", "api");
        await using var harness = await CreateAsync(null, saved);

        harness.Setup.EditConnection("c1");

        Assert.Equal(ElasticSetupStep.Connection, harness.Setup.Step);
        Assert.False(harness.Setup.Connection!.EndpointsOk);
        Assert.False(harness.Setup.CanGoNext);

        await harness.Setup.Connection.TestConnectionCommand.Execute();

        Assert.True(harness.Setup.CanGoNext);
        await harness.NextAsync();

        // The saved view is kept and the flow continues on the data view step with it.
        Assert.Equal(ElasticSetupStep.DataView, harness.Setup.Step);
        Assert.Equal("c1-view", harness.Setup.View!.Id);
        Assert.Single(harness.SavedConnection.Views);
    }

    [AvaloniaFact]
    public async Task AddSource_StartsAtDataViewWithAFreshViewAndKeepsTheOthersSaved()
    {
        var saved = ElasticFixtures.Connection("c1", "Ops", "api");
        await using var harness = await CreateAsync(null, saved);

        harness.Setup.AddSource("c1");
        var connection = harness.Setup.Connection!;

        Assert.Equal(ElasticSetupStep.DataView, harness.Setup.Step);
        Assert.NotEqual("c1-view", harness.Setup.View!.Id);
        Assert.Equal(2, harness.Setup.Connection!.Views.Count);
        Assert.Single(harness.SavedConnection.Views);
        Assert.Equal("c1-view", harness.SavedConnection.Views[0].Id);
        Assert.True(harness.Setup.Steps[0].IsReachable);
        Assert.False(harness.Setup.CanGoNext);

        harness.Setup.Close();

        Assert.Single(connection.Views);
        Assert.Equal("c1-view", connection.Views[0].Id);
        Assert.Single(harness.SavedConnection.Views);
    }

    [AvaloniaFact]
    public async Task SaveFailure_LandsInErrorKeepsTheStepAndDoesNotThrow()
    {
        var persistence = new TestPersistence();
        await using var harness = await CreateAsync(persistence);
        harness.Setup.StartNew();
        await harness.ConnectAsync();
        persistence.SaveError = new IOException("disk is full");

        await harness.NextAsync();

        Assert.Equal(ElasticSetupStep.Connection, harness.Setup.Step);
        Assert.Contains("disk is full", harness.Setup.Error);
        Assert.False(harness.Setup.IsBusy);
        Assert.True(harness.Setup.IsOpen);

        persistence.SaveError = null;
        await harness.NextAsync();

        Assert.Null(harness.Setup.Error);
        Assert.Equal(ElasticSetupStep.DataView, harness.Setup.Step);
    }

    [AvaloniaFact]
    public async Task FinishSaveFailure_KeepsTheFlowOpenAndOpensNothing()
    {
        var persistence = new TestPersistence();
        await using var harness = await CreateAsync(persistence);
        await harness.WalkToFinishAsync();
        persistence.SaveError = new IOException("disk is full");

        await harness.NextAsync();

        Assert.True(harness.Setup.IsOpen);
        Assert.Equal(ElasticSetupStep.Finish, harness.Setup.Step);
        Assert.Contains("disk is full", harness.Setup.Error);
        Assert.Empty(harness.Owner.State.Files);
        persistence.SaveError = null; // so disposing the workspace can save its state
    }

    [AvaloniaFact]
    public async Task StartingAgain_DoesNotLeakStateFromThePreviousFlow()
    {
        await using var harness = await CreateAsync();
        await harness.WalkToFinishAsync();
        harness.Setup.OpenNow = false;
        await harness.NextAsync();

        harness.Setup.StartNew();

        Assert.Equal(ElasticSetupStep.Connection, harness.Setup.Step);
        Assert.True(harness.Setup.OpenNow);
        Assert.Null(harness.Setup.Error);
        Assert.Null(harness.Setup.View);
        Assert.Equal(string.Empty, harness.Setup.Connection!.KibanaUrl);
        Assert.False(harness.Setup.CanGoNext);
        Assert.Equal(2, harness.Owner.Settings.ElasticConnections.Count);
        Assert.True(harness.Setup.Steps[0].IsCurrent);
        Assert.All(harness.Setup.Steps.Skip(1), step => Assert.False(step.IsReachable));
    }

    private sealed class GatedPersistence : IAppPersistence
    {
        private TaskCompletionSource? _gate;
        public AppConfig? Config { get; private set; } = new();
        public int SaveCount { get; private set; }

        public void HoldNextSave() =>
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate?.TrySetResult();

        public ValueTask<AppConfig?> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Config);

        public async ValueTask SaveAsync(
            AppConfig config,
            CancellationToken cancellationToken = default
        )
        {
            var gate = _gate;
            if (gate is not null)
                await gate.Task;
            SaveCount++;
            Config = config;
        }
    }

    private static async Task<(
        MainWindowViewModel Owner,
        GatedPersistence Persistence
    )> GatedAsync()
    {
        var persistence = new GatedPersistence();
        var owner = new MainWindowViewModel(
            new AppState(
                new LogSourceService(),
                persistence,
                credentials: new InMemoryCredentialVault(),
                elastic: new FakeElasticApiClient { DataViews = [new("logs", "logs-*")] }
            ),
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        await owner.InitializeAsync();
        var setup = owner.ElasticSetup;
        setup.StartNew();
        setup.Connection!.KibanaUrl = "https://kibana.example/";
        setup.Connection.ElasticsearchUrl = "https://elastic.example/";
        await setup.Connection.TestConnectionCommand.Execute();
        return (owner, persistence);
    }

    [AvaloniaFact]
    public async Task Next_IsNotReentrantAndCloseDuringASaveStopsTheFlow()
    {
        var (owner, persistence) = await GatedAsync();
        await using var _ = owner;
        var setup = owner.ElasticSetup;
        var savesBefore = persistence.SaveCount;
        persistence.HoldNextSave();

        var first = setup.NextCommand.Execute().ToTask();
        Assert.True(setup.IsBusy);
        Assert.False(await setup.NextCommand.CanExecute.FirstAsync());
        Assert.False(await setup.BackCommand.CanExecute.FirstAsync());
        // A second press while busy is ignored by NextAsync's guard, so it never saves twice.
        var second = setup.NextCommand.Execute().ToTask();
        setup.Close();
        persistence.Release();
        await first;
        await second;

        Assert.Equal(savesBefore + 1, persistence.SaveCount);
        Assert.False(setup.IsOpen);
        Assert.False(setup.IsBusy);
        Assert.Equal(ElasticSetupStep.Connection, setup.Step);
        Assert.Null(setup.View);
    }

    [AvaloniaFact]
    public async Task CloseCommand_CannotRunWhileASaveIsPending()
    {
        var (owner, persistence) = await GatedAsync();
        await using var _ = owner;
        var setup = owner.ElasticSetup;
        Assert.True(await setup.CloseCommand.CanExecute.FirstAsync());
        persistence.HoldNextSave();

        var first = setup.NextCommand.Execute().ToTask();

        Assert.True(setup.IsBusy);
        Assert.False(await setup.CloseCommand.CanExecute.FirstAsync());
        persistence.Release();
        await first;
        Assert.False(setup.IsBusy);
        Assert.True(setup.IsOpen);
        Assert.True(await setup.CloseCommand.CanExecute.FirstAsync());
        // The buttons come back too once the save is over.
        Assert.True(await setup.BackCommand.CanExecute.FirstAsync());
    }

    [AvaloniaFact]
    public async Task CloseCommand_ClosesTheFlow()
    {
        await using var harness = await CreateAsync();
        harness.Setup.StartNew();

        await harness.Setup.CloseCommand.Execute();

        Assert.False(harness.Setup.IsOpen);
        Assert.Null(harness.Setup.Connection);
        Assert.Empty(harness.Owner.Settings.ElasticConnections);
    }

    [AvaloniaFact]
    public async Task Close_RevertsEditsToASavedConnectionAndItsTypedSecret()
    {
        var vault = new InMemoryCredentialVault();
        var persistence = ElasticFixtures.Persistence(
            ElasticFixtures.Connection("c1", "Ops", "api")
        );
        await using var harness = await BuildAsync(persistence, vault);
        harness.Setup.EditConnection("c1");
        var edited = harness.Setup.Connection!;
        harness.Owner.Settings.SelectedElasticConnection = edited;
        edited.Name = "Abandoned";
        edited.KibanaUrl = "https://abandoned.example/";
        edited.Secret = "typed-secret";
        Assert.True(harness.Owner.Settings.HasElasticDrafts);

        harness.Setup.Close();

        Assert.False(harness.Owner.Settings.HasElasticDrafts);
        Assert.DoesNotContain(edited, harness.Owner.Settings.ElasticConnections);
        Assert.Same(
            harness.Owner.Settings.EditorFor("c1"),
            harness.Owner.Settings.SelectedElasticConnection
        );
        harness.Setup.EditConnection("c1");
        Assert.Equal("Ops", harness.Setup.Connection!.Name);
        Assert.Equal("https://kibana/", harness.Setup.Connection.KibanaUrl);
        Assert.Equal(string.Empty, harness.Setup.Connection.Secret);
        Assert.False(harness.Setup.Connection.IsDirty);
        Assert.Equal("Ops", harness.SavedConnection.Name);
        Assert.Equal("https://kibana/", harness.SavedConnection.KibanaUrl);
        Assert.Equal("c1-view", Assert.Single(harness.SavedConnection.Views).Id);
        Assert.Null(vault.Get("c1"));
    }

    [AvaloniaFact]
    public async Task Close_RevertsEditsToASavedView()
    {
        var saved = ElasticFixtures.Connection("c1", "Ops", "api");
        await using var harness = await CreateAsync(null, saved);
        harness.Setup.EditView("c1", "c1-view");
        harness.Setup.View!.FilterValue = "abandoned";
        Assert.True(harness.Setup.Connection!.IsDirty);

        harness.Setup.Close();

        Assert.False(harness.Owner.Settings.HasElasticDrafts);
        harness.Setup.EditView("c1", "c1-view");
        Assert.Equal("api", harness.Setup.View!.FilterValue);
        Assert.False(harness.Setup.Connection!.IsDirty);
        Assert.Equal("api", harness.SavedConnection.Views.Single().Sources.Single().ServerValue);
    }

    [AvaloniaFact]
    public async Task Close_AfterANewConnectionWasSavedKeepsItAndLeavesNoDrafts()
    {
        await using var harness = await CreateAsync();
        harness.Setup.StartNew();
        await harness.ConnectAsync();
        await harness.NextAsync();
        harness.Setup.Connection!.Name = "Renamed after saving";

        harness.Setup.Close();

        Assert.False(harness.Owner.Settings.HasElasticDrafts);
        var editor = Assert.Single(harness.Owner.Settings.ElasticConnections);
        Assert.Equal("Ops", editor.Name);
        Assert.Equal("Ops", harness.SavedConnection.Name);
    }

    private static ElasticViewEditorViewModel AddIncompleteView(ElasticSetupViewModel setup)
    {
        var view = setup.Connection!.AddNewView(string.Empty);
        view.Name = "Unfinished";
        return view;
    }

    [AvaloniaFact]
    public async Task Finish_SavesOnlyTheFinishedViewNotUnfinishedSiblings()
    {
        var saved = ElasticFixtures.Connection("c1", "Ops", "api");
        await using var harness = await CreateAsync(null, saved);
        harness.Setup.AddSource("c1");
        var unfinished = AddIncompleteView(harness.Setup);
        var finished = harness.Setup.View!;
        await harness.ResolveDataViewAsync();
        await harness.NextAsync();
        harness.FillFilter();
        await harness.NextAsync();
        harness.ChooseColumns();
        await harness.NextAsync();
        Assert.Equal(ElasticSetupStep.Finish, harness.Setup.Step);
        Assert.False(harness.Setup.Connection!.CanSave);
        Assert.True(harness.Setup.CanGoNext);
        harness.Setup.OpenNow = false;

        await harness.NextAsync();

        Assert.Null(harness.Setup.Error);
        Assert.False(harness.Setup.IsOpen);
        Assert.Equal(
            ["c1-view", finished.Id],
            harness.SavedConnection.Views.Select(view => view.Id)
        );
        Assert.DoesNotContain(unfinished.Id, harness.SavedConnection.Views.Select(view => view.Id));
    }

    [AvaloniaFact]
    public async Task Finish_OpenFailureKeepsTheFlowOpenAndARetryWorks()
    {
        var vault = new DroppingVault();
        await using var harness = await BuildAsync(null, vault);
        harness.Setup.StartNew();
        harness.Setup.Connection!.Name = "Ops";
        harness.Setup.Connection.KibanaUrl = "https://kibana.example/";
        harness.Setup.Connection.ElasticsearchUrl = "https://elastic.example/";
        harness.Setup.Connection.AuthMode = ElasticAuthMode.Basic;
        harness.Setup.Connection.Username = "elastic";
        harness.Setup.Connection.Secret = "pw";
        await harness.Setup.Connection.TestConnectionCommand.Execute();
        await harness.NextAsync();
        await harness.ResolveDataViewAsync();
        await harness.NextAsync();
        harness.FillFilter();
        await harness.NextAsync();
        harness.ChooseColumns();
        await harness.NextAsync();
        vault.ForgetOnSet = true;

        await harness.NextAsync();

        Assert.True(harness.Setup.IsOpen);
        Assert.Equal(ElasticSetupStep.Finish, harness.Setup.Step);
        Assert.Contains("credential", harness.Setup.Error);
        Assert.False(harness.Setup.IsBusy);
        Assert.Empty(harness.Owner.State.Files);
        Assert.Single(harness.SavedConnection.Views);

        vault.ForgetOnSet = false;
        harness.Setup.Connection.Secret = "pw";
        await harness.NextAsync();

        Assert.Null(harness.Setup.Error);
        Assert.False(harness.Setup.IsOpen);
        Assert.Equal(2, harness.Owner.State.Files.Count);
    }

    private sealed class DroppingVault : ICredentialVault
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
        public bool ForgetOnSet { get; set; }

        public string? Get(string connectionId) => _secrets.GetValueOrDefault(connectionId);

        public void Set(string connectionId, string secret)
        {
            if (ForgetOnSet)
                _secrets.Remove(connectionId);
            else
                _secrets[connectionId] = secret;
        }

        public void Delete(string connectionId) => _secrets.Remove(connectionId);
    }

    [AvaloniaFact]
    public async Task PaletteAddElasticSource_StartsAddSourceAtTheDataView()
    {
        var saved = ElasticFixtures.Connection("c1", "Ops", "api");
        await using var harness = await CreateAsync(null, saved);

        var item = Assert.Single(
            harness.Owner.BuildPaletteItems(),
            item => item.Title == "Add Elastic source"
        );
        await item.Execute();

        Assert.True(harness.Setup.IsOpen);
        Assert.Equal(ElasticSetupStep.DataView, harness.Setup.Step);
        Assert.Equal("c1", harness.Setup.Connection!.Id);
        Assert.Equal(2, harness.Setup.Connection.Views.Count);
    }

    [AvaloniaFact]
    public async Task PaletteAddElasticSource_IsAbsentWithoutAConnection()
    {
        await using var harness = await CreateAsync();

        var titles = harness.Owner.BuildPaletteItems().Select(item => item.Title).ToList();

        Assert.Contains("Set up Elastic", titles);
        Assert.DoesNotContain("Add Elastic source", titles);
    }
}
