using System.Collections;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;
using HexTail.ViewModels;

namespace HexTail.Behaviors;

/// <summary>Alt+Up / Alt+Down moves the focused item of a vertical list.</summary>
internal sealed class ListReorderBehavior : Behavior<ItemsControl>
{
    protected override void OnAttached() =>
        AssociatedObject!.AddHandler(
            InputElement.KeyDownEvent,
            OnKeyDown,
            RoutingStrategies.Tunnel
        );

    protected override void OnDetaching() =>
        AssociatedObject!.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (
            e.KeyModifiers != KeyModifiers.Alt
            || e.Key is not (Key.Up or Key.Down)
            || AssociatedObject is not { ItemsSource: IList items } list
        )
            return;
        var container = (e.Source as Visual)
            ?.GetSelfAndVisualAncestors()
            .OfType<Control>()
            .FirstOrDefault(control => list.IndexFromContainer(control) >= 0);
        if (container is null)
            return;
        var from = list.IndexFromContainer(container);
        var to = from + (e.Key == Key.Up ? -1 : 1);
        e.Handled = true;
        if (to < 0 || to >= items.Count)
            return;
        if (items is ObservableCollection<FileTabViewModel> files)
            files.Move(from, to);
        else if (items is ObservableCollection<string> names)
            names.Move(from, to);
        else
        {
            var item = items[from];
            items.RemoveAt(from);
            items.Insert(to, item);
        }
        Dispatcher.UIThread.Post(() =>
            list.ContainerFromIndex(to)
                ?.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault()
                ?.Focus()
        );
    }
}
