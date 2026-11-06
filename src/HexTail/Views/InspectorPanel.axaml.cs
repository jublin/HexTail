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

    private async void OnCopyFieldValue(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: InspectorFieldViewModel field })
            await CopyAsync(_ => field.Value);
    }

    /// <summary>
    /// Copies to the clipboard from an <c>async void</c> handler, so a clipboard failure is
    /// reported on the inspector instead of escaping as an unhandled exception.
    /// </summary>
    private async Task CopyAsync(Func<InspectorViewModel, string?> text)
    {
        if (
            DataContext is not InspectorViewModel viewModel
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard
            || text(viewModel) is not { } value
        )
            return;
        try
        {
            await clipboard.SetTextAsync(value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            viewModel.Error = ex.Message;
        }
    }
}
