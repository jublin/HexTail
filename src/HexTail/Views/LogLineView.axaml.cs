using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HexTail.ViewModels;

namespace HexTail.Views;

public partial class LogLineView : UserControl
{
    private ListBoxItem? _container;

    public LogLineView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            SetRowVisible(true);
            _container = this.FindAncestorOfType<ListBoxItem>();
            _container?.AddHandler(KeyDownEvent, OnRowKeyDown, RoutingStrategies.Tunnel);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            SetRowVisible(false);
            _container?.RemoveHandler(KeyDownEvent, OnRowKeyDown);
            _container = null;
        };
        DataContextChanged += (_, _) => SetRowVisible(VisualRoot is not null);
    }

    private void SetRowVisible(bool visible)
    {
        if (DataContext is LogLineViewModel row)
            row.SetVisible(visible);
    }

    private void OnTapped(object? sender, RoutedEventArgs e)
    {
        if (e.Handled || e.Source is SelectableTextBlock || DataContext is not LogLineViewModel row)
            return;
        row.Select();
        _container?.Focus();
        e.Handled = true;
    }

    private void OnDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (e.Source is SelectableTextBlock)
            return;
        if (DataContext is LogLineViewModel row)
            row.ToggleExpanded();
        e.Handled = true;
    }

    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is LogLineViewModel row)
            row.Select();
    }

    private void OnToggleDetails(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogLineViewModel row)
            return;
        row.Select();
        row.ToggleExpanded();
    }

    private async void OnCopyRawLine(object? sender, RoutedEventArgs e) =>
        await CopyAsync(fields: false);

    private async void OnCopyFields(object? sender, RoutedEventArgs e) =>
        await CopyAsync(fields: true);

    private async Task CopyAsync(bool fields)
    {
        if (
            DataContext is not LogLineViewModel row
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard
        )
            return;
        row.Select();
        await clipboard.SetTextAsync(fields ? row.ParsedFieldsText : row.Line.Raw);
    }

    private async void OnRowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || DataContext is not LogLineViewModel row)
            return;
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            row.Select();
            row.ToggleExpanded();
            e.Handled = true;
        }
        else if (e.Key == Key.Apps || (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift))
        {
            ContextMenu?.Open(this);
            e.Handled = true;
        }
        else if (
            e.Key == Key.C
            && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0
            && e.Source is not SelectableTextBlock
        )
        {
            var fields = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (fields && !row.HasParsedFields)
                return;
            e.Handled = true;
            await CopyAsync(fields);
        }
    }
}
