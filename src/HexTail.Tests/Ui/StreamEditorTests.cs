using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HexTail.Application;
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
            Assert.Equal(
                new[] { $"file:{paths[0]}", $"file:{paths[2]}", $"file:{paths[1]}" }.Order(),
                viewModel.State.Streams[0].Members.Select(member => member.Key).Order()
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
}
