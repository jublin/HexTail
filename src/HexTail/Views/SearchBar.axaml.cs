using Avalonia.Controls;

namespace HexTail.Views;

public partial class SearchBar : UserControl
{
    public SearchBar() => InitializeComponent();

    internal void FocusQuery() => QueryBox.Focus();
}
