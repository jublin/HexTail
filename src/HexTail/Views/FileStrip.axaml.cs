using Avalonia.Controls;
using Avalonia.Threading;
using HexTail.ViewModels;

namespace HexTail.Views;

public partial class FileStrip : UserControl
{
    public FileStrip() => InitializeComponent();

    private void OnFileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox picker || DataContext is not MainWindowViewModel workspace)
            return;
        if (picker.SelectedItem is FileTabViewModel file)
        {
            if (!ReferenceEquals(workspace.SelectedFile, file))
                workspace.SelectFile(file);
        }
        else
        {
            // Native selection briefly clears when the selected item is moved or removed.
            Dispatcher.UIThread.Post(() =>
                picker.SetCurrentValue(ComboBox.SelectedItemProperty, workspace.SelectedFile)
            );
        }
    }
}
