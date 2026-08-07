using Avalonia.Controls;
using Avalonia.Interactivity;
using HexTailSharp.ViewModels;

namespace HexTailSharp.Views;

public partial class LogLineView : UserControl
{
    public LogLineView() => InitializeComponent();

    private void OnTapped(object? sender, RoutedEventArgs e)
    {
        if (e.Handled || DataContext is not LogLineViewModel row)
            return;
        row.SelectCommand.Execute().Subscribe();
        e.Handled = true;
    }

    private void OnDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LogLineViewModel row)
            row.ToggleExpandedCommand.Execute().Subscribe();
        e.Handled = true;
    }
}
