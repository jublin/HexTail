using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using HexTail.Application;
using HexTail.Elastic;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.ViewModels;

public sealed class ElasticConnectionEditorTests
{
    private const string Secret = "s3cret-value";

    private static string CloudIdFor(string host, string elasticsearch, string? kibana) =>
        "deployment:"
        + Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{host}${elasticsearch}{(kibana is null ? "" : "$" + kibana)}")
        );

    private sealed class Harness(
        MainWindowViewModel owner,
        FakeElasticApiClient client,
        TestPersistence persistence,
        InMemoryCredentialVault vault
    ) : IAsyncDisposable
    {
        public MainWindowViewModel Owner => owner;
        public FakeElasticApiClient Client => client;
        public TestPersistence Persistence => persistence;
        public InMemoryCredentialVault Vault => vault;

        public ElasticConnectionEditorViewModel NewEditor()
        {
            owner.Settings.AddElasticConnection();
            var editor = owner.Settings.ElasticConnections.Last();
            editor.Name = "Ops";
            editor.KibanaUrl = "https://kibana.example/";
            editor.ElasticsearchUrl = "https://elastic.example/";
            return editor;
        }

        public ValueTask DisposeAsync() => owner.DisposeAsync();
    }

    private static async Task<Harness> CreateAsync(params ElasticConnectionSettings[] saved)
    {
        var client = new FakeElasticApiClient
        {
            DataViews = [new("logs", "logs-*"), new("metrics", "metrics-*")],
        };
        var persistence = ElasticFixtures.Persistence(saved);
        var vault = new InMemoryCredentialVault();
        var state = new AppState(
            new LogSourceService(),
            persistence,
            elastic: client,
            credentials: vault
        );
        var owner = new MainWindowViewModel(
            state,
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        await owner.InitializeAsync();
        return new Harness(owner, client, persistence, vault);
    }

    [AvaloniaFact]
    public async Task CloudId_FillsUrlsAndReportsInvalidIdsWithoutTouchingThem()
    {
        await using var harness = await CreateAsync();
        var editor = harness.NewEditor();

        editor.CloudId = CloudIdFor("us-east-1.aws.found.io", "esid", "kbid");

        Assert.Null(editor.CloudIdError);
        Assert.Equal("https://esid.us-east-1.aws.found.io", editor.ElasticsearchUrl.TrimEnd('/'));
        Assert.Equal("https://kbid.us-east-1.aws.found.io", editor.KibanaUrl.TrimEnd('/'));

        var kibana = editor.KibanaUrl;
        var elasticsearch = editor.ElasticsearchUrl;
        editor.CloudId = "not-a-cloud-id";

        Assert.NotNull(editor.CloudIdError);
        Assert.Equal(kibana, editor.KibanaUrl);
        Assert.Equal(elasticsearch, editor.ElasticsearchUrl);

        editor.CloudId = string.Empty;

        Assert.Null(editor.CloudIdError);
        Assert.Equal(kibana, editor.KibanaUrl);
        Assert.Equal(elasticsearch, editor.ElasticsearchUrl);
    }

    [AvaloniaFact]
    public async Task CloudId_WithoutKibanaLeavesTheKibanaUrlAlone()
    {
        await using var harness = await CreateAsync();
        var editor = harness.NewEditor();

        editor.CloudId = CloudIdFor("us-east-1.aws.found.io", "esid", null);

        Assert.Null(editor.CloudIdError);
        Assert.Equal("https://kibana.example/", editor.KibanaUrl);
        Assert.StartsWith("https://esid.", editor.ElasticsearchUrl, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task CloudId_DecodeRaisesConnectionEditedOnce()
    {
        await using var harness = await CreateAsync();
        var editor = harness.NewEditor();
        var edits = 0;
        editor.ConnectionEdited += (_, _) => edits++;

        editor.CloudId = CloudIdFor("us-east-1.aws.found.io", "esid", "kbid");

        Assert.Equal(1, edits);
    }

    [AvaloniaFact]
    public async Task TestConnection_ReportsEachEndpointAndKeepsTheCombinedStatus()
    {
        await using var harness = await CreateAsync();
        var editor = harness.NewEditor();
        Assert.Equal(
            ["Kibana", "Elasticsearch", "Credentials"],
            editor.EndpointResults.Select(row => row.Label)
        );
        Assert.All(editor.EndpointResults, row => Assert.Equal(EndpointState.NotTested, row.State));

        await editor.TestConnectionCommand.Execute().FirstAsync();

        Assert.Equal(EndpointState.Ok, editor.EndpointResults[0].State);
        Assert.Equal("✓ 2 data views", editor.EndpointResults[0].Message);
        Assert.Equal(EndpointState.Ok, editor.EndpointResults[1].State);
        Assert.True(editor.EndpointResults[0].IsOk);
        Assert.False(editor.EndpointResults[0].IsFailed);
        Assert.True(editor.EndpointsOk);
        Assert.Equal("Connected (2 data views)", editor.Status);
        Assert.Null(editor.Error);
    }

    [AvaloniaFact]
    public async Task TestConnection_ElasticsearchFailureLeavesKibanaOk()
    {
        await using var harness = await CreateAsync();
        harness.Client.ElasticsearchError = new InvalidOperationException("es unreachable");
        var editor = harness.NewEditor();

        await editor.TestConnectionCommand.Execute().FirstAsync();

        Assert.Equal(EndpointState.Ok, editor.EndpointResults[0].State);
        Assert.Equal(EndpointState.Failed, editor.EndpointResults[1].State);
        Assert.True(editor.EndpointResults[1].IsFailed);
        Assert.Contains("es unreachable", editor.EndpointResults[1].Message);
        Assert.False(editor.EndpointsOk);
        Assert.Equal("es unreachable", editor.Error);
        Assert.Equal("Connection failed", editor.Status);
        Assert.Equal(2, editor.DataViews.Count);
    }

    [AvaloniaFact]
    public async Task TestConnection_KibanaFailureLeavesElasticsearchOk()
    {
        await using var harness = await CreateAsync();
        harness.Client.DataViewsHandler = () =>
            Task.FromException<IReadOnlyList<ElasticDataViewSummary>>(
                new InvalidOperationException("kibana down")
            );
        var editor = harness.NewEditor();

        await editor.TestConnectionCommand.Execute().FirstAsync();

        Assert.Equal(EndpointState.Failed, editor.EndpointResults[0].State);
        Assert.Contains("kibana down", editor.EndpointResults[0].Message);
        Assert.Equal(EndpointState.Ok, editor.EndpointResults[1].State);
        Assert.False(editor.EndpointsOk);
        Assert.Equal("kibana down", editor.Error);
        Assert.Equal("Connection failed", editor.Status);
    }

    [AvaloniaFact]
    public async Task TestConnection_ShowsChecking_WhileRequestsAreInFlight()
    {
        await using var harness = await CreateAsync();
        var pending = new TaskCompletionSource<IReadOnlyList<ElasticDataViewSummary>>();
        harness.Client.DataViewsHandler = () => pending.Task;
        var editor = harness.NewEditor();

        var testing = editor.TestConnectionCommand.Execute().FirstAsync().ToTask();

        Assert.Equal(EndpointState.Checking, editor.EndpointResults[0].State);
        Assert.Equal("Checking…", editor.EndpointResults[0].Message);
        pending.SetResult([new("logs", "logs-*")]);
        await testing;
        Assert.Equal("✓ 1 data view", editor.EndpointResults[0].Message);
    }

    [AvaloniaFact]
    public async Task TestConnection_ResultsOfASupersededTestAreDiscarded()
    {
        await using var harness = await CreateAsync();
        var pending = new TaskCompletionSource<IReadOnlyList<ElasticDataViewSummary>>();
        harness.Client.DataViewsHandler = () => pending.Task;
        var editor = harness.NewEditor();

        var testing = editor.TestConnectionCommand.Execute().FirstAsync().ToTask();
        editor.ElasticsearchUrl = "https://other.example/";
        pending.SetResult([new("logs", "logs-*")]);
        await testing;

        Assert.All(editor.EndpointResults, row => Assert.Equal(EndpointState.NotTested, row.State));
        Assert.False(editor.EndpointsOk);
        Assert.Equal("Not tested", editor.Status);
    }

    [AvaloniaFact]
    public async Task CredentialsRow_DescribesWhereTheSecretLives()
    {
        await using var harness = await CreateAsync();
        var anonymous = harness.NewEditor();
        await anonymous.TestConnectionCommand.Execute().FirstAsync();
        Assert.Equal(EndpointState.Ok, anonymous.EndpointResults[2].State);
        Assert.Equal("no credentials (anonymous)", anonymous.EndpointResults[2].Message);

        var basic = harness.NewEditor();
        basic.AuthMode = ElasticAuthMode.ApiKey;
        basic.Secret = Secret;
        await basic.TestConnectionCommand.Execute().FirstAsync();
        Assert.Equal(EndpointState.NotTested, basic.EndpointResults[2].State);
        Assert.Equal("will be stored in keyring on Next", basic.EndpointResults[2].Message);

        Assert.True(await basic.SaveConnectionOnlyAsync());
        Assert.Equal(EndpointState.Ok, basic.EndpointResults[2].State);
        Assert.Equal("stored in keyring", basic.EndpointResults[2].Message);
        Assert.True(basic.EndpointsOk);
    }

    [AvaloniaFact]
    public async Task EditingAfterASuccessfulTestResetsRowsAndRaisesConnectionEdited()
    {
        await using var harness = await CreateAsync();
        var editor = harness.NewEditor();
        editor.AuthMode = ElasticAuthMode.ApiKey;
        editor.Secret = "first";
        await editor.TestConnectionCommand.Execute().FirstAsync();
        Assert.True(editor.EndpointsOk);
        var edits = 0;
        editor.ConnectionEdited += (_, _) => edits++;

        editor.Secret = "second";

        Assert.Equal(1, edits);
        Assert.False(editor.EndpointsOk);
        Assert.All(editor.EndpointResults, row => Assert.Equal(EndpointState.NotTested, row.State));
        Assert.Equal("Not tested", editor.Status);
    }

    [AvaloniaFact]
    public async Task EveryConnectionFieldRaisesConnectionEditedExactlyOncePerChange()
    {
        await using var harness = await CreateAsync();
        var editor = harness.NewEditor();
        var edits = 0;
        editor.ConnectionEdited += (_, _) => edits++;

        editor.KibanaUrl = "https://kibana2.example/";
        Assert.Equal(1, edits);
        editor.KibanaUrl = "https://kibana2.example/";
        Assert.Equal(1, edits);
        editor.ElasticsearchUrl = "https://elastic2.example/";
        Assert.Equal(2, edits);
        editor.AuthMode = ElasticAuthMode.Basic;
        Assert.Equal(3, edits);
        editor.Username = "user";
        Assert.Equal(4, edits);
        editor.Secret = "pw";
        Assert.Equal(5, edits);
        editor.Name = "Renamed";
        Assert.Equal(5, edits);
    }

    [AvaloniaFact]
    public async Task CanTestConnection_RequiresValidUrlsAndCompleteCredentials()
    {
        await using var harness = await CreateAsync();
        var editor = harness.NewEditor();
        Assert.True(editor.CanTestConnection);

        editor.KibanaUrl = "not a url";
        Assert.False(editor.CanTestConnection);
        editor.KibanaUrl = "https://kibana.example/";
        Assert.True(editor.CanTestConnection);

        editor.AuthMode = ElasticAuthMode.Basic;
        Assert.False(editor.CanTestConnection);
        editor.Username = "user";
        Assert.False(editor.CanTestConnection);
        editor.Secret = "pw";
        Assert.True(editor.CanTestConnection);
        editor.Username = string.Empty;
        Assert.False(editor.CanTestConnection);

        editor.AuthMode = ElasticAuthMode.ApiKey;
        Assert.True(editor.CanTestConnection);
        editor.Secret = string.Empty;
        Assert.False(editor.CanTestConnection);
    }

    [AvaloniaFact]
    public async Task CanTestConnection_AllowsRetestingASavedConnectionWithAStoredSecret()
    {
        await using var harness = await CreateAsync();
        var editor = harness.NewEditor();
        editor.AuthMode = ElasticAuthMode.ApiKey;
        editor.Secret = Secret;
        Assert.False(editor.IsSaved);
        Assert.True(await editor.SaveConnectionOnlyAsync());

        Assert.True(editor.IsSaved);
        Assert.Equal(string.Empty, editor.Secret);
        Assert.True(editor.CanTestConnection);
    }

    [AvaloniaFact]
    public async Task SaveConnectionOnly_PersistsANewConnectionWithoutViewsAndKeepsTheSecretInTheVault()
    {
        await using var harness = await CreateAsync();
        var editor = harness.NewEditor();
        editor.AuthMode = ElasticAuthMode.Basic;
        editor.Username = "user";
        editor.Secret = Secret;
        editor.AddNewView();
        await editor.TestConnectionCommand.Execute().FirstAsync();

        var ok = await editor.SaveConnectionOnlyAsync();
        Assert.True(ok, editor.Error);

        var saved = Assert.Single(harness.Persistence.Config!.Settings.ElasticConnections);
        Assert.Equal(editor.Id, saved.Id);
        Assert.Empty(saved.Views);
        Assert.Equal(Secret, harness.Vault.Get(editor.Id));
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(harness.Persistence.Config));
        Assert.Equal(string.Empty, editor.Secret);
        Assert.True(editor.IsSaved);
        Assert.True(editor.EndpointsOk);
        Assert.Single(editor.Views);
    }

    [AvaloniaFact]
    public async Task SaveConnectionOnly_KeepsSavedViewsAndSkipsTheInProgressOne()
    {
        var connection = ElasticFixtures.Connection("c1", "Ops", "api");
        await using var harness = await CreateAsync(connection);
        var editor = Assert.Single(harness.Owner.Settings.ElasticConnections);
        editor.AddNewView();
        Assert.Equal(2, editor.Views.Count);
        editor.Name = "Ops renamed";

        Assert.True(await editor.SaveConnectionOnlyAsync());

        var saved = Assert.Single(harness.Persistence.Config!.Settings.ElasticConnections);
        Assert.Equal("Ops renamed", saved.Name);
        var view = Assert.Single(saved.Views);
        Assert.Equal("c1-view", view.Id);
        Assert.Equal(2, editor.Views.Count);
        Assert.True(editor.IsDirty);
    }

    [AvaloniaFact]
    public async Task SaveConnectionOnly_ReportsFailuresOnError()
    {
        await using var harness = await CreateAsync();
        var editor = harness.NewEditor();
        harness.Persistence.SaveError = new IOException("disk full");

        Assert.False(await editor.SaveConnectionOnlyAsync());

        Assert.Equal("disk full", editor.Error);
        Assert.False(editor.IsSaved);
        harness.Persistence.SaveError = null;
    }

    [AvaloniaFact]
    public async Task SaveConnectionOnly_RefusesInvalidUrls()
    {
        await using var harness = await CreateAsync();
        var editor = harness.NewEditor();
        editor.ElasticsearchUrl = "nope";

        Assert.False(await editor.SaveConnectionOnlyAsync());

        Assert.Empty(harness.Persistence.Config!.Settings.ElasticConnections);
    }

    private static async Task<(
        Harness Harness,
        ElasticConnectionEditorViewModel Editor
    )> SavedAsync(ElasticAuthMode mode)
    {
        var harness = await CreateAsync();
        var editor = harness.NewEditor();
        editor.AuthMode = mode;
        if (mode == ElasticAuthMode.Basic)
            editor.Username = "user";
        editor.Secret = Secret;
        Assert.True(await editor.SaveConnectionOnlyAsync(), editor.Error);
        harness.Client.DataViewSecrets.Clear();
        return (harness, editor);
    }

    [AvaloniaFact]
    public async Task StoredSecret_IsNotReusedWhenTheLoginMethodChangesFromApiKeyToPassword()
    {
        var (harness, editor) = await SavedAsync(ElasticAuthMode.ApiKey);
        await using var _ = harness;

        editor.AuthMode = ElasticAuthMode.Basic;
        editor.Username = "alice";

        Assert.False(editor.CanTestConnection);
        Assert.False(await editor.SaveConnectionOnlyAsync());
        Assert.Empty(harness.Client.DataViewSecrets);
        Assert.Equal(Secret, harness.Vault.Get(editor.Id));
        Assert.Equal(
            ElasticAuthMode.ApiKey,
            harness.Persistence.Config!.Settings.ElasticConnections.Single().AuthMode
        );

        editor.Secret = "new-password";
        Assert.True(editor.CanTestConnection);
        Assert.True(await editor.SaveConnectionOnlyAsync(), editor.Error);
        Assert.Equal("new-password", harness.Vault.Get(editor.Id));
    }

    [AvaloniaFact]
    public async Task StoredSecret_IsNotReusedWhenTheLoginMethodChangesFromPasswordToApiKey()
    {
        var (harness, editor) = await SavedAsync(ElasticAuthMode.Basic);
        await using var _ = harness;

        editor.AuthMode = ElasticAuthMode.ApiKey;

        Assert.False(editor.CanTestConnection);
        Assert.False(await editor.SaveConnectionOnlyAsync());
        Assert.Empty(harness.Client.DataViewSecrets);
        Assert.Equal(Secret, harness.Vault.Get(editor.Id));

        editor.Secret = "new-key";
        Assert.True(editor.CanTestConnection);
        Assert.True(await editor.SaveConnectionOnlyAsync(), editor.Error);
        Assert.Equal("new-key", harness.Vault.Get(editor.Id));
    }

    [AvaloniaTheory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task StoredSecret_IsNotReusedWhenAnEndpointMovesToAnotherHost(
        bool kibana,
        bool elasticsearch
    )
    {
        var (harness, editor) = await SavedAsync(ElasticAuthMode.ApiKey);
        await using var _ = harness;

        if (kibana)
            editor.KibanaUrl = "https://elsewhere.example/";
        if (elasticsearch)
            editor.ElasticsearchUrl = "https://elsewhere.example:9243/";

        Assert.False(editor.CanTestConnection);
        Assert.False(await editor.SaveConnectionOnlyAsync());
        Assert.Empty(harness.Client.DataViewSecrets);
        Assert.Equal(Secret, harness.Vault.Get(editor.Id));

        editor.Secret = "other-key";
        Assert.True(editor.CanTestConnection);
        Assert.True(await editor.SaveConnectionOnlyAsync(), editor.Error);
        Assert.Equal("other-key", harness.Vault.Get(editor.Id));
    }

    [AvaloniaFact]
    public async Task StoredSecret_StillServesTheSameLoginMethodAndOrigins()
    {
        var (harness, editor) = await SavedAsync(ElasticAuthMode.ApiKey);
        await using var _ = harness;

        // A different path, case or trailing slash on the same origin is the same server.
        editor.KibanaUrl = "HTTPS://Kibana.Example/s/ops/";
        editor.ElasticsearchUrl = "https://elastic.example";

        Assert.True(editor.CanTestConnection);
        await editor.TestConnectionCommand.Execute().FirstAsync();
        Assert.Equal([Secret], harness.Client.DataViewSecrets.Distinct());
        Assert.True(await editor.SaveConnectionOnlyAsync(), editor.Error);
        Assert.Equal(Secret, harness.Vault.Get(editor.Id));
    }

    [AvaloniaFact]
    public async Task StoredSecret_IsNotSentByTheAppStateEitherWhenTheEditorIsBypassed()
    {
        var (harness, editor) = await SavedAsync(ElasticAuthMode.ApiKey);
        await using var _ = harness;
        var state = harness.Owner.State;
        var saved = harness.Persistence.Config!.Settings.ElasticConnections.Single();

        await state.GetDataViewsAsync(
            saved with
            {
                AuthMode = ElasticAuthMode.Basic,
                Username = "u",
            }
        );
        await state.GetDataViewsAsync(saved with { KibanaUrl = "https://elsewhere.example/" });
        await state.GetDataViewsAsync(saved);

        Assert.Equal([null, null, Secret], harness.Client.DataViewSecrets);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await state.SaveElasticConnectionAsync(
                saved with
                {
                    AuthMode = ElasticAuthMode.Basic,
                    Username = "u",
                },
                null
            )
        );
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await state.SaveElasticConnectionAsync(
                saved with
                {
                    ElasticsearchUrl = "https://elsewhere.example/",
                },
                null
            )
        );
        Assert.Equal(Secret, harness.Vault.Get(editor.Id));
        await state.SaveElasticConnectionAsync(saved with { Name = "Renamed" }, null);
        Assert.Equal(Secret, harness.Vault.Get(editor.Id));
    }

    [AvaloniaTheory]
    [InlineData("https://user:pass@kibana.example/")]
    [InlineData("https://user@kibana.example/")]
    public async Task UrlsWithAUserNameOrPasswordAreRejected(string url)
    {
        await using var harness = await CreateAsync();
        var editor = harness.NewEditor();

        editor.KibanaUrl = url;
        editor.ElasticsearchUrl = url;

        Assert.Equal(
            "Enter an absolute HTTP or HTTPS Kibana URL without a user name or password.",
            editor.KibanaUrlError
        );
        Assert.Equal(
            "Enter an absolute HTTP or HTTPS Elasticsearch URL without a user name or password.",
            editor.ElasticsearchUrlError
        );
        Assert.False(editor.CanTestConnection);
        Assert.False(await editor.SaveConnectionOnlyAsync());
        Assert.Empty(harness.Persistence.Config!.Settings.ElasticConnections);

        editor.KibanaUrl = "kibana.example";
        Assert.Equal("Enter an absolute HTTP or HTTPS Kibana URL.", editor.KibanaUrlError);
    }

    [AvaloniaTheory]
    [InlineData("https://user:pass@kibana.example/", "https://elastic.example/")]
    [InlineData("https://kibana.example/", "https://user:pass@elastic.example/")]
    public async Task AppState_RejectsUrlsThatCarryCredentials(string kibana, string elasticsearch)
    {
        await using var harness = await CreateAsync();
        var connection = new ElasticConnectionSettings
        {
            Id = "c9",
            Name = "Ops",
            KibanaUrl = kibana,
            ElasticsearchUrl = elasticsearch,
        };

        var failure = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await harness.Owner.State.SaveElasticConnectionAsync(connection, null)
        );

        Assert.Contains(
            "without a user name or password",
            failure.Message,
            StringComparison.Ordinal
        );
        Assert.Empty(harness.Persistence.Config!.Settings.ElasticConnections);
    }
}
