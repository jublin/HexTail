using System.Globalization;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HexTail.Application;
using HexTail.Domain;
using HexTail.Elastic;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.Ui;

public sealed class StreamRangeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static string Stamp(TimeSpan ago) =>
        (Now - ago).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>A stream of one file plus one Elastic source (or a second file) behind the real workspace view model.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly bool _ownsDirectory;

        public string Folder => _directory;

        public TestPersistence Persistence { get; }
        public FakeElasticApiClient Client { get; }
        public LogSourceService Tailers { get; }
        public MainWindowViewModel Workspace { get; }
        public FileTabState File { get; private set; } = null!;
        public FileTabState? Elastic { get; private set; }
        public FileTabState Stream { get; private set; } = null!;

        public AppState State => Workspace.State;
        public LogViewViewModel View => Workspace.SelectedFile!.SelectedView;

        private Harness(bool elastic, TestPersistence persistence, string? directory = null)
        {
            _ownsDirectory = directory is null;
            _directory =
                directory ?? Directory.CreateTempSubdirectory("hextail-stream-range-").FullName;
            Persistence = persistence;
            Client = new FakeElasticApiClient
            {
                SearchHandler = _ => Task.FromResult(new ElasticSearchPage("pit", [])),
            };
            Tailers = new LogSourceService(
                new TailerOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(20),
                    UseFileSystemWatcher = false,
                }
            );
            var state = new AppState(
                Tailers,
                persistence,
                new AppSettings
                {
                    TimeZoneMode = AppTimeZoneMode.Utc,
                    ElasticConnections = elastic
                        ? [ElasticFixtures.Connection("c-1", "prod", "app")]
                        : [],
                },
                credentials: new InMemoryCredentialVault(),
                elastic: Client
            );
            Workspace = new MainWindowViewModel(
                state,
                scheduler: ImmediateScheduler.Instance,
                startPolling: false
            );
        }

        public static async Task<Harness> CreateAsync(
            bool elastic,
            TestPersistence? persistence = null
        )
        {
            var h = new Harness(elastic, persistence ?? new TestPersistence());
            await h.BuildAsync(elastic);
            return h;
        }

        public static async Task<Harness> RestoreAsync(
            bool elastic,
            TestPersistence persistence,
            string directory
        )
        {
            var h = new Harness(elastic, persistence, directory);
            await h.State.RestoreAsync(Ct);
            h.Stream = h.State.Files.Single(f => f.Source.Kind == LogSourceKind.Stream);
            h.File = h.State.Files.First(f => f.Source.Kind == LogSourceKind.File);
            h.Elastic = h.State.Files.FirstOrDefault(f => f.Source.Kind == LogSourceKind.Elastic);
            await UiWait.Until(() => ReferenceEquals(h.Workspace.SelectedFile?.Model, h.Stream));
            return h;
        }

        private async Task BuildAsync(bool elastic)
        {
            var path = Path.Combine(_directory, "a.log");
            // The first line has no timestamp; then one an hour old and one five minutes old.
            await System.IO.File.WriteAllLinesAsync(
                path,
                [
                    "plain header",
                    $"{Stamp(TimeSpan.FromHours(1))} INFO old",
                    $"{Stamp(TimeSpan.FromMinutes(5))} INFO recent",
                ],
                Ct
            );
            File = await State.OpenFileAsync(path, cancellationToken: Ct);
            await UiWait.Until(() =>
            {
                State.DrainTailerEvents();
                return File.Buffer.Count == 3;
            });
            FileTabState second;
            if (elastic)
                second = Elastic = await State.OpenElasticSourceAsync(
                    "c-1-app",
                    cancellationToken: Ct
                );
            else
            {
                // A stream needs two members; a second file keeps the "no Elastic source" case honest.
                var other = Path.Combine(_directory, "b.log");
                await System.IO.File.WriteAllLinesAsync(
                    other,
                    [$"{Stamp(TimeSpan.FromMinutes(30))} INFO other"],
                    Ct
                );
                second = await State.OpenFileAsync(other, cancellationToken: Ct);
                await UiWait.Until(() =>
                {
                    State.DrainTailerEvents();
                    return second.Buffer.Count == 1;
                });
            }
            var made = await State.CreateStreamAsync("Mixed", [File, second], Ct);
            Stream = (await State.OpenStreamAsync(made.Id, Ct))!;
            await UiWait.Until(() => ReferenceEquals(Workspace.SelectedFile?.Model, Stream));
        }

        public string[] Visible()
        {
            Dispatcher.UIThread.RunJobs();
            State.DrainTailerEvents();
            View.Sync();
            return [.. View.Lines.Select(row => row.Line.Raw)];
        }

        public async ValueTask DisposeAsync()
        {
            await State.DisposeAsync();
            if (!_ownsDirectory)
                return;
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException) { }
        }
    }

    // True when a row is "<time> INFO <word>" (so "old" does not match "elastic-old").
    private static bool Has(string[] rows, string word) =>
        rows.Any(row => row.EndsWith(" INFO " + word, StringComparison.Ordinal));

    [AvaloniaFact]
    public async Task ApplyingARangeOnAStream_SendsItToEveryOpenElasticMember()
    {
        await using var h = await Harness.CreateAsync(elastic: true);

        await h.Workspace.ApplyRangePresetCommand.Execute("now-15m").FirstAsync();

        Assert.Null(h.Workspace.RangeError);
        Assert.Equal("now-15m", h.Stream.Stream!.RangeFrom);
        Assert.Equal("now", h.Stream.Stream.RangeTo);
        Assert.False(h.Stream.Stream.Live);
        // The member's own pill state changed to the stream's range.
        Assert.Equal("now-15m", h.Elastic!.ElasticFrom);
        Assert.Equal("now", h.Elastic.ElasticTo);
        // And its tailer queries the new window.
        await UiWait.Until(
            () =>
            {
                var searches = h.Client.Searches.ToArray();
                return searches.Any(s =>
                    Math.Abs((DateTimeOffset.UtcNow - s.FromInclusive).TotalMinutes - 15) < 1
                );
            },
            timeoutMs: 15_000
        );
    }

    [AvaloniaFact]
    public async Task ApplyingARange_FiltersFileRowsByTimestamp_AndKeepsRowsWithoutOne()
    {
        await using var h = await Harness.CreateAsync(elastic: false);
        Assert.True(h.Stream.Stream!.Live);
        var all = h.Visible();
        Assert.Equal(4, all.Length);

        await h.Workspace.ApplyRangePresetCommand.Execute("now-15m").FirstAsync();
        var filtered = h.Visible();

        Assert.True(Has(filtered, "recent"));
        Assert.False(Has(filtered, "old"));
        Assert.False(Has(filtered, "other"));
        Assert.Contains("plain header", filtered);
        Assert.Equal(2, filtered.Length);
        // Only the stream's own rows changed: the members keep their lines.
        Assert.Equal(3, h.File.Buffer.Count);
    }

    [AvaloniaFact]
    public async Task AnAbsoluteRange_FiltersBothEnds()
    {
        await using var h = await Harness.CreateAsync(elastic: false);
        h.Workspace.RangeFromText = Stamp(TimeSpan.FromMinutes(40));
        h.Workspace.ElasticToNow = false;
        h.Workspace.RangeToText = Stamp(TimeSpan.FromMinutes(10));

        await h.Workspace.ApplyElasticTimeRangeCommand.Execute().FirstAsync();
        var rows = h.Visible();

        Assert.Null(h.Workspace.RangeError);
        Assert.True(Has(rows, "other"));
        Assert.False(Has(rows, "recent"));
        Assert.False(Has(rows, "old"));
        Assert.Contains("plain header", rows);
    }

    [AvaloniaFact]
    public async Task Live_RemovesTheFileFilter()
    {
        await using var h = await Harness.CreateAsync(elastic: false);
        await h.Workspace.ApplyRangePresetCommand.Execute("now-15m").FirstAsync();
        Assert.Equal(2, h.Visible().Length);

        await h.Workspace.ClearStreamRangeCommand.Execute().FirstAsync();

        Assert.True(h.Stream.Stream!.Live);
        Assert.Equal(4, h.Visible().Length);
        Assert.StartsWith("Live", h.Workspace.ElasticRangePill);
    }

    [AvaloniaFact]
    public async Task ElasticMemberRows_AreNotFilteredAgain()
    {
        await using var h = await Harness.CreateAsync(elastic: true);
        var tailer = (ElasticTailer)h.Elastic!.Tailer;
        await tailer.DisposeAsync();
        await h.Workspace.ApplyRangePresetCommand.Execute("now-15m").FirstAsync();
        var oldRow = new Line($"{Stamp(TimeSpan.FromHours(2))} INFO elastic-old")
        {
            Timestamp = Now - TimeSpan.FromHours(2),
            SourceId = h.Elastic.Id,
        };
        Assert.True(
            h.Tailers.Publish(
                new SourceLines(h.Elastic.Id, [oldRow])
                {
                    Generation = tailer.Generation,
                    InstanceId = tailer.InstanceId,
                }
            )
        );
        await UiWait.Until(() => Has(h.Visible(), "elastic-old"));

        var rows = h.Visible();

        Assert.False(Has(rows, "old"));
        Assert.True(Has(rows, "elastic-old"));
        Assert.True(Has(rows, "recent"));
    }

    [AvaloniaFact]
    public async Task TheRange_IsSavedAndRestoredWithTheStream()
    {
        var persistence = new TestPersistence();
        await using var first = await Harness.CreateAsync(elastic: false, persistence);
        await first.Workspace.ApplyRangePresetCommand.Execute("now-15m").FirstAsync();
        await first.State.SaveAsync(Ct);
        var saved = Assert.Single(persistence.Config!.OpenStreams);
        Assert.Equal(("now-15m", "now", false), (saved.From, saved.To, saved.Live));

        await using var restored = await Harness.RestoreAsync(
            elastic: false,
            persistence,
            first.Folder
        );

        Assert.Equal("now-15m", restored.Stream.Stream!.RangeFrom);
        Assert.False(restored.Stream.Stream.Live);
        await UiWait.Until(() => !Has(restored.Visible(), "old") && restored.Visible().Length > 0);
        Assert.True(Has(restored.Visible(), "recent"));
        Assert.Equal("now-15m", restored.Workspace.RangeFromText);
    }

    [AvaloniaFact]
    public async Task ALiveStream_IsRestoredLive()
    {
        var persistence = new TestPersistence();
        await using var first = await Harness.CreateAsync(elastic: false, persistence);
        await first.State.SaveAsync(Ct);

        await using var restored = await Harness.RestoreAsync(
            elastic: false,
            persistence,
            first.Folder
        );

        Assert.True(restored.Stream.Stream!.Live);
        await UiWait.Until(() => restored.Visible().Length == 4);
    }

    [AvaloniaFact]
    public async Task AStreamWithoutElasticMembers_SaysThePillOnlyFiltersFiles()
    {
        await using var h = await Harness.CreateAsync(elastic: false);
        Assert.True(h.Workspace.IsTimeRangeSelected);
        Assert.False(h.Workspace.IsElasticSelected);

        await h.Workspace.ApplyRangePresetCommand.Execute("now-1h").FirstAsync();

        Assert.StartsWith("Last hour", h.Workspace.ElasticRangePill);
        Assert.Equal(
            "Filters rows from files; no Elastic sources in this stream",
            h.Workspace.ElasticAppliedRange
        );
    }

    [AvaloniaFact]
    public async Task AStreamWithElasticMembers_DescribesWhereTheRangeApplies()
    {
        await using var h = await Harness.CreateAsync(elastic: true);

        await h.Workspace.ApplyRangePresetCommand.Execute("now-1h").FirstAsync();

        Assert.DoesNotContain("no Elastic sources", h.Workspace.ElasticAppliedRange);
        Assert.Contains("1 Elastic source", h.Workspace.ElasticAppliedRange);
    }

    [AvaloniaFact]
    public async Task ThePill_ShowsForStreamsAndFilesButOnlyTimeRangeTabs()
    {
        await using var h = await Harness.CreateAsync(elastic: false);
        var window = new MainWindow(h.Workspace, registerNativePicker: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var panel = window.FindControl<StackPanel>("ElasticTimeRangePanel")!;

        Assert.True(panel.IsVisible);

        h.State.SelectFile(h.File);
        await UiWait.Until(() => ReferenceEquals(h.Workspace.SelectedFile?.Model, h.File));
        Dispatcher.UIThread.RunJobs();
        Assert.False(panel.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ARangeWithFromAfterTo_IsRejectedAndChangesNothing()
    {
        await using var h = await Harness.CreateAsync(elastic: true);
        h.Workspace.RangeFromText = Stamp(TimeSpan.FromMinutes(10));
        h.Workspace.ElasticToNow = false;
        h.Workspace.RangeToText = Stamp(TimeSpan.FromMinutes(40));

        await h.Workspace.ApplyElasticTimeRangeCommand.Execute().FirstAsync();

        Assert.Contains("From must be at or before To", h.Workspace.RangeError);
        Assert.True(h.Stream.Stream!.Live);
        Assert.Equal("now-5m", h.Elastic!.ElasticFrom);
    }

    [AvaloniaFact]
    public async Task AMemberRangeChange_RebuildsTheStreamWithoutLosingTheStreamRange()
    {
        await using var h = await Harness.CreateAsync(elastic: true);
        await h.Workspace.ApplyRangePresetCommand.Execute("now-15m").FirstAsync();
        Assert.False(Has(h.Visible(), "old"));

        // The member's own pill moves; the stream keeps its range and re-merges.
        h.State.SetElasticTimeRange(h.Elastic!, "now-2h", "now-1h");
        h.State.DrainTailerEvents();

        Assert.Equal("now-15m", h.Stream.Stream!.RangeFrom);
        Assert.Equal("now", h.Stream.Stream.RangeTo);
        Assert.False(h.Stream.Stream.Live);
        var rows = h.Visible();
        Assert.True(Has(rows, "recent"));
        Assert.False(Has(rows, "old"));
        Assert.Contains("plain header", rows);
    }
}
