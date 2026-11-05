using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Persistence;
using HexTail.Tests.Support;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class StatusBarTests
{
    [AvaloniaFact]
    public async Task StatusBarShowsSourceSummaryAndSelectedViewCount()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllLinesAsync(path, ["a", "b", "c"]);
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log");
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await viewModel.OpenPathsCommand.Execute([path]);
            await UiWait.Until(() =>
            {
                viewModel.State.DrainTailerEvents();
                return Text(window, "ResultCount") == "3 visible · 3 loaded from source";
            });
            Assert.Equal("1 source open", Text(window, "SourceSummary"));

            await viewModel.OpenPathsCommand.Execute([missing]);
            await UiWait.Until(() =>
                Text(window, "SourceSummary") == "2 sources open · 1 with errors"
            );
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task DisplayZonePickerIsTheOnlyTimeZoneControl()
    {
        var window = TestWindow.Create(out var viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var picker = window
            .GetVisualDescendants()
            .OfType<StatusBar>()
            .Single()
            .FindControl<ComboBox>("DisplayZonePicker")!;

        picker.SelectedItem = AppTimeZoneMode.Local;
        await UiWait.Until(() => viewModel.State.Settings.TimeZoneMode == AppTimeZoneMode.Local);

        viewModel.SettingsOpen = true;
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(
            window.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text == "Source clock time zone"
        );
        window.Close();
    }

    [AvaloniaFact]
    public async Task ViewToolbarTogglesFollowAndContextForTheSelectedView()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await viewModel.OpenPathsCommand.Execute([path]);
            Dispatcher.UIThread.RunJobs();
            var follow = Toggle(window, "AutoScrollToggle");
            var context = Toggle(window, "InlineContextToggle");

            follow.IsChecked = false;
            context.IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            Assert.False(viewModel.SelectedFile!.Model.FollowAll);
            Assert.True(viewModel.SelectedFile.Model.ShowContext);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string? Text(Window window, string name) =>
        window
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .SingleOrDefault(block => block.Name == name)
            ?.Text;

    private static ToggleButton Toggle(Window window, string name) =>
        window
            .GetVisualDescendants()
            .OfType<ToggleButton>()
            .Single(toggle => toggle.Name == name && toggle.IsEffectivelyVisible);
}
