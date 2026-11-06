using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Domain;
using HexTail.Tests.Support;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class LogLineInteractionTests
{
    [AvaloniaFact]
    public async Task KeyboardExpansionShowsTheEntireSelectedRawLine()
    {
        var path = Path.GetTempFileName();
        var window = TestWindow.Create(out var workspace);
        try
        {
            await workspace.OpenPathsCommand.Execute([path]);
            var file = workspace.SelectedFile!;
            var raw = "  " + new string('x', 2_000) + "\tfinal text  ";
            file.Model.Buffer.Append([new Line("other row"), new Line(raw)]);
            file.SyncViews();
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var list = window
                .GetVisualDescendants()
                .OfType<LogView>()
                .Single()
                .FindControl<ListBox>("LogList")!;
            list.SelectedIndex = 1;
            var item = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(1));
            item.Focus();

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, file.Model.ExpandedLine);
            var row = item.GetVisualDescendants().OfType<LogLineView>().Single();
            var fullText = row.FindControl<SelectableTextBlock>("RawLineDetails");
            Assert.NotNull(fullText);
            Assert.True(fullText.IsVisible);
            Assert.Equal(raw, fullText.Text);
        }
        finally
        {
            window.Close();
            await workspace.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task RowMenuAndKeyboardCopyTheAddressedLineAndItsFields()
    {
        var path = Path.GetTempFileName();
        var window = TestWindow.Create(out var workspace);
        try
        {
            await workspace.OpenPathsCommand.Execute([path]);
            var file = workspace.SelectedFile!;
            file.Model.Buffer.Append([
                new Line(
                    "first raw",
                    new Dictionary<string, string> { ["message"] = "first field" }
                ),
                new Line(
                    "second\traw  ",
                    new Dictionary<string, string> { ["message"] = "second field" }
                ),
            ]);
            file.SyncViews();
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var list = window
                .GetVisualDescendants()
                .OfType<LogView>()
                .Single()
                .FindControl<ListBox>("LogList")!;
            list.SelectedIndex = 1;
            var secondItem = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(1));
            var clipboard = Assert.IsAssignableFrom<IClipboard>(window.Clipboard);
            secondItem.Focus();
            window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.None, null);
            Assert.Equal("second\traw  ", await clipboard.TryGetTextAsync());
            window.KeyPress(
                Key.C,
                RawInputModifiers.Meta | RawInputModifiers.Shift,
                PhysicalKey.None,
                null
            );
            Assert.Equal("message=second field", await clipboard.TryGetTextAsync());

            var firstItem = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0));
            var firstRow = firstItem.GetVisualDescendants().OfType<LogLineView>().Single();
            Assert.NotNull(firstRow.ContextMenu);
            var copy = firstRow
                .ContextMenu.Items.OfType<MenuItem>()
                .Single(item => item.Name == "CopyRawLine");
            copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal("first raw", await clipboard.TryGetTextAsync());
            Assert.Equal(0, file.Model.SelectedLine);
        }
        finally
        {
            window.Close();
            await workspace.DisposeAsync();
            File.Delete(path);
        }
    }
}
