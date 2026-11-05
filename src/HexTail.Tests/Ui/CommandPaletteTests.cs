using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Tests.Support;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class CommandPaletteTests
{
    [AvaloniaFact]
    public async Task ControlKOpensPaletteAndEnterSwitchesSource()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var api = Path.Combine(directory, "api.log");
        var worker = Path.Combine(directory, "worker.log");
        File.WriteAllText(api, "");
        File.WriteAllText(worker, "");
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await viewModel.OpenPathsCommand.Execute([api, worker]);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("worker.log", viewModel.SelectedFile!.DisplayName);

            window.KeyPress(Key.K, RawInputModifiers.Control, PhysicalKey.K, null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(viewModel.Palette.IsOpen);
            var query = window
                .GetVisualDescendants()
                .OfType<CommandPalette>()
                .Single()
                .FindControl<TextBox>("PaletteQuery")!;
            Assert.True(query.IsFocused);

            query.Text = "api.log";
            Dispatcher.UIThread.RunJobs();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await UiWait.Until(() => viewModel.SelectedFile?.DisplayName == "api.log");
            Assert.False(viewModel.Palette.IsOpen);

            window.KeyPress(Key.K, RawInputModifiers.Control, PhysicalKey.K, null);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False(viewModel.Palette.IsOpen);
            window.Close();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public void PaletteKeysStayInsideThePaletteWhileItIsOpen()
    {
        var window = TestWindow.Create(out var viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(
            Key.P,
            RawInputModifiers.Control | RawInputModifiers.Shift,
            PhysicalKey.P,
            null
        );
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.Palette.IsOpen);
        var palette = window.GetVisualDescendants().OfType<CommandPalette>().Single();
        var query = palette.FindControl<TextBox>("PaletteQuery")!;
        Assert.True(query.IsFocused);

        var sidebarOpen = viewModel.SidebarPaneOpen;
        var paused = viewModel.IsPaused;
        window.KeyPress(Key.B, RawInputModifiers.Control, PhysicalKey.B, null);
        window.KeyTextInput(" fg");
        Assert.Equal(sidebarOpen, viewModel.SidebarPaneOpen);
        Assert.Equal(paused, viewModel.IsPaused);
        Assert.Equal(" fg", query.Text);
        Assert.True(viewModel.Palette.IsOpen);

        query.Text = "";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, viewModel.Palette.SelectedIndex);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.Equal(1, viewModel.Palette.SelectedIndex);
        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Assert.Equal(0, viewModel.Palette.SelectedIndex);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.Palette.IsOpen);
        Assert.False(palette.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void PaletteButtonOpensThePalette()
    {
        var window = TestWindow.Create(out var viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.FindControl<Button>("CommandPaletteButton")!.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.Palette.IsOpen);
        window.Close();
    }
}
