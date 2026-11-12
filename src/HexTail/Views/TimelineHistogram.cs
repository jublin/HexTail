using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using HexTail.Domain;
using HexTail.ViewModels;

namespace HexTail.Views;

/// <summary>
/// Row counts over time for one log view, stacked by level (errors at the bottom). Drag across the bars
/// to restrict the view to a time range, click to jump to the first row of a bucket, double-click to
/// reset the range. The histogram is rebuilt (coalesced) after the view syncs, and only while visible.
/// </summary>
internal sealed class TimelineHistogram : Control
{
    internal const double LabelHeight = 12;
    private const double CollapseButtonWidth = 16;
    private const double DragThreshold = 4;
    private const double LabelSize = 10;
    private const double HintSize = 11;

    public static readonly StyledProperty<LogViewViewModel?> ViewProperty =
        AvaloniaProperty.Register<TimelineHistogram, LogViewViewModel?>(nameof(View));

    private readonly CoalescedRebuild _rebuild;
    private LogOverviewViewModel? _subscribed;
    private TimeHistogram? _histogram;
    private bool _dragging;
    private bool _noRows;
    private double _startX;
    private double _currentX;

    public TimelineHistogram()
    {
        ClipToBounds = true;
        _rebuild = new CoalescedRebuild(
            () => IsEffectivelyVisible && Bounds.Width > 0 && !_dragging,
            Rebuild
        );
        ResourcesChanged += (_, _) => InvalidateVisual();
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public LogViewViewModel? View
    {
        get => GetValue(ViewProperty);
        set => SetValue(ViewProperty, value);
    }

    /// <summary>The histogram the control last built (null until then, or when no row has a time).</summary>
    internal TimeHistogram? CurrentHistogram => _histogram;

    /// <summary>A rebuild is scheduled (the coalescing timer is running).</summary>
    internal bool RebuildScheduled => _rebuild.IsScheduled;

    /// <summary>Shown over the empty panel when a built view has no rows left in the range.</summary>
    internal const string NoRowsText = "No rows in range – double-click to reset";

    /// <summary>The panel is showing <see cref="NoRowsText"/> instead of bars.</summary>
    internal bool ShowsNoRowsHint => _noRows && View?.File.ElasticLoading != true;

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
            _histogram = null;
            _noRows = false;
            _dragging = false;
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
            var width = ((Rect)change.OldValue!).Width;
            if (Math.Abs(width - Bounds.Width) > 0.5)
            {
                _rebuild.MarkDirty();
                if (VisualRoot is not null)
                    _rebuild.Request();
            }
        }
    }

    // Becoming visible (an expand) is followed by a layout pass: a rebuild that came due while hidden runs then.
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
        if (View?.Overview is not { } overview)
            return;
        _histogram = overview.BuildHistogram(Math.Max(1, (int)(Bounds.Width / 4)));
        _noRows = _histogram is null;
        InvalidateVisual();
    }

    private IBrush Resolve(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : Brushes.Gray;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        context.FillRectangle(Resolve("SurfaceAltBrush"), new Rect(0, 0, width, height));
        if (ShowsNoRowsHint && width > 0)
        {
            var hint = Label(NoRowsText, Resolve("FaintTextBrush"), HintSize);
            context.DrawText(hint, new Point((width - hint.Width) / 2, (height - hint.Height) / 2));
        }
        if (_histogram is not { } histogram || width <= 0 || histogram.Buckets.Count == 0)
            return;

        var count = histogram.Buckets.Count;
        var barsHeight = Math.Max(0, height - LabelHeight);
        var slot = width / count;
        var barWidth = Math.Max(1, slot - (slot >= 3 ? 1 : 0));
        var error = Resolve("ErrorBrush");
        var warn = Resolve("WarnBrush");
        var info = Resolve("SuccessBrush");
        var debug = Resolve("MutedBrush");
        var other = Resolve("FaintTextBrush");
        var max = Math.Max(1, histogram.MaxTotal);
        for (var index = 0; index < count; index++)
        {
            var bucket = histogram.Buckets[index];
            if (bucket.Total == 0)
                continue;
            var x = index * slot;
            var bottom = height;
            void Segment(int rows, IBrush brush)
            {
                if (rows <= 0)
                    return;
                var segment = (double)rows / max * barsHeight;
                context.FillRectangle(brush, new Rect(x, bottom - segment, barWidth, segment));
                bottom -= segment;
            }
            Segment(bucket.Error, error);
            Segment(bucket.Warn, warn);
            Segment(bucket.Info, info);
            Segment(bucket.Debug, debug);
            Segment(bucket.Other, other);
        }

        if (_dragging)
        {
            var first = BucketAt(Math.Min(_startX, _currentX));
            var last = BucketAt(Math.Max(_startX, _currentX));
            context.FillRectangle(
                Resolve("AccentSoftBrush"),
                new Rect(first * slot, LabelHeight, (last - first + 1) * slot, barsHeight)
            );
        }

        if (View?.Overview is { } overview)
        {
            var faint = Resolve("FaintTextBrush");
            var start = Label(overview.FormatTime(histogram, histogram.Start), faint, LabelSize);
            var end = Label(overview.FormatTime(histogram, histogram.End), faint, LabelSize);
            context.DrawText(start, new Point(4, 0));
            var endX = width - 4 - CollapseButtonWidth - end.Width;
            if (endX > 4 + start.Width + 8)
                context.DrawText(end, new Point(endX, 0));
        }
    }

    private static FormattedText Label(string text, IBrush brush, double size) =>
        new(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            size,
            brush
        );

    private int BucketAt(double x) =>
        _histogram is { } histogram && Bounds.Width > 0
            ? histogram.BucketIndexAt(Math.Clamp(x, 0, Bounds.Width) / Bounds.Width)
            : 0;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var began = PressAt(e.GetPosition(this).X, e.ClickCount);
        if (began)
            e.Pointer.Capture(this);
        // A double-click was a reset: nothing else should act on it.
        if (began || e.ClickCount == 2)
            e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        MoveTo(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging)
        {
            ReleaseAt(e.GetPosition(this).X);
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_dragging)
        {
            _dragging = false;
            InvalidateVisual();
            _rebuild.Poke();
        }
    }

    /// <summary>Starts a gesture (or resets the range on a double-click). True when it began a drag.</summary>
    internal bool PressAt(double x, int clickCount)
    {
        if (View?.Overview is not { } overview)
            return false;
        if (clickCount == 2)
        {
            overview.ResetRange();
            return false;
        }
        if (_histogram is null)
            return false;
        _startX = _currentX = x;
        _dragging = true;
        return true;
    }

    internal void MoveTo(double x)
    {
        if (_dragging)
        {
            _currentX = x;
            InvalidateVisual();
        }
        else if (_histogram is { } histogram && View?.Overview is { } overview)
            ToolTip.SetTip(this, overview.DescribeBucket(histogram, BucketAt(x)));
    }

    internal void ReleaseAt(double x)
    {
        if (!_dragging)
            return;
        _dragging = false;
        _currentX = x;
        InvalidateVisual();
        // A rebuild skipped during the drag runs now.
        _rebuild.Poke();
        if (_histogram is not { } histogram || View?.Overview is not { } overview)
            return;
        if (Math.Abs(x - _startX) >= DragThreshold)
        {
            var first = BucketAt(Math.Min(_startX, x));
            var last = BucketAt(Math.Max(_startX, x));
            var rows = 0;
            for (var index = first; index <= last; index++)
                rows += histogram.Buckets[index].Total;
            // A range with no rows would only hide the panel's own bars: ignore it.
            if (rows > 0)
                overview.ApplyRange(histogram.Buckets[first].Start, histogram.Buckets[last].End);
        }
        else
            overview.JumpToBucket(histogram, BucketAt(x));
    }
}
