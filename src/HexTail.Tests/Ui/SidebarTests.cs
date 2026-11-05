using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Persistence;
using HexTail.Tests.Support;
using HexTail.ViewModels;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class SidebarTests
{
    [AvaloniaFact]
    public async Task FileRowsSelectCloseAndShowLineCounts()
    {
        var first = Path.GetTempFileName();
        var second = Path.GetTempFileName();
        await File.WriteAllLinesAsync(first, ["a", "b", "c"]);
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Width = 1280;
            window.Show();
            await viewModel.OpenPathsCommand.Execute([first, second]);
            await UiWait.Until(() =>
            {
                viewModel.State.DrainTailerEvents();
                return viewModel.FileTabs[0].LineCountText == "3";
            });
            Dispatcher.UIThread.RunJobs();
            var firstTab = viewModel.FileTabs[0];

            Assert.Equal(2, Rows(window).Length);
            Assert.Equal(first, ToolTip.GetTip(Row(window, firstTab)));
            SelectButton(window, firstTab).Command!.Execute(firstTab);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(firstTab, viewModel.SelectedFile);
            Assert.Contains("selected", Row(window, firstTab).Classes);
            Assert.Equal($"Close {first}", ToolTip.GetTip(CloseButton(window, firstTab)));

            CloseButton(window, firstTab).Command!.Execute(firstTab);
            await UiWait.Until(() => !viewModel.FileTabs.Contains(firstTab));
            window.Close();
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [AvaloniaFact]
    public async Task AltDownReordersFileRowsAndKeepsSelection()
    {
        var paths = Enumerable.Range(0, 3).Select(_ => Path.GetTempFileName()).ToArray();
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Width = 1280;
            window.Show();
            await viewModel.OpenPathsCommand.Execute(paths);
            Dispatcher.UIThread.RunJobs();
            var moved = viewModel.FileTabs[0];
            viewModel.SelectFileCommand.Execute(moved).Subscribe();
            Dispatcher.UIThread.RunJobs();

            var button = SelectButton(window, moved);
            button.Focus();
            button.RaiseEvent(
                new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Down,
                    KeyModifiers = KeyModifiers.Alt,
                }
            );
            Dispatcher.UIThread.RunJobs();

            Assert.Same(moved, viewModel.FileTabs[1]);
            Assert.Same(moved, viewModel.SelectedFile);
            window.Close();
        }
        finally
        {
            foreach (var path in paths)
                File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ShortcutCollapsesSidebarAndClosePersistsPreference()
    {
        var persistence = new TestPersistence();
        var window = TestWindow.Create(persistence, out var viewModel);
        window.Width = 1280;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SplitViewDisplayMode.Inline, viewModel.SidebarDisplayMode);
        Assert.True(viewModel.SidebarPaneOpen);

        window.KeyPress(Key.B, RawInputModifiers.Control, PhysicalKey.None, null);
        Assert.False(viewModel.SidebarOpen);
        Assert.False(viewModel.SidebarPaneOpen);

        window.Close();
        await UiWait.Until(() => persistence.Config?.Window.SidebarCollapsed == true);
    }

    [AvaloniaFact]
    public async Task NarrowWindowUsesAnOverlayThatClosesAfterSelection()
    {
        var paths = Enumerable.Range(0, 2).Select(_ => Path.GetTempFileName()).ToArray();
        try
        {
            // The restored window size (default 1280) is re-applied after startup, so persist 720.
            var persistence = new TestPersistence(
                new AppConfig
                {
                    Window = new AppWindowState { Width = 720, Height = 720 },
                }
            );
            var window = TestWindow.Create(persistence, out var viewModel);
            window.Width = 720;
            window.Show();
            await viewModel.OpenPathsCommand.Execute(paths);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(SplitViewDisplayMode.Overlay, viewModel.SidebarDisplayMode);
            Assert.False(viewModel.SidebarPaneOpen);

            await viewModel.ToggleSidebarCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            Assert.True(viewModel.SidebarPaneOpen);
            Assert.True(viewModel.SidebarOpen);

            var target = viewModel.FileTabs[0];
            SelectButton(window, target).Command!.Execute(target);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(target, viewModel.SelectedFile);
            Assert.False(viewModel.SidebarPaneOpen);
            Assert.True(viewModel.SidebarOpen);
            window.Close();
        }
        finally
        {
            foreach (var path in paths)
                File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ElasticRowsOpenSelectAndCloseSources()
    {
        var viewModel = ElasticFixtures.Workspace(
            ElasticFixtures.Connection("c-1", "prod-eu", "checkout")
        );
        var window = new MainWindow(viewModel, registerNativePicker: false) { Width = 1280 };
        window.Show();
        await UiWait.Until(() => viewModel.ElasticConnectionGroups.Count == 1);
        Dispatcher.UIThread.RunJobs();
        var source = viewModel.ElasticConnectionGroups[0].Sources[0];

        Assert.Contains(
            Sidebar(window).GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text == "prod-eu"
        );
        SelectButton(window, source).Command!.Execute(null);
        await UiWait.Until(() => source.IsOpen && source.IsSelected);
        Assert.Contains("selected", Row(window, source).Classes);
        var close = CloseButton(window, source);
        Assert.True(close.IsVisible);
        close.Command!.Execute(null);
        await UiWait.Until(() => !source.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void ElasticSectionOffersSetupWhenNothingIsConfigured()
    {
        var window = TestWindow.Create(out var viewModel);
        window.Width = 1280;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var sidebar = Sidebar(window);

        Assert.True(sidebar.FindControl<TextBlock>("ElasticEmptyHint")!.IsVisible);
        var setup = sidebar.FindControl<Button>("SidebarElasticSetupButton")!;
        setup.Command!.Execute(null);
        Assert.True(viewModel.SettingsOpen);
        Assert.Equal(2, viewModel.Settings.SectionIndex);
        window.Close();
    }

    private static SourceSidebar Sidebar(Window window) =>
        window.GetVisualDescendants().OfType<SourceSidebar>().Single();

    private static Border[] Rows(Window window) =>
        Sidebar(window)
            .GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.Classes.Contains("source-row"))
            .ToArray();

    private static Border Row(Window window, object item) =>
        Rows(window).Single(row => ReferenceEquals(row.DataContext, item));

    private static Button SelectButton(Window window, object item) =>
        Sidebar(window)
            .GetVisualDescendants()
            .OfType<Button>()
            .Single(button =>
                ReferenceEquals(button.DataContext, item)
                && button.Classes.Contains("source-select")
            );

    private static Button CloseButton(Window window, object item) =>
        Sidebar(window)
            .GetVisualDescendants()
            .OfType<Button>()
            .Single(button =>
                ReferenceEquals(button.DataContext, item) && button.Classes.Contains("tab-close")
            );
}
