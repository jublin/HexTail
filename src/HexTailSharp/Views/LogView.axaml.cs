using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTailSharp.Application;
using HexTailSharp.Domain;
using HexTailSharp.Persistence;
using HexTailSharp.ViewModels;

namespace HexTailSharp.Views;

public partial class LogView : UserControl
{
    private LogViewViewModel? _viewModel;
    private ScrollViewer? _scrollViewer;
    private bool _scrollAttached;

    public LogView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        LogList.SelectionChanged += OnLogSelectionChanged;
        ContextList.SelectionChanged += OnContextSelectionChanged;
        LogList.TemplateApplied += (_, _) => TryAttachScrollHandler();
        LogList.AttachedToVisualTree += (_, _) =>
            Dispatcher.UIThread.Post(TryAttachScrollHandler, DispatcherPriority.Background);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.Lines.CollectionChanged -= OnLinesChanged;
            _viewModel.ContextLines.CollectionChanged -= OnContextLinesChanged;
        }

        _viewModel = DataContext as LogViewViewModel;
        _scrollAttached = false;
        _scrollViewer = null;
        LogList.ItemTemplate = new FuncDataTemplate<Line>((line, _) => BuildLogRow(line));
        ContextList.ItemTemplate = new FuncDataTemplate<Line>((line, _) => BuildContextRow(line));
        if (_viewModel is not null)
        {
            _viewModel.Lines.CollectionChanged += OnLinesChanged;
            _viewModel.ContextLines.CollectionChanged += OnContextLinesChanged;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                TryAttachScrollHandler();
                ScrollContextToSelected();
            },
            DispatcherPriority.Background
        );
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel?.IsFollowing != true || e.Action is not NotifyCollectionChangedAction.Add)
            return;

        Dispatcher.UIThread.Post(
            () =>
            {
                if (_viewModel?.IsFollowing == true && LogList.ItemCount > 0)
                    LogList.ScrollIntoView(LogList.ItemCount - 1);
            },
            DispatcherPriority.Background
        );
    }

    private void OnContextLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ScrollContextToSelected();

    private void OnLogSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null && LogList.SelectedItem is Line line)
            _viewModel.SelectLineCommand.Execute(line).Subscribe();
    }

    private void OnContextSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null && ContextList.SelectedItem is Line line)
            _viewModel.SelectLineCommand.Execute(line).Subscribe();
    }

    private void TryAttachScrollHandler()
    {
        if (_scrollAttached)
            return;

        var viewer = LogList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (viewer is null)
            return;

        _scrollAttached = true;
        _scrollViewer = viewer;
        viewer.ScrollChanged += OnScrollChanged;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_viewModel is null || _scrollViewer is null)
            return;
        if (
            _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height - _scrollViewer.Offset.Y
            > 8
        )
            _viewModel.IsFollowing = false;
    }

    private Control BuildContextRow(Line? line)
    {
        if (_viewModel is null || line is null)
            return new Border();

        var text = new TextBlock
        {
            FontFamily = LogFont,
            FontSize = ToLogFontSize(_viewModel.Settings.LogFontSize),
            Foreground = Brush("#CBD5E1"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        AddHighlightedRuns(text, _viewModel.File, line);
        return new Border
        {
            Child = text,
            Padding = LogRowPadding(),
            BorderBrush = Brush("#263449"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    private Control BuildLogRow(Line? line)
    {
        if (_viewModel is null || line is null)
            return new Border();

        var file = _viewModel.File;
        var lineIndex = FindLineIndex(file, line);
        var text = new TextBlock
        {
            FontFamily = LogFont,
            FontSize = ToLogFontSize(_viewModel.Settings.LogFontSize),
            Foreground = Brush("#E2E8F0"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        AddHighlightedRuns(text, file, line);

        Control content = text;
        if (line.ParsedFields is { Count: > 0 } && file.ExpandedLine == lineIndex)
            content = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    text,
                    new TextBlock
                    {
                        Text = string.Join(
                            "  ",
                            line.ParsedFields.Select(field => $"{field.Key}={field.Value}")
                        ),
                        FontFamily = LogFont,
                        FontSize = ToLogFontSize(_viewModel.Settings.LogFontSize),
                        Foreground = Brush("#CBD5E1"),
                        TextWrapping = TextWrapping.Wrap,
                        Padding = new Thickness(16, 2, 0, 6),
                    },
                },
            };

        var row = new Border
        {
            Child = content,
            Padding = LogRowPadding(),
            BorderBrush = Brush("#263449"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = Brushes.Transparent,
            Focusable = true,
        };
        row.Tapped += (_, e) =>
        {
            if (e.Handled || _viewModel is null)
                return;
            _viewModel.SelectLineCommand.Execute(line).Subscribe();
            e.Handled = true;
        };
        row.DoubleTapped += (_, e) =>
        {
            _viewModel?.ToggleExpandedCommand.Execute(line).Subscribe();
            e.Handled = true;
        };
        return row;
    }

    private void AddHighlightedRuns(TextBlock target, FileTabState file, Line line)
    {
        if (_viewModel is null)
            return;

        var ranges = file
            .Searches.SelectMany(search =>
                search
                    .GetHighlights(line)
                    .Select(range => (range.Start, range.Length, search.Color))
            )
            .Concat(
                _viewModel
                    .Settings.GetLabelHighlights(line.Raw)
                    .Select(range => (range.Start, range.Length, range.Color))
            )
            .Where(range =>
                range.Start >= 0
                && range.Length > 0
                && range.Start + range.Length <= line.Raw.Length
            )
            .OrderBy(range => range.Start)
            .ThenByDescending(range => range.Length)
            .ToList();

        if (ranges.Count == 0)
        {
            target.Text = line.Raw;
            return;
        }

        var inlines = new InlineCollection();
        var cursor = 0;
        foreach (var range in ranges)
        {
            if (range.Start < cursor)
                continue;
            if (range.Start > cursor)
                inlines.Add(new Run { Text = line.Raw[cursor..range.Start] });
            inlines.Add(
                new Run
                {
                    Text = line.Raw.Substring(range.Start, range.Length),
                    Background = Brush(range.Color),
                    Foreground = Brush("#111827"),
                }
            );
            cursor = range.Start + range.Length;
        }

        if (cursor < line.Raw.Length)
            inlines.Add(new Run { Text = line.Raw[cursor..] });
        target.Inlines = inlines;
    }

    private void ScrollContextToSelected()
    {
        if (
            _viewModel is null
            || _viewModel.File.SelectedLine is not int selected
            || selected < 0
            || selected >= _viewModel.File.Buffer.Count
        )
            return;

        var line = _viewModel.File.Buffer[selected];
        var index = _viewModel.ContextLines.IndexOf(line);
        if (index < 0)
            return;

        ContextList.SelectedItem = line;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (
                    _viewModel?.File.SelectedLine is int current
                    && current >= 0
                    && current < _viewModel.File.Buffer.Count
                    && ReferenceEquals(_viewModel.File.Buffer[current], line)
                    && index < _viewModel.ContextLines.Count
                )
                    ContextList.ScrollIntoView(index);
            },
            DispatcherPriority.Background
        );
    }

    private static int FindLineIndex(FileTabState file, Line line)
    {
        for (var index = 0; index < file.Buffer.Count; index++)
            if (ReferenceEquals(file.Buffer[index], line))
                return index;
        return -1;
    }

    private static FontFamily LogFont => new("Cascadia Mono,Consolas,Menlo,monospace");

    private double ToLogFontSize(LogFontSize size) =>
        size switch
        {
            LogFontSize.Small => 11,
            LogFontSize.Large => 14,
            LogFontSize.ExtraLarge => 17,
            _ => 13,
        };

    private Thickness LogRowPadding() =>
        _viewModel?.Settings.Density switch
        {
            UiDensity.Compact => new Thickness(12, 0.6),
            UiDensity.Cozy => new Thickness(12, 2),
            _ => new Thickness(12, 3),
        };

    private IBrush Brush(string value)
    {
        var light = _viewModel?.Settings.Theme == "light";
        return new SolidColorBrush(Color.Parse(light ? LightColor(value) : value));
    }

    private static string LightColor(string value) =>
        value switch
        {
            "#111827" => "#F8FAFC",
            "#172033" => "#F1F5F9",
            "#263449" => "#E2E8F0",
            "#94A3B8" => "#475569",
            "#E2E8F0" => "#1E293B",
            "#CBD5E1" => "#334155",
            _ => value,
        };
}
