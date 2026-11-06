using Avalonia.Controls;
using Avalonia.Input;
using HexTail.ViewModels;

namespace HexTail.Views;

public partial class LogWorkspace : UserControl
{
    public LogWorkspace() => InitializeComponent();

    private void OnInspectorResize(object? sender, VectorEventArgs e)
    {
        if (DataContext is MainWindowViewModel owner)
            owner.Inspector.Width -= e.Vector.X;
    }
}
