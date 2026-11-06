using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
        ChipList.AddHandler(Button.ClickEvent, OnChipBodyClick, RoutingStrategies.Bubble);
    }

    private void OnChipBodyClick(object? sender, RoutedEventArgs e)
    {
        if (
            e.Source is not Button button
            || !button.Classes.Contains("chip-body")
            || button.DataContext is not RuleChipViewModel { IsRuleChip: true } chip
            || FlyoutBase.GetAttachedFlyout(button) is not { } flyout
        )
            return;
        chip.OpenEditor();
        if (flyout is Flyout { Content: Control content })
            content.DataContext = chip;
        // The view model drives closing (Apply, Cancel, Escape); light dismissal reports back.
        void OnChipChanged(object? s, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(RuleChipViewModel.IsEditorOpen) && !chip.IsEditorOpen)
                flyout.Hide();
        }
        void OnClosed(object? s, EventArgs args)
        {
            flyout.Closed -= OnClosed;
            chip.PropertyChanged -= OnChipChanged;
            chip.IsEditorOpen = false;
        }
        chip.PropertyChanged += OnChipChanged;
        flyout.Closed += OnClosed;
        FlyoutBase.ShowAttachedFlyout(button);
        e.Handled = true;
    }

    internal void FocusQuery() => this.FindControl<TextBox>("QueryBox")?.Focus();

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (
            e.Key != Key.Enter
            || e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || e.KeyModifiers.HasFlag(KeyModifiers.Meta)
            || DataContext is not FilterBarViewModel bar
        )
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
