using System.Collections;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;
using HexTail.ViewModels;

namespace HexTail.Behaviors;

internal sealed class TabReorderBehavior : Behavior<TabControl>
{
    public static readonly StyledProperty<int> FixedHeaderCountProperty = AvaloniaProperty.Register<
        TabReorderBehavior,
        int
    >(nameof(FixedHeaderCount));

    private TabItem? _draggedTab;
    private Point _start;

    public int FixedHeaderCount
    {
        get => GetValue(FixedHeaderCountProperty);
        set => SetValue(FixedHeaderCountProperty, value);
    }

    protected override void OnAttached()
    {
        AssociatedObject!.AddHandler(
            InputElement.PointerPressedEvent,
            OnPointerPressed,
            handledEventsToo: true
        );
        AssociatedObject.AddHandler(
            InputElement.PointerReleasedEvent,
            OnPointerReleased,
            handledEventsToo: true
        );
        AssociatedObject.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetaching()
    {
        AssociatedObject!.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        AssociatedObject.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
        AssociatedObject.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _draggedTab = FindTab(e.Source as Visual);
        if (
            _draggedTab is null
            || AssociatedObject!.IndexFromContainer(_draggedTab) < FixedHeaderCount
        )
            _draggedTab = null;
        else
            _start = e.GetPosition(AssociatedObject);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var draggedTab = _draggedTab;
        _draggedTab = null;
        var point = e.GetPosition(AssociatedObject);
        if (draggedTab is null || Math.Abs(point.X - _start.X) < 4)
            return;

        var from = AssociatedObject!.IndexFromContainer(draggedTab);
        var to = Enumerable
            .Range(FixedHeaderCount, AssociatedObject.ItemCount - FixedHeaderCount)
            .Select(AssociatedObject.ContainerFromIndex)
            .OfType<TabItem>()
            .OrderBy(tab =>
                Math.Abs(
                    (
                        tab.TranslatePoint(new Point(tab.Bounds.Width / 2, 0), AssociatedObject)?.X
                        ?? 0
                    ) - point.X
                )
            )
            .Select(AssociatedObject.IndexFromContainer)
            .FirstOrDefault(-1);

        MoveTab(from, to);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Alt || e.Key is not (Key.Left or Key.Right))
            return;
        var tab = FindTab(e.Source as Visual);
        if (tab is null)
            return;
        var from = AssociatedObject!.IndexFromContainer(tab);
        var to = from + (e.Key == Key.Left ? -1 : 1);
        e.Handled = true;
        MoveTab(from, to);
        if (to >= FixedHeaderCount && to < AssociatedObject.ItemCount)
            AssociatedObject.ContainerFromIndex(to)?.Focus();
    }

    private void MoveTab(int from, int to)
    {
        if (
            from < FixedHeaderCount
            || to < FixedHeaderCount
            || to >= AssociatedObject!.ItemCount
            || from == to
            || AssociatedObject.ItemsSource is not IList items
        )
            return;
        var selected = AssociatedObject.SelectedItem;
        if (items is ObservableCollection<FileTabViewModel> files)
            files.Move(from, to);
        else if (items is ObservableCollection<LogViewViewModel> views)
            views.Move(from, to);
        else
        {
            var item = items[from];
            items.RemoveAt(from);
            items.Insert(to, item);
        }
        AssociatedObject.SelectedItem = selected;
    }

    private static TabItem? FindTab(Visual? control) =>
        control?.GetSelfAndVisualAncestors().OfType<TabItem>().FirstOrDefault();
}
