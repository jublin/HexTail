using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HexTail.Application;
using HexTail.Domain;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.Application;

public sealed class FileTimeWindowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset At(int hour, int minute, int second) =>
        new(2026, 1, 1, hour, minute, second, TimeSpan.Zero);

    private sealed class NullTailer : ILogTailer
    {
        public string SourceId => "t";
        public string DisplayName => "t";
        public Task Completion => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory = Directory
            .CreateTempSubdirectory("hextail-file-window-")
            .FullName;

        private Harness()
        {
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
                    new AppSettings { TimeZoneMode = AppTimeZoneMode.Utc },
                    credentials: new InMemoryCredentialVault()
                ),
                scheduler: ImmediateScheduler.Instance,
                startPolling: false
            );
        }

        public MainWindowViewModel Workspace { get; }
        public AppState State => Workspace.State;
        public FileTabState Tab { get; private set; } = null!;
        public LogViewViewModel View => Workspace.SelectedFile!.SelectedView;

        public static async Task<Harness> CreateAsync()
        {
            var h = new Harness();
            var path = Path.Combine(h._directory, "a.log");
            // The continuation line has no timestamp of its own and inherits 10:00:05 from its parent.
            await File.WriteAllLinesAsync(
                path,
                [
                    "2026-01-01T10:00:00Z INFO a",
                    "2026-01-01T10:00:05Z INFO b",
                    "  continuation of b",
                    "2026-01-01T10:00:10Z INFO c",
                ],
                Ct
            );
            h.Tab = await h.State.OpenFileAsync(path, cancellationToken: Ct);
            await UiWait.Until(() =>
            {
                h.State.DrainTailerEvents();
                return h.Tab.Buffer.Count == 4;
            });
            await UiWait.Until(() => ReferenceEquals(h.Workspace.SelectedFile?.Model, h.Tab));
            return h;
        }

        public static async Task<Harness> CreateStreamAsync()
        {
            var h = new Harness();
            var first = Path.Combine(h._directory, "a.log");
            await File.WriteAllLinesAsync(
                first,
                ["2026-01-01T10:00:00Z INFO a", "2026-01-01T10:00:10Z INFO c"],
                Ct
            );
            var second = Path.Combine(h._directory, "b.log");
            await File.WriteAllLinesAsync(second, ["2026-01-01T10:00:05Z INFO b"], Ct);
            var one = await h.State.OpenFileAsync(first, cancellationToken: Ct);
            var two = await h.State.OpenFileAsync(second, cancellationToken: Ct);
            await UiWait.Until(() =>
            {
                h.State.DrainTailerEvents();
                return one.Buffer.Count == 2 && two.Buffer.Count == 1;
            });
            var made = await h.State.CreateStreamAsync("Mixed", [one, two], Ct);
            h.Tab = (await h.State.OpenStreamAsync(made.Id, Ct))!;
            await UiWait.Until(() => ReferenceEquals(h.Workspace.SelectedFile?.Model, h.Tab));
            return h;
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
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task FileWindow_FiltersTimestampedRows_AndKeepsUntimedOnes()
    {
        await using var h = await Harness.CreateAsync();
        Assert.Equal(4, h.Visible().Length);
        Assert.False(h.Workspace.FilterBar.HasTimeWindow);

        h.Workspace.ApplyHistogramRange(At(10, 0, 4), At(10, 0, 6));
        var rows = h.Visible();

        Assert.Equal(["2026-01-01T10:00:05Z INFO b", "  continuation of b"], rows);
        Assert.Equal(new TimeWindow(At(10, 0, 4), At(10, 0, 6)), h.Tab.TimeWindow);
        Assert.True(h.Workspace.FilterBar.HasTimeWindow);
        Assert.StartsWith("Time ", h.Workspace.FilterBar.TimeWindowLabel);

        await h.Workspace.FilterBar.ClearTimeWindowCommand.Execute().FirstAsync();

        Assert.Null(h.Tab.TimeWindow);
        Assert.Null(h.Tab.HistogramPrior);
        Assert.False(h.Workspace.FilterBar.HasTimeWindow);
        Assert.Equal(4, h.Visible().Length);
    }

    [AvaloniaFact]
    public async Task FileWindow_Reset_RestoresThePreviousWindow_ThenClears()
    {
        await using var h = await Harness.CreateAsync();

        h.Workspace.ApplyHistogramRange(At(10, 0, 4), At(10, 0, 6));
        h.Workspace.ApplyHistogramRange(At(10, 0, 9), At(10, 0, 11));
        Assert.Equal(new TimeWindow(At(10, 0, 9), At(10, 0, 11)), h.Tab.TimeWindow);
        Assert.Equal(["2026-01-01T10:00:10Z INFO c"], h.Visible());

        // The prior was captured before the first drag, when there was no window.
        h.Workspace.ResetHistogramRange();
        Assert.Null(h.Tab.TimeWindow);
        Assert.Null(h.Tab.HistogramPrior);
        Assert.Equal(4, h.Visible().Length);

        h.Workspace.ResetHistogramRange();
        Assert.Null(h.Tab.TimeWindow);
        Assert.Equal(4, h.Visible().Length);
    }

    [AvaloniaFact]
    public async Task FileWindow_Reset_ReturnsToAWindowThatWasThereBeforeTheDrag()
    {
        await using var h = await Harness.CreateAsync();
        h.State.SetFileTimeWindow(h.Tab, new TimeWindow(At(10, 0, 0), At(10, 0, 6)));

        h.Workspace.ApplyHistogramRange(At(10, 0, 4), At(10, 0, 6));
        h.Workspace.ResetHistogramRange();

        Assert.Equal(new TimeWindow(At(10, 0, 0), At(10, 0, 6)), h.Tab.TimeWindow);
        Assert.Null(h.Tab.HistogramPrior);
    }

    [AvaloniaFact]
    public async Task Stream_HistogramRange_SetsTheStreamRange_AndResetRestoresAllRows()
    {
        await using var h = await Harness.CreateStreamAsync();
        Assert.True(h.Tab.Stream!.Live);
        Assert.Equal(3, h.Visible().Length);

        h.Workspace.ApplyHistogramRange(At(10, 0, 4), At(10, 0, 6));
        var rows = h.Visible();

        Assert.False(h.Tab.Stream.Live);
        Assert.Equal(At(10, 0, 4).ToString("O"), h.Tab.Stream.RangeFrom);
        Assert.Equal(At(10, 0, 6).ToString("O"), h.Tab.Stream.RangeTo);
        Assert.Equal(["2026-01-01T10:00:05Z INFO b"], rows);
        Assert.Null(h.Workspace.RangeError);

        h.Workspace.ResetHistogramRange();

        Assert.True(h.Tab.Stream.Live);
        Assert.Null(h.Tab.HistogramPrior);
        Assert.Equal(3, h.Visible().Length);
    }

    [AvaloniaFact]
    public async Task PillApply_ClearsThePrior()
    {
        await using var h = await Harness.CreateStreamAsync();
        h.Workspace.ApplyHistogramRange(At(10, 0, 4), At(10, 0, 6));
        Assert.NotNull(h.Tab.HistogramPrior);

        h.Workspace.RangeFromText = "2026-01-01 10:00:00";
        h.Workspace.ElasticToNow = false;
        h.Workspace.RangeToText = "2026-01-01 10:00:07";
        await h.Workspace.ApplyElasticTimeRangeCommand.Execute().FirstAsync();

        Assert.Null(h.Workspace.RangeError);
        Assert.Null(h.Tab.HistogramPrior);
        Assert.Equal(2, h.Visible().Length);

        // With no prior, a reset on a stream shows all rows.
        h.Workspace.ResetHistogramRange();
        Assert.True(h.Tab.Stream!.Live);
        Assert.Equal(3, h.Visible().Length);
    }

    [AvaloniaFact]
    public async Task ShowAllRows_ClearsThePrior()
    {
        await using var h = await Harness.CreateStreamAsync();
        h.Workspace.ApplyHistogramRange(At(10, 0, 4), At(10, 0, 6));
        Assert.NotNull(h.Tab.HistogramPrior);

        await h.Workspace.ClearStreamRangeCommand.Execute().FirstAsync();

        Assert.Null(h.Tab.HistogramPrior);
        Assert.True(h.Tab.Stream!.Live);
    }

    [Fact]
    public async Task SetFileTimeWindow_RejectsReversedBounds_AndElasticOrStreamTabs()
    {
        await using var state = new AppState(
            new LogSourceService(
                new TailerOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(10),
                    UseFileSystemWatcher = false,
                }
            ),
            new TestPersistence(),
            credentials: new InMemoryCredentialVault()
        );
        var path = Path.Combine(Path.GetTempPath(), $"hextail-{Guid.NewGuid():N}.log");
        await File.WriteAllTextAsync(path, "2026-01-01T10:00:00Z INFO a\n", Ct);
        try
        {
            var tab = await state.OpenFileAsync(path, save: false, cancellationToken: Ct);
            var window = new TimeWindow(At(10, 0, 1), At(10, 0, 2));
            state.SetFileTimeWindow(tab, window);

            Assert.Throws<ArgumentException>(() =>
                state.SetFileTimeWindow(tab, new TimeWindow(At(10, 0, 5), At(10, 0, 4)))
            );
            Assert.Equal(window, tab.TimeWindow);

            var connection = ElasticFixtures.Connection("c-1", "prod", "app");
            var view = connection.Views[0];
            var elastic = new FileTabState(
                LogSourceDescriptor.Elastic(connection, view, view.Sources[0]),
                new FileBuffer(10),
                new PlainTextParser(),
                new NullTailer()
            );
            Assert.Throws<ArgumentException>(() => state.SetFileTimeWindow(elastic, window));
            Assert.Null(elastic.TimeWindow);

            var otherPath = Path.Combine(Path.GetTempPath(), $"hextail-{Guid.NewGuid():N}.log");
            await File.WriteAllTextAsync(otherPath, "2026-01-01T10:00:00Z INFO b\n", Ct);
            try
            {
                var other = await state.OpenFileAsync(
                    otherPath,
                    save: false,
                    cancellationToken: Ct
                );
                var made = await state.CreateStreamAsync("Mixed", [tab, other], Ct);
                var stream = (await state.OpenStreamAsync(made.Id, Ct))!;
                Assert.Throws<ArgumentException>(() => state.SetFileTimeWindow(stream, window));
                Assert.Null(stream.TimeWindow);
            }
            finally
            {
                File.Delete(otherPath);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
