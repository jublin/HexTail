using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTailSharp.Application;
using HexTailSharp.Persistence;
using HexTailSharp.Tailing;
using HexTailSharp.ViewModels;
using HexTailSharp.Views;
using ReactiveUI;

namespace HexTailSharp;

public partial class MainWindow : Window
{
    public MainWindow()
        : this((string[]?)null) { }

    public MainWindow(string[]? startupPaths)
        : this(
            new MainWindowViewModel(
                new AppState(new TailerService(), new JsonFileAppPersistence()),
                startupPaths
            )
        ) { }

    internal MainWindow(MainWindowViewModel viewModel)
        : this(viewModel, registerNativePicker: true) { }

    internal MainWindow(MainWindowViewModel viewModel, bool registerNativePicker)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;
        if (registerNativePicker)
            ViewModel.PickFiles.RegisterHandler(context => _ = HandlePickFilesAsync(context));
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Opened += OnOpened;
        Closed += OnClosed;
        SizeChanged += (_, args) => UpdateResponsiveLayout(args.NewSize.Width);
        UpdateResponsiveLayout(Width);
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

    private async void OnOpened(object? sender, EventArgs e) => await InitializeOnUiThreadAsync();

    private async Task InitializeOnUiThreadAsync()
    {
        await ViewModel.InitializeAsync();
        if (Dispatcher.UIThread.CheckAccess())
            ApplyWindowState();
        else
            await Dispatcher.UIThread.InvokeAsync(ApplyWindowState);
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && ViewModel.SettingsOpen)
        {
            ViewModel.SettingsOpen = false;
            e.Handled = true;
            return;
        }

        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0)
            return;

        switch (e.Key)
        {
            case Key.O:
                Dispatcher.UIThread.Post(() => ViewModel.OpenCommand.Execute().Subscribe());
                break;
            case Key.F:
                this.GetVisualDescendants().OfType<SearchBar>().FirstOrDefault()?.FocusQuery();
                break;
            case Key.S:
                ViewModel.SaveCommand.Execute().Subscribe();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void UpdateResponsiveLayout(double width) =>
        SettingsSplitView.DisplayMode =
            width < 960 ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
}
