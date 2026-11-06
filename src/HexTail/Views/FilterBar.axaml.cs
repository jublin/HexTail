using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HexTail.ViewModels;
using ReactiveUI.Reactive;

namespace HexTail.Views;

public partial class FilterBar : UserControl
{
    public FilterBar()
    {
        InitializeComponent();
        ChipList.AddHandler(PointerReleasedEvent, OnChipPointerReleased, RoutingStrategies.Bubble);
    }

    private void OnChipPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (
            e.InitialPressMouseButton == MouseButton.Middle
            && (e.Source as Control)?.DataContext is RuleChipViewModel chip
        )
        {
            chip.ToggleEnabledCommand.Execute().Subscribe();
            e.Handled = true;
        }
    }
}
