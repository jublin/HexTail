using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HexTail.Application;
using HexTail.Persistence;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.Ui;

public sealed class StreamEditorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string[]> OpenFilesAsync(
        MainWindowViewModel viewModel,
        int count,
        string directory
    )
    {
        var paths = Enumerable
            .Range(0, count)
            .Select(index =>
            {
                var path = Path.Combine(directory, $"source-{index}.log");
                File.WriteAllLines(path, [$"2026-01-01 10:00:0{index} INFO line {index}"]);
                return path;
            })
            .ToArray();
        await viewModel.OpenPathsCommand.Execute(paths);
        Dispatcher.UIThread.RunJobs();
        return paths;
    }

    [AvaloniaFact]
    public async Task NewStreamEditorPrefillsTheNameAndOffersEveryOpenSourceUnchecked()
    {
        var directory = Directory.CreateTempSubdirectory("hextail-editor-").FullName;
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await OpenFilesAsync(viewModel, 3, directory);

            await viewModel.NewStreamCommand.Execute();

            var editor = Assert.IsType<StreamEditorViewModel>(viewModel.StreamEditor);
            Assert.True(viewModel.IsStreamEditorOpen);
            Assert.False(editor.IsEditMode);
            Assert.Equal("Stream 1", editor.Name);
            Assert.Equal(
                ["source-0.log", "source-1.log", "source-2.log"],
                editor.Choices.Select(choice => choice.DisplayName)
            );
            Assert.All(editor.Choices, choice => Assert.False(choice.IsChecked));
            Assert.False(editor.CanSave);
            Assert.False(editor.DeleteCommand.CanExecute.FirstAsync().GetAwaiter().GetResult());
            window.Close();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task NewStreamNameSkipsNamesThatAreTaken()
    {
        var directory = Directory.CreateTempSubdirectory("hextail-editor-").FullName;
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await OpenFilesAsync(viewModel, 2, directory);
            await viewModel.State.CreateStreamAsync(
                "Stream 2",
                viewModel.State.Files.ToArray(),
                Ct
            );

            await viewModel.NewStreamCommand.Execute();

            Assert.Equal("Stream 3", viewModel.StreamEditor!.Name);
            window.Close();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task SaveNeedsANameAndAtLeastTwoCheckedSources()
    {
        var directory = Directory.CreateTempSubdirectory("hextail-editor-").FullName;
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await OpenFilesAsync(viewModel, 3, directory);
            await viewModel.NewStreamCommand.Execute();
            var editor = viewModel.StreamEditor!;

            Assert.False(await editor.SaveCommand.CanExecute.FirstAsync());
            editor.Choices[0].IsChecked = true;
            Assert.False(editor.CanSave);
            Assert.False(await editor.SaveCommand.CanExecute.FirstAsync());
            editor.Choices[1].IsChecked = true;
            Assert.True(editor.CanSave);
            Assert.True(await editor.SaveCommand.CanExecute.FirstAsync());
            editor.Name = "   ";
            Assert.False(editor.CanSave);
            Assert.False(await editor.SaveCommand.CanExecute.FirstAsync());
            editor.Name = "Pair";
            Assert.True(await editor.SaveCommand.CanExecute.FirstAsync());
            editor.Choices[1].IsChecked = false;
            Assert.False(await editor.SaveCommand.CanExecute.FirstAsync());
            window.Close();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task SavingANewStreamCreatesOpensAndSelectsIt()
    {
        var directory = Directory.CreateTempSubdirectory("hextail-editor-").FullName;
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            var paths = await OpenFilesAsync(viewModel, 3, directory);
            await viewModel.NewStreamCommand.Execute();
            var editor = viewModel.StreamEditor!;
            editor.Name = "  Pair  ";
            editor.Choices[0].IsChecked = true;
            editor.Choices[2].IsChecked = true;

            await editor.SaveCommand.Execute();

            await UiWait.Until(() =>
                viewModel.SelectedFile?.Model.Source.Kind == LogSourceKind.Stream
            );
            Assert.Null(viewModel.StreamEditor);
            Assert.False(viewModel.IsStreamEditorOpen);
            var saved = Assert.Single(viewModel.State.Streams);
            Assert.Equal("Pair", saved.Name);
            Assert.Equal(
                [$"file:{paths[0]}", $"file:{paths[2]}"],
                saved.Members.Select(member => member.Key)
            );
            Assert.Equal("Pair", viewModel.SelectedFile!.DisplayName);
            window.Close();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task EditModePreChecksMembersAndSavingUpdatesTheStream()
    {
        var directory = Directory.CreateTempSubdirectory("hextail-editor-").FullName;
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            var paths = await OpenFilesAsync(viewModel, 3, directory);
            var files = viewModel.State.Files.ToArray();
            var made = await viewModel.State.CreateStreamAsync("Old", [files[0], files[1]], Ct);

            await viewModel.EditStreamCommand.Execute(made.Id);

            var editor = viewModel.StreamEditor!;
            Assert.True(editor.IsEditMode);
            Assert.Equal("Old", editor.Name);
            Assert.Equal([true, true, false], editor.Choices.Select(choice => choice.IsChecked));
            Assert.True(await editor.DeleteCommand.CanExecute.FirstAsync());

            editor.Name = "New";
            editor.Choices[1].IsChecked = false;
            editor.Choices[2].IsChecked = true;
            await editor.SaveCommand.Execute();

            await UiWait.Until(() => viewModel.State.Streams[0].Name == "New");
            Assert.Equal(
                [$"file:{paths[0]}", $"file:{paths[2]}"],
                viewModel.State.Streams[0].Members.Select(member => member.Key)
            );
            Assert.Equal(made.Id, viewModel.State.Streams[0].Id);
            Assert.Null(viewModel.StreamEditor);
            window.Close();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task MissingMembersStayCheckedAreLabelledAndCanBeRemoved()
    {
        var directory = Directory.CreateTempSubdirectory("hextail-editor-").FullName;
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            var paths = await OpenFilesAsync(viewModel, 3, directory);
            var files = viewModel.State.Files.ToArray();
            var made = await viewModel.State.CreateStreamAsync(
                "Trio",
                [files[0], files[1], files[2]],
                Ct
            );
            await viewModel.CloseFileCommand.Execute(viewModel.FileTabs[1]);
            await UiWait.Until(() => viewModel.FileTabs.Count == 2);

            await viewModel.EditStreamCommand.Execute(made.Id);

            var editor = viewModel.StreamEditor!;
            var missing = Assert.Single(editor.Choices, choice => choice.IsMissing);
            Assert.True(missing.IsChecked);
            Assert.Equal("source-1.log (missing)", missing.Label);
            Assert.Equal(3, editor.Choices.Count);
            Assert.True(editor.CanSave);

            // Saving untouched keeps the missing member in the definition.
            await editor.SaveCommand.Execute();
            await UiWait.Until(() => viewModel.StreamEditor is null);
            // The definition keeps its saved order and colours (the missing member stays in the middle).
            Assert.Equal(
                [$"file:{paths[0]}", $"file:{paths[1]}", $"file:{paths[2]}"],
                viewModel.State.Streams[0].Members.Select(member => member.Key)
            );
            Assert.Equal(
                made.Members.Select(member => member.Color),
                viewModel.State.Streams[0].Members.Select(member => member.Color)
            );

            // Unchecking it removes it.
            await viewModel.EditStreamCommand.Execute(made.Id);
            viewModel.StreamEditor!.Choices.Single(choice => choice.IsMissing).IsChecked = false;
            await viewModel.StreamEditor.SaveCommand.Execute();
            await UiWait.Until(() => viewModel.StreamEditor is null);
            Assert.Equal(
                [$"file:{paths[0]}", $"file:{paths[2]}"],
                viewModel.State.Streams[0].Members.Select(member => member.Key)
            );
            window.Close();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task DeleteAsksInlineFirstAndThenRemovesTheStream()
    {
        var directory = Directory.CreateTempSubdirectory("hextail-editor-").FullName;
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await OpenFilesAsync(viewModel, 2, directory);
            var made = await viewModel.State.CreateStreamAsync(
                "Doomed",
                viewModel.State.Files.ToArray(),
                Ct
            );
            await viewModel.State.OpenStreamAsync(made.Id, Ct);
            await viewModel.EditStreamCommand.Execute(made.Id);
            var editor = viewModel.StreamEditor!;

            Assert.False(editor.IsConfirmingDelete);
            await editor.DeleteCommand.Execute();
            Assert.True(editor.IsConfirmingDelete);
            Assert.Single(viewModel.State.Streams);

            await editor.CancelDeleteCommand.Execute();
            Assert.False(editor.IsConfirmingDelete);
            Assert.Single(viewModel.State.Streams);

            await editor.DeleteCommand.Execute();
            await editor.ConfirmDeleteCommand.Execute();

            await UiWait.Until(() => viewModel.State.Streams.Count == 0);
            Assert.Null(viewModel.StreamEditor);
            Assert.DoesNotContain(
                viewModel.State.Files,
                tab => tab.Source.Kind == LogSourceKind.Stream
            );
            window.Close();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task ASaveFailureLandsOnTheEditorsErrorLineAndKeepsItOpen()
    {
        var directory = Directory.CreateTempSubdirectory("hextail-editor-").FullName;
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await OpenFilesAsync(viewModel, 2, directory);
            var made = await viewModel.State.CreateStreamAsync(
                "Gone",
                viewModel.State.Files.ToArray(),
                Ct
            );
            await viewModel.EditStreamCommand.Execute(made.Id);
            var editor = viewModel.StreamEditor!;
            await viewModel.State.DeleteStreamAsync(made.Id, Ct);

            await editor.SaveCommand.Execute();

            Assert.Contains("does not exist", editor.Error);
            Assert.Same(editor, viewModel.StreamEditor);
            window.Close();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task CancelClosesTheEditorWithoutChangingAnything()
    {
        var directory = Directory.CreateTempSubdirectory("hextail-editor-").FullName;
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await OpenFilesAsync(viewModel, 2, directory);
            await viewModel.NewStreamCommand.Execute();

            await viewModel.StreamEditor!.CancelCommand.Execute();

            Assert.Null(viewModel.StreamEditor);
            Assert.Empty(viewModel.State.Streams);
            window.Close();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task SavingAfterAMemberWasClosedMarksItClosedAndExplainsInsteadOfFailingOpaquely()
    {
        var directory = Directory.CreateTempSubdirectory("hextail-editor-").FullName;
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await OpenFilesAsync(viewModel, 3, directory);
            await viewModel.NewStreamCommand.Execute();
            var editor = viewModel.StreamEditor!;
            editor.Choices[0].IsChecked = true;
            editor.Choices[1].IsChecked = true;
            await viewModel.State.CloseFileAsync(viewModel.State.Files[1], Ct);

            await editor.SaveCommand.Execute();

            var closed = editor.Choices[1];
            Assert.Equal("source-1.log (closed)", closed.Label);
            Assert.Equal("source-1.log was closed. Reopen it or untick it.", editor.Error);
            Assert.Same(editor, viewModel.StreamEditor);
            Assert.Empty(viewModel.State.Streams);

            // Unticking the closed one (and ticking another) lets the save through.
            closed.IsChecked = false;
            editor.Choices[2].IsChecked = true;
            await editor.SaveCommand.Execute();
            await UiWait.Until(() => viewModel.State.Streams.Count == 1);
            Assert.Null(editor.Error);
            window.Close();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task SourcesOpenedWhileTheEditorIsOpenAppearUncheckedAndCheckedStateIsKept()
    {
        var directory = Directory.CreateTempSubdirectory("hextail-editor-").FullName;
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await OpenFilesAsync(viewModel, 2, directory);
            await viewModel.NewStreamCommand.Execute();
            var editor = viewModel.StreamEditor!;
            editor.Choices[0].IsChecked = true;
            var extra = Path.Combine(directory, "extra.log");
            File.WriteAllLines(extra, ["2026-01-01 10:00:09 INFO extra"]);

            await viewModel.OpenPathsCommand.Execute([extra]);
            Dispatcher.UIThread.RunJobs();

            await UiWait.Until(() => editor.Choices.Count == 3);
            Assert.Equal("extra.log", editor.Choices[2].DisplayName);
            Assert.Equal([true, false, false], editor.Choices.Select(choice => choice.IsChecked));
            Assert.Same(viewModel.State.Files[0], editor.Choices[0].Tab);
            window.Close();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task OpeningTheEditorForTheSameStreamFocusesItAndADifferentOneReplacesIt()
    {
        var directory = Directory.CreateTempSubdirectory("hextail-editor-").FullName;
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await OpenFilesAsync(viewModel, 3, directory);
            var files = viewModel.State.Files.ToArray();
            var alpha = await viewModel.State.CreateStreamAsync("Alpha", [files[0], files[1]], Ct);
            var beta = await viewModel.State.CreateStreamAsync("Beta", [files[1], files[2]], Ct);

            await viewModel.NewStreamCommand.Execute();
            var fresh = viewModel.StreamEditor!;
            fresh.Name = "Unsaved";
            var focusRequests = 0;
            fresh.FocusRequested += () => focusRequests++;
            await viewModel.NewStreamCommand.Execute();
            Assert.Same(fresh, viewModel.StreamEditor);
            Assert.Equal("Unsaved", fresh.Name);
            Assert.Equal(1, focusRequests);

            await viewModel.EditStreamCommand.Execute(alpha.Id);
            var editingAlpha = viewModel.StreamEditor!;
            Assert.NotSame(fresh, editingAlpha);
            Assert.Equal(alpha.Id, editingAlpha.StreamId);
            editingAlpha.Name = "Changed";
            focusRequests = 0;
            editingAlpha.FocusRequested += () => focusRequests++;
            await viewModel.EditStreamCommand.Execute(alpha.Id);
            Assert.Same(editingAlpha, viewModel.StreamEditor);
            Assert.Equal("Changed", editingAlpha.Name);
            Assert.Equal(1, focusRequests);

            await viewModel.EditStreamCommand.Execute(beta.Id);
            Assert.Equal(beta.Id, viewModel.StreamEditor!.StreamId);
            Assert.Equal("Beta", viewModel.StreamEditor.Name);
            window.Close();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task ADeleteFailureLandsOnTheEditorsErrorLineAndHidesTheConfirmation()
    {
        var directory = Directory.CreateTempSubdirectory("hextail-editor-").FullName;
        try
        {
            var persistence = new TestPersistence();
            var window = TestWindow.Create(persistence, out var viewModel);
            window.Show();
            await OpenFilesAsync(viewModel, 2, directory);
            var made = await viewModel.State.CreateStreamAsync(
                "Doomed",
                viewModel.State.Files.ToArray(),
                Ct
            );
            await viewModel.EditStreamCommand.Execute(made.Id);
            var editor = viewModel.StreamEditor!;
            await editor.DeleteCommand.Execute();
            persistence.SaveError = new IOException("disk full");

            await editor.ConfirmDeleteCommand.Execute();

            Assert.Equal("disk full", editor.Error);
            Assert.False(editor.IsConfirmingDelete);
            Assert.Same(editor, viewModel.StreamEditor);
            Assert.Single(viewModel.State.Streams);
            persistence.SaveError = null;
            window.Close();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
