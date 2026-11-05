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
    public async Task TenLongFileNamesFitTheSidebarAndCanBeSelectedAndClosed()
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
            window.Width = 1280;
            window.Height = 720;
            await workspace.OpenPathsCommand.Execute(paths);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var sidebar = window.GetVisualDescendants().OfType<SourceSidebar>().Single();
            var selectButtons = sidebar
                .GetVisualDescendants()
                .OfType<Button>()
                .Where(button => button.Classes.Contains("source-select"))
                .ToArray();
            Assert.Equal(10, selectButtons.Length);
            foreach (var button in selectButtons)
            {
                var right = button
                    .TranslatePoint(new Point(button.Bounds.Width, 0), sidebar)!
                    .Value.X;
                Assert.True(right <= sidebar.Bounds.Width, "row text must trim, not overflow");
                button.Command!.Execute(button.CommandParameter);
                Dispatcher.UIThread.RunJobs();
                Assert.Same(button.DataContext, workspace.SelectedFile);
            }

            foreach (var file in workspace.FileTabs.ToArray())
            {
                await workspace.CloseFileCommand.Execute(file);
                await UiWait.Until(() => !workspace.Files.Contains(file));
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
