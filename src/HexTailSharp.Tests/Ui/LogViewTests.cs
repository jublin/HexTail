using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
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
            var template = Assert.IsType<FuncDataTemplate<Line>>(
                view.FindControl<ListBox>("LogList")!.ItemTemplate
            );

            Assert.IsType<Border>(template.Build(null));
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
