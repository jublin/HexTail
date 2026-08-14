using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Media;
using HexTailSharp;
using HexTailSharp.Application;
using HexTailSharp.Domain;
using HexTailSharp.Persistence;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTailSharp.ViewModels;

internal sealed class LogLineViewModel : ReactiveObject
{
    private readonly LogViewViewModel _owner;
    private readonly FileTabViewModel _file;

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
        SelectCommand = ReactiveCommand.Create(() =>
        {
            _owner.SelectLineCommand.Execute(Line).Subscribe();
        });
        ToggleExpandedCommand = ReactiveCommand.Create(() =>
        {
            _owner.ToggleExpandedCommand.Execute(Line).Subscribe();
        });
        Refresh();
    }

    public Line Line { get; }
    public bool IsContext { get; }
    public ObservableCollection<LogTextSegmentViewModel> Segments { get; } = [];
    public ReactiveCommand<Unit, Unit> SelectCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleExpandedCommand { get; }
    public string ParsedFieldsText { get; private set; } = string.Empty;
    public bool HasParsedFields => Line.ParsedFields is { Count: > 0 };
    public bool IsExpanded { get; private set; }
    public bool FieldsVisible => IsExpanded && HasParsedFields;
    public double FontSize { get; private set; }
    public Thickness RowPadding { get; private set; }
    public IBrush Foreground { get; private set; } = Brushes.White;
    public IBrush Background { get; private set; } = Brushes.Transparent;

    internal void Refresh()
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
        ParsedFieldsText = Line.ParsedFields is { Count: > 0 }
            ? string.Join("  ", Line.ParsedFields.Select(field => $"{field.Key}={field.Value}"))
            : string.Empty;
        IsExpanded =
            _file.Model.ExpandedLine is int index
            && index >= 0
            && index < _file.Model.Buffer.Count
            && ReferenceEquals(_file.Model.Buffer[index], Line);

        Segments.Clear();
        var ranges = _file
            .Model.Searches.SelectMany(search =>
                search.GetHighlights(Line).Select(range => (Range: range, Color: search.Color))
            )
            .Concat(
                settings
                    .GetLabelHighlights(Line.Raw)
                    .Select(range =>
                        (Range: new HighlightRange(range.Start, range.Length), Color: range.Color)
                    )
            )
            .Where(item =>
                item.Range.Start >= 0
                && item.Range.Length > 0
                && item.Range.Start + item.Range.Length <= Line.Raw.Length
            )
            .OrderBy(item => item.Range.Start)
            .ThenByDescending(item => item.Range.Length)
            .ToList();

        var cursor = 0;
        foreach (var (range, color) in ranges)
        {
            if (range.Start < cursor)
                continue;
            if (range.Start > cursor)
                Segments.Add(
                    new LogTextSegmentViewModel(
                        Line.Raw[cursor..range.Start],
                        foreground: Foreground
                    )
                );
            Segments.Add(
                new LogTextSegmentViewModel(
                    Line.Raw.Substring(range.Start, range.Length),
                    Brush(color),
                    ThemeManager.Brush("TextBrush")
                )
            );
            cursor = range.Start + range.Length;
        }

        if (cursor < Line.Raw.Length || Segments.Count == 0)
            Segments.Add(new LogTextSegmentViewModel(Line.Raw[cursor..], foreground: Foreground));

        this.RaisePropertyChanged(nameof(ParsedFieldsText));
        this.RaisePropertyChanged(nameof(HasParsedFields));
        this.RaisePropertyChanged(nameof(IsExpanded));
        this.RaisePropertyChanged(nameof(FieldsVisible));
        this.RaisePropertyChanged(nameof(FontSize));
        this.RaisePropertyChanged(nameof(RowPadding));
        this.RaisePropertyChanged(nameof(Foreground));
        this.RaisePropertyChanged(nameof(Background));
    }

    private static SolidColorBrush Brush(string value) => new(Color.Parse(value));
}

public sealed class LogTextSegmentViewModel
{
    public LogTextSegmentViewModel(
        string text,
        IBrush? background = null,
        IBrush? foreground = null
    )
    {
        Text = text;
        Background = background;
        Foreground = foreground;
    }

    public string Text { get; }
    public IBrush? Background { get; }
    public IBrush? Foreground { get; }
}
