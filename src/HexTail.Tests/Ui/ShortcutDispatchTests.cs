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

public sealed class ShortcutDispatchTests
{
    [AvaloniaFact]
    public async Task SpaceAndFToggleViewStateButNotWhileTyping()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await viewModel.OpenPathsCommand.Execute([path]);
            Dispatcher.UIThread.RunJobs();
            var view = viewModel.SelectedFile!.SelectedView;

            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Assert.True(viewModel.IsPaused);
            window.KeyPress(Key.F, RawInputModifiers.None, PhysicalKey.F, "f");
            Assert.False(view.IsFollowing);

            var query = window
                .GetVisualDescendants()
                .OfType<SearchBar>()
                .Single()
                .FindControl<TextBox>("QueryBox")!;
            query.Focus();
            window.KeyPress(Key.F, RawInputModifiers.None, PhysicalKey.F, "f");
            Assert.False(view.IsFollowing);
            Assert.True(viewModel.IsPaused);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ControlWClosesSearchViewThenSource()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await viewModel.OpenPathsCommand.Execute([path]);
            viewModel.Query = "error";
            await viewModel.AddSearchCommand.Execute();
            var file = viewModel.SelectedFile!;
            file.SelectedViewIndex = 1;
            Dispatcher.UIThread.RunJobs();

            window.KeyPress(Key.W, RawInputModifiers.Control, PhysicalKey.W, null);
            await UiWait.Until(() => file.Model.Searches.Count == 0);
            window.KeyPress(Key.W, RawInputModifiers.Control, PhysicalKey.W, null);
            await UiWait.Until(() => viewModel.Files.Count == 0);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ControlDigitAndControlTabMoveBetweenViews()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await viewModel.OpenPathsCommand.Execute([path]);
            viewModel.Query = "a";
            await viewModel.AddSearchCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            var file = viewModel.SelectedFile!;

            window.KeyPress(Key.D2, RawInputModifiers.Control, PhysicalKey.Digit2, null);
            Assert.Equal(1, file.SelectedViewIndex);
            window.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
            Assert.Equal(0, file.SelectedViewIndex);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task SpaceOnFocusedPauseToggleTogglesOnce()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Show();
            await viewModel.OpenPathsCommand.Execute([path]);
            Dispatcher.UIThread.RunJobs();

            var changes = 0;
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(viewModel.IsPaused))
                    changes++;
            };
            window.FindControl<ToggleButton>("PauseToggle")!.Focus();
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Dispatcher.UIThread.RunJobs();
            Assert.True(viewModel.IsPaused);
            Assert.Equal(1, changes);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task SpaceOnFocusedLogRowStillPauses()
    {
        var path = Path.GetTempFileName();
        var window = TestWindow.Create(out var viewModel);
        try
        {
            await viewModel.OpenPathsCommand.Execute([path]);
            var file = viewModel.SelectedFile!;
            file.Model.Buffer.Append([new Line("one"), new Line("two")]);
            file.SyncViews();
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var list = window
                .GetVisualDescendants()
                .OfType<LogView>()
                .Single()
                .FindControl<ListBox>("LogList")!;
            list.SelectedIndex = 1;
            Assert.IsType<ListBoxItem>(list.ContainerFromIndex(1)).Focus();

            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Dispatcher.UIThread.RunJobs();

            Assert.True(viewModel.IsPaused);
        }
        finally
        {
            window.Close();
            File.Delete(path);
        }
    }
}
