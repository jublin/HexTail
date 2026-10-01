using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Application;
using HexTail.Domain;
using HexTail.Elastic;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.Ui;

public sealed class ResultStateTests
{
    [AvaloniaFact]
    public async Task AppliedIntervalDisclosesInitialLimitAndClearsItForTheNextLoad()
    {
        var timestamp = DateTimeOffset.Parse("2026-08-20T10:00:00Z");
        var page = new ElasticSearchPage(
            "pit",
            Enumerable
                .Range(0, 3)
                .Select(index => new ElasticHit(
                    $"id-{index}",
                    timestamp.AddSeconds(index),
                    new Line($"row {index}"),
                    []
                ))
                .ToArray()
        );
        var client = new FakeElasticApiClient { SearchHandler = _ => Task.FromResult(page) };
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
                    DataViewId = "logs",
                    DataViewTitle = "logs-*",
                    TimeFieldName = "@timestamp",
                    ServerField = "server",
                    OutputFields = ["message"],
                    Sources = [new ElasticSourceSettings { Id = "s1", ServerValue = "api" }],
                },
            ],
        };
        var state = new AppState(
            new LogSourceService(new TailerOptions { MaxInitialLines = 2 }),
            new TestPersistence(),
            new AppSettings { ElasticConnections = [connection] },
            elastic: client
        );
        await using var workspace = new MainWindowViewModel(
            state,
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        var window = new MainWindow(workspace);
        try
        {
            var tab = await state.OpenElasticSourceAsync(
                "s1",
                from: "2026-08-20T09:00:00Z",
                to: "2026-08-20T11:00:00Z"
            );
            window.Show();
            await DrainUntilLoaded();
            var limit = window
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .Single(block => block.Name == "InitialLimitMessage");
            Assert.True(limit.IsVisible);
            Assert.Contains("Initial limit reached (2 newest logs)", limit.Text);
            Assert.Contains("may contain more logs", limit.Text);
            Assert.Equal(2, tab.Buffer.Count);

            client.SearchHandler = _ => Task.FromResult(new ElasticSearchPage("pit", []));
            state.SetElasticTimeRange(tab, "2026-08-20T08:00:00Z", "2026-08-20T09:00:00Z");
            Dispatcher.UIThread.RunJobs();
            Assert.False(limit.IsVisible);
            await DrainUntilLoaded();
            var message = window
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .Single(block => block.Name == "ResultMessage");
            Assert.Equal("No logs returned for the applied interval.", message.Text);
            Assert.False(limit.IsVisible);

            async Task DrainUntilLoaded()
            {
                var timeout = DateTime.UtcNow.AddSeconds(5);
                while (tab.ElasticLoading && DateTime.UtcNow < timeout)
                {
                    state.DrainTailerEvents();
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(10);
                }
                Assert.False(tab.ElasticLoading);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ResultMessagesDistinguishLoadingFailureEmptySearchAndExclusions()
    {
        var path = Path.GetTempFileName();
        var window = TestWindow.Create(out var workspace);
        try
        {
            await workspace.OpenPathsCommand.Execute([path]);
            var file = workspace.SelectedFile!;
            window.Show();
            Dispatcher.UIThread.RunJobs();

            TextBlock Message() =>
                window
                    .GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Single(block => block.Name == "ResultMessage");
            TextBlock Count() =>
                window
                    .GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Single(block => block.Name == "ResultCount");

            Assert.Equal("No logs in this source yet.", Message().Text);
            Assert.Equal("0 visible · 0 loaded from source", Count().Text);
            file.Model.ElasticLoading = true;
            file.SyncViews();
            Assert.Equal("Loading logs for the applied interval…", Message().Text);
            file.Model.ElasticLoading = false;
            file.Model.Error = "Source error: server unavailable";
            file.SyncViews();
            Assert.Equal("Source error: server unavailable", Message().Text);
            file.Model.Error = null;
            file.Model.Buffer.Append([new Line("alpha"), new Line("beta")]);
            file.SyncViews();
            Assert.False(Message().IsVisible);
            Assert.Equal("2 visible · 2 loaded from source", Count().Text);

            workspace.State.AddSearch(file.Model, "gamma", MatchMode.Literal, false, "#f59e0b");
            file.SyncViews();
            file.SelectedViewIndex = 1;
            file.SyncViews();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("No logs match this search.", Message().Text);
            Assert.Equal("0 visible · 2 loaded from source", Count().Text);

            file.SelectedViewIndex = 0;
            await workspace.State.UpdateSettingsAsync(
                workspace.State.Settings with
                {
                    GlobalExcludeLabels = ["alpha", "beta"],
                }
            );
            file.SyncViews();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("All matching logs are hidden by global exclusions.", Message().Text);
            Assert.Equal("0 visible · 2 loaded from source", Count().Text);
        }
        finally
        {
            window.Close();
            await workspace.DisposeAsync();
            File.Delete(path);
        }
    }
}
