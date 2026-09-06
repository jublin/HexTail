using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Application;
using HexTail.Domain;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;
using HexTail.ViewModels;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class LogViewTests
{
    [AvaloniaFact]
    public async Task GlobalLabelHighlightsPreserveSearchPriorityAndLabelsWithoutTabs()
    {
        var path = Path.GetTempFileName();
        try
        {
            var settings = new AppSettings
            {
                GlobalLabels =
                [
                    new GlobalLabel
                    {
                        Text = "error",
                        Color = "#FF0000",
                        ShowInOpenFile = true,
                    },
                    new GlobalLabel
                    {
                        Text = "warn",
                        Color = "#00FF00",
                        ShowInOpenFile = false,
                    },
                ],
            };
            await using var state = new AppState(
                new LogSourceService(),
                new TestPersistence(),
                settings
            );
            await using var viewModel = new MainWindowViewModel(
                state,
                scheduler: ImmediateScheduler.Instance,
                startPolling: false
            );
            await viewModel.OpenPathsCommand.Execute([path]);
            var file = viewModel.SelectedFile!;
            file.Model.Buffer.Append(new Line("error warn"));
            state.AddSearch(file.Model, "error", MatchMode.Literal, true, "#0000FF");
            file.SyncViews();
            var row = Assert.Single(file.Views[0].Lines);
            row.SetVisible(true);
            var highlights = row
                .Segments.Where(segment => segment.Background is not null)
                .ToArray();
            Assert.Equal(["error", "warn"], highlights.Select(segment => segment.Text));
            Assert.Equal(
                Colors.Red,
                Assert.IsType<SolidColorBrush>(highlights[0].Background).Color
            );
            Assert.Equal(
                Colors.Lime,
                Assert.IsType<SolidColorBrush>(highlights[1].Background).Color
            );
            Assert.Equal(
                ["error"],
                file.Model.Searches.Where(search => search.IsGlobalLabel)
                    .Select(search => search.Query.Query)
            );
            // The settings pass should only evaluate labels not already supplied by search tabs.
            Assert.Equal(
                [new LabelHighlight(6, 4, "#00FF00")],
                state.Settings.GetLabelHighlights("error warn", includeSearchTabs: false)
            );
            Assert.Equal(2, state.Settings.GetLabelHighlights("error warn").Count());
            file.Model.ShowContext = true;
            file.Views[0].Sync();
            var contextRow = Assert.Single(file.Views[0].ContextLines);
            contextRow.SetVisible(true);
            file.Views[1].Sync();
            var searchRow = Assert.Single(file.Views[1].Lines);
            searchRow.SetVisible(true);
            Assert.Equal(
                row.Segments.Select(segment => segment.Text),
                contextRow.Segments.Select(segment => segment.Text)
            );
            Assert.Equal(
                row.Segments.Select(segment => segment.Text),
                searchRow.Segments.Select(segment => segment.Text)
            );

            await state.UpdateSettingsAsync(
                state.Settings with
                {
                    GlobalLabels =
                    [
                        new GlobalLabel
                        {
                            Text = "warn",
                            Color = "#FF00FF",
                            ShowInOpenFile = false,
                        },
                    ],
                },
                TestContext.Current.CancellationToken
            );
            file.SyncViews();
            highlights = row.Segments.Where(segment => segment.Background is not null).ToArray();
            Assert.Equal(
                Colors.Blue,
                Assert.IsType<SolidColorBrush>(highlights[0].Background).Color
            );
            Assert.Equal(
                Colors.Magenta,
                Assert.IsType<SolidColorBrush>(highlights[1].Background).Color
            );
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void HundredThousandRowsRemainVirtualized()
    {
        var list = new ListBox
        {
            Height = 400,
            ItemsSource = Enumerable
                .Range(0, 100_000)
                .Select(index => new Line(index.ToString()))
                .ToArray(),
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
        };
        var window = new Window
        {
            Width = 900,
            Height = 500,
            Content = list,
        };
        try
        {
            window.Show();

            Assert.True(list.GetVisualDescendants().OfType<ListBoxItem>().Count() < 200);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task RecycledRowsTolerateNullItems()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var viewModel);
            await viewModel.OpenPathsCommand.Execute([path]);
            window.Show();

            var view = window.GetVisualDescendants().OfType<LogView>().Single();
            var template = view.FindControl<ListBox>("LogList")!.ItemTemplate;
            Assert.NotNull(template);
            Assert.IsNotType<FuncDataTemplate<Line>>(template);

            Assert.NotNull(template.Build(new Line("line")));
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ContextPanelRequiresToggleAndSelectedLine()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "line");
        try
        {
            var window = TestWindow.Create(out var viewModel);
            await viewModel.OpenPathsCommand.Execute([path]);
            window.Show();
            var file = viewModel.SelectedFile;
            Assert.NotNull(file);
            var view = window.GetVisualDescendants().OfType<LogView>().Single();
            var contextList = view.FindControl<ListBox>("ContextList")!;

            file.ShowContext = true;
            file.Model.SelectedLine = null;
            file.SyncViews();
            Assert.False(contextList.IsVisible);

            file.Model.SelectedLine = 0;
            file.SyncViews();
            Assert.True(contextList.IsVisible);

            file.ShowContext = false;
            file.SyncViews();
            Assert.False(contextList.IsVisible);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task HiddenContextRowDoesNotReserveSpace()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "line");
        try
        {
            var window = TestWindow.Create(out var viewModel);
            await viewModel.OpenPathsCommand.Execute([path]);
            window.Show();
            var view = window.GetVisualDescendants().OfType<LogView>().Single();
            var layout = view.FindControl<Grid>("LogLayout")!;

            Assert.Equal(0, layout.RowDefinitions[2].ActualHeight);
            viewModel.SelectedFile!.ShowContext = true;
            viewModel.SelectedFile.Model.SelectedLine = 0;
            viewModel.SelectedFile.SyncViews();
            Dispatcher.UIThread.RunJobs();
            Assert.True(layout.RowDefinitions[2].ActualHeight > 0);

            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task EnablingFollowScrollsToEndAndStaysEnabled()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllLinesAsync(
            path,
            Enumerable.Range(0, 200).Select(index => $"line {index}")
        );
        try
        {
            var window = TestWindow.Create(out var viewModel);
            await viewModel.OpenPathsCommand.Execute([path]);
            window.Show();
            var view = window.GetVisualDescendants().OfType<LogView>().Single();
            var logViewModel = viewModel.SelectedFile!.Views[0];

            logViewModel.IsFollowing = false;
            Dispatcher.UIThread.RunJobs();
            logViewModel.IsFollowing = true;
            Dispatcher.UIThread.RunJobs();

            Assert.True(logViewModel.IsFollowing);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ShowingContextIsIndependentFromFollow()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "line");
        try
        {
            var window = TestWindow.Create(out var viewModel);
            await viewModel.OpenPathsCommand.Execute([path]);
            window.Show();
            var file = viewModel.SelectedFile!;
            var log = file.Views[0];
            file.Model.SelectedLine = 0;

            log.ShowContext = true;
            file.SyncViews();

            Assert.True(file.Model.ShowContext);
            Assert.True(log.IsFollowing);
            Assert.True(log.ContextVisible);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task FollowDoesNotMoveInlineContextSelection()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllLinesAsync(path, ["line 0", "line 1"]);
        try
        {
            await using var viewModel = new MainWindowViewModel(
                new AppState(new LogSourceService(), new TestPersistence()),
                scheduler: ImmediateScheduler.Instance,
                startPolling: false
            );
            await viewModel.OpenPathsCommand.Execute([path]);
            var file = viewModel.SelectedFile!;
            var log = file.Views[0];

            file.Model.FollowAll = false;
            file.Model.SelectedLine = 0;
            log.ShowContext = true;
            file.SyncViews();

            file.Model.FollowAll = true;
            file.Model.Buffer.Append(new Line("line 2"));
            file.SyncViews();

            Assert.Equal(0, file.Model.SelectedLine);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ContextViewContainsFullFileAndOwnsScrolling()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllLinesAsync(
            path,
            Enumerable.Range(0, 200).Select(index => $"line {index}")
        );
        try
        {
            var window = TestWindow.Create(out var viewModel);
            await viewModel.OpenPathsCommand.Execute([path]);
            window.Show();

            var file = viewModel.SelectedFile!;
            file.Model.Buffer.Append(
                Enumerable.Range(0, 200).Select(index => new Line($"line {index}"))
            );
            file.Model.SelectedLine = 100;
            file.Views[0].ShowContext = true;
            file.SyncViews();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(200, file.Views[0].ContextLines.Count);

            var view = window.GetVisualDescendants().OfType<LogView>().Single();
            var contextList = view.FindControl<ListBox>("ContextList")!;
            Assert.Equal(200, contextList.ItemCount);
            Assert.NotNull(
                contextList.GetVisualDescendants().OfType<ScrollViewer>().SingleOrDefault()
            );

            var firstRow = file.Views[0].Lines[0];
            file.Model.Buffer.Append(new Line("line 200"));
            file.SyncViews();
            Assert.Same(firstRow, file.Views[0].Lines[0]);
            Assert.Equal(201, file.Views[0].Lines.Count);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
