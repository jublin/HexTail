using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HexTail.ViewModels;

namespace HexTail.Views;

public partial class CommandPalette : UserControl
{
    private CommandPaletteViewModel? _viewModel;
    private IInputElement? _opener;

    public CommandPalette()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPaletteKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
                _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel = DataContext as CommandPaletteViewModel;
            if (_viewModel is not null)
                _viewModel.PropertyChanged += OnViewModelChanged;
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CommandPaletteViewModel.IsOpen) || _viewModel is null)
            return;
        var focus = TopLevel.GetTopLevel(this)?.FocusManager;
        if (_viewModel.IsOpen)
        {
            _opener = focus?.GetFocusedElement();
            Dispatcher.UIThread.Post(() => PaletteQuery.Focus());
        }
        else if (_opener is Control { IsEffectivelyVisible: true } opener)
            Dispatcher.UIThread.Post(() => opener.Focus());
    }

    private void OnPaletteKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is not { IsOpen: true })
            return;
        switch (e.Key)
        {
            case Key.Escape:
                _viewModel.Close();
                break;
            case Key.Down:
                _viewModel.Move(1);
                break;
            case Key.Up:
                _viewModel.Move(-1);
                break;
            case Key.Enter:
                _ = _viewModel.ExecuteSelectedAsync();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e) =>
        _viewModel?.Close();

    private void OnResultActivated(object? sender, TappedEventArgs e) =>
        _ = _viewModel?.ExecuteSelectedAsync();
}
