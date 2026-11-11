using System.Globalization;
using System.Reactive.Concurrency;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HexTail.Application;
using HexTail.Domain;
using HexTail.Elastic;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.ViewModels;

public sealed class LogOverviewTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] FiveRows =
    [
        "2026-01-01T10:00:00Z ERROR boom",
        "2026-01-01T10:00:10Z WARN slow",
        "2026-01-01T10:00:20Z INFO ok",
        "2026-01-01T10:00:30Z DEBUG a",
        "2026-01-01T10:00:40Z DEBUG b",
    ];

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory = Directory
            .CreateTempSubdirectory("hextail-overview-")
            .FullName;

        private Harness()
        {
            Client = new FakeElasticApiClient
            {
                SearchHandler = _ => Task.FromResult(new ElasticSearchPage("pit", [])),
            };
            Workspace = new MainWindowViewModel(
                new AppState(
                    new LogSourceService(
                        new TailerOptions
                        {
                            PollInterval = TimeSpan.FromMilliseconds(20),
                            UseFileSystemWatcher = false,
                        }
                    ),
                    new TestPersistence(),
                    new AppSettings
                    {
                        TimeZoneMode = AppTimeZoneMode.Utc,
                        ElasticConnections = [ElasticFixtures.Connection("c-1", "prod", "app")],
                    },
                    credentials: new InMemoryCredentialVault(),
                    elastic: Client
                ),
                scheduler: ImmediateScheduler.Instance,
                startPolling: false
            );
        }

        public FakeElasticApiClient Client { get; }
        public MainWindowViewModel Workspace { get; }
        public AppState State => Workspace.State;
        public FileTabState Tab { get; private set; } = null!;
        public string Path { get; private set; } = null!;
        public LogViewViewModel View => Workspace.SelectedFile!.SelectedView;
        public LogOverviewViewModel Overview => View.Overview;

        public static async Task<Harness> CreateAsync(params string[] lines)
        {
            var h = new Harness();
            var path = System.IO.Path.Combine(h._directory, "a.log");
            h.Path = path;
            await File.WriteAllLinesAsync(path, lines, Ct);
            h.Tab = await h.State.OpenFileAsync(path, cancellationToken: Ct);
            await UiWait.Until(() =>
            {
                h.State.DrainTailerEvents();
                return h.Tab.Buffer.Count == lines.Length;
            });
            await UiWait.Until(() => ReferenceEquals(h.Workspace.SelectedFile?.Model, h.Tab));
            h.Sync();
            return h;
        }

        public void Sync()
        {
            Dispatcher.UIThread.RunJobs();
            State.DrainTailerEvents();
            Workspace.SelectedFile!.SyncViews();
            Workspace.SelectedFile.SelectedView.Sync();
        }

        public async ValueTask DisposeAsync()
        {
            await State.DisposeAsync();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException) { }
        }
    }

    private static ViewRule Highlight(string pattern, string color) =>
        new()
        {
            Pattern = pattern,
            Color = color,
            Action = RuleAction.Highlight,
            Scope = RuleScope.Source,
        };

    [AvaloniaFact]
    public async Task BuildHistogram_CountsTheDisplayedRows_AndHidesWhenNoneHaveTimestamps()
    {
        await using (var h = await Harness.CreateAsync(FiveRows))
        {
            var histogram = h.Overview.BuildHistogram(100);

            Assert.NotNull(histogram);
            Assert.Equal(5, histogram.Buckets.Sum(bucket => bucket.Total));
            Assert.True(h.Overview.HasTimeline);
        }

        await using var plain = await Harness.CreateAsync("plain a", "plain b");
        Assert.True(plain.Overview.HasRows);
        Assert.Null(plain.Overview.BuildHistogram(100));
        Assert.False(plain.Overview.HasTimeline);
    }

    [AvaloniaFact]
    public async Task HasTimeline_FollowsTheRowsOnSync_WithoutBuildingAHistogram()
    {
        await using var h = await Harness.CreateAsync(FiveRows);
        var raised = new List<string?>();
        h.Overview.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.True(h.Overview.HasTimeline);
        Assert.True(h.Overview.HasRows);
        h.Overview.Invalidate();
        Assert.Contains(nameof(LogOverviewViewModel.HasRows), raised);

        await using var plain = await Harness.CreateAsync("plain a", "plain b");
        Assert.False(plain.Overview.HasTimeline);
    }

    [AvaloniaFact]
    public async Task BuildMarkers_UsesSearchColourFirst_ThenHighlightRules_AndMarksErrors()
    {
        await using var h = await Harness.CreateAsync(FiveRows);
        h.State.AddSearch(h.Tab, "slow", MatchMode.Literal, false, "#112233");
        h.Tab.SetRules([Highlight("slow", "#AA0000"), Highlight("ok", "#00BB00")]);
        h.Sync();

        var markers = h.Overview.BuildMarkers();

        Assert.Equal(
            [
                new ScrollMarker(0, null, true),
                new ScrollMarker(1, "#112233", false),
                new ScrollMarker(2, "#00BB00", false),
            ],
            markers
        );
    }

    [AvaloniaFact]
    public async Task BuildMarkers_InASearchView_IgnoresThatViewsOwnSearch()
    {
        await using var h = await Harness.CreateAsync(FiveRows);
        h.State.AddSearch(h.Tab, "slow", MatchMode.Literal, false, "#112233");
        h.Sync();
        var searchView = h.Workspace.SelectedFile!.Views[1];
        searchView.Sync();

        Assert.Single(searchView.Lines);
        Assert.Empty(searchView.Overview.BuildMarkers());
        Assert.Equal(
            "#112233",
            h.Workspace.SelectedFile.Views[0].Overview.BuildMarkers()[1].RuleColor
        );
    }

    [AvaloniaFact]
    public async Task BuildMarkers_RecomputesAfterARuleChange()
    {
        await using var h = await Harness.CreateAsync(FiveRows);
        h.Tab.SetRules([Highlight("slow", "#AA0000")]);
        h.Sync();
        Assert.Equal(
            "#AA0000",
            Assert.Single(h.Overview.BuildMarkers(), m => m.Row == 1).RuleColor
        );

        h.Tab.SetRules([Highlight("slow", "#00CC00")]);
        h.Sync();

        Assert.Equal(
            "#00CC00",
            Assert.Single(h.Overview.BuildMarkers(), m => m.Row == 1).RuleColor
        );
    }

    [AvaloniaFact]
    public async Task DescribeBucket_UsesTheExactFormat()
    {
        await using var h = await Harness.CreateAsync(FiveRows);
        var histogram = h.Overview.BuildHistogram(1)!;

        Assert.Single(histogram.Buckets);
        Assert.Equal(
            "10:00:00 – 10:01:00 · 5 rows (1 error, 1 warn, 1 info, 2 debug)",
            h.Overview.DescribeBucket(histogram, 0)
        );
    }

    [AvaloniaFact]
    public async Task DescribeBucket_OmitsTheBreakdownWhenEmpty_AndShowsDatesAcrossDays()
    {
        await using var h = await Harness.CreateAsync(
            "2026-01-01T10:00:00Z INFO a",
            "2026-01-03T10:00:00Z INFO b"
        );
        var histogram = h.Overview.BuildHistogram(4)!;

        var first = h.Overview.DescribeBucket(histogram, 0);
        var empty = h.Overview.DescribeBucket(histogram, 1);

        Assert.Equal("01-01 00:00 – 01-02 00:00 · 1 rows (1 info)", first);
        Assert.Equal("01-02 00:00 – 01-03 00:00 · 0 rows", empty);
    }

    [AvaloniaFact]
    public async Task JumpToBucket_SelectsTheFirstRowInTheBucket_AndRaisesSelectionMoved()
    {
        await using var h = await Harness.CreateAsync(FiveRows);
        var histogram = h.Overview.BuildHistogram(100)!;
        LogLineViewModel? moved = null;
        h.View.SelectionMoved += row => moved = row;

        // One-second buckets: row 2 sits at 10:00:20.
        h.Overview.JumpToBucket(histogram, 20);

        Assert.Same(h.View.Lines[2], moved);
        Assert.Equal(2, h.Tab.SelectedLine);
    }

    [AvaloniaFact]
    public async Task JumpToBucket_OnAnEmptyBucket_DoesNothing()
    {
        await using var h = await Harness.CreateAsync(FiveRows);
        var histogram = h.Overview.BuildHistogram(100)!;
        var raised = false;
        h.View.SelectionMoved += _ => raised = true;
        var before = h.Tab.SelectedLine;

        h.Overview.JumpToBucket(histogram, 5);

        Assert.False(raised);
        Assert.Equal(before, h.Tab.SelectedLine);
    }

    [AvaloniaFact]
    public async Task JumpToFraction_SelectsTheRowAtThatHeight()
    {
        await using var h = await Harness.CreateAsync(FiveRows.Take(4).ToArray());
        var selected = new List<LogLineViewModel>();
        h.View.SelectionMoved += selected.Add;

        h.Overview.JumpToFraction(0.0);
        h.Overview.JumpToFraction(0.999);
        h.Overview.JumpToFraction(0.5);
        h.Overview.JumpToFraction(1.5);
        h.Overview.JumpToFraction(-0.5);

        Assert.Equal(
            [h.View.Lines[0], h.View.Lines[3], h.View.Lines[2], h.View.Lines[3], h.View.Lines[0]],
            selected
        );
    }

    [AvaloniaFact]
    public async Task ApplyRange_OnAnElasticTab_RequeriesWithIsoBounds_AndCapturesThePriorRange()
    {
        await using var h = await Harness.CreateAsync(FiveRows);
        var tab = await h.State.OpenElasticSourceAsync(
            "c-1-app",
            cancellationToken: Ct,
            from: "2026-01-01 08:00:00",
            to: "2026-01-01 09:00:00",
            inputZone: AppTimeZoneMode.Local
        );
        await UiWait.Until(() => ReferenceEquals(h.Workspace.SelectedFile?.Model, tab));
        var from = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 1, 1, 10, 5, 0, TimeSpan.Zero);

        h.Overview.ApplyRange(from, to);

        Assert.Equal(from.ToString("O", CultureInfo.InvariantCulture), tab.ElasticFrom);
        Assert.Equal(to.ToString("O", CultureInfo.InvariantCulture), tab.ElasticTo);
        Assert.Equal(
            new HistogramPrior(
                "2026-01-01 08:00:00",
                "2026-01-01 09:00:00",
                false,
                AppTimeZoneMode.Local
            ),
            tab.HistogramPrior
        );

        h.Overview.ResetRange();

        Assert.Equal("2026-01-01 08:00:00", tab.ElasticFrom);
        Assert.Equal("2026-01-01 09:00:00", tab.ElasticTo);
        Assert.Equal(AppTimeZoneMode.Local, tab.ElasticInputZone);
        Assert.Null(tab.HistogramPrior);
    }

    [AvaloniaFact]
    public async Task Invalidate_BumpsVersion()
    {
        await using var h = await Harness.CreateAsync(FiveRows);
        var before = h.Overview.Version;
        var changes = new List<string?>();
        h.Overview.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        h.Overview.Invalidate();

        Assert.Equal(before + 1, h.Overview.Version);
        Assert.Contains(nameof(LogOverviewViewModel.Version), changes);
        Assert.Contains(nameof(LogOverviewViewModel.HasRows), changes);
    }

    private static DateTimeOffset At(int second) => new(2026, 1, 1, 10, 0, second, TimeSpan.Zero);

    [AvaloniaFact]
    public async Task Sync_WithNothingChanged_DoesNotBumpVersion_ButARowRuleOrSearchChangeDoes()
    {
        await using var h = await Harness.CreateAsync(FiveRows);
        h.Sync();
        var version = h.Overview.Version;

        h.Sync();
        h.Sync();
        Assert.Equal(version, h.Overview.Version);

        await File.AppendAllLinesAsync(h.Path, ["2026-01-01T10:00:50Z INFO late"], Ct);
        await UiWait.Until(() =>
        {
            h.Sync();
            return h.View.Lines.Count == 6;
        });
        Assert.True(h.Overview.Version > version);

        version = h.Overview.Version;
        h.Sync();
        Assert.Equal(version, h.Overview.Version);

        h.Tab.SetRules([Highlight("slow", "#AA0000")]);
        h.Sync();
        Assert.True(h.Overview.Version > version);

        version = h.Overview.Version;
        h.State.AddSearch(h.Tab, "slow", MatchMode.Literal, false, "#112233");
        h.Sync();
        Assert.True(h.Overview.Version > version);

        version = h.Overview.Version;
        h.Workspace.ApplyHistogramRange(At(10), At(30));
        h.Sync();
        Assert.True(h.Overview.Version > version);
    }

    [AvaloniaFact]
    public async Task HasTimeline_StaysTrueWhileThereIsAPrior_EvenWithNoRowsLeft()
    {
        await using var h = await Harness.CreateAsync(FiveRows);
        Assert.True(h.Overview.HasTimeline);

        h.Workspace.ApplyHistogramRange(At(50), At(55));
        h.Sync();

        Assert.False(h.Overview.HasRows);
        Assert.NotNull(h.Tab.HistogramPrior);
        Assert.True(h.Overview.HasTimeline);
        Assert.Null(h.Overview.BuildHistogram(100));
        Assert.True(h.Overview.HasTimeline);

        h.Workspace.ResetHistogramRange();
        h.Sync();

        Assert.True(h.Overview.HasRows);
        Assert.True(h.Overview.HasTimeline);
    }

    [AvaloniaFact]
    public async Task HasTimeline_StaysTrueWhileAnElasticTabLoads()
    {
        await using var h = await Harness.CreateAsync(FiveRows);
        var tab = await h.State.OpenElasticSourceAsync("c-1-app", cancellationToken: Ct);
        await UiWait.Until(() => ReferenceEquals(h.Workspace.SelectedFile?.Model, tab));
        await UiWait.Until(() =>
        {
            h.State.DrainTailerEvents();
            return !tab.ElasticLoading;
        });
        h.Sync();
        Assert.False(h.Overview.HasTimeline);

        tab.ElasticLoading = true;
        h.Sync();

        Assert.True(h.Overview.HasTimeline);

        tab.ElasticLoading = false;
        h.Sync();

        Assert.False(h.Overview.HasTimeline);
    }

    [AvaloniaFact]
    public async Task JumpToBucket_SelectsTheRowBuiltForTheBucket_EvenWhenTheRowsShifted()
    {
        await using var h = await Harness.CreateAsync(FiveRows);
        var histogram = h.Overview.BuildHistogram(100)!;
        // The view moves on (rows hide) before the histogram is rebuilt: "ok" is now the first row.
        h.Workspace.ApplyHistogramRange(At(15), At(45));
        h.Sync();
        Assert.Equal(3, h.View.Lines.Count);
        var selected = new List<LogLineViewModel>();
        h.View.SelectionMoved += selected.Add;

        h.Overview.JumpToBucket(histogram, 20);
        h.Overview.JumpToBucket(histogram, 0);

        Assert.Same(h.View.Lines[0], Assert.Single(selected));
        Assert.EndsWith("INFO ok", selected[0].Line.Raw);
    }
}
