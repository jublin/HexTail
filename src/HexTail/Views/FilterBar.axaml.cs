using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HexTail.Domain;
using HexTail.ViewModels;
using ReactiveUI.Reactive;

namespace HexTail.Views;

public partial class FilterBar : UserControl
{
    public FilterBar()
    {
        InitializeComponent();
        this.FindControl<TextBox>("QueryBox")!.KeyDown += OnQueryKeyDown;
        ChipList.AddHandler(PointerReleasedEvent, OnChipPointerReleased, RoutingStrategies.Bubble);
    }

    internal void FocusQuery() => this.FindControl<TextBox>("QueryBox")?.Focus();

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not FilterBarViewModel bar)
            return;
        var action =
            e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? RuleAction.Hide
            : e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? RuleAction.ShowOnly
            : RuleAction.Highlight;
        e.Handled = true;
        _ = bar.SubmitAsync(action);
    }

    private void OnChipPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (
            e.InitialPressMouseButton == MouseButton.Middle
            && (e.Source as Control)?.DataContext is RuleChipViewModel chip
        )
        {
            var bar = DataContext as FilterBarViewModel;
            chip.ToggleEnabledCommand.Execute()
                .Subscribe(
                    _ => { },
                    ex =>
                    {
                        if (bar is not null)
                            bar.Error = ex.Message;
                    }
                );
            e.Handled = true;
        }
    }
}
