using System.Reactive.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Application;
using HexTail.Persistence;
using HexTail.Tests.Support;
using HexTail.ViewModels;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class StreamSidebarTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Directory
            .CreateTempSubdirectory("hextail-sidebar-streams-")
            .FullName;

        public MainWindow Window { get; }
        public MainWindowViewModel ViewModel { get; }
        public string[] Paths { get; }

        public Fixture(int sources = 3, double width = 1280, AppConfig? config = null)
        {
            Window = config is null
                ? TestWindow.Create(out var viewModel)
                : TestWindow.Create(new TestPersistence(config), out viewModel);
            ViewModel = viewModel;
            Window.Width = width;
            Window.Show();
            Paths = Enumerable
                .Range(0, sources)
                .Select(index =>
                {
                    var path = Path.Combine(_directory, $"source-{index}.log");
                    File.WriteAllLines(path, [$"2026-01-01 10:00:0{index} INFO line {index}"]);
                    return path;
                })
                .ToArray();
        }

        public async Task OpenSourcesAsync()
        {
            await ViewModel.OpenPathsCommand.Execute(Paths);
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            Window.Close();
            Directory.Delete(_directory, true);
        }
    }

    private static SourceSidebar Sidebar(Window window) =>
        window.GetVisualDescendants().OfType<SourceSidebar>().Single();

    private static Button NamedButton(Window window, string name) =>
        Sidebar(window)
            .GetVisualDescendants()
            .OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == name);

    private static bool HasButton(Window window, string name) =>
        Sidebar(window)
            .GetVisualDescendants()
            .OfType<Button>()
            .Any(button =>
                AutomationProperties.GetName(button) == name && button.IsEffectivelyVisible
            );

    [AvaloniaFact]
    public async Task StreamsSectionListsOpenAndClosedStreamsAndClosedOnesOpenOnClick()
    {
        using var fixture = new Fixture();
        await fixture.OpenSourcesAsync();
        var viewModel = fixture.ViewModel;
        var files = viewModel.State.Files.ToArray();
        var alpha = await viewModel.State.CreateStreamAsync("Alpha", [files[0], files[1]], Ct);
        var beta = await viewModel.State.CreateStreamAsync("Beta", [files[1], files[2]], Ct);
        await viewModel.State.OpenStreamAsync(alpha.Id, Ct);
        await UiWait.Until(() => viewModel.StreamRows.Count == 2);
        Dispatcher.UIThread.RunJobs();

        var sidebar = Sidebar(fixture.Window);
        Assert.Contains(
            sidebar.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text == "STREAMS"
        );
        Assert.False(sidebar.FindControl<TextBlock>("NoStreamsHint")!.IsVisible);
        Assert.Equal(["Alpha", "Beta"], viewModel.StreamRows.Select(row => row.Name));
        Assert.True(viewModel.StreamRows[0].IsOpen);
        Assert.False(viewModel.StreamRows[1].IsOpen);
        Assert.Equal(2, viewModel.StreamRows[0].Dots.Count);
        // Streams are not sources: the sidebar's file list and the summary leave them out.
        Assert.Equal(3, viewModel.FileTabs.Count);

        Assert.True(HasButton(fixture.Window, "Close Alpha"));
        Assert.False(HasButton(fixture.Window, "Close Beta"));
        Assert.True(HasButton(fixture.Window, "Edit Alpha"));
        Assert.True(HasButton(fixture.Window, "Edit Beta"));

        NamedButton(fixture.Window, "Open Beta")
            .Command!.Execute(NamedButton(fixture.Window, "Open Beta").CommandParameter);
        await UiWait.Until(() => viewModel.SelectedFile?.DisplayName == "Beta");
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.StreamRows[1].IsOpen);
        Assert.True(viewModel.StreamRows[1].IsSelected);
        Assert.False(viewModel.StreamRows[0].IsSelected);
        Assert.True(HasButton(fixture.Window, "Close Beta"));

        // Clicking an open stream selects it.
        NamedButton(fixture.Window, "Open Alpha")
            .Command!.Execute(NamedButton(fixture.Window, "Open Alpha").CommandParameter);
        await UiWait.Until(() => viewModel.SelectedFile?.DisplayName == "Alpha");
    }

    [AvaloniaFact]
    public async Task EmptyStreamsSectionShowsAHintAndNewStreamButton()
    {
        using var fixture = new Fixture();
        await fixture.OpenSourcesAsync();
        Dispatcher.UIThread.RunJobs();
        var sidebar = Sidebar(fixture.Window);

        Assert.True(sidebar.FindControl<TextBlock>("NoStreamsHint")!.IsVisible);
        var add = NamedButton(fixture.Window, "New stream");
        add.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(fixture.ViewModel.StreamEditor);
        var editorPanel = sidebar.FindControl<Control>("StreamEditorPanel")!;
        Assert.True(editorPanel.IsEffectivelyVisible);
        Assert.Equal(3, fixture.ViewModel.StreamEditor!.Choices.Count);
        var name = sidebar.FindControl<TextBox>("StreamNameBox")!;
        Assert.Equal("Stream 1", name.Text);
        Assert.Equal("Stream name", AutomationProperties.GetName(name));
        var boxes = editorPanel.GetVisualDescendants().OfType<CheckBox>().ToArray();
        Assert.Equal(3, boxes.Length);
        Assert.All(
            boxes,
            box => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(box)))
        );
        Assert.Contains("source-0.log", AutomationProperties.GetName(boxes[0]));
        var save = sidebar.FindControl<Button>("StreamSaveButton")!;
        Assert.False(save.IsEffectivelyEnabled);
        Assert.Equal("Save stream", AutomationProperties.GetName(save));

        boxes[0].IsChecked = true;
        boxes[1].IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(save.IsEffectivelyEnabled);
        save.Command!.Execute(null);
        await UiWait.Until(() => fixture.ViewModel.SelectedFile?.Model.Stream is not null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(editorPanel.IsEffectivelyVisible);
        Assert.Contains(
            "Open Stream 1",
            Sidebar(fixture.Window)
                .GetVisualDescendants()
                .OfType<Button>()
                .Select(button => AutomationProperties.GetName(button))
        );
    }

    [AvaloniaFact]
    public async Task EditButtonOpensTheEditorWithMembersCheckedAndDeleteConfirmsInline()
    {
        using var fixture = new Fixture();
        await fixture.OpenSourcesAsync();
        var viewModel = fixture.ViewModel;
        var files = viewModel.State.Files.ToArray();
        await viewModel.State.CreateStreamAsync("Alpha", [files[0], files[2]], Ct);
        await UiWait.Until(() => viewModel.StreamRows.Count == 1);
        Dispatcher.UIThread.RunJobs();

        var edit = NamedButton(fixture.Window, "Edit Alpha");
        edit.Command!.Execute(edit.CommandParameter);
        Dispatcher.UIThread.RunJobs();

        var sidebar = Sidebar(fixture.Window);
        var panel = sidebar.FindControl<Control>("StreamEditorPanel")!;
        var boxes = panel.GetVisualDescendants().OfType<CheckBox>().ToArray();
        Assert.Equal([true, false, true], boxes.Select(box => box.IsChecked == true));
        Assert.Equal("Alpha", sidebar.FindControl<TextBox>("StreamNameBox")!.Text);

        var delete = sidebar.FindControl<Button>("StreamDeleteButton")!;
        Assert.True(delete.IsEffectivelyVisible);
        Assert.Equal("Delete stream", AutomationProperties.GetName(delete));
        Assert.False(sidebar.FindControl<Control>("StreamDeleteConfirm")!.IsEffectivelyVisible);
        delete.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(sidebar.FindControl<Control>("StreamDeleteConfirm")!.IsEffectivelyVisible);
        var confirm = sidebar.FindControl<Button>("StreamDeleteConfirmButton")!;
        Assert.Equal("Confirm delete stream", AutomationProperties.GetName(confirm));
        confirm.Command!.Execute(null);
        await UiWait.Until(() => viewModel.State.Streams.Count == 0);
        Dispatcher.UIThread.RunJobs();
        Assert.False(panel.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task NewStreamEditorHidesDeleteBecauseThereIsNothingToDelete()
    {
        using var fixture = new Fixture();
        await fixture.OpenSourcesAsync();
        await fixture.ViewModel.NewStreamCommand.Execute();
        Dispatcher.UIThread.RunJobs();

        Assert.False(
            Sidebar(fixture.Window).FindControl<Button>("StreamDeleteButton")!.IsEffectivelyVisible
        );
    }

    [AvaloniaFact]
    public async Task ClosingAStreamTabKeepsItsDefinitionAndNeverClosesMembers()
    {
        using var fixture = new Fixture();
        await fixture.OpenSourcesAsync();
        var viewModel = fixture.ViewModel;
        var files = viewModel.State.Files.ToArray();
        var made = await viewModel.State.CreateStreamAsync("Alpha", [files[0], files[1]], Ct);
        await viewModel.State.OpenStreamAsync(made.Id, Ct);
        await UiWait.Until(() => viewModel.StreamRows.Count == 1 && viewModel.StreamRows[0].IsOpen);
        Dispatcher.UIThread.RunJobs();

        var close = NamedButton(fixture.Window, "Close Alpha");
        close.Command!.Execute(close.CommandParameter);
        await UiWait.Until(() => !viewModel.StreamRows[0].IsOpen);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(viewModel.State.Streams);
        Assert.Equal(3, viewModel.State.Files.Count);
        Assert.Equal(3, viewModel.FileTabs.Count);
        Assert.DoesNotContain(
            viewModel.Files,
            tab => tab.Model.Source.Kind == LogSourceKind.Stream
        );
        Assert.False(HasButton(fixture.Window, "Close Alpha"));
        Assert.True(HasButton(fixture.Window, "Open Alpha"));
    }

    [AvaloniaFact]
    public async Task SourceSummaryDoesNotCountStreamsAsSources()
    {
        using var fixture = new Fixture();
        await fixture.OpenSourcesAsync();
        var viewModel = fixture.ViewModel;
        var files = viewModel.State.Files.ToArray();
        Assert.Equal("3 sources open", viewModel.SourceSummary);
        var made = await viewModel.State.CreateStreamAsync("Alpha", [files[0], files[1]], Ct);
        await viewModel.State.OpenStreamAsync(made.Id, Ct);

        await UiWait.Until(() => viewModel.SourceSummary == "3 sources open · 1 stream open");
        Assert.Equal("No sources open", MainWindowViewModel.DescribeSources([]));
    }

    [AvaloniaFact]
    public async Task PaletteOffersNewStreamAndOpenStreamForEachSavedStream()
    {
        using var fixture = new Fixture();
        await fixture.OpenSourcesAsync();
        var viewModel = fixture.ViewModel;
        var files = viewModel.State.Files.ToArray();
        await viewModel.State.CreateStreamAsync("Alpha", [files[0], files[1]], Ct);
        await viewModel.State.CreateStreamAsync("Beta", [files[1], files[2]], Ct);

        var items = viewModel.BuildPaletteItems().ToArray();
        Assert.Contains(items, item => item.Title == "New stream" && item.Category == "Streams");
        Assert.Contains(items, item => item.Title == "Open stream Alpha");
        var beta = Assert.Single(items, item => item.Title == "Open stream Beta");

        await beta.Execute();
        await UiWait.Until(() => viewModel.SelectedFile?.DisplayName == "Beta");

        await items.Single(item => item.Title == "New stream").Execute();
        Assert.NotNull(viewModel.StreamEditor);
    }

    [AvaloniaFact]
    public async Task NarrowSidebarDismissesAfterOpeningAStream()
    {
        using var fixture = new Fixture(
            width: 720,
            config: new AppConfig
            {
                Window = new AppWindowState { Width = 720, Height = 720 },
            }
        );
        await fixture.OpenSourcesAsync();
        var viewModel = fixture.ViewModel;
        var files = viewModel.State.Files.ToArray();
        await viewModel.State.CreateStreamAsync("Alpha", [files[0], files[1]], Ct);
        await UiWait.Until(() => viewModel.StreamRows.Count == 1);
        await viewModel.ToggleSidebarCommand.Execute();
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.SidebarPaneOpen);

        var open = NamedButton(fixture.Window, "Open Alpha");
        open.Command!.Execute(open.CommandParameter);
        await UiWait.Until(() => viewModel.SelectedFile?.DisplayName == "Alpha");
        Assert.False(viewModel.SidebarPaneOpen);
    }

    [AvaloniaFact]
    public async Task EveryStreamControlHasAnAccessibleNameContainingItsText()
    {
        using var fixture = new Fixture();
        await fixture.OpenSourcesAsync();
        var viewModel = fixture.ViewModel;
        var files = viewModel.State.Files.ToArray();
        var made = await viewModel.State.CreateStreamAsync("Alpha", [files[0], files[1]], Ct);
        await viewModel.State.OpenStreamAsync(made.Id, Ct);
        await viewModel.EditStreamCommand.Execute(made.Id);
        Dispatcher.UIThread.RunJobs();

        var sidebar = Sidebar(fixture.Window);
        var controls = sidebar
            .FindControl<Control>("StreamsSection")!
            .GetVisualDescendants()
            .OfType<Control>()
            .Where(control => control is Button or CheckBox or TextBox)
            .Where(control => control.IsEffectivelyVisible)
            .ToArray();
        Assert.NotEmpty(controls);
        Assert.All(
            controls,
            control =>
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)))
        );
        foreach (var button in controls.OfType<Button>().Where(b => b.Content is string))
            Assert.Contains((string)button.Content!, AutomationProperties.GetName(button));
    }
}
