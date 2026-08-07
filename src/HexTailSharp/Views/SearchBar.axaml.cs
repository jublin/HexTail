using Avalonia.Controls;

namespace HexTailSharp.Views;

public partial class SearchBar : UserControl
{
    public SearchBar() => InitializeComponent();

    internal void FocusQuery() => QueryBox.Focus();
}
