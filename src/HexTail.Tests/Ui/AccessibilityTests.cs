using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Application;
using HexTail.Tailing;
using HexTail.Tests.Support;
using HexTail.ViewModels;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class AccessibilityTests
{
    [AvaloniaFact]
    public async Task SettingsFocusEntersCyclesAndReturnsToItsOpener()
    {
        await using var owner = new MainWindowViewModel(
            new AppState(new LogSourceService(), new TestPersistence()),
            startPolling: false
        );
        await owner.InitializeAsync();
        var window = new MainWindow(owner, registerNativePicker: false);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var opener = window.FindControl<Control>("SettingsButton")!;
            Assert.True(opener.Focus());

            await owner.ToggleSettingsCommand.Execute().FirstAsync();
            Dispatcher.UIThread.RunJobs();
            var panel = window.GetVisualDescendants().OfType<SettingsPanel>().Single();
            AssertFocusInPanel(window, panel);

            // Walk beyond every control in this section, including backwards across the first one.
            foreach (var modifier in new[] { RawInputModifiers.None, RawInputModifiers.Shift })
                for (var step = 0; step < 24; step++)
                {
                    window.KeyPress(Key.Tab, modifier, PhysicalKey.None, null);
                    Dispatcher.UIThread.RunJobs();
                    AssertFocusInPanel(window, panel);
                }

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(owner.SettingsOpen);
            Assert.True(opener.IsFocused);
        }
        finally
        {
            await owner.DisposeAsync();
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(RawInputModifiers.Control)]
    [InlineData(RawInputModifiers.Meta)]
    public async Task WorkspaceFindShortcutCannotStealFocusFromSettings(RawInputModifiers modifier)
    {
        var path = Path.GetTempFileName();
        await using var owner = new MainWindowViewModel(
            new AppState(new LogSourceService(), new TestPersistence()),
            startPolling: false
        );
        await owner.InitializeAsync();
        await owner.OpenPathsCommand.Execute([path]).FirstAsync();
        var window = new MainWindow(owner, registerNativePicker: false);
        try
        {
            window.Show();
            owner.SettingsOpen = true;
            Dispatcher.UIThread.RunJobs();
            var panel = window.GetVisualDescendants().OfType<SettingsPanel>().Single();
            Assert.True(panel.FindControl<Button>("SettingsCloseButton")!.Focus());

            window.KeyPress(Key.F, modifier, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();

            AssertFocusInPanel(window, panel);
        }
        finally
        {
            await owner.DisposeAsync();
            window.Close();
            File.Delete(path);
        }
    }

    private static void AssertFocusInPanel(Window window, SettingsPanel panel)
    {
        var focused = Assert.IsAssignableFrom<Control>(window.FocusManager!.GetFocusedElement());
        Assert.True(focused == panel || focused.GetVisualAncestors().Contains(panel));
    }
}
