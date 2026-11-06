using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Domain;
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
    public async Task WideningRestoresThePreferenceAndNarrowingResetsTheOverlay()
    {
        // ApplyWindowState re-applies the restored size once after startup. Seeding a position
        // gives a deterministic signal that it has finished before the test resizes the window.
        var persistence = new TestPersistence(
            new AppConfig
            {
                Window = new AppWindowState
                {
                    Width = 720,
                    Height = 720,
                    X = 10,
                    Y = 20,
                },
            }
        );
        var window = TestWindow.Create(persistence, out var viewModel);
        window.Width = 720;
        window.Show();
        await UiWait.Until(() => window.Position == new PixelPoint(10, 20));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SplitViewDisplayMode.Overlay, viewModel.SidebarDisplayMode);
        Assert.False(viewModel.SidebarPaneOpen);

        await viewModel.ToggleSidebarCommand.Execute();
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.SidebarPaneOpen);

        await ResizeAsync(window, 1280);
        await UiWait.Until(() => viewModel.SidebarDisplayMode == SplitViewDisplayMode.Inline);
        Assert.True(viewModel.SidebarOpen);
        Assert.True(viewModel.SidebarPaneOpen);

        await viewModel.ToggleSidebarCommand.Execute();
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.SidebarOpen);
        Assert.False(viewModel.SidebarPaneOpen);

        await ResizeAsync(window, 720);
        await UiWait.Until(() => viewModel.SidebarDisplayMode == SplitViewDisplayMode.Overlay);
        Assert.False(viewModel.SidebarPaneOpen);
        Assert.False(viewModel.SidebarOpen);

        await ResizeAsync(window, 1280);
        await UiWait.Until(() => viewModel.SidebarDisplayMode == SplitViewDisplayMode.Inline);
        Assert.False(viewModel.SidebarPaneOpen);
        Assert.False(viewModel.SidebarOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task RestoredSidebarPreferenceIsAppliedAndWidthIsClamped()
    {
        var persistence = new TestPersistence(
            new AppConfig
            {
                Window = new AppWindowState
                {
                    Width = 1280,
                    Height = 720,
                    SidebarCollapsed = true,
                    SidebarWidth = 9999,
                },
            }
        );
        var window = TestWindow.Create(persistence, out var viewModel);
        window.Width = 1280;
        window.Show();
        await UiWait.Until(() => viewModel.SidebarWidth == 480);
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.SidebarOpen);
        Assert.False(viewModel.SidebarPaneOpen);
        Assert.Equal(480, viewModel.SidebarWidth);

        viewModel.SidebarWidth = 10;
        Assert.Equal(160, viewModel.SidebarWidth);
        window.Close();
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

    [AvaloniaFact]
    public async Task SavedFilterRowsListApplyAndDelete()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Width = 1280;
            window.Show();
            await viewModel.OpenPathsCommand.Execute([path]);
            var file = viewModel.SelectedFile!.Model;
            var sidebar = Sidebar(window);
            Assert.True(sidebar.FindControl<TextBlock>("SavedFiltersEmptyHint")!.IsVisible);

            await viewModel.State.SetSourceRulesAsync(
                file,
                [TestRules.Source("boom"), TestRules.Source("noise", RuleAction.Hide)]
            );
            await viewModel.State.SetLevelsAsync(file, LogLevels.Warn | LogLevels.Error);
            Dispatcher.UIThread.RunJobs();
            Assert.True(viewModel.FilterBar.CanSaveFilter);
            viewModel.FilterBar.NewFilterName = "mine";
            await viewModel.FilterBar.SaveFilterCommand.Execute();
            await UiWait.Until(() => viewModel.SavedFilters.Count == 1);
            Dispatcher.UIThread.RunJobs();

            Assert.False(sidebar.FindControl<TextBlock>("SavedFiltersEmptyHint")!.IsVisible);
            Assert.Equal(string.Empty, viewModel.FilterBar.NewFilterName);
            var saved = Assert.Single(viewModel.SavedFilters);
            Assert.Equal("2 rules · levels W E", saved.Summary);
            Assert.Single(
                sidebar
                    .FindControl<ItemsControl>("SavedFilterList")!
                    .GetVisualDescendants()
                    .OfType<Border>(),
                border => border.Classes.Contains("source-row")
            );

            await viewModel.State.SetSourceRulesAsync(file, []);
            await viewModel.State.SetLevelsAsync(file, LogLevels.All);
            Dispatcher.UIThread.RunJobs();
            SelectButton(window, saved).Command!.Execute(null);
            await UiWait.Until(() => file.Rules.Count == 2);
            Assert.Equal(["boom", "noise"], file.Rules.Select(rule => rule.Pattern));
            Assert.Equal(LogLevels.Warn | LogLevels.Error, file.Levels);

            CloseButton(window, saved).Command!.Execute(null);
            await UiWait.Until(() => viewModel.SavedFilters.Count == 0);
            Dispatcher.UIThread.RunJobs();
            Assert.True(sidebar.FindControl<TextBlock>("SavedFiltersEmptyHint")!.IsVisible);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task ResizeAsync(Window window, double width)
    {
        window.Width = width;
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();
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
