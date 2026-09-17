using Avalonia.Controls;
using Avalonia.Interactivity;
using HexTail.ViewModels;

namespace HexTail.Views;

public partial class LogLineView : UserControl
{
    public LogLineView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => SetRowVisible(true);
        DetachedFromVisualTree += (_, _) => SetRowVisible(false);
        DataContextChanged += (_, _) => SetRowVisible(VisualRoot is not null);
    }

    private void SetRowVisible(bool visible)
    {
        if (DataContext is LogLineViewModel row)
            row.SetVisible(visible);
    }

    private void OnTapped(object? sender, RoutedEventArgs e)
    {
        if (e.Handled || DataContext is not LogLineViewModel row)
            return;
        row.Select();
        e.Handled = true;
    }

    private void OnDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LogLineViewModel row)
            row.ToggleExpanded();
        e.Handled = true;
    }
}
