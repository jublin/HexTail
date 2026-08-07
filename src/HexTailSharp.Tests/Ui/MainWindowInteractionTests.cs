using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentIcons.Avalonia;
using HexTailSharp.Application;
using HexTailSharp.Domain;
using HexTailSharp.Persistence;
using HexTailSharp.Tailing;
using HexTailSharp.Tests.Support;
using HexTailSharp.ViewModels;

namespace HexTailSharp.Tests.Ui;

public sealed class MainWindowInteractionTests
{
    [AvaloniaFact]
    public void SettingsInspectorAndEveryComboBoxOpen()
    {
        var window = TestWindow.Create(out var viewModel);
        var pane = window.FindControl<SplitView>("SettingsSplitView")!;
        viewModel.SettingsOpen = true;
        window.Show();

        Assert.True(pane.IsPaneOpen);
        foreach (var name in new[] { "MatchModeBox", "DensityBox", "FontSizeBox" })
        {
            var combo = window.FindControl<ComboBox>(name)!;
            combo.IsDropDownOpen = true;
            Assert.True(combo.IsDropDownOpen);
            combo.IsDropDownOpen = false;
        }

        window.FindControl<ComboBox>("MatchModeBox")!.SelectedItem = MatchMode.Regex;
        window.FindControl<ComboBox>("DensityBox")!.SelectedItem = UiDensity.Compact;
        window.FindControl<ComboBox>("FontSizeBox")!.SelectedItem = LogFontSize.Large;
        Assert.Equal(MatchMode.Regex, viewModel.MatchMode);
        Assert.Equal(UiDensity.Compact, viewModel.Settings.Density);
        Assert.Equal(LogFontSize.Large, viewModel.Settings.FontSize);
        window.Close();
    }

    [AvaloniaFact]
    public void SettingsOpenCloseAndEscape()
    {
        var window = TestWindow.Create(out var viewModel);
        var button = window.FindControl<Button>("SettingsButton")!;

        Click(button);
        Assert.True(viewModel.SettingsOpen);
        Click(button);
        Assert.False(viewModel.SettingsOpen);
        viewModel.SettingsOpen = true;

        window.Show();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);

        Assert.False(viewModel.SettingsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task LabelsAndExclusionsAddEditAndRemove()
    {
        var persistence = new TestPersistence();
        var window = TestWindow.Create(persistence, out var viewModel);
        await viewModel.InitializeAsync();
        viewModel.SettingsOpen = true;
        window.Show();

        viewModel.Settings.NewLabelText = "ERROR";
        viewModel.Settings.NewLabelColor = Colors.Magenta;
        Click(window.FindControl<Button>("AddLabelButton")!);
        Assert.Equal(string.Empty, viewModel.Settings.NewLabelText);
        await WaitFor(() => viewModel.Settings.Labels.Count == 1);
        var label = Assert.Single(viewModel.Settings.Labels);
        Dispatcher.UIThread.RunJobs();
        var labelControls = window
            .GetVisualDescendants()
            .Where(control => ReferenceEquals(control.DataContext, label))
            .ToArray();
        labelControls.OfType<TextBox>().Single().Text = "WARN";
        await WaitFor(() => viewModel.State.Settings.GlobalLabels[0].Text == "WARN");
        Click(labelControls.OfType<Button>().Single());
        await WaitFor(() => viewModel.Settings.Labels.Count == 0);

        viewModel.Settings.SectionIndex = 1;
        viewModel.Settings.NewExclusionText = "healthcheck";
        Click(window.FindControl<Button>("AddExclusionButton")!);
        await WaitFor(() => viewModel.Settings.Exclusions.Count == 1);
        var exclusion = Assert.Single(viewModel.Settings.Exclusions);
        Dispatcher.UIThread.RunJobs();
        var exclusionControls = window
            .GetVisualDescendants()
            .Where(control => ReferenceEquals(control.DataContext, exclusion))
            .ToArray();
        exclusionControls.OfType<TextBox>().Single().Text = "probe";
        await WaitFor(() => viewModel.State.Settings.GlobalExcludeLabels[0] == "probe");
        Click(exclusionControls.OfType<Button>().Single());
        await WaitFor(() => viewModel.Settings.Exclusions.Count == 0);

        Assert.True(persistence.SaveCount >= 6);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SearchCanBeAddedByButtonAndEnter()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var viewModel);
            await viewModel.OpenPathsCommand.Execute([path]);
            window.Show();
            var query = window.FindControl<TextBox>("QueryBox")!;

            query.Text = "first";
            Dispatcher.UIThread.RunJobs();
            Click(window.FindControl<Button>("AddSearchButton")!);
            await WaitFor(() => viewModel.SelectedFile!.Model.Searches.Count == 1);

            query.Text = "ignored";
            query.Focus();
            window.KeyPress(Key.A, RawInputModifiers.None, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(viewModel.SelectedFile!.Model.Searches);

            query.Text = "second";
            query.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            await WaitFor(() => viewModel.SelectedFile!.Model.Searches.Count == 2);

            Assert.Equal("first", viewModel.SelectedFile!.Model.Searches[0].Query.Query);
            Assert.Equal("second", viewModel.SelectedFile.Model.Searches[1].Query.Query);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task InvalidRegexStaysVisibleAndPreservesQuery()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var viewModel);
            await viewModel.OpenPathsCommand.Execute([path]);
            window.Show();
            viewModel.MatchMode = MatchMode.Regex;
            viewModel.Query = "[";
            Dispatcher.UIThread.RunJobs();

            Click(window.FindControl<Button>("AddSearchButton")!);
            await WaitFor(() => viewModel.HasSearchError);

            Assert.Equal("[", viewModel.Query);
            Assert.True(window.FindControl<TextBlock>("SearchError")!.IsVisible);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task InvalidPathShowsPersistentWorkspaceError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.log");
        var window = TestWindow.Create(out var viewModel);
        await viewModel.OpenPathsCommand.Execute([path]);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var alert = window.FindControl<Border>("FileErrorAlert");
        Assert.True(viewModel.HasFileError);
        Assert.NotNull(alert);
        Assert.True(alert.IsVisible);
        Assert.Contains(path, Assert.IsType<TextBlock>(alert.Child).Text);

        window.Close();
    }

    [AvaloniaFact]
    public async Task FileTabsExposeSelectionPathAndFluentCloseAction()
    {
        var firstPath = Path.GetTempFileName();
        var secondPath = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await viewModel.OpenPathsCommand.Execute([firstPath, secondPath]);
            Dispatcher.UIThread.RunJobs();
            var first = viewModel.Files[0];
            var second = viewModel.Files[1];
            var tabs = window
                .GetVisualDescendants()
                .OfType<Border>()
                .Where(border => border.Classes.Contains("file-tab"))
                .ToDictionary(border => Assert.IsType<FileTabViewModel>(border.DataContext));

            Click(
                window
                    .GetVisualDescendants()
                    .OfType<Button>()
                    .Single(button =>
                        ReferenceEquals(button.DataContext, first)
                        && Equals(button.CommandParameter, first)
                        && button.Content is TextBlock
                    )
            );
            Dispatcher.UIThread.RunJobs();

            Assert.Contains("selected", tabs[first].Classes);
            Assert.DoesNotContain("selected", tabs[second].Classes);
            Assert.NotNull(tabs[first].Background);
            Assert.Equal(firstPath, ToolTip.GetTip(tabs[first]));
            var closeButton = window
                .GetVisualDescendants()
                .OfType<Button>()
                .Single(button =>
                    ReferenceEquals(button.DataContext, first) && button.Content is FluentIcon
                );
            Assert.Equal($"Close {firstPath}", ToolTip.GetTip(closeButton));
            Assert.Equal($"Close {firstPath}", AutomationProperties.GetName(closeButton));

            window.Close();
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [AvaloniaFact]
    public async Task FileTabsSelectAndCloseThroughWindowCommands()
    {
        var firstPath = Path.GetTempFileName();
        var secondPath = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await viewModel.OpenPathsCommand.Execute([firstPath, secondPath]);
            Dispatcher.UIThread.RunJobs();
            var first = viewModel.Files[0];
            var buttons = window
                .GetVisualDescendants()
                .OfType<Button>()
                .Where(button => ReferenceEquals(button.DataContext, first))
                .ToArray();

            Click(
                buttons.Single(button =>
                    Equals(button.CommandParameter, first) && button.Content is TextBlock
                )
            );
            Assert.Same(first, viewModel.SelectedFile);
            Click(
                buttons.Single(button =>
                    Equals(button.CommandParameter, first) && button.Content is not TextBlock
                )
            );
            await WaitFor(() => !viewModel.Files.Contains(first));

            Assert.Single(viewModel.Files);
            window.Close();
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [AvaloniaFact]
    public async Task OpenShortcutAcceptsControlOrMeta()
    {
        var viewModel = new MainWindowViewModel(
            new AppState(new TailerService(), new TestPersistence()),
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        using var handler = viewModel.PickFiles.RegisterHandler(context => context.SetOutput([]));
        var window = new ShortcutWindow(viewModel);
        var completions = 0;
        using var subscription = viewModel.OpenCommand.Subscribe(_ => completions++);

        foreach (var modifier in new[] { KeyModifiers.Control, KeyModifiers.Meta })
        {
            var key = window.PressShortcut(Key.O, modifier);
            Assert.True(key.Handled);
            Dispatcher.UIThread.RunJobs();
            await WaitFor(() => completions > 0);
            completions = 0;
        }
    }

    [AvaloniaTheory]
    [InlineData(RawInputModifiers.Control)]
    [InlineData(RawInputModifiers.Meta)]
    public async Task FindShortcutAcceptsControlOrMeta(RawInputModifiers modifier)
    {
        var path = Path.GetTempFileName();
        var window = TestWindow.Create(out var viewModel);
        await viewModel.OpenPathsCommand.Execute([path]);
        window.Show();
        var query = window.FindControl<TextBox>("QueryBox")!;

        window.KeyPress(Key.F, modifier, PhysicalKey.None, null);

        Assert.True(query.IsFocused);
        window.Close();
        File.Delete(path);
    }

    [AvaloniaTheory]
    [InlineData(RawInputModifiers.Control)]
    [InlineData(RawInputModifiers.Meta)]
    public async Task SaveShortcutAcceptsControlOrMeta(RawInputModifiers modifier)
    {
        var persistence = new TestPersistence();
        var window = TestWindow.Create(persistence, out _);
        window.Show();

        window.KeyPress(Key.S, modifier, PhysicalKey.None, null);

        await WaitFor(() => persistence.SaveCount == 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SettingsFailureStaysInsideInspector()
    {
        var persistence = new TestPersistence { SaveError = new IOException("disk full") };
        var window = TestWindow.Create(persistence, out var viewModel);
        viewModel.SettingsOpen = true;
        window.Show();

        await viewModel.Settings.CommitAsync(
            viewModel.State.Settings with
            {
                Density = UiDensity.Compact,
            }
        );

        Assert.True(viewModel.Settings.HasSaveError);
        Assert.Contains("disk full", viewModel.Settings.SaveError);
        Assert.True(window.FindControl<TextBlock>("SettingsSaveError")!.IsVisible);
        Assert.False(viewModel.HasFileError);
        persistence.SaveError = null;
        window.Close();
    }

    [AvaloniaFact]
    public void NarrowWidthUsesOverlayAndWideWidthUsesInline()
    {
        var window = TestWindow.Create(out _);
        var pane = window.FindControl<SplitView>("SettingsSplitView")!;
        window.Show();

        window.Width = 900;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SplitViewDisplayMode.Overlay, pane.DisplayMode);
        window.Width = 1200;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SplitViewDisplayMode.Inline, pane.DisplayMode);
        window.Close();
    }

    private static void Click(Button button)
    {
        Assert.NotNull(button.Command);
        Assert.True(button.Command.CanExecute(button.CommandParameter));
        button.Command.Execute(button.CommandParameter);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < timeout)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class ShortcutWindow(MainWindowViewModel viewModel)
        : MainWindow(viewModel, registerNativePicker: false)
    {
        public KeyEventArgs PressShortcut(Key key, KeyModifiers modifiers)
        {
            var args = new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = key,
                KeyModifiers = modifiers,
            };
            OnKeyDown(args);
            return args;
        }
    }
}
