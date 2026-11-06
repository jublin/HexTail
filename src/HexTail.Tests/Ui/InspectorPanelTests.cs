using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Domain;
using HexTail.Tests.Support;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class InspectorPanelTests
{
    [AvaloniaFact]
    public async Task CtrlITogglesThePanelAndItShowsTheSelectedRow()
    {
        var path = Path.GetTempFileName();
        var window = TestWindow.CreatePolling(out var owner);
        try
        {
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            var line = new Line("k=v", new Dictionary<string, string> { ["k"] = "v" });
            file.Model.Buffer.Append([line]);
            file.SyncViews();
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window
                .GetVisualDescendants()
                .OfType<LogView>()
                .Single()
                .FindControl<ListBox>("LogList")!
                .SelectedIndex = 0;
            Assert.DoesNotContain(
                window.GetVisualDescendants().OfType<InspectorPanel>(),
                panel => panel.IsEffectivelyVisible
            );

            window.KeyPress(Key.I, RawInputModifiers.Control, PhysicalKey.I, "i");
            await UiWait.Until(() => owner.Inspector.IsOpen);
            Dispatcher.UIThread.RunJobs();

            var panel = window.GetVisualDescendants().OfType<InspectorPanel>().Single();
            Assert.True(panel.IsEffectivelyVisible);
            await UiWait.Until(() => owner.Inspector.HasLine);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("k=v", panel.FindControl<SelectableTextBlock>("InspectorRaw")!.Text);

            window.KeyPress(Key.I, RawInputModifiers.Control, PhysicalKey.I, "i");
            await UiWait.Until(() => !owner.Inspector.IsOpen);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }
}
