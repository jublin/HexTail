using System.Collections.ObjectModel;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;
using HexTail.Behaviors;
using HexTail.Tests.Support;
using HexTail.ViewModels;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class TabNavigationTests
{
    [AvaloniaFact]
    public async Task TenLongTabsCanBeSelectedReorderedAndClosedAtMinimumWidth()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var paths = Enumerable
            .Range(0, 10)
            .Select(index => Path.Combine(directory, $"{index}-" + new string('x', 150) + ".log"))
            .ToArray();
        foreach (var path in paths)
            File.WriteAllText(path, "");
        var window = TestWindow.Create(out var workspace);
        try
        {
            window.Width = 720;
            window.Height = 480;
            await workspace.OpenPathsCommand.Execute(paths);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var strip = window.GetVisualDescendants().OfType<FileStrip>().Single();
            var picker = strip.FindControl<ComboBox>("FileTabPicker");
            Assert.NotNull(picker);
            Assert.Equal(10, picker.ItemCount);
            Assert.True(picker.Bounds.Width > 0);
            Assert.True(
                picker.TranslatePoint(default, window)!.Value.X + picker.Bounds.Width
                    <= window.Bounds.Width
            );
            var original = workspace.Files.ToArray();
            foreach (var file in original)
            {
                picker.SelectedItem = file;
                Dispatcher.UIThread.RunJobs();
                Assert.Same(file, workspace.SelectedFile);
            }

            var selected = workspace.SelectedFile!;
            selected.FollowAll = false;
            var tabs = strip.GetVisualDescendants().OfType<TabControl>().Single();
            var header = Assert.IsType<TabItem>(tabs.ContainerFromIndex(9));
            header.RaiseEvent(
                new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Left,
                    KeyModifiers = KeyModifiers.Alt,
                }
            );
            Dispatcher.UIThread.RunJobs();
            Assert.Same(selected, workspace.Files[8]);
            Assert.Same(selected, workspace.SelectedFile);
            Assert.False(selected.FollowAll);
            Assert.Same(selected, picker.SelectedItem);

            var close = strip.FindControl<Button>("CloseSelectedFile")!;
            foreach (var file in original)
            {
                picker.SelectedItem = file;
                Dispatcher.UIThread.RunJobs();
                var point = close
                    .TranslatePoint(
                        new Point(close.Bounds.Width / 2, close.Bounds.Height / 2),
                        window
                    )!
                    .Value;
                window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
                for (var attempt = 0; attempt < 50 && workspace.Files.Contains(file); attempt++)
                {
                    await Task.Delay(10);
                    Dispatcher.UIThread.RunJobs();
                }
                Assert.DoesNotContain(file, workspace.Files);
            }
        }
        finally
        {
            window.Close();
            await workspace.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public void KeyboardReorderPreservesSelectedIdentityAndKeepsAllPinned()
    {
        var items = new ObservableCollection<string> { "All", "first", "selected", "last" };
        var tabs = new TabControl { ItemsSource = items, SelectedItem = "selected" };
        Interaction.GetBehaviors(tabs).Add(new TabReorderBehavior { FixedHeaderCount = 1 });
        var window = new Window { Content = tabs };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var selected = Assert.IsType<TabItem>(tabs.ContainerFromIndex(2));
            void MoveLeft() =>
                selected.RaiseEvent(
                    new KeyEventArgs
                    {
                        RoutedEvent = InputElement.KeyDownEvent,
                        Key = Key.Left,
                        KeyModifiers = KeyModifiers.Alt,
                    }
                );
            MoveLeft();
            Assert.Equal(new[] { "All", "selected", "first", "last" }, items);
            Assert.Equal("selected", tabs.SelectedItem);
            selected = Assert.IsType<TabItem>(tabs.ContainerFromIndex(1));
            MoveLeft();
            Assert.Equal("All", items[0]);
            Assert.Equal("selected", items[1]);
            Assert.Equal("selected", tabs.SelectedItem);
        }
        finally
        {
            window.Close();
        }
    }
}
