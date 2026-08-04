using Avalonia.Controls;
using HexTailSharp.Application;
using HexTailSharp.Persistence;
using HexTailSharp.Tailing;

namespace HexTailSharp;

public partial class MainWindow : Window
{
    private readonly string[] _startupPaths;
    private readonly TailerService _tailers = new();
    private readonly AppState _state;
    private bool _started;

    public MainWindow(string[]? startupPaths = null)
    {
        InitializeComponent();
        _startupPaths = startupPaths ?? [];
        _state = new AppState(_tailers, new JsonFileAppPersistence());
        Opened += OnOpened;
        Closed += OnClosed;
    }

    public AppState State => _state;

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_started)
            return;

        _started = true;
        await _state.RestoreAsync();
        foreach (var path in _startupPaths.Where(File.Exists))
            await _state.OpenFileAsync(path);
    }

    private async void OnClosed(object? sender, EventArgs e) => await _state.DisposeAsync();
}
