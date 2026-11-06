using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using HexTail.ViewModels;

namespace HexTail.Views;

public partial class InspectorPanel : UserControl
{
    public InspectorPanel() => InitializeComponent();

    private async void OnCopyRaw(object? sender, RoutedEventArgs e) =>
        await CopyAsync(viewModel => viewModel.RawText);

    private async void OnCopyJson(object? sender, RoutedEventArgs e) =>
        await CopyAsync(viewModel => viewModel.JsonText);

    private async Task CopyAsync(Func<InspectorViewModel, string?> text)
    {
        if (
            DataContext is not InspectorViewModel viewModel
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard
            || text(viewModel) is not { } value
        )
            return;
        await clipboard.SetTextAsync(value);
    }
}
