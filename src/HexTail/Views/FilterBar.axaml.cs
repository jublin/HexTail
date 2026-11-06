using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
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
        this.FindControl<Button>("SaveFilterButton")!.Click += OnSaveFilterClick;
    }

    private void OnSaveFilterClick(object? sender, RoutedEventArgs e)
    {
        if (
            sender is not Button button
            || DataContext is not FilterBarViewModel bar
            || FlyoutBase.GetAttachedFlyout(button)
                is not Flyout { Content: Control content } flyout
        )
            return;
        content.DataContext = bar;
        bar.SaveError = null;
        bar.SaveFlyoutOpen = true;
        // The view model closes the flyout after a save or Cancel; light dismissal reports back.
        void OnBarChanged(object? s, PropertyChangedEventArgs args)
        {
            if (
                args.PropertyName == nameof(FilterBarViewModel.SaveFlyoutOpen)
                && !bar.SaveFlyoutOpen
            )
                flyout.Hide();
        }
        void OnOpened(object? s, EventArgs args)
        {
            flyout.Opened -= OnOpened;
            if (
                content
                    .GetVisualDescendants()
                    .Prepend(content)
                    .OfType<TextBox>()
                    .FirstOrDefault(box => box.Name == "SaveFilterNameBox") is
                { } nameBox
            )
            {
                nameBox.Focus();
                nameBox.SelectAll();
            }
        }
        void OnClosed(object? s, EventArgs args)
        {
            flyout.Opened -= OnOpened;
            flyout.Closed -= OnClosed;
            bar.PropertyChanged -= OnBarChanged;
            bar.SaveFlyoutOpen = false;
        }
        bar.PropertyChanged += OnBarChanged;
        flyout.Opened += OnOpened;
        flyout.Closed += OnClosed;
        FlyoutBase.ShowAttachedFlyout(button);
        e.Handled = true;
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
        void OnOpened(object? s, EventArgs args)
        {
            flyout.Opened -= OnOpened;
            // Keyboard users land on the pattern, ready to retype it (Enter applies, Escape cancels).
            if (
                flyout is Flyout { Content: Control root }
                && root.GetVisualDescendants()
                    .Prepend(root)
                    .OfType<TextBox>()
                    .FirstOrDefault(box => box.Name == "DraftPatternBox")
                    is { } patternBox
            )
            {
                patternBox.Focus();
                patternBox.SelectAll();
            }
        }
        void OnClosed(object? s, EventArgs args)
        {
            flyout.Opened -= OnOpened;
            flyout.Closed -= OnClosed;
            chip.PropertyChanged -= OnChipChanged;
            chip.IsEditorOpen = false;
        }
        chip.PropertyChanged += OnChipChanged;
        flyout.Opened += OnOpened;
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
