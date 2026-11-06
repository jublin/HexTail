using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

    [AvaloniaFact]
    public async Task AFieldHideCommandAddsARuleAndHidesTheRow()
    {
        var path = Path.GetTempFileName();
        var window = TestWindow.CreatePolling(out var owner);
        try
        {
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            var noisy = new Line(
                "level=debug msg=tick",
                new Dictionary<string, string> { ["level"] = "debug", ["msg"] = "tick" }
            );
            var kept = new Line(
                "level=info msg=ok",
                new Dictionary<string, string> { ["level"] = "info", ["msg"] = "ok" }
            );
            file.Model.Buffer.Append([noisy, kept]);
            file.SyncViews();
            window.Show();
            Dispatcher.UIThread.RunJobs();
            owner.Inspector.Open();
            window
                .GetVisualDescendants()
                .OfType<LogView>()
                .Single()
                .FindControl<ListBox>("LogList")!
                .SelectedIndex = 0;
            await UiWait.Until(() => owner.Inspector.Fields.Count == 2);
            Dispatcher.UIThread.RunJobs();

            var panel = window.GetVisualDescendants().OfType<InspectorPanel>().Single();
            Assert.Equal(2, panel.FindControl<ItemsControl>("InspectorFields")!.ItemCount);

            await owner.Inspector.Fields[1].HideCommand.Execute();
            file.SyncViews();

            var rule = Assert.Single(file.Model.Rules);
            Assert.Equal(RuleAction.Hide, rule.Action);
            Assert.Equal("tick", rule.Pattern);
            await UiWait.Until(() => file.SelectedView.Lines.Count == 1);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task TheContextToggleListsNeighboursAndClickingOneSelectsIt()
    {
        var path = Path.GetTempFileName();
        var window = TestWindow.CreatePolling(out var owner);
        try
        {
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            file.Model.Buffer.Append(Enumerable.Range(0, 8).Select(i => new Line($"row {i}")));
            file.SyncViews();
            window.Show();
            Dispatcher.UIThread.RunJobs();
            owner.Inspector.Open();
            window
                .GetVisualDescendants()
                .OfType<LogView>()
                .Single()
                .FindControl<ListBox>("LogList")!
                .SelectedIndex = 4;
            await UiWait.Until(() => owner.Inspector.HasLine);
            var panel = window.GetVisualDescendants().OfType<InspectorPanel>().Single();
            Assert.Empty(owner.Inspector.ContextRows);

            panel.FindControl<ToggleButton>("InspectorContextToggle")!.IsChecked = true;
            await UiWait.Until(() => owner.Inspector.ContextRows.Count == 7);
            Dispatcher.UIThread.RunJobs();

            var buttons = panel
                .FindControl<ItemsControl>("InspectorContext")!
                .GetVisualDescendants()
                .OfType<Button>()
                .ToList();
            // Row 4 with 3 lines above and the file ending at row 7: rows 1 to 7.
            Assert.Equal(7, buttons.Count);
            buttons[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            await UiWait.Until(() => file.Model.SelectedLine == 1);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }
}
