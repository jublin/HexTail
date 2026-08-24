using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;

namespace HexTailSharp.Behaviors;

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
    }

    protected override void OnDetaching()
    {
        AssociatedObject!.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        AssociatedObject.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
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
        var point = e.GetPosition(AssociatedObject);
        if (_draggedTab is null || Math.Abs(point.X - _start.X) < 4)
            return;

        var from = AssociatedObject!.IndexFromContainer(_draggedTab);
        var to = Enumerable
            .Range(FixedHeaderCount, AssociatedObject.ItemCount - FixedHeaderCount)
            .Select(AssociatedObject.ContainerFromIndex)
            .OfType<TabItem>()
            .OrderBy(tab => Math.Abs(tab.Bounds.Center.X - point.X))
            .Select(AssociatedObject.IndexFromContainer)
            .FirstOrDefault(-1);

        if (to >= FixedHeaderCount && from != to && AssociatedObject.ItemsSource is IList items)
        {
            var item = items[from];
            items.RemoveAt(from);
            items.Insert(to, item);
            AssociatedObject.SelectedIndex = to;
        }

        _draggedTab = null;
    }

    private static TabItem? FindTab(Visual? control) =>
        control?.GetSelfAndVisualAncestors().OfType<TabItem>().FirstOrDefault();
}
