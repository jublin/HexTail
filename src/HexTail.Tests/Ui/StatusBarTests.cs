using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Application;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;
using HexTail.ViewModels;
using HexTail.Views;
using ReactiveUI;
using ReactiveUI.Reactive;

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

    [AvaloniaFact]
    public async Task LongElasticStatusKeepsDisplayZonePickerInsideTheWindowAtMinimumWidth()
    {
        var connection = new ElasticConnectionSettings
        {
            Id = "server-1",
            Name = "Elastic",
            KibanaUrl = "https://kibana/",
            ElasticsearchUrl = "https://elastic/",
            Views =
            [
                new ElasticViewSettings
                {
                    Id = "view-1",
                    Name = "Logs",
                    DataViewTitle = "logs-*",
                    TimeFieldName = "@timestamp",
                    ServerField = "ident",
                    NamespaceField = "service.name",
                    OutputFields = ["message"],
                    Sources =
                    [
                        new ElasticSourceSettings
                        {
                            Id = "source-1",
                            ServerValue = "app1",
                            NamespaceValue = "prod",
                        },
                    ],
                },
            ],
        };
        await using var state = new AppState(
            new LogSourceService(),
            new TestPersistence(),
            new AppSettings { ElasticConnections = [connection] },
            elastic: new FakeElasticApiClient()
        );
        var viewModel = new MainWindowViewModel(
            state,
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        await state.OpenElasticSourceAsync("source-1", save: false);
        var window = new MainWindow(viewModel, registerNativePicker: false);
        window.Show();
        window.Width = 720;
        Dispatcher.UIThread.RunJobs();

        state.Files.Single().Error = new string('x', 600);
        viewModel.RaisePropertyChanged(nameof(MainWindowViewModel.ElasticRangeStatus));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var statusBar = window.GetVisualDescendants().OfType<StatusBar>().Single();
        var range = statusBar.FindControl<TextBlock>("StatusRange")!;
        var picker = statusBar.FindControl<ComboBox>("DisplayZonePicker")!;
        Assert.True(viewModel.IsElasticSelected);
        Assert.True(range.IsEffectivelyVisible);
        Assert.StartsWith("Failed: xxx", range.Text);
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
