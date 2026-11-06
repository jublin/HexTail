using System.Reactive.Linq;
using Avalonia;
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
    public async Task EnterOnARowOpensTheInspectorOnThatRow()
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
            Assert.False(workspace.Inspector.IsOpen);

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await UiWait.Until(() => workspace.Inspector.IsOpen);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, file.Model.SelectedLine);
            Assert.Equal(raw, workspace.Inspector.RawText);
        }
        finally
        {
            window.Close();
            await workspace.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task DoubleTapOpensTheInspectorOnThatRow()
    {
        var path = Path.GetTempFileName();
        var window = TestWindow.Create(out var workspace);
        try
        {
            await workspace.OpenPathsCommand.Execute([path]);
            var file = workspace.SelectedFile!;
            file.Model.Buffer.Append([new Line("first row"), new Line("second row")]);
            file.SyncViews();
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var list = window
                .GetVisualDescendants()
                .OfType<LogView>()
                .Single()
                .FindControl<ListBox>("LogList")!;
            var item = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(1));
            var border = item.GetVisualDescendants()
                .OfType<LogLineView>()
                .Single()
                .GetVisualDescendants()
                .OfType<Border>()
                .First(b => b.Child is StackPanel);
            var point = border
                .TranslatePoint(
                    new Point(border.Bounds.Width / 2, border.Bounds.Height / 2),
                    window
                )!
                .Value;
            Assert.False(workspace.Inspector.IsOpen);

            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            await UiWait.Until(() => workspace.Inspector.IsOpen);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, file.Model.SelectedLine);
            Assert.Equal("second row", workspace.Inspector.RawText);
        }
        finally
        {
            window.Close();
            await workspace.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task InspectMenuItemOpensTheInspectorOnThatRow()
    {
        var path = Path.GetTempFileName();
        var window = TestWindow.Create(out var workspace);
        try
        {
            await workspace.OpenPathsCommand.Execute([path]);
            var file = workspace.SelectedFile!;
            file.Model.Buffer.Append([new Line("first row"), new Line("second row")]);
            file.SyncViews();
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var list = window
                .GetVisualDescendants()
                .OfType<LogView>()
                .Single()
                .FindControl<ListBox>("LogList")!;
            var row = Assert
                .IsType<ListBoxItem>(list.ContainerFromIndex(0))
                .GetVisualDescendants()
                .OfType<LogLineView>()
                .Single();
            var inspect = row.ContextMenu!.Items.OfType<MenuItem>()
                .Single(item => item.Name == "InspectRow");

            inspect.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await UiWait.Until(() => workspace.Inspector.IsOpen);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, file.Model.SelectedLine);
            Assert.Equal("first row", workspace.Inspector.RawText);
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
