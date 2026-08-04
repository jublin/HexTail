using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using HexTailSharp.Application;
using HexTailSharp.Persistence;
using HexTailSharp.Tailing;
using HexTailSharp.ViewModels;
using ReactiveUI;

namespace HexTailSharp;

public partial class MainWindow : AtomUI.Desktop.Controls.Window
{
    public MainWindow()
        : this(null) { }

    public MainWindow(string[]? startupPaths = null)
    {
        InitializeComponent();

        var state = new AppState(new TailerService(), new JsonFileAppPersistence());
        ViewModel = new MainWindowViewModel(state, startupPaths);
        DataContext = ViewModel;
        ViewModel.PickFiles.RegisterHandler(context => _ = HandlePickFilesAsync(context));

        Opened += OnOpened;
        Closed += OnClosed;
    }

    internal MainWindowViewModel ViewModel { get; }

    private async Task HandlePickFilesAsync(
        IInteractionContext<System.Reactive.Unit, IReadOnlyList<string>> context
    )
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions { AllowMultiple = true, Title = "Open log files" }
            );
            var paths = new List<string>();
            foreach (var file in files)
            {
                try
                {
                    if (file.Path.IsFile)
                        paths.Add(file.Path.LocalPath);
                }
                finally
                {
                    file.Dispose();
                }
            }
            context.SetOutput(paths);
        }
        catch (Exception ex)
        {
            ViewModel.SetFileError($"Could not pick files: {ex.Message}");
            context.SetOutput([]);
        }
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        await ViewModel.InitializeAsync();
        ApplyWindowState();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        ViewModel.State.SetWindowState(
            new AppWindowState
            {
                Width = Width,
                Height = Height,
                X = Position.X,
                Y = Position.Y,
                ContextPaneSize = ViewModel.State.Window.ContextPaneSize,
                VerticalFileTabs = ViewModel.State.Window.VerticalFileTabs,
            }
        );
        await ViewModel.DisposeAsync();
    }

    private void ApplyWindowState()
    {
        if (ViewModel.State.Window.Width > 0)
            Width = ViewModel.State.Window.Width;
        if (ViewModel.State.Window.Height > 0)
            Height = ViewModel.State.Window.Height;
        if (ViewModel.State.Window.X is int x && ViewModel.State.Window.Y is int y)
            Position = new PixelPoint(x, y);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Formats.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is not { } files)
            return;

        var paths = new List<string>();
        foreach (var file in files)
        {
            try
            {
                if (file.Path.IsFile)
                    paths.Add(file.Path.LocalPath);
            }
            finally
            {
                file.Dispose();
            }
        }

        ViewModel.OpenPathsCommand.Execute(paths).Subscribe();
    }

    private void QueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        ViewModel.AddSearchCommand.Execute().Subscribe();
        e.Handled = true;
    }
}
