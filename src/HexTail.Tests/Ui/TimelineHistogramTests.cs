using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Application;
using HexTail.Domain;
using HexTail.Persistence;
using HexTail.Tests.Support;
using HexTail.ViewModels;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class TimelineHistogramTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] TimestampedRows =
    [
        "2026-01-01T10:00:00Z ERROR boom",
        "2026-01-01T10:00:10Z WARN slow",
        "2026-01-01T10:00:20Z INFO ok",
        "2026-01-01T10:00:30Z DEBUG a",
        "2026-01-01T10:00:40Z DEBUG b",
    ];

    /// <summary>Presses a button the way the user does: through its command (a raised Click does not run it).</summary>
    private static void Click(Button button)
    {
        Assert.True(button.IsEffectivelyVisible);
        Assert.True(button.Command!.CanExecute(null));
        button.Command.Execute(null);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Directory
            .CreateTempSubdirectory("hextail-timeline-")
            .FullName;

        public string? Path { get; private set; }
        public required MainWindow Window { get; init; }
        public required MainWindowViewModel Workspace { get; init; }
        public required TestPersistence Persistence { get; init; }

        public FileTabViewModel Tab => Workspace.SelectedFile!;
        public LogView LogView => Window.GetVisualDescendants().OfType<LogView>().First();
        public TimelineHistogram Histogram => Find<TimelineHistogram>("Histogram");
        public Panel Panel => Find<Panel>("TimelinePanel");
        public Panel Expanded => Find<Panel>("TimelineExpanded");
        public Panel Collapsed => Find<Panel>("TimelineCollapsed");

        public T Find<T>(string name)
            where T : Control => LogView.FindControl<T>(name)!;

        public static async Task<Fixture> CreateAsync(
            string[] lines,
            TestPersistence? persistence = null
        )
        {
            persistence ??= new TestPersistence();
            var window = TestWindow.Create(persistence, out var workspace);
            window.Width = 1000;
            window.Height = 700;
            var fixture = new Fixture
            {
                Window = window,
                Workspace = workspace,
                Persistence = persistence,
            };
            window.Show();
            await fixture.OpenAsync(lines);
            return fixture;
        }

        public async Task OpenAsync(string[] lines)
        {
            Path = System.IO.Path.Combine(_directory, $"{Guid.NewGuid():N}.log");
            await File.WriteAllLinesAsync(Path, lines, Ct);
            await Workspace.OpenPathsCommand.Execute([Path]);
            await UiWait.Until(() =>
            {
                Workspace.State.DrainTailerEvents();
                Dispatcher.UIThread.RunJobs();
                return Workspace.SelectedFile is { } tab
                    && tab.SelectedView.Lines.Count == lines.Length;
            });
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Waits for the histogram to be built, then returns it.</summary>
        public async Task<TimeHistogram> BuiltAsync()
        {
            await UiWait.Until(() => Histogram.CurrentHistogram is not null);
            return Histogram.CurrentHistogram!;
        }

        /// <summary>The x position at the middle of bucket <paramref name="index"/>.</summary>
        public double XOf(TimeHistogram histogram, int index) =>
            (index + 0.5) * Histogram.Bounds.Width / histogram.Buckets.Count;

        public async ValueTask DisposeAsync()
        {
            try
            {
                Window.Close();
            }
            catch (InvalidOperationException) { }
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException) { }
            await Task.CompletedTask;
        }
    }

    [AvaloniaFact]
    public async Task Histogram_IsShownAboveTheList_WithTheExpectedHeight()
    {
        await using var f = await Fixture.CreateAsync(TimestampedRows);
        var histogram = await f.BuiltAsync();

        Assert.True(f.Panel.IsVisible);
        Assert.True(f.Expanded.IsVisible);
        Assert.False(f.Collapsed.IsVisible);
        Assert.Equal(40, f.Histogram.Bounds.Height);
        Assert.True(f.Histogram.Bounds.Width > 100);
        Assert.Equal(5, histogram.Buckets.Sum(bucket => bucket.Total));
        var list = f.Find<ListBox>("LogList");
        Assert.True(
            f.Histogram.TranslatePoint(default, f.LogView)!.Value.Y
                < list.TranslatePoint(default, f.LogView)!.Value.Y
        );
        Assert.Equal(
            "Timeline histogram",
            Avalonia.Automation.AutomationProperties.GetName(f.Histogram)
        );
    }

    [AvaloniaFact]
    public async Task Histogram_HiddenWhenNoRowHasATimestamp()
    {
        await using var f = await Fixture.CreateAsync(["plain a", "plain b", "plain c"]);
        await Task.Delay(400, Ct);
        Dispatcher.UIThread.RunJobs();

        Assert.False(f.Panel.IsVisible);
        Assert.Null(f.Histogram.CurrentHistogram);
    }

    [AvaloniaFact]
    public async Task Collapse_ShowsTheStrip_AndPersistsInTheWindowState()
    {
        var persistence = new TestPersistence();
        await using (var f = await Fixture.CreateAsync(TimestampedRows, persistence))
        {
            await f.BuiltAsync();
            Assert.True(f.Workspace.TimelineOpen);

            Click(f.Find<Button>("CollapseTimelineButton"));
            Dispatcher.UIThread.RunJobs();

            Assert.False(f.Workspace.TimelineOpen);
            Assert.False(f.Expanded.IsVisible);
            Assert.True(f.Collapsed.IsVisible);
            Assert.Equal(18, f.Collapsed.Bounds.Height);
            var expand = f.Find<Button>("ExpandTimelineButton");
            Assert.Equal(
                "Expand timeline",
                Avalonia.Automation.AutomationProperties.GetName(expand)
            );
            Assert.Equal(
                "Collapse timeline",
                Avalonia.Automation.AutomationProperties.GetName(
                    f.Find<Button>("CollapseTimelineButton")
                )
            );

            f.Window.Close();
            await UiWait.Until(() => persistence.Config!.Window.HistogramCollapsed);
        }

        await using var again = await Fixture.CreateAsync(TimestampedRows, persistence);
        await UiWait.Until(() => !again.Workspace.TimelineOpen);
        Assert.False(again.Expanded.IsVisible);
        Assert.True(again.Collapsed.IsVisible);

        Click(again.Find<Button>("ExpandTimelineButton"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(again.Workspace.TimelineOpen);
        Assert.Equal(40, again.Histogram.Bounds.Height);
    }

    [AvaloniaFact]
    public async Task PaletteToggleTimeline_FlipsTheState()
    {
        await using var f = await Fixture.CreateAsync(TimestampedRows);
        await f.BuiltAsync();

        async Task Toggle()
        {
            f.Workspace.Palette.Open();
            f.Workspace.Palette.Query = "Toggle timeline";
            var item = Assert.Single(
                f.Workspace.Palette.Results,
                i => i.Title == "Toggle timeline"
            );
            Assert.Equal("View", item.Category);
            Assert.Null(item.Gesture);
            await item.Execute();
            Dispatcher.UIThread.RunJobs();
        }

        await Toggle();
        Assert.False(f.Workspace.TimelineOpen);
        Assert.True(f.Collapsed.IsVisible);

        await Toggle();
        Assert.True(f.Workspace.TimelineOpen);
        Assert.True(f.Expanded.IsVisible);
    }

    [AvaloniaFact]
    public async Task Drag_AppliesTheBucketRangeToTheFileTab()
    {
        await using var f = await Fixture.CreateAsync(TimestampedRows);
        var histogram = await f.BuiltAsync();

        // The drag may run right to left; the range still covers the first to the last bucket.
        Assert.True(f.Histogram.PressAt(f.XOf(histogram, 25), 1));
        f.Histogram.MoveTo(f.XOf(histogram, 12));
        f.Histogram.ReleaseAt(f.XOf(histogram, 10));

        Assert.Equal(
            new TimeWindow(histogram.Buckets[10].Start, histogram.Buckets[25].End),
            f.Tab.Model.TimeWindow
        );
    }

    [AvaloniaFact]
    public async Task Click_JumpsToTheFirstRowOfTheBucket()
    {
        await using var f = await Fixture.CreateAsync(TimestampedRows);
        var histogram = await f.BuiltAsync();
        var bucket = histogram.BucketIndexAt(20.5 / 41);
        Assert.Equal(2, histogram.Buckets[bucket].FirstRow);

        var x = f.XOf(histogram, bucket);
        f.Histogram.PressAt(x, 1);
        f.Histogram.ReleaseAt(x + 2);

        Assert.Null(f.Tab.Model.TimeWindow);
        Assert.Equal(2, f.Tab.Model.SelectedLine);
    }

    [AvaloniaFact]
    public async Task DoubleClick_ResetsTheWindow()
    {
        await using var f = await Fixture.CreateAsync(TimestampedRows);
        var histogram = await f.BuiltAsync();
        f.Histogram.PressAt(f.XOf(histogram, 5), 1);
        f.Histogram.ReleaseAt(f.XOf(histogram, 15));
        Assert.NotNull(f.Tab.Model.TimeWindow);

        Assert.False(f.Histogram.PressAt(f.XOf(histogram, 5), 2));

        Assert.Null(f.Tab.Model.TimeWindow);
    }

    [AvaloniaFact]
    public async Task Hover_ShowsTheBucketDescription()
    {
        await using var f = await Fixture.CreateAsync(TimestampedRows);
        var histogram = await f.BuiltAsync();

        f.Histogram.MoveTo(f.XOf(histogram, 10));

        Assert.Equal(
            f.Tab.SelectedView.Overview.DescribeBucket(histogram, 10),
            ToolTip.GetTip(f.Histogram)
        );
        Assert.Null(f.Tab.Model.TimeWindow);
    }

    [AvaloniaFact]
    public async Task Histogram_RebuildsAfterTheViewChanges()
    {
        await using var f = await Fixture.CreateAsync(TimestampedRows);
        var before = await f.BuiltAsync();

        await File.AppendAllLinesAsync(f.Path!, ["2026-01-01T10:00:50Z INFO late"], Ct);
        await UiWait.Until(() =>
        {
            f.Workspace.State.DrainTailerEvents();
            f.Tab.SyncViews();
            f.Tab.SelectedView.Sync();
            return f.Histogram.CurrentHistogram is { } now
                && !ReferenceEquals(now, before)
                && now.Buckets.Sum(bucket => bucket.Total) == 6;
        });
    }

    private static async Task Settle(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, Ct);
        }
    }

    [AvaloniaFact]
    public async Task Histogram_RebuildsOnExpand_WhenTheViewChangedWhileCollapsed()
    {
        await using var f = await Fixture.CreateAsync(TimestampedRows);
        var before = await f.BuiltAsync();
        f.Workspace.TimelineOpen = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(f.Histogram.IsEffectivelyVisible);

        f.Workspace.ApplyHistogramRange(before.Buckets[0].Start, before.Buckets[0].End);
        f.Tab.SelectedView.Sync();
        await Settle(600);
        Assert.Same(before, f.Histogram.CurrentHistogram);

        f.Workspace.TimelineOpen = true;

        await UiWait.Until(() =>
            f.Histogram.CurrentHistogram is { } now
            && !ReferenceEquals(now, before)
            && now.Buckets.Sum(bucket => bucket.Total) == f.Tab.SelectedView.Lines.Count
        );
    }

    [AvaloniaFact]
    public async Task Drag_OverBucketsWithNoRows_DoesNothing()
    {
        await using var f = await Fixture.CreateAsync([
            "2026-01-01T10:00:00Z INFO first",
            "2026-01-01T10:10:00Z INFO last",
        ]);
        var histogram = await f.BuiltAsync();
        Assert.All(histogram.Buckets.Skip(10).Take(11), bucket => Assert.Equal(0, bucket.Total));

        f.Histogram.PressAt(f.XOf(histogram, 10), 1);
        f.Histogram.ReleaseAt(f.XOf(histogram, 20));

        Assert.Null(f.Tab.Model.TimeWindow);
        Assert.Null(f.Tab.Model.HistogramPrior);
        Assert.Null(f.Tab.Model.SelectedLine);
    }

    [AvaloniaFact]
    public async Task EmptyRange_KeepsThePanel_ShowsTheHint_AndDoubleClickResets()
    {
        await using var f = await Fixture.CreateAsync(TimestampedRows);
        var histogram = await f.BuiltAsync();
        f.Workspace.ApplyHistogramRange(histogram.End.AddMinutes(1), histogram.End.AddMinutes(2));

        await UiWait.Until(() =>
        {
            f.Tab.SelectedView.Sync();
            return f.Histogram.CurrentHistogram is null && f.Histogram.ShowsNoRowsHint;
        });
        Assert.Equal(0, f.Tab.SelectedView.Lines.Count);
        Assert.True(f.Panel.IsVisible);
        Assert.Equal("No rows in range – double-click to reset", TimelineHistogram.NoRowsText);

        Assert.False(f.Histogram.PressAt(10, 2));

        Assert.Null(f.Tab.Model.TimeWindow);
        await UiWait.Until(() =>
        {
            f.Tab.SelectedView.Sync();
            return f.Histogram.CurrentHistogram is not null && !f.Histogram.ShowsNoRowsHint;
        });
    }

    [AvaloniaFact]
    public async Task Rebuild_IsSkippedMidDrag_AndRunsAfterTheRelease()
    {
        await using var f = await Fixture.CreateAsync(TimestampedRows);
        var before = await f.BuiltAsync();
        Assert.True(f.Histogram.PressAt(f.XOf(before, 10), 1));
        f.Histogram.MoveTo(f.XOf(before, 12));

        await File.AppendAllLinesAsync(f.Path!, ["2026-01-01T10:00:50Z INFO late"], Ct);
        await UiWait.Until(() =>
        {
            f.Workspace.State.DrainTailerEvents();
            f.Tab.SyncViews();
            f.Tab.SelectedView.Sync();
            return f.Tab.SelectedView.Lines.Count == 6;
        });
        await Settle(600);
        Assert.Same(before, f.Histogram.CurrentHistogram);

        f.Histogram.ReleaseAt(f.XOf(before, 10));

        await UiWait.Until(() =>
            f.Histogram.CurrentHistogram is { } now && !ReferenceEquals(now, before)
        );
    }

    [AvaloniaFact]
    public void DetachedControl_DoesNotStartItsTimer_WhenTheViewIsSet()
    {
        var control = new TimelineHistogram();

        control.View = null;
        Assert.False(control.RebuildScheduled);
    }

    [AvaloniaFact]
    public void WarnBrush_IsDefinedInEveryTheme()
    {
        var app = Avalonia.Application.Current!;
        try
        {
            foreach (var theme in ThemeCatalog.Names)
            {
                ThemeManager.Apply(theme);
                Assert.True(app.TryGetResource("WarnBrush", ThemeVariant.Dark, out var value));
                var brush = Assert.IsType<SolidColorBrush>(value);
                Assert.Equal(Color.Parse("#F5B942"), brush.Color);
            }
        }
        finally
        {
            ThemeManager.Apply("cyber-tail");
        }
    }
}
