using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTailSharp.Domain;
using HexTailSharp.ViewModels;

namespace HexTailSharp.Views;

public partial class LogView : UserControl
{
    private LogViewViewModel? _viewModel;
    private ScrollViewer? _scrollViewer;
    private bool _scrollAttached;
    private bool _scrollingToEnd;

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
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as LogViewViewModel;
        _scrollAttached = false;
        _scrollViewer = null;
        if (_viewModel is not null)
        {
            _viewModel.Lines.CollectionChanged += OnLinesChanged;
            _viewModel.ContextLines.CollectionChanged += OnContextLinesChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
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

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LogViewViewModel.IsFollowing))
            return;

        Dispatcher.UIThread.Post(
            () =>
            {
                if (_viewModel?.IsFollowing == true)
                    ScrollLogToEnd();
            },
            DispatcherPriority.Background
        );
    }

    private void OnLogSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null && LogList.SelectedItem is LogLineViewModel row)
            _viewModel.SelectLineCommand.Execute(row.Line).Subscribe();
    }

    private void OnContextSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null && ContextList.SelectedItem is LogLineViewModel row)
            _viewModel.SelectLineCommand.Execute(row.Line).Subscribe();
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
        if (_scrollingToEnd || _viewModel is null || _scrollViewer is null)
            return;
        if (
            _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height - _scrollViewer.Offset.Y
            > 8
        )
            _viewModel.IsFollowing = false;
    }

    private void ScrollLogToEnd()
    {
        if (_viewModel?.IsFollowing != true || LogList.ItemCount == 0)
            return;

        _scrollingToEnd = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_viewModel?.IsFollowing == true && LogList.ItemCount > 0)
                    LogList.ScrollIntoView(LogList.ItemCount - 1);
                Dispatcher.UIThread.Post(
                    () => _scrollingToEnd = false,
                    DispatcherPriority.Background
                );
            },
            DispatcherPriority.Background
        );
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
        var row = _viewModel.ContextLines.FirstOrDefault(item => ReferenceEquals(item.Line, line));
        var index = row is null ? -1 : _viewModel.ContextLines.IndexOf(row);
        if (index < 0)
            return;

        ContextList.SelectedItem = row;
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
}
