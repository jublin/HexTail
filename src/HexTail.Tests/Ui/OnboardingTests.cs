using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Persistence;
using HexTail.Tests.Support;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class OnboardingTests
{
    [AvaloniaFact]
    public async Task EmptyWorkspaceOffersFileAndElasticSetupAtMinimumWidth()
    {
        var window = TestWindow.Create(out var workspace);
        try
        {
            window.Width = 720;
            window.Height = 480;
            workspace.Settings.SectionIndex = 0;
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var content = Assert.Single(window.GetVisualDescendants().OfType<LogWorkspace>());
            var open = content.FindControl<Button>("OpenEmptyFileButton");
            var setup = content.FindControl<Button>("SetUpElasticButton");
            Assert.NotNull(open);
            Assert.NotNull(setup);
            Assert.True(open.IsEffectivelyVisible);
            Assert.True(setup.IsEffectivelyVisible);
            Assert.Same(workspace.OpenCommand, open.Command);
            Assert.True(setup.Bounds.Width > 0);
            var right = setup.TranslatePoint(new Point(setup.Bounds.Width, 0), window)!.Value.X;
            Assert.True(right <= window.Bounds.Width);

            var point = setup
                .TranslatePoint(new Point(setup.Bounds.Width / 2, setup.Bounds.Height / 2), window)!
                .Value;
            window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(workspace.SettingsOpen);
            Assert.Equal(2, workspace.Settings.SectionIndex);
        }
        finally
        {
            await workspace.DisposeAsync();
            Dispatcher.UIThread.RunJobs();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task EmptyStateListsRecentFilesAndOpensTheSample()
    {
        var recent = Path.GetTempFileName();
        try
        {
            var persistence = new TestPersistence(new AppConfig { RecentFiles = [recent] });
            var window = TestWindow.Create(persistence, out var viewModel);
            window.Show();
            await UiWait.Until(() => viewModel.RecentFiles.Count == 1);
            Assert.True(viewModel.HasRecents);
            Assert.Equal(Path.GetFileName(recent), viewModel.RecentFiles[0].Name);

            await viewModel.OpenRecentCommand.Execute(recent);
            await UiWait.Until(() => viewModel.SelectedFile?.Path == recent);
            await viewModel.CloseFileCommand.Execute(viewModel.SelectedFile!);

            await viewModel.OpenSampleCommand.Execute();
            await UiWait.Until(() => viewModel.SelectedFile?.DisplayName == "hextail-sample.log");
            Assert.DoesNotContain(
                viewModel.State.RecentFiles,
                path => path.EndsWith("hextail-sample.log")
            );
            window.Close();
        }
        finally
        {
            File.Delete(recent);
        }
    }

    [AvaloniaFact]
    public async Task MissingRecentFileIsShownDisabledAndReportsAnErrorWhenOpened()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log");
        var persistence = new TestPersistence(new AppConfig { RecentFiles = [missing] });
        var window = TestWindow.Create(persistence, out var viewModel);
        window.Show();
        await UiWait.Until(() => viewModel.RecentFiles.Count == 1);
        Assert.False(viewModel.RecentFiles[0].Exists);
        Assert.EndsWith("(missing)", viewModel.RecentFiles[0].ToolTip);

        await viewModel.OpenRecentCommand.Execute(missing);
        Assert.Contains("no longer exists", viewModel.FileError);
        Assert.Null(viewModel.SelectedFile);
        Assert.Single(viewModel.RecentFiles);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ClosingTheSampleStopsItsWriter()
    {
        var persistence = new TestPersistence();
        var window = TestWindow.Create(persistence, out var viewModel);
        window.Show();
        await viewModel.OpenSampleCommand.Execute();
        await UiWait.Until(() => viewModel.SelectedFile?.DisplayName == "hextail-sample.log");
        var path = viewModel.SelectedFile!.Path;
        await viewModel.CloseFileCommand.Execute(viewModel.SelectedFile!);
        await UiWait.Until(() => viewModel.Files.Count == 0);
        await Task.Delay(1200);
        var length = new FileInfo(path).Length;
        await Task.Delay(1200);
        Assert.Equal(length, new FileInfo(path).Length);
        await viewModel.DisposeAsync();
        window.Close();
        File.Delete(path);
    }
}
