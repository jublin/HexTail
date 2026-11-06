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
            new AppState(
                new LogSourceService(),
                new TestPersistence(),
                credentials: new InMemoryCredentialVault()
            ),
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
            // Focus moves into the panel on a dispatcher post, so wait for it rather than racing it.
            await UiWait.Until(() => FocusInPanel(window, panel));
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
            await UiWait.Until(() => opener.IsFocused);
        }
        finally
        {
            await owner.DisposeAsync();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task SettingsFocusReturnsWhenTheCloseButtonIsUsableAgain()
    {
        await using var owner = new MainWindowViewModel(
            new AppState(
                new LogSourceService(),
                new TestPersistence(),
                credentials: new InMemoryCredentialVault()
            ),
            startPolling: false
        );
        await owner.InitializeAsync();
        var window = new MainWindow(owner, registerNativePicker: false);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await owner.ToggleSettingsCommand.Execute().FirstAsync();
            Dispatcher.UIThread.RunJobs();
            var panel = window.GetVisualDescendants().OfType<SettingsPanel>().Single();
            await UiWait.Until(() => FocusInPanel(window, panel));

            // The command bound to the close button disables it while it finishes, which drops focus.
            var close = panel.FindControl<Button>("SettingsCloseButton")!;
            close.IsEnabled = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(FocusInPanel(window, panel));
            close.IsEnabled = true;
            await UiWait.Until(() => FocusInPanel(window, panel));

            // Focus pulled out by anything else (the dialog host grabbing it as it opens) comes back.
            Assert.True(window.FindControl<Control>("SettingsButton")!.Focus());
            await UiWait.Until(() => FocusInPanel(window, panel));
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
            new AppState(
                new LogSourceService(),
                new TestPersistence(),
                credentials: new InMemoryCredentialVault()
            ),
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

    private static bool FocusInPanel(Window window, SettingsPanel panel) =>
        window.FocusManager!.GetFocusedElement() is Control focused
        && (focused == panel || focused.GetVisualAncestors().Contains(panel));

    private static void AssertFocusInPanel(Window window, SettingsPanel panel)
    {
        var focused = Assert.IsAssignableFrom<Control>(window.FocusManager!.GetFocusedElement());
        Assert.True(focused == panel || focused.GetVisualAncestors().Contains(panel));
    }
}
