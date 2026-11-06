using System.Collections.ObjectModel;
using System.Reactive;
using System.Text.Encodings.Web;
using System.Text.Json;
using HexTail.Domain;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

/// <summary>Projects the selected row of the selected source into the right-hand inspector panel.</summary>
internal sealed class InspectorViewModel : ReactiveObject
{
    internal const double MinWidth = 260;
    internal const double MaxWidth = 640;
    internal const double DefaultWidth = 360;
    private const string NoRowTitle = "No row selected";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly MainWindowViewModel _owner;
    private bool _isOpen;
    private double _width = DefaultWidth;
    private Line? _line;
    private int _index = -1;
    private string _title = NoRowTitle;
    private string _rawText = string.Empty;
    private string? _jsonText;
    private string? _error;
    private bool _contextOpen;
    private bool _isBookmarked;
    private int _lastBookmarksVersion = -1;

    internal InspectorViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
        CloseCommand = ReactiveCommand.Create(
            () =>
            {
                IsOpen = false;
            },
            owner.Scheduler
        );
        ToggleBookmarkCommand = ReactiveCommand.Create(ToggleBookmarkOnSelection, owner.Scheduler);
    }

    internal MainWindowViewModel Owner => _owner;
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleBookmarkCommand { get; }
    public ObservableCollection<InspectorFieldViewModel> Fields { get; } = [];
    public ObservableCollection<InspectorContextRowViewModel> ContextRows { get; } = [];

    /// <summary>Whether the row's true neighbours are listed. In-session only; not persisted.</summary>
    public bool ContextOpen
    {
        get => _contextOpen;
        set
        {
            this.RaiseAndSetIfChanged(ref _contextOpen, value);
            RebuildContext();
        }
    }

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            this.RaiseAndSetIfChanged(ref _isOpen, value);
            this.RaisePropertyChanged(nameof(IsShown));
        }
    }

    public bool IsShown => IsOpen && _owner.SelectedFile is not null;

    public double Width
    {
        get => _width;
        set => this.RaiseAndSetIfChanged(ref _width, Math.Clamp(value, MinWidth, MaxWidth));
    }

    /// <summary>Whether the inspected row is bookmarked. In-session only; not persisted.</summary>
    public bool IsBookmarked => _isBookmarked;

    public bool HasLine => _line is not null;
    public string Title => _title;
    public string RawText => _rawText;
    public bool HasFields => Fields.Count > 0;
    public string? JsonText => _jsonText;
    public bool CanCopyJson => _jsonText is not null;

    public string? Error
    {
        get => _error;
        internal set
        {
            this.RaiseAndSetIfChanged(ref _error, value);
            this.RaisePropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    internal async Task ApplyFieldRuleAsync(InspectorFieldViewModel field, RuleAction action)
    {
        var file = _owner.SelectedFile?.Model;
        if (file is null)
            return;
        Error = null;
        if (string.IsNullOrWhiteSpace(field.Value))
        {
            Error = "This field has no value to match.";
            return;
        }

        try
        {
            var rule = _owner.FilterBar.NewSourceRule(
                file,
                field.Value,
                MatchMode.Literal,
                caseSensitive: true,
                action
            );
            await _owner.State.AddRuleAsync(file, rule);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex.Message;
        }
    }

    internal void SelectContextRow(InspectorContextRowViewModel row)
    {
        var file = _owner.SelectedFile?.Model;
        if (file is null)
            return;
        try
        {
            _owner.State.SelectLine(file, row.Line);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex.Message;
        }
    }

    internal void ToggleBookmarkOnSelection()
    {
        // Read the live selection rather than the last synced row: the shortcut also works with the panel closed.
        var file = _owner.SelectedFile?.Model;
        if (file?.SelectedLine is not int selected || selected < 0 || selected >= file.Buffer.Count)
            return;
        _owner.State.ToggleBookmark(file, file.Buffer[selected]);
    }

    internal void Open() => IsOpen = true;

    internal void Toggle() => IsOpen = !IsOpen;

    internal void Sync()
    {
        this.RaisePropertyChanged(nameof(IsShown));
        var file = _owner.SelectedFile?.Model;
        Line? line = null;
        var index = -1;
        if (file?.SelectedLine is int selected && selected >= 0 && selected < file.Buffer.Count)
        {
            index = selected;
            line = file.Buffer[selected];
        }

        var bookmarked = line is not null && file is not null && file.IsBookmarked(line);
        var bookmarksVersion = file?.BookmarksVersion ?? -1;
        if (bookmarked != _isBookmarked || bookmarksVersion != _lastBookmarksVersion)
        {
            _lastBookmarksVersion = bookmarksVersion;
            _isBookmarked = bookmarked;
            this.RaisePropertyChanged(nameof(IsBookmarked));
        }

        if (ReferenceEquals(line, _line) && index == _index)
        {
            // The neighbours can change under an unchanged selection (settings, appended lines).
            RebuildContext();
            return;
        }

        if (ReferenceEquals(line, _line) && line is not null)
        {
            // Same row, new index (older lines rolled out): keep the fields, their menus and any error.
            _index = index;
            _title = $"Line {index + 1:N0}";
            this.RaisePropertyChanged(nameof(Title));
            RebuildContext();
            return;
        }

        _line = line;
        _index = index;
        Error = null;
        _title = line is null ? NoRowTitle : $"Line {index + 1:N0}";
        _rawText = line?.Raw ?? string.Empty;

        Fields.Clear();
        _jsonText = null;
        if (line?.ParsedFields is { Count: > 0 } fields)
        {
            foreach (var pair in fields)
                Fields.Add(new InspectorFieldViewModel(this, pair.Key, pair.Value));
            _jsonText = JsonSerializer.Serialize(fields, JsonOptions);
        }

        this.RaisePropertyChanged(nameof(HasLine));
        this.RaisePropertyChanged(nameof(Title));
        this.RaisePropertyChanged(nameof(RawText));
        this.RaisePropertyChanged(nameof(HasFields));
        this.RaisePropertyChanged(nameof(JsonText));
        this.RaisePropertyChanged(nameof(CanCopyJson));
        RebuildContext();
    }

    private void RebuildContext()
    {
        var file = _owner.SelectedFile?.Model;
        if (!ContextOpen || file is null || _index < 0 || _index >= file.Buffer.Count)
        {
            if (ContextRows.Count > 0)
                ContextRows.Clear();
            return;
        }

        var settings = _owner.State.Settings;
        var window = file.Buffer.GetContextWindow(
            _index,
            settings.ContextAbove,
            settings.ContextBelow
        );
        // Sync runs on every poll; leave the list alone while nothing in it would change.
        if (
            ContextRows.Count == window.Count
            && ContextRows
                .Select(
                    (row, i) =>
                        ReferenceEquals(row.Line, window[i])
                        && row.IsSelected == ReferenceEquals(window[i], _line)
                )
                .All(same => same)
        )
            return;
        ContextRows.Clear();
        foreach (var line in window)
            ContextRows.Add(new InspectorContextRowViewModel(line, ReferenceEquals(line, _line)));
    }
}
