using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using HexTail.ViewModels;

namespace HexTail.Views;

public partial class ColumnHeaderBar : UserControl
{
    public ColumnHeaderBar()
    {
        InitializeComponent();
        // The thumbs live in the item template; their routed events bubble up to here.
        AddHandler(Thumb.DragDeltaEvent, OnThumbDragDelta);
        AddHandler(Thumb.DragCompletedEvent, OnThumbDragCompleted);
    }

    private ColumnHeaderViewModel? Model => DataContext as ColumnHeaderViewModel;

    private static ColumnHeaderItemViewModel? ItemOf(RoutedEventArgs e) =>
        (e.Source as Control)?.DataContext as ColumnHeaderItemViewModel;

    private void OnThumbDragDelta(object? sender, VectorEventArgs e)
    {
        if (ItemOf(e) is { } item)
            Model?.Resize(item.Key, e.Vector.X);
    }

    private void OnThumbDragCompleted(object? sender, VectorEventArgs e)
    {
        if (ItemOf(e) is { } item)
            Model?.CompleteResize(item.Key);
    }

    private void OnMenuOpened(object? sender, EventArgs e) => Model?.SetMenuOpen(true);

    private void OnMenuClosed(object? sender, EventArgs e) => Model?.SetMenuOpen(false);
}
