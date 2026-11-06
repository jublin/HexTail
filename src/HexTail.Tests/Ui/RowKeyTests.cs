using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Tests.Support;
using HexTail.ViewModels;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class RowKeyTests
{
    [AvaloniaFact]
    public async Task MoveSelectionWalksVisibleRowsAndStopsAtTheEnds()
    {
        var path = await WriteLinesAsync("a", "b", "c");
        try
        {
            var window = TestWindow.Create(out var owner);
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            var view = await LoadedView(owner, 3);
            Assert.Null(file.Model.SelectedLine);

            view.MoveSelection(1);
            Assert.Equal(0, file.Model.SelectedLine);
            view.MoveSelection(1);
            view.MoveSelection(1);
            view.MoveSelection(1);
            Assert.Equal(2, file.Model.SelectedLine);
            view.MoveSelection(-5);
            Assert.Equal(0, file.Model.SelectedLine);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task MoveToMatchJumpsBetweenRowsThatHaveHighlights()
    {
        var path = await WriteLinesAsync("x", "hit one", "y", "z", "hit two", "w");
        try
        {
            var window = TestWindow.Create(out var owner);
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            var view = await LoadedView(owner, 6);
            await owner.State.SetSourceRulesAsync(
                file.Model,
                [TestRules.Source("hit")],
                TestContext.Current.CancellationToken
            );
            view.Sync();

            view.MoveToMatch(1);
            Assert.Equal(1, file.Model.SelectedLine);
            view.MoveToMatch(1);
            Assert.Equal(4, file.Model.SelectedLine);
            view.MoveToMatch(1);
            Assert.Equal(4, file.Model.SelectedLine);
            view.MoveToMatch(-1);
            Assert.Equal(1, file.Model.SelectedLine);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task RowKeysMoveTheSelectedItemButNotWhileTyping()
    {
        var path = await WriteLinesAsync("a hit", "b", "c hit", "d");
        try
        {
            var window = TestWindow.Create(out var owner);
            window.Show();
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            var view = await LoadedView(owner, 4);
            await owner.State.SetSourceRulesAsync(
                file.Model,
                [TestRules.Source("hit")],
                TestContext.Current.CancellationToken
            );
            view.Sync();
            Dispatcher.UIThread.RunJobs();
            var list = window
                .GetVisualDescendants()
                .OfType<ListBox>()
                .First(l => l.Name == "LogList");

            window.KeyPress(Key.J, RawInputModifiers.None, PhysicalKey.J, "j");
            await UiWait.Until(() =>
                list.SelectedItem is LogLineViewModel { } row && row.Line.Raw == "a hit"
            );
            window.KeyPress(Key.J, RawInputModifiers.None, PhysicalKey.J, "j");
            await UiWait.Until(() =>
                list.SelectedItem is LogLineViewModel { } row && row.Line.Raw == "b"
            );
            window.KeyPress(Key.N, RawInputModifiers.None, PhysicalKey.N, "n");
            await UiWait.Until(() =>
                list.SelectedItem is LogLineViewModel { } row && row.Line.Raw == "c hit"
            );
            window.KeyPress(Key.N, RawInputModifiers.Shift, PhysicalKey.N, "N");
            await UiWait.Until(() =>
                list.SelectedItem is LogLineViewModel { } row && row.Line.Raw == "a hit"
            );
            window.KeyPress(Key.K, RawInputModifiers.None, PhysicalKey.K, "k");
            Assert.Equal(0, file.Model.SelectedLine);

            var query = window
                .GetVisualDescendants()
                .OfType<FilterBar>()
                .Single()
                .FindControl<TextBox>("QueryBox")!;
            query.Focus();
            window.KeyTextInput("j");
            window.KeyPress(Key.J, RawInputModifiers.None, PhysicalKey.J, "j");
            window.KeyPress(Key.N, RawInputModifiers.None, PhysicalKey.N, "n");
            Assert.Equal(0, file.Model.SelectedLine);
            Assert.Contains("j", query.Text);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task RowKeysAreIgnoredWhileSettingsAreOpen()
    {
        var path = await WriteLinesAsync("a", "b");
        try
        {
            var window = TestWindow.Create(out var owner);
            window.Show();
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            await LoadedView(owner, 2);
            owner.SettingsOpen = true;
            Dispatcher.UIThread.RunJobs();

            window.KeyPress(Key.J, RawInputModifiers.None, PhysicalKey.J, "j");
            Assert.Null(file.Model.SelectedLine);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<string> WriteLinesAsync(params string[] lines)
    {
        var path = Path.GetTempFileName();
        await File.WriteAllLinesAsync(path, lines, TestContext.Current.CancellationToken);
        return path;
    }

    private static async Task<LogViewViewModel> LoadedView(MainWindowViewModel owner, int lines)
    {
        await UiWait.Until(() =>
        {
            owner.State.DrainTailerEvents();
            return owner.SelectedFile?.SelectedView.Lines.Count == lines;
        });
        return owner.SelectedFile!.SelectedView;
    }
}
