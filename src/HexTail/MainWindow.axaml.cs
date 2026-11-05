using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DialogHostAvalonia;
using HexTail.Application;
using HexTail.Elastic;
using HexTail.Input;
using HexTail.Persistence;
using HexTail.Security;
using HexTail.Tailing;
using HexTail.ViewModels;
using HexTail.Views;
using ReactiveUI;

namespace HexTail;

public partial class MainWindow : Window
{
    private WindowNotificationManager _notificationManager;
    private Control? _settingsOpener;

    public MainWindow()
        : this((string[]?)null) { }

    public MainWindow(string[]? startupPaths)
        : this(
            new MainWindowViewModel(
                new AppState(
                    new LogSourceService(),
                    new JsonFileAppPersistence(),
                    credentials: new OsCredentialVault(),
                    elastic: new ElasticApiClient(new HttpClient())
                ),
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
        this.FindControl<DialogHost>("SettingsDialogHost")!.PropertyChanged += (_, args) =>
        {
            if (args.Property == DialogHost.IsOpenProperty)
                UpdateSettingsFocus();
        };
        _notificationManager = new WindowNotificationManager(this)
        {
            Position = NotificationPosition.BottomRight,
            MaxItems = 3,
        };
        if (registerNativePicker)
            ViewModel.PickFiles.RegisterHandler(context => _ = HandlePickFilesAsync(context));
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Opened += OnOpened;
        Closed += OnClosed;
        SizeChanged += (_, args) => ViewModel.SetShellWidth(args.NewSize.Width);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        ViewModel.FocusSearchRequested += (_, _) =>
            Dispatcher.UIThread.Post(() =>
                this.GetVisualDescendants().OfType<SearchBar>().FirstOrDefault()?.FocusQuery()
            );
    }

    internal MainWindowViewModel ViewModel { get; }

    private void UpdateSettingsFocus()
    {
        if (
            ViewModel.SettingsOpen
            && FocusManager?.GetFocusedElement() is Control focused
            && !focused.GetVisualAncestors().OfType<SettingsPanel>().Any()
        )
            _settingsOpener = focused;
        Dispatcher.UIThread.Post(() =>
        {
            if (ViewModel.SettingsOpen)
                this.GetVisualDescendants()
                    .OfType<SettingsPanel>()
                    .FirstOrDefault()
                    ?.FindControl<Button>("SettingsCloseButton")
                    ?.Focus();
            else if (_settingsOpener is { IsEffectivelyVisible: true } opener)
            {
                opener.Focus();
                _settingsOpener = null;
            }
        });
    }

    private void OnSettingsDialogClosing(object? sender, DialogClosingEventArgs args)
    {
        if (!ViewModel.SettingsOpen)
            return;
        ViewModel.SettingsOpen = false;
        if (ViewModel.SettingsOpen && args.CanBeCancelled)
            args.Cancel();
    }

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
        if (ClientSize.Width > 0)
            ViewModel.SetShellWidth(ClientSize.Width);
        await InitializeOnUiThreadAsync();
        if (ViewModel.SettingsOpen)
            UpdateSettingsFocus();
    }

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
                SidebarCollapsed = !ViewModel.SidebarOpen,
                SidebarWidth = ViewModel.SidebarWidth,
            }
        );
        await ViewModel.DisposeAsync();
    }

    private void OnSidebarResize(object? sender, VectorEventArgs e) =>
        ViewModel.SidebarWidth += e.Vector.X;

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

    // Tab is consumed by focus navigation, and a focused log row consumes Space for selection,
    // before the bubbling OnKeyDown runs, so those two get a tunnel handler.
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel.SettingsOpen || ViewModel.Palette.IsOpen || e.Handled)
            return;
        var onLogRow =
            e.Key == Key.Space
            && e.Source is Visual source
            && source.FindAncestorOfType<ListBox>(includeSelf: true)?.Name
                is "LogList"
                    or "ContextList";
        if (e.Key != Key.Tab && !onLogRow)
            return;
        var command = ShellShortcuts.Resolve(e.Key, e.KeyModifiers, textInputFocused: false);
        if (
            onLogRow
                ? command.Action != ShellAction.TogglePause
                : command.Action is not (ShellAction.NextView or ShellAction.PreviousView)
        )
            return;
        Execute(command);
        e.Handled = true;
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
        if (ViewModel.SettingsOpen || ViewModel.Palette.IsOpen || e.Handled)
            return;

        var command = ShellShortcuts.Resolve(
            e.Key,
            e.KeyModifiers,
            FocusManager?.GetFocusedElement() is TextBox
        );
        if (command.Action == ShellAction.None)
            return;
        Execute(command);
        e.Handled = true;
    }

    private void Execute(ShellCommand command)
    {
        var file = ViewModel.SelectedFile;
        switch (command.Action)
        {
            case ShellAction.CommandPalette:
                ViewModel.OpenPaletteCommand.Execute().Subscribe();
                break;
            case ShellAction.OpenFiles:
                Dispatcher.UIThread.Post(() => ViewModel.OpenCommand.Execute().Subscribe());
                break;
            case ShellAction.FocusSearch:
                this.GetVisualDescendants().OfType<SearchBar>().FirstOrDefault()?.FocusQuery();
                break;
            case ShellAction.SaveSession:
                ViewModel.SaveCommand.Execute().Subscribe();
                break;
            case ShellAction.ToggleSidebar:
                ViewModel.ToggleSidebarCommand.Execute().Subscribe();
                break;
            case ShellAction.TogglePause:
                ViewModel.TogglePauseCommand.Execute().Subscribe();
                break;
            case ShellAction.CloseView when file is not null:
                if (file.SelectedView.Search is not null)
                    ViewModel.RemoveSearchCommand.Execute(file.SelectedView).Subscribe();
                else
                    ViewModel.CloseFileCommand.Execute(file).Subscribe();
                break;
            case ShellAction.NextView or ShellAction.PreviousView when file is not null:
                var step = command.Action == ShellAction.NextView ? 1 : -1;
                file.SelectedViewIndex =
                    (file.SelectedViewIndex + step + file.Views.Count) % file.Views.Count;
                break;
            case ShellAction.SelectView when file is not null && command.Index < file.Views.Count:
                file.SelectedViewIndex = command.Index;
                break;
            case ShellAction.ToggleFollow when file is not null:
                file.SelectedView.IsFollowing = !file.SelectedView.IsFollowing;
                break;
            case ShellAction.ScrollToBottom when file is not null:
                file.SelectedView.IsFollowing = true;
                break;
            case ShellAction.ScrollToTop:
                this.GetVisualDescendants()
                    .OfType<LogView>()
                    .FirstOrDefault(view => view.IsEffectivelyVisible)
                    ?.ScrollToTop();
                break;
        }
    }
}
