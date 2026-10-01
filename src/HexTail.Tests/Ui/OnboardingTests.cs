using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
}
