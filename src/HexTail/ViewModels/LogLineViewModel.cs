using System.Reactive.Linq;
using Avalonia;
using Avalonia.Media;
using HexTail;
using HexTail.Application;
using HexTail.Domain;
using HexTail.Persistence;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal sealed class LogLineViewModel : ReactiveObject
{
    private readonly LogViewViewModel _owner;
    private readonly FileTabViewModel _file;
    private bool _isVisible;
    private bool _renderDirty = true;

    internal LogLineViewModel(
        LogViewViewModel owner,
        FileTabViewModel file,
        Line line,
        bool isContext
    )
    {
        _owner = owner;
        _file = file;
        Line = line;
        IsContext = isContext;
        Refresh(notify: false);
    }

    public Line Line { get; }
    public bool IsContext { get; }
    public IReadOnlyList<HighlightSpan> Spans { get; private set; } = [];
    public string ParsedFieldsText { get; private set; } = string.Empty;
    public bool HasParsedFields => Line.ParsedFields is { Count: > 0 };
    public bool IsExpanded { get; private set; }
    public bool FieldsVisible => IsExpanded && HasParsedFields;
    public double FontSize { get; private set; }
    public Thickness RowPadding { get; private set; }
    public IBrush Foreground { get; private set; } = Brushes.White;
    public IBrush Background { get; private set; } = Brushes.Transparent;

    internal void Refresh() => Refresh(notify: true);

    private void Refresh(bool notify)
    {
        var settings = _owner.Settings;
        FontSize = settings.LogFontSize switch
        {
            LogFontSize.Small => 11,
            LogFontSize.Large => 14,
            LogFontSize.ExtraLarge => 17,
            _ => 13,
        };
        RowPadding = settings.Density switch
        {
            UiDensity.Compact => new Thickness(12, 0.6),
            UiDensity.Cozy => new Thickness(12, 2),
            _ => new Thickness(12, 3),
        };
        Foreground = IsContext ? ThemeManager.Brush("MutedBrush") : ThemeManager.Brush("TextBrush");
        Background = IsContext
            ? ThemeManager.Brush("RaisedBrush")
            : ThemeManager.Brush("SurfaceBrush");
        ParsedFieldsText = string.Empty;
        IsExpanded =
            _file.Model.ExpandedLine is int index
            && index >= 0
            && index < _file.Model.Buffer.Count
            && ReferenceEquals(_file.Model.Buffer[index], Line);

        _renderDirty = true;
        if (_isVisible)
            Render();

        if (notify)
        {
            this.RaisePropertyChanged(nameof(ParsedFieldsText));
            this.RaisePropertyChanged(nameof(HasParsedFields));
            this.RaisePropertyChanged(nameof(IsExpanded));
            this.RaisePropertyChanged(nameof(FieldsVisible));
            this.RaisePropertyChanged(nameof(FontSize));
            this.RaisePropertyChanged(nameof(RowPadding));
            this.RaisePropertyChanged(nameof(Foreground));
            this.RaisePropertyChanged(nameof(Background));
        }
    }

    internal void SetVisible(bool visible)
    {
        _isVisible = visible;
        if (visible)
            Render();
    }

    internal void InvalidateRender()
    {
        _renderDirty = true;
        if (_isVisible)
            Render();
    }

    internal void SyncExpansion()
    {
        var expanded =
            _file.Model.ExpandedLine is int index
            && index >= 0
            && index < _file.Model.Buffer.Count
            && ReferenceEquals(_file.Model.Buffer[index], Line);
        if (IsExpanded == expanded)
            return;
        IsExpanded = expanded;
        this.RaisePropertyChanged(nameof(IsExpanded));
        this.RaisePropertyChanged(nameof(FieldsVisible));
    }

    internal void Select() => _owner.SelectLineCommand.Execute(Line).Subscribe();

    internal void ToggleExpanded() => _owner.ToggleExpandedCommand.Execute(Line).Subscribe();

    private void Render()
    {
        if (!_renderDirty)
            return;

        var settings = _owner.Settings;
        ParsedFieldsText = Line.ParsedFields is { Count: > 0 }
            ? string.Join("  ", Line.ParsedFields.Select(field => $"{field.Key}={field.Value}"))
            : string.Empty;
        Spans = _file.HighlightSpans.Get(Line, _file.Model.Searches, settings);
        this.RaisePropertyChanged(nameof(Spans));
        this.RaisePropertyChanged(nameof(ParsedFieldsText));
        _renderDirty = false;
    }
}
