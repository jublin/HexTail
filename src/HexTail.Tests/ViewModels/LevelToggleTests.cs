using System.Reactive.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Domain;
using HexTail.Tests.Support;

namespace HexTail.Tests.ViewModels;

public sealed class LevelToggleTests
{
    [AvaloniaFact]
    public async Task TogglesFollowTheSelectedSourcesMask()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var owner);
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            var model = owner.SelectedFile!.Model;
            owner.FilterBar.Sync();
            Assert.Equal(
                ["T", "D", "I", "W", "E"],
                owner.FilterBar.LevelToggles.Select(toggle => toggle.Label)
            );
            Assert.All(owner.FilterBar.LevelToggles, toggle => Assert.True(toggle.IsOn));
            Assert.False(owner.FilterBar.HasLevelFilter);

            await owner
                .FilterBar.LevelToggles.Single(t => t.Level == LogLevels.Debug)
                .ToggleCommand.Execute();
            Assert.Equal(LogLevels.All & ~LogLevels.Debug, model.Levels);
            owner.FilterBar.Sync();
            var debug = owner.FilterBar.LevelToggles.Single(t => t.Level == LogLevels.Debug);
            Assert.False(debug.IsOn);
            Assert.Equal("Debug hidden", debug.AutomationName);
            Assert.True(owner.FilterBar.HasLevelFilter);
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task TheLastEnabledLevelCannotBeCleared()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var owner);
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            var model = owner.SelectedFile!.Model;
            await owner.State.SetLevelsAsync(model, LogLevels.Error);
            owner.FilterBar.Sync();
            await owner
                .FilterBar.LevelToggles.Single(t => t.Level == LogLevels.Error)
                .ToggleCommand.Execute();
            Assert.Equal(LogLevels.Error, model.Levels);
            Assert.True(owner.FilterBar.LevelToggles.Single(t => t.Level == LogLevels.Error).IsOn);
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ClickingTheLastEnabledLevelButtonKeepsItChecked()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var owner);
            window.Show();
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            var model = owner.SelectedFile!.Model;
            await owner.State.SetLevelsAsync(model, LogLevels.Error);
            Dispatcher.UIThread.RunJobs();
            owner.FilterBar.Sync();
            await UiWait.Until(() => LevelButtons(window).Count == 5);
            var error = LevelButtons(window)[4];
            Assert.True(error.IsChecked);
            var center = error
                .TranslatePoint(
                    new Avalonia.Point(error.Bounds.Width / 2, error.Bounds.Height / 2),
                    window
                )!
                .Value;
            window.MouseDown(
                center,
                Avalonia.Input.MouseButton.Left,
                Avalonia.Input.RawInputModifiers.None
            );
            window.MouseUp(
                center,
                Avalonia.Input.MouseButton.Left,
                Avalonia.Input.RawInputModifiers.None
            );
            Dispatcher.UIThread.RunJobs();
            await UiWait.Until(() => error.IsChecked == true);
            Assert.Equal(LogLevels.Error, model.Levels);
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static List<Avalonia.Controls.Primitives.ToggleButton> LevelButtons(
        Avalonia.Controls.Window window
    ) =>
        Avalonia
            .VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<Avalonia.Controls.Primitives.ToggleButton>()
            .Where(button => button.Name == "LevelToggle")
            .ToList();

    [AvaloniaFact]
    public async Task TogglingALevelHidesThoseRowsInTheView()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var owner);
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            file.Model.Buffer.Append(new Line("INFO started"));
            file.Model.Buffer.Append(new Line("ERROR failed"));
            file.Model.Buffer.Append(new Line("continuation without a level"));
            file.SelectedView.Sync();
            Assert.Equal(3, file.SelectedView.Lines.Count);

            await owner.State.SetLevelsAsync(file.Model, LogLevels.Error);
            file.SelectedView.Sync();
            Assert.Equal(
                ["ERROR failed", "continuation without a level"],
                file.SelectedView.Lines.Select(row => row.Line.Raw)
            );
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task TheControlRendersFiveNamedToggleButtonsThatFollowTheMask()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var owner);
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            List<ToggleButton> Buttons() =>
                window
                    .GetVisualDescendants()
                    .OfType<ToggleButton>()
                    .Where(button => button.Name == "LevelToggle")
                    .ToList();
            Assert.Equal(5, Buttons().Count);
            Assert.All(Buttons(), button => Assert.True(button.Focusable));
            Assert.Equal(
                ["Trace shown", "Debug shown", "Info shown", "Warn shown", "Error shown"],
                Buttons().Select(button => AutomationProperties.GetName(button))
            );

            await owner.State.SetLevelsAsync(
                owner.SelectedFile!.Model,
                LogLevels.Warn | LogLevels.Error
            );
            owner.FilterBar.Sync();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(
                [false, false, false, true, true],
                Buttons().Select(button => button.IsChecked == true)
            );
            Assert.Equal("Trace hidden", AutomationProperties.GetName(Buttons()[0]));
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
