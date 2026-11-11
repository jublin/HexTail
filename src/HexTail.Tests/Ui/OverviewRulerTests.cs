using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Domain;
using HexTail.Tests.Support;
using HexTail.ViewModels;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class OverviewRulerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Rows =
    [
        "2026-01-01T10:00:00Z INFO start",
        "2026-01-01T10:00:10Z ERROR boom",
        "2026-01-01T10:00:20Z INFO ok",
        "2026-01-01T10:00:30Z INFO flaky thing",
        "2026-01-01T10:00:40Z INFO done",
    ];

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Directory
            .CreateTempSubdirectory("hextail-ruler-")
            .FullName;

        public required MainWindow Window { get; init; }
        public required MainWindowViewModel Workspace { get; init; }

        public FileTabViewModel Tab => Workspace.SelectedFile!;
        public LogView LogView => Window.GetVisualDescendants().OfType<LogView>().First();
        public OverviewRuler Ruler => LogView.FindControl<OverviewRuler>("Ruler")!;
        public ListBox List => LogView.FindControl<ListBox>("LogList")!;

        public static async Task<Fixture> CreateAsync(string[] lines)
        {
            var window = TestWindow.Create(new TestPersistence(), out var workspace);
            window.Width = 1000;
            window.Height = 700;
            var fixture = new Fixture { Window = window, Workspace = workspace };
            window.Show();
            var path = System.IO.Path.Combine(fixture._directory, $"{Guid.NewGuid():N}.log");
            await File.WriteAllLinesAsync(path, lines, Ct);
            await workspace.OpenPathsCommand.Execute([path]);
            await UiWait.Until(() =>
            {
                workspace.State.DrainTailerEvents();
                Dispatcher.UIThread.RunJobs();
                return workspace.SelectedFile is { } tab
                    && tab.SelectedView.Lines.Count == lines.Length;
            });
            Dispatcher.UIThread.RunJobs();
            return fixture;
        }

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
    public async Task Ruler_IsTenPixelsWide_BesideTheList()
    {
        await using var f = await Fixture.CreateAsync(Rows);

        Assert.True(f.Ruler.IsVisible);
        Assert.Equal(10, f.Ruler.Bounds.Width);
        var listRight = f
            .List.TranslatePoint(new Point(f.List.Bounds.Width, 0), f.LogView)!
            .Value.X;
        var rulerLeft = f.Ruler.TranslatePoint(default, f.LogView)!.Value.X;
        Assert.Equal(listRight, rulerLeft, 0.5);
        Assert.Equal(f.List.Bounds.Height, f.Ruler.Bounds.Height, 0.5);
        Assert.Equal("Overview ruler", Avalonia.Automation.AutomationProperties.GetName(f.Ruler));
    }

    [AvaloniaFact]
    public async Task Ruler_MarksHighlightRuleMatchesAndErrors()
    {
        await using var f = await Fixture.CreateAsync(Rows);
        await f.Workspace.State.AddRuleAsync(
            f.Tab.Model,
            TestRules.Highlight("flaky", "#112233") with
            {
                Scope = RuleScope.Source,
            }
        );

        await UiWait.Until(() => f.Ruler.CurrentMarkers.Count == 2);
        Assert.Equal(new ScrollMarker(1, null, true), f.Ruler.CurrentMarkers[0]);
        Assert.Equal(new ScrollMarker(3, "#112233", false), f.Ruler.CurrentMarkers[1]);
    }

    [AvaloniaFact]
    public async Task Ruler_UpdatesWhenARuleIsAdded()
    {
        await using var f = await Fixture.CreateAsync(Rows);
        await UiWait.Until(() => f.Ruler.CurrentMarkers.Count == 1);
        Assert.Equal([1], f.Ruler.CurrentMarkers.Select(marker => marker.Row));

        await f.Workspace.State.AddRuleAsync(
            f.Tab.Model,
            TestRules.Highlight("INFO", "#445566") with
            {
                Scope = RuleScope.Source,
            }
        );

        await UiWait.Until(() => f.Ruler.CurrentMarkers.Count == 5);
        Assert.Equal([0, 1, 2, 3, 4], f.Ruler.CurrentMarkers.Select(marker => marker.Row));
        Assert.Equal("#445566", f.Ruler.CurrentMarkers[0].RuleColor);
        Assert.True(f.Ruler.CurrentMarkers[1].Error);
    }

    [AvaloniaFact]
    public async Task Ruler_ClickSelectsTheRowAtThatHeight_AndScrollsToIt()
    {
        var rows = Enumerable.Range(0, 400).Select(index => $"INFO row {index}").ToArray();
        await using var f = await Fixture.CreateAsync(rows);

        f.Ruler.ClickAt(f.Ruler.Bounds.Height * 0.75);

        await UiWait.Until(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return f.List.SelectedItem is LogLineViewModel { Line.Raw: "INFO row 300" }
                && RealizedRows(f).Any(row => row.Line.Raw == "INFO row 300");
        });
        var selected = Assert.IsType<LogLineViewModel>(f.List.SelectedItem);
        Assert.Equal("INFO row 300", selected.Line.Raw);
    }

    private static IEnumerable<LogLineViewModel> RealizedRows(Fixture f) =>
        f
            .List.GetVisualDescendants()
            .OfType<LogLineView>()
            .Select(view => view.DataContext)
            .OfType<LogLineViewModel>();

    [AvaloniaFact]
    public async Task Ruler_HiddenWhenTheViewHasNoRows()
    {
        await using var f = await Fixture.CreateAsync(Rows);
        Assert.True(f.Ruler.IsVisible);

        await f.Workspace.State.AddRuleAsync(
            f.Tab.Model,
            TestRules.Highlight("nothing-matches-this") with
            {
                Scope = RuleScope.Source,
                Action = RuleAction.ShowOnly,
            }
        );

        await UiWait.Until(() =>
        {
            f.Workspace.State.DrainTailerEvents();
            Dispatcher.UIThread.RunJobs();
            return f.Tab.SelectedView.Lines.Count == 0;
        });
        Assert.False(f.Ruler.IsVisible);
    }
}
