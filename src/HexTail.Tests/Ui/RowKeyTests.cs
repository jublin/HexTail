using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Domain;
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
    public async Task MoveToBookmarkJumpsBetweenBookmarkedVisibleRows()
    {
        var path = await WriteLinesAsync("a", "b", "c", "d", "e");
        try
        {
            var window = TestWindow.Create(out var owner);
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            var view = await LoadedView(owner, 5);
            owner.State.ToggleBookmark(file.Model, file.Model.Buffer[1]);
            owner.State.ToggleBookmark(file.Model, file.Model.Buffer[3]);
            view.Sync();

            view.MoveToBookmark(1);
            Assert.Equal(1, file.Model.SelectedLine);
            view.MoveToBookmark(1);
            Assert.Equal(3, file.Model.SelectedLine);
            view.MoveToBookmark(1);
            Assert.Equal(3, file.Model.SelectedLine);
            view.MoveToBookmark(-1);
            Assert.Equal(1, file.Model.SelectedLine);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task RowsFollowTheirBookmarkAfterASync()
    {
        var path = await WriteLinesAsync("a", "b");
        try
        {
            var window = TestWindow.Create(out var owner);
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            var view = await LoadedView(owner, 2);
            Assert.All(view.Lines, row => Assert.False(row.IsBookmarked));

            view.Lines[1].ToggleBookmark();
            view.Sync();

            Assert.False(view.Lines[0].IsBookmarked);
            Assert.True(view.Lines[1].IsBookmarked);
            Assert.Equal([1], file.Model.Bookmarks);

            view.Lines[1].ToggleBookmark();
            view.Sync();
            Assert.False(view.Lines[1].IsBookmarked);
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
            // From the first match: j, j reaches "c hit" (row 2) and k must step back up to "b".
            window.KeyPress(Key.J, RawInputModifiers.None, PhysicalKey.J, "j");
            window.KeyPress(Key.J, RawInputModifiers.None, PhysicalKey.J, "j");
            await UiWait.Until(() =>
                list.SelectedItem is LogLineViewModel { } row && row.Line.Raw == "c hit"
            );
            window.KeyPress(Key.K, RawInputModifiers.None, PhysicalKey.K, "k");
            await UiWait.Until(() =>
                list.SelectedItem is LogLineViewModel { } row && row.Line.Raw == "b"
            );
            Assert.Equal(1, file.Model.SelectedLine);

            var query = window
                .GetVisualDescendants()
                .OfType<FilterBar>()
                .Single()
                .FindControl<TextBox>("QueryBox")!;
            query.Focus();
            window.KeyTextInput("j");
            window.KeyPress(Key.J, RawInputModifiers.None, PhysicalKey.J, "j");
            window.KeyPress(Key.N, RawInputModifiers.None, PhysicalKey.N, "n");
            Assert.Equal(1, file.Model.SelectedLine);
            Assert.Contains("j", query.Text);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task MoveToMatchWithNoHighlightedRowsLeavesTheSelectionAlone()
    {
        var path = await WriteLinesAsync("a", "b", "c");
        try
        {
            var window = TestWindow.Create(out var owner);
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            var view = await LoadedView(owner, 3);

            view.MoveToMatch(1);
            view.MoveToMatch(-1);
            Assert.Null(file.Model.SelectedLine);

            view.MoveSelection(1);
            view.MoveSelection(1);
            view.MoveToMatch(1);
            Assert.Equal(1, file.Model.SelectedLine);
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
    public async Task MoveToMatchFromAHiddenSelectionStartsAtTheEdgeItHeadsAwayFrom()
    {
        var path = await WriteLinesAsync("hit a", "x", "hit b", "y");
        try
        {
            var window = TestWindow.Create(out var owner);
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            var view = await LoadedView(owner, 4);
            await owner.State.SetSourceRulesAsync(
                file.Model,
                [TestRules.Source("hit"), TestRules.Source("y", RuleAction.Hide)],
                TestContext.Current.CancellationToken
            );
            view.Sync();
            Assert.Equal(3, view.Lines.Count);

            // Line 3 ("y") is hidden, so there is no current row: N starts from the end ...
            file.Model.SelectedLine = 3;
            view.MoveToMatch(-1);
            Assert.Equal(2, file.Model.SelectedLine);

            // ... and n from the start.
            file.Model.SelectedLine = 3;
            view.MoveToMatch(1);
            Assert.Equal(0, file.Model.SelectedLine);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task MovingTheSelectionOffTheLastRowStopsFollowingEvenWhenLinesArrive()
    {
        var path = await WriteLinesAsync([.. Enumerable.Range(0, 20).Select(i => $"line {i}")]);
        try
        {
            var window = TestWindow.CreatePolling(out var owner);
            window.Show();
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            var view = await LoadedView(owner, 20);
            Dispatcher.UIThread.RunJobs();
            var list = window
                .GetVisualDescendants()
                .OfType<ListBox>()
                .First(l => l.Name == "LogList");
            Assert.True(view.IsFollowing);

            window.KeyPress(Key.J, RawInputModifiers.None, PhysicalKey.J, "j");
            await UiWait.Until(() =>
                list.SelectedItem is LogLineViewModel { } row && row.Line.Raw == "line 0"
            );
            Assert.False(view.IsFollowing);

            await File.AppendAllLinesAsync(
                path,
                ["new 1", "new 2", "new 3"],
                TestContext.Current.CancellationToken
            );
            await UiWait.Until(() =>
            {
                owner.State.DrainTailerEvents();
                view.Sync();
                return view.Lines.Count == 23;
            });

            // Scrolling cannot be asserted headlessly; the selection and the follow flag are the
            // observable halves of "the append did not snap the view back to the end".
            Assert.False(view.IsFollowing);
            Assert.Equal(0, file.Model.SelectedLine);
            Assert.True(
                list.SelectedItem is LogLineViewModel { } kept && kept.Line.Raw == "line 0"
            );
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task MovingTheSelectionOntoTheLastRowKeepsFollowing()
    {
        var path = await WriteLinesAsync("a", "b", "c");
        try
        {
            var window = TestWindow.Create(out var owner);
            window.Show();
            await owner.OpenPathsCommand.Execute([path]);
            var view = await LoadedView(owner, 3);
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.IsFollowing);

            view.MoveSelection(1);
            view.MoveSelection(1);
            Assert.False(view.IsFollowing);
            view.IsFollowing = true;
            view.MoveSelection(1);

            Assert.Equal(2, owner.SelectedFile!.Model.SelectedLine);
            Assert.True(view.IsFollowing);
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
