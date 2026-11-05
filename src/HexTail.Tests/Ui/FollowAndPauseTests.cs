using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.Ui;

public sealed class FollowAndPauseTests
{
    [AvaloniaFact]
    public async Task ScrolledAwayViewCountsNewLinesAndJumpsToLatest()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllLinesAsync(path, Enumerable.Range(0, 20).Select(i => $"line {i}"));
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await viewModel.OpenPathsCommand.Execute([path]);
            var view = await LoadedView(viewModel, 20);

            view.IsFollowing = false;
            await File.AppendAllLinesAsync(path, ["new 1", "new 2", "new 3"]);
            await UiWait.Until(() =>
            {
                viewModel.State.DrainTailerEvents();
                return view.UnseenCount == 3;
            });
            Assert.Equal("↓ 3 new lines", view.UnseenLabel);
            var pill = window
                .GetVisualDescendants()
                .OfType<Button>()
                .Single(button => button.Name == "JumpToLatestButton");
            Assert.True(pill.IsVisible);

            await view.JumpToLatestCommand.Execute();
            Assert.True(view.IsFollowing);
            Assert.Equal(0, view.UnseenCount);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task PauseFreezesRowsWhileBufferingAndResumeCatchesUp()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllLinesAsync(path, Enumerable.Range(0, 10).Select(i => $"line {i}"));
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await viewModel.OpenPathsCommand.Execute([path]);
            var view = await LoadedView(viewModel, 10);

            await viewModel.TogglePauseCommand.Execute();
            Assert.True(viewModel.IsPaused);
            await File.AppendAllLinesAsync(path, ["late 1", "late 2"]);
            await UiWait.Until(() =>
            {
                viewModel.State.DrainTailerEvents();
                return viewModel.SelectedFile!.Model.Buffer.Count == 12;
            });
            Assert.Equal(10, view.Lines.Count);
            Assert.Equal(2, view.UnseenCount);

            await viewModel.TogglePauseCommand.Execute();
            Assert.Equal(12, view.Lines.Count);
            Assert.Equal(0, view.UnseenCount);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task PausedIndicatorKeepsDisplayZonePickerInsideTheWindowAtMinimumWidth()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllLinesAsync(path, Enumerable.Range(0, 5).Select(i => $"line {i}"));
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            window.Width = 720;
            await viewModel.OpenPathsCommand.Execute([path]);
            await LoadedView(viewModel, 5);
            await viewModel.TogglePauseCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var statusBar = window.GetVisualDescendants().OfType<Views.StatusBar>().Single();
            var indicator = statusBar.FindControl<TextBlock>("PausedIndicator")!;
            var picker = statusBar.FindControl<ComboBox>("DisplayZonePicker")!;
            Assert.True(indicator.IsEffectivelyVisible);
            Assert.True(picker.IsEffectivelyVisible && picker.Bounds.Width > 0);
            var left = picker.TranslatePoint(default, statusBar)!.Value.X;
            Assert.True(left >= 0, $"picker left {left}");
            Assert.True(
                left + picker.Bounds.Width <= statusBar.Bounds.Width,
                $"picker right {left + picker.Bounds.Width} exceeds status bar width {statusBar.Bounds.Width}"
            );
            Assert.True(statusBar.Bounds.Width <= 720);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<LogViewViewModel> LoadedView(MainWindowViewModel viewModel, int lines)
    {
        await UiWait.Until(() =>
        {
            viewModel.State.DrainTailerEvents();
            return viewModel.SelectedFile?.SelectedView.Lines.Count == lines;
        });
        return viewModel.SelectedFile!.SelectedView;
    }
}
