using Avalonia.Threading;

namespace HexTail.Views;

/// <summary>
/// Turns a burst of change notifications into at most one rebuild per interval, and only while the
/// owner is able to draw (visible and laid out). A request that arrives while it is not leaves the
/// rebuild pending; <see cref="Request"/> again (for example when the owner becomes visible) runs it.
/// </summary>
internal sealed class CoalescedRebuild
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);

    private readonly Func<bool> _canRun;
    private readonly Action _rebuild;
    private readonly DispatcherTimer _timer;
    private bool _dirty;

    public CoalescedRebuild(Func<bool> canRun, Action rebuild, TimeSpan? interval = null)
    {
        _canRun = canRun;
        _rebuild = rebuild;
        _timer = new DispatcherTimer { Interval = interval ?? DefaultInterval };
        _timer.Tick += OnTick;
    }

    /// <summary>A rebuild is wanted and has not run yet.</summary>
    public bool IsDirty => _dirty;

    /// <summary>The timer is running: a rebuild is scheduled.</summary>
    public bool IsScheduled => _timer.IsEnabled;

    /// <summary>Marks the build dirty without scheduling it (for an owner that is not attached).</summary>
    public void MarkDirty() => _dirty = true;

    /// <summary>Marks the build dirty and schedules one rebuild after the interval.</summary>
    public void Request()
    {
        _dirty = true;
        if (!_timer.IsEnabled)
            _timer.Start();
    }

    /// <summary>
    /// Schedules a pending rebuild that has become runnable since it was requested (for example the owner
    /// turned visible). Cheap enough to call after every layout pass: Avalonia 12 exposes no public change
    /// notification for <c>IsEffectivelyVisible</c>.
    /// </summary>
    public void Poke()
    {
        if (_dirty && !_timer.IsEnabled && _canRun())
            _timer.Start();
    }

    /// <summary>Stops the timer; a pending build stays dirty for the next <see cref="Request"/>.</summary>
    public void Stop() => _timer.Stop();

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (!_dirty || !_canRun())
            return;
        _dirty = false;
        _rebuild();
    }
}
