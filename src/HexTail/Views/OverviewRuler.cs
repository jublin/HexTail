using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using HexTail.Domain;
using HexTail.ViewModels;

namespace HexTail.Views;

/// <summary>
/// A narrow column beside the log list that marks where rule matches (left lane, in the rule's colour)
/// and errors (right lane) sit among all rows, like the VS Code overview ruler. A click selects the
/// row at that height. It is rebuilt (coalesced) after the view syncs, and only while visible.
/// </summary>
internal sealed class OverviewRuler : Control
{
    internal const double LaneWidth = 5;
    private const double MarkHeight = 2;

    public static readonly StyledProperty<LogViewViewModel?> ViewProperty =
        AvaloniaProperty.Register<OverviewRuler, LogViewViewModel?>(nameof(View));

    private static readonly IReadOnlyList<ScrollMarker> NoMarkers = [];

    private readonly CoalescedRebuild _rebuild;
    private readonly Dictionary<string, IBrush?> _ruleBrushes = [];
    private LogOverviewViewModel? _subscribed;
    private IReadOnlyList<ScrollMarker> _markers = NoMarkers;
    private int _rowCount;

    public OverviewRuler()
    {
        ClipToBounds = true;
        _rebuild = new CoalescedRebuild(() => IsEffectivelyVisible && Bounds.Height > 0, Rebuild);
        ResourcesChanged += (_, _) => InvalidateVisual();
        ActualThemeVariantChanged += (_, _) =>
        {
            _ruleBrushes.Clear();
            InvalidateVisual();
        };
    }

    public LogViewViewModel? View
    {
        get => GetValue(ViewProperty);
        set => SetValue(ViewProperty, value);
    }

    /// <summary>The markers the control last built (empty until then).</summary>
    internal IReadOnlyList<ScrollMarker> CurrentMarkers => _markers;

    /// <summary>A rebuild is scheduled (the coalescing timer is running).</summary>
    internal bool RebuildScheduled => _rebuild.IsScheduled;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe();
        LayoutUpdated += OnLayoutUpdated;
        _rebuild.Request();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        LayoutUpdated -= OnLayoutUpdated;
        _rebuild.Stop();
        Unsubscribe();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ViewProperty)
        {
            _markers = NoMarkers;
            _rowCount = 0;
            if (VisualRoot is not null)
            {
                Unsubscribe();
                Subscribe();
                _rebuild.Request();
            }
            else
                _rebuild.MarkDirty();
            InvalidateVisual();
        }
        else if (change.Property == BoundsProperty)
        {
            var height = ((Rect)change.OldValue!).Height;
            if (Math.Abs(height - Bounds.Height) > 0.5)
            {
                if (_rebuild.IsDirty && VisualRoot is not null)
                    _rebuild.Request();
                InvalidateVisual();
            }
        }
    }

    // Becoming visible is followed by a layout pass: a rebuild that came due while hidden runs then.
    private void OnLayoutUpdated(object? sender, EventArgs e) => _rebuild.Poke();

    private void Subscribe()
    {
        var overview = View?.Overview;
        if (overview is null || ReferenceEquals(_subscribed, overview))
            return;
        _subscribed = overview;
        overview.PropertyChanged += OnOverviewChanged;
    }

    private void Unsubscribe()
    {
        if (_subscribed is null)
            return;
        _subscribed.PropertyChanged -= OnOverviewChanged;
        _subscribed = null;
    }

    private void OnOverviewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogOverviewViewModel.Version))
            _rebuild.Request();
    }

    private void Rebuild()
    {
        if (View is not { } view)
            return;
        _markers = view.Overview.BuildMarkers();
        _rowCount = view.Lines.Count;
        InvalidateVisual();
    }

    private IBrush Resolve(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : Brushes.Gray;

    private IBrush RuleBrush(string? color, IBrush fallback)
    {
        if (color is null)
            return fallback;
        if (!_ruleBrushes.TryGetValue(color, out var brush))
        {
            brush = Color.TryParse(color, out var parsed)
                ? new ImmutableSolidColorBrush(parsed)
                : null;
            _ruleBrushes[color] = brush;
        }
        return brush ?? fallback;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        context.FillRectangle(Resolve("SurfaceAltBrush"), new Rect(0, 0, width, height));
        if (_rowCount <= 0 || height <= 0 || _markers.Count == 0)
            return;

        var accent = Resolve("AccentBrush");
        var error = Resolve("ErrorBrush");
        var lastRuleY = -1;
        var lastErrorY = -1;
        foreach (var marker in _markers)
        {
            var y = (int)Math.Floor((double)marker.Row / _rowCount * height);
            if (marker.RuleColor is not null && y != lastRuleY)
            {
                lastRuleY = y;
                context.FillRectangle(
                    RuleBrush(marker.RuleColor, accent),
                    new Rect(0, y, LaneWidth, MarkHeight)
                );
            }
            if (marker.Error && y != lastErrorY)
            {
                lastErrorY = y;
                context.FillRectangle(error, new Rect(LaneWidth, y, LaneWidth, MarkHeight));
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        ClickAt(e.GetPosition(this).Y);
        e.Handled = true;
    }

    /// <summary>Selects the row at height <paramref name="y"/> (a click on the ruler).</summary>
    internal void ClickAt(double y)
    {
        if (View?.Overview is not { } overview || Bounds.Height <= 0)
            return;
        overview.JumpToFraction(y / Bounds.Height);
    }
}
