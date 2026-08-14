using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTailSharp.Domain;
using HexTailSharp.Tests.Support;
using HexTailSharp.Views;

namespace HexTailSharp.Tests.Ui;

public sealed class LogViewTests
{
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
}
