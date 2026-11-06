using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
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
                Rules =
                [
                    TestRules.Highlight("error", "#FF0000", pinned: true),
                    TestRules.Highlight("warn", "#00FF00"),
                ],
            };
            await using var state = new AppState(
                new LogSourceService(),
                new TestPersistence(),
                settings,
                credentials: new InMemoryCredentialVault()
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
            Assert.Equal(
                [new HighlightSpan(0, 5, "#FF0000"), new HighlightSpan(6, 4, "#00FF00")],
                row.Spans
            );
            Assert.Equal(
                ["error"],
                file.Model.Searches.Where(search => search.IsGlobalRule)
                    .Select(search => search.Query.Query)
            );
            // The rule pass should only evaluate rules not already supplied by search tabs.
            var sample = new Line("error warn");
            Assert.Equal(
                [new ColoredRange(6, 4, "#00FF00")],
                RuleSet.For(state.Settings.Rules).Highlights(sample)
            );
            Assert.Single(
                file.Model.Searches.Single(search => search.IsGlobalRule).GetHighlights(sample)
            );
            file.Views[1].Sync();
            var searchRow = Assert.Single(file.Views[1].Lines);
            searchRow.SetVisible(true);
            Assert.Same(row.Spans, searchRow.Spans);

            await state.UpdateSettingsAsync(
                state.Settings with
                {
                    Rules = [TestRules.Highlight("warn", "#FF00FF")],
                },
                TestContext.Current.CancellationToken
            );
            file.SyncViews();
            Assert.Equal(
                [new HighlightSpan(0, 5, "#0000FF"), new HighlightSpan(6, 4, "#FF00FF")],
                row.Spans
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
    public async Task FollowDoesNotMoveTheSelection()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllLinesAsync(path, ["line 0", "line 1"]);
        try
        {
            await using var viewModel = new MainWindowViewModel(
                new AppState(
                    new LogSourceService(),
                    new TestPersistence(),
                    credentials: new InMemoryCredentialVault()
                ),
                scheduler: ImmediateScheduler.Instance,
                startPolling: false
            );
            await viewModel.OpenPathsCommand.Execute([path]);
            var file = viewModel.SelectedFile!;
            var log = file.Views[0];

            file.Model.FollowAll = false;
            file.Model.SelectedLine = 0;
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
    public async Task AppendingToALargeBufferKeepsExistingRowsInPlace()
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
            file.SyncViews();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(200, file.Views[0].Lines.Count);

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

    [AvaloniaFact]
    public async Task SourceRulesAndLevelMaskFilterRowsAndExplainAnEmptyView()
    {
        var path = Path.GetTempFileName();
        var other = Path.GetTempFileName();
        await File.WriteAllLinesAsync(
            path,
            ["INFO keep", "ERROR boom", "INFO healthz", "   at Foo.Bar()"],
            TestContext.Current.CancellationToken
        );
        await File.WriteAllLinesAsync(other, ["INFO x"], TestContext.Current.CancellationToken);
        try
        {
            var window = TestWindow.CreatePolling(out var viewModel);
            window.Show();
            await viewModel.OpenPathsCommand.Execute([path]);
            var view = await LoadedView(viewModel, 4);
            var tab = viewModel.SelectedFile!.Model;

            await viewModel.State.SetSourceRulesAsync(
                tab,
                [
                    new ViewRule
                    {
                        Pattern = "healthz",
                        Action = RuleAction.Hide,
                        Scope = RuleScope.Source,
                    },
                ],
                TestContext.Current.CancellationToken
            );
            await UiWait.Until(() =>
            {
                view.Sync();
                return view.Lines.Count == 3;
            });
            Assert.Equal(
                ["INFO keep", "ERROR boom", "   at Foo.Bar()"],
                view.Lines.Select(row => row.Line.Raw)
            );
            Assert.Equal(string.Empty, view.ResultMessage);

            await viewModel.State.SetLevelsAsync(
                tab,
                LogLevels.Error,
                TestContext.Current.CancellationToken
            );
            await UiWait.Until(() =>
            {
                view.Sync();
                return view.Lines.Count == 2;
            });
            Assert.Equal(["ERROR boom", "   at Foo.Bar()"], view.Lines.Select(row => row.Line.Raw));
            Assert.Equal(string.Empty, view.ResultMessage);

            await viewModel.OpenPathsCommand.Execute([other]);
            var otherView = await LoadedView(viewModel, 1);
            await viewModel.State.SetLevelsAsync(
                viewModel.SelectedFile!.Model,
                LogLevels.Warn,
                TestContext.Current.CancellationToken
            );
            await UiWait.Until(() =>
            {
                otherView.Sync();
                return otherView.Lines.Count == 0;
            });
            Assert.Equal("All matching logs are hidden by filter rules.", otherView.ResultMessage);
            window.Close();
        }
        finally
        {
            File.Delete(path);
            File.Delete(other);
        }
    }

    [AvaloniaFact]
    public async Task SourceRuleChangeRefreshesAlreadyRenderedRowHighlights()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllLinesAsync(
            path,
            ["INFO keep", "INFO marked"],
            TestContext.Current.CancellationToken
        );
        try
        {
            var window = TestWindow.CreatePolling(out var viewModel);
            window.Show();
            await viewModel.OpenPathsCommand.Execute([path]);
            var view = await LoadedView(viewModel, 2);
            var row = view.Lines.Single(item => item.Line.Raw == "INFO marked");
            row.SetVisible(true);
            Assert.Empty(row.Spans);

            await viewModel.State.SetSourceRulesAsync(
                viewModel.SelectedFile!.Model,
                [
                    new ViewRule
                    {
                        Pattern = "marked",
                        Color = "#112233",
                        Action = RuleAction.Highlight,
                        Scope = RuleScope.Source,
                    },
                ],
                TestContext.Current.CancellationToken
            );
            await UiWait.Until(() =>
            {
                view.Sync();
                return row.Spans.Count == 1;
            });
            Assert.Equal([new HighlightSpan(5, 6, "#112233")], row.Spans);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<LogViewViewModel> LoadedView(MainWindowViewModel viewModel, int lines)
    {
        await UiWait.Until(() =>
        {
            viewModel.State.DrainTailerEvents();
            return viewModel.SelectedFile?.SelectedView.Lines.Count == lines;
        });
        return viewModel.SelectedFile!.SelectedView;
    }
}
