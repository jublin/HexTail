using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Threading;
using HexTail.Application;
using HexTail.Persistence;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

/// <summary>One title of the column header: a column of the layout, as wide as its row cells.</summary>
internal sealed class ColumnHeaderItemViewModel : ReactiveObject
{
    private double _width;
    private bool _isLast;

    internal ColumnHeaderItemViewModel(string key, double width, bool isLast)
    {
        Key = key;
        Title = ColumnHeaderViewModel.TitleOf(key);
        _width = width;
        _isLast = isLast;
    }

    public string Key { get; }
    public string Title { get; }
    public string ResizeName => $"Resize {Title} column";

    /// <summary>Pixels, gutter included: exactly the width of this column's cells.</summary>
    public double Width => _width;
    public bool IsLast => _isLast;

    /// <summary>NaN (fill the rest) for the last column, like its cells.</summary>
    public double RenderWidth => _isLast ? double.NaN : _width;

    internal void Update(double width, bool isLast)
    {
        if (_width != width)
        {
            _width = width;
            this.RaisePropertyChanged(nameof(Width));
            this.RaisePropertyChanged(nameof(RenderWidth));
        }
        if (_isLast != isLast)
        {
            _isLast = isLast;
            this.RaisePropertyChanged(nameof(IsLast));
            this.RaisePropertyChanged(nameof(RenderWidth));
        }
    }
}

/// <summary>One checkbox of the Columns menu: a column that can be shown or hidden.</summary>
internal sealed class ColumnChoiceViewModel : ReactiveObject
{
    private readonly ColumnHeaderViewModel _owner;
    private bool _isOn;
    private bool _isEnabled = true;

    internal ColumnChoiceViewModel(ColumnHeaderViewModel owner, string key)
    {
        _owner = owner;
        Key = key;
        Title = ColumnHeaderViewModel.TitleOf(key);
    }

    public string Key { get; }
    public string Title { get; }

    /// <summary>Two-way: checking appends the column, unchecking removes it.</summary>
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value)
                return;
            _isOn = value;
            this.RaisePropertyChanged();
            _owner.SetColumnShown(Key, value);
        }
    }

    /// <summary>False for the last remaining column, which cannot be removed.</summary>
    public bool IsEnabled => _isEnabled;

    internal void Update(bool isOn, bool isEnabled)
    {
        if (_isOn != isOn)
        {
            _isOn = isOn;
            this.RaisePropertyChanged(nameof(IsOn));
        }
        if (_isEnabled != isEnabled)
        {
            _isEnabled = isEnabled;
            this.RaisePropertyChanged(nameof(IsEnabled));
        }
    }
}

/// <summary>
/// The header above a source's rows: one resizable title per column and the Columns menu. It edits
/// the source's <see cref="ColumnLayout"/>; resizing is live in memory and saved once when the drag ends.
/// </summary>
internal sealed class ColumnHeaderViewModel : ReactiveObject
{
    private readonly LogViewViewModel _owner;
    private ColumnLayout? _lastLayout;
    private bool _isStructured;
    private bool _isVisible;
    private bool _menuOpen;
    private string? _error;

    // Drag state: the layout the user had before the drag (for a failed save), the width the dragged
    // column started at and how far the pointer has moved since.
    private string? _dragKey;
    private ColumnLayout? _dragBase;
    private double _dragOrigin;
    private double _dragTotal;
    private bool _renderPending;

    internal ColumnHeaderViewModel(LogViewViewModel owner)
    {
        _owner = owner;
        ResetCommand = ReactiveCommand.CreateFromTask(ResetAsync, owner.Workspace.Scheduler);
    }

    /// <summary>True when the titles are shown: a structured source that is not set to composed rows.</summary>
    public bool IsVisible => _isVisible;

    /// <summary>True for any structured source; the Columns button stays reachable in composed mode.</summary>
    public bool IsBarVisible => _isStructured;

    public ObservableCollection<ColumnHeaderItemViewModel> Columns { get; } = [];
    public ObservableCollection<ColumnChoiceViewModel> Choices { get; } = [];
    public ReactiveCommand<Unit, Unit> ResetCommand { get; }

    public string? Error
    {
        get => _error;
        private set
        {
            this.RaiseAndSetIfChanged(ref _error, value);
            this.RaisePropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(_error);

    public bool ComposedRows
    {
        get => CurrentLayout().ComposedRows;
        set
        {
            if (CurrentLayout().ComposedRows == value)
                return;
            _ = ApplyAsync(layout => layout with { ComposedRows = value });
        }
    }

    public TimeDisplay TimeDisplay
    {
        get => CurrentLayout().TimeDisplay;
        set
        {
            if (CurrentLayout().TimeDisplay == value)
                return;
            _ = ApplyAsync(layout => layout with { TimeDisplay = value });
        }
    }

    public bool IsAbsoluteTime
    {
        get => TimeDisplay == TimeDisplay.Absolute;
        set
        {
            if (value)
                TimeDisplay = TimeDisplay.Absolute;
        }
    }

    public bool IsRelativeTime
    {
        get => TimeDisplay == TimeDisplay.Relative;
        set
        {
            if (value)
                TimeDisplay = TimeDisplay.Relative;
        }
    }

    internal static string TitleOf(string key) =>
        key switch
        {
            ColumnLayout.Time => "Time",
            ColumnLayout.Level => "Level",
            ColumnLayout.Message => "Message",
            _ => key,
        };

    /// <summary>The Time, Level and Message pseudo-columns; any other key, <c>@timestamp</c> too, is a field.</summary>
    private static bool IsPseudo(string key) =>
        key is ColumnLayout.Time or ColumnLayout.Level or ColumnLayout.Message;

    private ColumnLayout CurrentLayout() => _owner.ComputeLayout();

    /// <summary>Re-reads the layout: titles, widths, menu state and visibility. Cheap when nothing changed.</summary>
    internal void Sync()
    {
        var structured = _owner.File.IsStructured;
        var layout = _owner.Layout;
        var changed = !ReferenceEquals(layout, _lastLayout) || structured != _isStructured;
        if (changed)
        {
            var layoutChanged = !ReferenceEquals(layout, _lastLayout);
            _lastLayout = layout;
            _isStructured = structured;
            var visible = structured && !layout.ComposedRows;
            if (visible != _isVisible)
            {
                _isVisible = visible;
                this.RaisePropertyChanged(nameof(IsVisible));
            }
            this.RaisePropertyChanged(nameof(IsBarVisible));
            SyncColumns(layout);
            if (layoutChanged)
            {
                this.RaisePropertyChanged(nameof(ComposedRows));
                this.RaisePropertyChanged(nameof(TimeDisplay));
                this.RaisePropertyChanged(nameof(IsAbsoluteTime));
                this.RaisePropertyChanged(nameof(IsRelativeTime));
            }
        }
        if (changed || _menuOpen)
            SyncChoices(layout, scanFields: _menuOpen);
    }

    private void SyncColumns(ColumnLayout layout)
    {
        var specs = layout.Columns;
        var sameKeys =
            Columns.Count == specs.Count
            && Columns.Select((item, index) => item.Key == specs[index].Key).All(same => same);
        if (sameKeys)
        {
            // Update in place, so a thumb that is being dragged keeps its element.
            for (var index = 0; index < specs.Count; index++)
                Columns[index].Update(specs[index].Width, index == specs.Count - 1);
            return;
        }
        // Other columns now: a drag that was in progress belongs to a header that is gone.
        _dragKey = null;
        _dragBase = null;
        _dragTotal = 0;
        Columns.Clear();
        for (var index = 0; index < specs.Count; index++)
            Columns.Add(
                new ColumnHeaderItemViewModel(
                    specs[index].Key,
                    specs[index].Width,
                    index == specs.Count - 1
                )
            );
    }

    private void SyncChoices(ColumnLayout layout, bool scanFields)
    {
        List<string> fields = [];
        if (scanFields)
            fields.AddRange(_owner.File.KnownFields());
        else
            fields.AddRange(
                Choices.Select(choice => choice.Key).Where(key => !IsPseudo(key)).ToList()
            );
        foreach (var column in layout.Columns)
            if (!IsPseudo(column.Key) && !fields.Contains(column.Key))
                fields.Add(column.Key);
        var keys = new List<string> { ColumnLayout.Time, ColumnLayout.Level, ColumnLayout.Message };
        keys.AddRange(fields.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

        if (!Choices.Select(choice => choice.Key).SequenceEqual(keys))
        {
            var existing = Choices.ToDictionary(choice => choice.Key);
            Choices.Clear();
            foreach (var key in keys)
                Choices.Add(
                    existing.TryGetValue(key, out var choice)
                        ? choice
                        : new ColumnChoiceViewModel(this, key)
                );
        }
        var onlyOne = layout.Columns.Count <= 1;
        foreach (var choice in Choices)
        {
            var isOn = layout.Columns.Any(column => column.Key == choice.Key);
            choice.Update(isOn, !(isOn && onlyOne));
        }
    }

    /// <summary>The Columns menu opened or closed; while it is open the field list follows the buffer.</summary>
    internal void SetMenuOpen(bool open)
    {
        _menuOpen = open;
        if (open)
            SyncChoices(_owner.Layout, scanFields: true);
    }

    /// <summary>A checkbox of the menu changed: show or hide that column.</summary>
    internal void SetColumnShown(string key, bool shown) =>
        _ = ApplyAsync(layout =>
            shown ? layout.WithColumn(key, _owner.LogFontSize) : layout.WithoutColumn(key)
        );

    internal Task ToggleComposedAsync() =>
        ApplyAsync(layout => layout with { ComposedRows = !layout.ComposedRows });

    private Task ResetAsync() =>
        ApplyAsync(layout =>
            _owner.DefaultLayout() with
            {
                ComposedRows = layout.ComposedRows,
                TimeDisplay = layout.TimeDisplay,
            }
        );

    /// <summary>
    /// Applies a change to the layout and saves it. Failures never escape: they show on
    /// <see cref="Error"/>, and the layout stays as it was.
    /// </summary>
    private async Task ApplyAsync(Func<ColumnLayout, ColumnLayout> change)
    {
        try
        {
            Error = null;
            var current = CurrentLayout();
            var layout = change(current);
            if (!ReferenceEquals(layout, current))
                await _owner.Workspace.State.SetColumnLayoutAsync(_owner.File, layout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex.Message;
        }
        finally
        {
            // Refresh even after a failure: the checkbox that was just flipped has to flip back.
            _lastLayout = null;
            _owner.RefreshLayout();
        }
    }

    /// <summary>
    /// A header thumb moved by <paramref name="delta"/> pixels: widen or narrow the column live. Rows
    /// re-render at most once per frame; nothing is saved until <see cref="CompleteResize"/>.
    /// </summary>
    internal void Resize(string key, double delta)
    {
        var current = CurrentLayout();
        if (current.Columns.All(column => column.Key != key))
            return;
        if (_dragKey != key)
        {
            _dragKey = key;
            _dragBase = _owner.File.Columns;
            _dragOrigin = current.Columns.First(column => column.Key == key).Width;
            _dragTotal = 0;
        }
        _dragTotal += delta;
        var layout = current.WithWidth(key, _dragOrigin + _dragTotal);
        _owner.File.Columns = layout;
        _lastLayout = layout;
        SyncColumns(layout);
        ScheduleRender();
    }

    private void ScheduleRender()
    {
        if (_renderPending)
            return;
        _renderPending = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!_renderPending)
                    return;
                _renderPending = false;
                _owner.RefreshLayout();
            },
            DispatcherPriority.Render
        );
    }

    /// <summary>The drag ended: save the width once. A failed save puts back the width from before the drag.</summary>
    internal void CompleteResize(string key)
    {
        if (_dragKey != key)
            return;
        _dragKey = null;
        var final = _owner.File.Columns;
        var start = _dragBase;
        _dragBase = null;
        if (final is null)
            return;
        var unchanged =
            final.Columns.FirstOrDefault(column => column.Key == key)?.Width == _dragOrigin;
        if (unchanged)
        {
            // Dragged back to where it started: nothing to save, only put the rows back in step.
            _owner.File.Columns = start;
            _renderPending = false;
            _owner.RefreshLayout();
            return;
        }
        // The live layout was only in memory: restore the saved one, so a failed save rolls back to
        // it (no re-render can happen before SetColumnLayoutAsync has put the new layout in again).
        _owner.File.Columns = start;
        _renderPending = false;
        _ = ApplyAsync(_ => final);
    }
}
