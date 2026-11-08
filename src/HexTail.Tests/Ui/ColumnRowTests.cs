using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Application;
using HexTail.Domain;
using HexTail.Tests.Support;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class ColumnRowTests
{
    [AvaloniaFact]
    public async Task RealizedStructuredRow_ShowsOneTextBlockPerCell_WithTheCellWidths()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, string.Empty);
        var window = TestWindow.Create(out var workspace);
        try
        {
            await workspace.OpenPathsCommand.Execute([path]);
            var file = workspace.SelectedFile!;
            file.Model.Buffer.Append([
                new Line(
                    "{\"msg\":\"hello\",\"level\":\"error\"}",
                    new Dictionary<string, string> { ["msg"] = "hello", ["level"] = "error" }
                )
                {
                    Timestamp = new DateTimeOffset(2020, 1, 2, 3, 4, 5, 6, TimeSpan.Zero),
                },
            ]);
            file.SyncViews();
            window.Width = 1280;
            window.Height = 800;
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var list = window
                .GetVisualDescendants()
                .OfType<LogView>()
                .Single()
                .FindControl<ListBox>("LogList")!;
            await UiWait.Until(() => list.ContainerFromIndex(0) is not null);
            var item = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0));
            var rowView = item.GetVisualDescendants().OfType<LogLineView>().Single();
            var cells = rowView.FindControl<ItemsControl>("RowCells")!;
            Assert.True(cells.IsVisible);
            await UiWait.Until(() =>
                cells.GetVisualDescendants().OfType<HighlightedTextBlock>().Count() == 3
            );
            var blocks = cells.GetVisualDescendants().OfType<HighlightedTextBlock>().ToList();
            var zone =
                workspace.State.Settings.TimeZoneMode == HexTail.Persistence.AppTimeZoneMode.Utc
                    ? TimeZoneInfo.Utc
                    : TimeZoneInfo.Local;
            var expectedTime = HexTail.ViewModels.LogLineViewModel.FormatTime(
                new DateTimeOffset(2020, 1, 2, 3, 4, 5, 6, TimeSpan.Zero),
                zone,
                DateTimeOffset.UtcNow
            );
            Assert.Equal([expectedTime, "ERR", "hello"], blocks.Select(b => b.Text));
            Assert.Equal(150, blocks[0].Width);
            Assert.Equal(48, blocks[1].Width);
            Assert.True(double.IsNaN(blocks[2].Width));
            Assert.True(blocks[2].Bounds.Width > 400, $"last cell fills: {blocks[2].Bounds.Width}");
            Assert.Contains("err", blocks[1].Classes);
            Assert.DoesNotContain("err", blocks[0].Classes);
            // The level style must actually reach the (derived) text block type.
            var error = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(
                HexTail.ThemeManager.Brush("ErrorBrush")
            );
            var errorForeground = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(
                blocks[1].Foreground
            );
            Assert.Equal(error.Color, errorForeground.Color);
            Assert.NotEqual(
                error.Color,
                ((Avalonia.Media.ISolidColorBrush)blocks[2].Foreground!).Color
            );
        }
        finally
        {
            window.Close();
            await workspace.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task PlainTextSource_ShowsTheComposedRow_AndHidesTheCells()
    {
        var path = Path.GetTempFileName();
        var window = TestWindow.Create(out var workspace);
        try
        {
            await workspace.OpenPathsCommand.Execute([path]);
            var file = workspace.SelectedFile!;
            file.Model.Buffer.Append([new Line("plain row")]);
            file.SyncViews();
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var list = window
                .GetVisualDescendants()
                .OfType<LogView>()
                .Single()
                .FindControl<ListBox>("LogList")!;
            await UiWait.Until(() => list.ContainerFromIndex(0) is not null);
            var rowView = list.ContainerFromIndex(0)!
                .GetVisualDescendants()
                .OfType<LogLineView>()
                .Single();
            Assert.False(rowView.FindControl<ItemsControl>("RowCells")!.IsVisible);
            Assert.True(rowView.FindControl<HighlightedTextBlock>("ComposedText")!.IsVisible);
        }
        finally
        {
            window.Close();
            await workspace.DisposeAsync();
            File.Delete(path);
        }
    }
}
