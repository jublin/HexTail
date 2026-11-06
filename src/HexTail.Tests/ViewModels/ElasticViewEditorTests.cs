using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using HexTail.Application;
using HexTail.Domain;
using HexTail.Elastic;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;
using HexTail.ViewModels;
using ReactiveUI.Reactive.Builder;

namespace HexTail.Tests.ViewModels;

public sealed class ElasticViewEditorTests
{
    private sealed class Harness(
        MainWindowViewModel owner,
        FakeElasticApiClient client,
        ElasticViewEditorViewModel view
    ) : IAsyncDisposable
    {
        public FakeElasticApiClient Client => client;
        public ElasticViewEditorViewModel View => view;

        public ValueTask DisposeAsync() => owner.DisposeAsync();
    }

    private static async Task<Harness> CreateAsync(
        string[]? outputFields = null,
        string? serverField = "service",
        string[]? sourceValues = null
    )
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
                    ServerField = serverField,
                    OutputFields = [.. outputFields ?? ["@timestamp", "message", "service"]],
                    Sources =
                    [
                        .. (sourceValues ?? []).Select(value => new ElasticSourceSettings
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            ServerValue = value,
                        }),
                    ],
                },
            ],
        };
        var client = new FakeElasticApiClient
        {
            DataViewFields =
            [
                new("@timestamp", "date", true),
                new("message", "text", true),
                new("service", "keyword", true),
                new("host", "keyword", true),
                new("bytes", "long", false),
            ],
        };
        client.DataViewHandler = id =>
            Task.FromResult(new ElasticDataView(id, "logs-*", "@timestamp", client.DataViewFields));
        var state = new AppState(
            new LogSourceService(),
            new TestPersistence(),
            new AppSettings { ElasticConnections = [connection] },
            new InMemoryCredentialVault(),
            client
        );
        var owner = new MainWindowViewModel(
            state,
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        owner.Settings.Sync(state.Settings);
        var view = Assert.Single(Assert.Single(owner.Settings.ElasticConnections).Views);
        view.SelectedDataViewId = null;
        view.SelectedDataViewId = "view-1";
        await WaitForAsync(() => view.IsSelectionResolved);
        Assert.True(view.IsSelectionResolved);
        return new Harness(owner, client, view);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 300 && !condition(); attempt++)
            await Task.Delay(10);
    }

    private static TaskCompletionSource<T> Pending<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ElasticHit Hit(string raw) =>
        new(raw, DateTimeOffset.UtcNow, new Line(raw), Array.Empty<JsonElement>());

    [AvaloniaFact]
    public async Task MoveOutputField_ReordersSelectionSettingsAndRowPreview()
    {
        await using var harness = await CreateAsync(["@timestamp", "message", "service"]);
        var view = harness.View;

        view.MoveOutputField(0, 2);

        Assert.Equal(["message", "service", "@timestamp"], view.SelectedOutputFields);
        Assert.Equal(["message", "service", "@timestamp"], view.ToSettings().OutputFields);
        Assert.Equal("<message> <service> <@timestamp>", view.RowPreview);

        view.MoveOutputField(-1, 1);
        view.MoveOutputField(0, 3);
        view.MoveOutputField(5, 0);
        view.MoveOutputField(1, 1);

        Assert.Equal(["message", "service", "@timestamp"], view.SelectedOutputFields);
    }

    [AvaloniaFact]
    public async Task TogglingIsOutput_KeepsSelectedOutputFieldsInSync()
    {
        await using var harness = await CreateAsync(["message"]);
        var view = harness.View;
        var message = view.Fields.Single(field => field.Name == "message");
        var service = view.Fields.Single(field => field.Name == "service");
        var changes = 0;
        view.SelectedOutputFields.CollectionChanged += (_, _) => changes++;

        service.IsOutput = true;

        Assert.Equal(["message", "service"], view.SelectedOutputFields);
        Assert.Equal("message → service", view.SelectedFieldsSummary);
        Assert.Equal(2, view.SelectedFieldCount);
        Assert.Equal(["message", "service"], view.OutputFieldNames);

        message.IsOutput = false;

        Assert.Equal(["service"], view.SelectedOutputFields);
        Assert.Equal("service", view.SelectedFieldsSummary);
        Assert.True(changes >= 2);

        service.IsOutput = false;

        Assert.Empty(view.SelectedOutputFields);
        Assert.Equal("No output fields selected.", view.SelectedFieldsSummary);
    }

    [AvaloniaFact]
    public async Task SelectedOutputFields_RaisesPrerequisitesWhenReordered()
    {
        await using var harness = await CreateAsync(["@timestamp", "message"]);
        var view = harness.View;
        var raised = new List<string?>();
        view.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        view.MoveOutputField(0, 1);

        Assert.Contains(nameof(ElasticViewEditorViewModel.RowPreview), raised);
        Assert.Contains(nameof(ElasticViewEditorViewModel.SelectedFieldsSummary), raised);
    }

    [AvaloniaFact]
    public async Task SearchableAndSuggestedFields_RankAndExclude()
    {
        await using var harness = await CreateAsync(["service"]);
        var view = harness.View;

        var searchable = view.SearchableFields.Select(field => field.Name).ToArray();
        Assert.DoesNotContain("bytes", searchable);
        Assert.Contains("service", searchable);

        var suggested = view.SuggestedFields.Select(field => field.Name).ToArray();
        Assert.Equal(["@timestamp", "message", "host", "bytes"], suggested);
        Assert.DoesNotContain("service", suggested);

        view.Fields.Single(field => field.Name == "message").IsOutput = true;

        Assert.DoesNotContain(view.SuggestedFields, field => field.Name == "message");
    }

    [AvaloniaFact]
    public async Task AddFilterValue_AddsSourceAndCounts()
    {
        await using var harness = await CreateAsync();
        var view = harness.View;
        var count = Pending<long>();
        harness.Client.CountHandler = _ => count.Task;

        Assert.False(view.AddFilterValue("   "));
        Assert.False(view.AddFilterValue(""));
        Assert.Empty(harness.Client.Counts);

        Assert.True(view.AddFilterValue("  checkout "));

        var source = Assert.Single(view.Sources, s => s.ServerValue == "checkout");
        Assert.Equal("counting…", source.CountText);
        Assert.False(view.AddFilterValue("checkout"));
        Assert.Single(view.Sources, s => s.ServerValue == "checkout");
        Assert.Equal(new[] { ("service", "checkout") }, harness.Client.Counts);

        count.SetResult(3200);
        await WaitForAsync(() => source.CountText != "counting…");

        Assert.Equal("~3.2k docs · 15 min", source.CountText);
        Assert.False(source.IsZeroCount);
    }

    [AvaloniaFact]
    public async Task AddFilterValue_KeepsExistingSourcesAndAddsAnother()
    {
        await using var harness = await CreateAsync(sourceValues: ["api"]);
        var view = harness.View;
        harness.Client.CountHandler = _ => Task.FromResult(5L);

        Assert.True(view.AddFilterValue("web"));

        Assert.Equal(["api", "web"], view.Sources.Select(s => s.ServerValue));
        Assert.False(view.AddFilterValue("api"));
        Assert.Equal(2, view.Sources.Count);
        await WaitForAsync(() => view.Sources[1].CountText != "counting…");
        Assert.Equal("~5 docs · 15 min", view.Sources[1].CountText);
    }

    [AvaloniaFact]
    public async Task Count_ZeroSetsIsZeroCount_FailureSetsCountFailed()
    {
        await using var harness = await CreateAsync();
        var view = harness.View;
        harness.Client.CountHandler = _ => Task.FromResult(0L);

        Assert.True(view.AddFilterValue("empty"));
        var empty = view.Sources.Single(s => s.ServerValue == "empty");
        await WaitForAsync(() => empty.CountText != "counting…");

        Assert.Equal("0 docs · 15 min", empty.CountText);
        Assert.True(empty.IsZeroCount);

        harness.Client.CountHandler = _ => throw new InvalidOperationException("boom");
        Assert.True(view.AddFilterValue("broken"));
        var broken = view.Sources.Single(s => s.ServerValue == "broken");
        await WaitForAsync(() => broken.CountText != "counting…");

        Assert.Equal("count failed", broken.CountText);
        Assert.False(broken.IsZeroCount);

        harness.Client.CountHandler = _ => Task.FromException<long>(new IOException("down"));
        await view.RefreshCountsCommand.Execute();

        Assert.Equal("count failed", broken.CountText);
        Assert.Equal("count failed", empty.CountText);
    }

    [Theory]
    [InlineData(0L, "0")]
    [InlineData(842L, "842")]
    [InlineData(999L, "999")]
    [InlineData(1000L, "1.0k")]
    [InlineData(3200L, "3.2k")]
    [InlineData(999_949L, "999.9k")]
    [InlineData(999_950L, "1.0M")]
    [InlineData(1_000_000L, "1.0M")]
    [InlineData(2_540_000L, "2.5M")]
    public void FormatCount_UsesExactThenKThenM(long count, string expected) =>
        Assert.Equal(expected, ElasticSourceSettingViewModel.FormatCount(count));

    [AvaloniaFact]
    public async Task RemoveFilterValue_RemovesSourceAndKeepsOneEmptySource()
    {
        await using var harness = await CreateAsync(sourceValues: ["api", "web"]);
        var view = harness.View;

        view.RemoveFilterValue(view.Sources[0]);

        Assert.Equal(["web"], view.Sources.Select(s => s.ServerValue));

        view.RemoveFilterValue(view.Sources[0]);

        var placeholder = Assert.Single(view.Sources);
        Assert.Equal(string.Empty, placeholder.ServerValue);
    }

    [AvaloniaFact]
    public async Task ChangingServerField_RecountsEveryValueWithTheNewField()
    {
        await using var harness = await CreateAsync(sourceValues: ["api", "web"]);
        var view = harness.View;
        harness.Client.CountHandler = _ => Task.FromResult(10L);

        view.ServerField = "host";

        Assert.Equal(
            new[] { ("host", "api"), ("host", "web") },
            harness.Client.Counts.OrderBy(count => count.Value)
        );
        await WaitForAsync(() => view.Sources.All(s => s.CountText != "counting…"));
        Assert.All(view.Sources, source => Assert.Equal("~10 docs · 15 min", source.CountText));
    }

    [AvaloniaFact]
    public async Task Sync_DoesNotStartCounts()
    {
        await using var harness = await CreateAsync(sourceValues: ["api"]);
        var view = harness.View;

        view.Sync(view.ToSettings());

        Assert.Empty(harness.Client.Counts);
        Assert.All(view.Sources, source => Assert.Equal(string.Empty, source.CountText));
    }

    [AvaloniaFact]
    public async Task StaleCountResults_AreDropped()
    {
        await using var harness = await CreateAsync(sourceValues: ["api"]);
        var view = harness.View;
        var first = Pending<long>();
        var second = Pending<long>();
        var calls = 0;
        harness.Client.CountHandler = _ => ++calls == 1 ? first.Task : second.Task;

        var refresh = view.RefreshCountsCommand.Execute().FirstAsync().ToTask();
        var source = view.Sources.Single();
        Assert.Equal("counting…", source.CountText);

        view.ServerField = "host";
        second.SetResult(7);
        await WaitForAsync(() => source.CountText != "counting…");
        Assert.Equal("~7 docs · 15 min", source.CountText);

        first.SetResult(9999);
        await refresh;
        await Task.Delay(50);

        Assert.Equal("~7 docs · 15 min", source.CountText);
    }

    [AvaloniaFact]
    public async Task StaleCountResult_DoesNotResurrectARemovedOrEditedSource()
    {
        await using var harness = await CreateAsync(sourceValues: ["api"]);
        var view = harness.View;
        var pending = Pending<long>();
        harness.Client.CountHandler = _ => pending.Task;
        Assert.True(view.AddFilterValue("web"));
        var source = view.Sources.Single(s => s.ServerValue == "web");

        source.ServerValue = "web2";
        pending.SetResult(12);
        await Task.Delay(50);

        Assert.Equal(string.Empty, source.CountText);
    }

    [AvaloniaFact]
    public async Task RefreshPreview_FillsRowsAndClearsMessage()
    {
        await using var harness = await CreateAsync(sourceValues: ["api"]);
        var view = harness.View;
        IReadOnlyList<string>? requested = null;
        harness.Client.PreviewHandler = fields =>
        {
            requested = fields;
            return Task.FromResult<IReadOnlyList<ElasticHit>>([Hit("first line"), Hit("second")]);
        };

        await view.RefreshPreviewCommand.Execute();

        Assert.Equal(["first line", "second"], view.PreviewRows.Select(row => row.Text));
        Assert.All(view.PreviewRows, row => Assert.NotEmpty(row.Time));
        Assert.Null(view.PreviewMessage);
        Assert.Equal(["@timestamp", "message", "service"], requested);
    }

    [AvaloniaFact]
    public async Task RefreshPreview_ZeroHitsAndErrorsSetAMessage()
    {
        await using var harness = await CreateAsync();
        var view = harness.View;
        harness.Client.PreviewHandler = _ => Task.FromResult<IReadOnlyList<ElasticHit>>([]);

        await view.RefreshPreviewCommand.Execute();

        Assert.Empty(view.PreviewRows);
        Assert.Equal("No documents in the last 15 minutes", view.PreviewMessage);

        harness.Client.PreviewHandler = _ => throw new InvalidOperationException("kaput");
        await view.RefreshPreviewCommand.Execute();

        Assert.Empty(view.PreviewRows);
        Assert.Contains("kaput", view.PreviewMessage);
    }

    [AvaloniaFact]
    public async Task RefreshPreview_ShowsLoadingAndDropsStaleResults()
    {
        await using var harness = await CreateAsync();
        var view = harness.View;
        var first = Pending<IReadOnlyList<ElasticHit>>();
        var second = Pending<IReadOnlyList<ElasticHit>>();
        var calls = 0;
        harness.Client.PreviewHandler = _ => ++calls == 1 ? first.Task : second.Task;

        var firstRun = view.RefreshPreviewCommand.Execute().FirstAsync().ToTask();
        Assert.Equal("Loading preview…", view.PreviewMessage);
        var secondRun = view.RefreshPreviewCommand.Execute().FirstAsync().ToTask();
        second.SetResult([Hit("new")]);
        await secondRun;
        first.SetResult([Hit("old")]);
        await firstRun;

        Assert.Equal(["new"], view.PreviewRows.Select(row => row.Text));
        Assert.Null(view.PreviewMessage);
    }

    [AvaloniaFact]
    public async Task SuggestedName_UsesTitleAndFirstFilterValue()
    {
        await using var harness = await CreateAsync();
        var view = harness.View;

        Assert.Equal("logs-*", view.SuggestedName);

        harness.Client.CountHandler = _ => Task.FromResult(1L);
        Assert.True(view.AddFilterValue("checkout"));

        Assert.Equal("logs-* · checkout", view.SuggestedName);
    }
}
