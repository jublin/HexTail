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
    private bool _ignoreScrollChanges;

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

        if (_scrollViewer is not null)
            _scrollViewer.ScrollChanged -= OnScrollChanged;

        _viewModel = DataContext as LogViewViewModel;
        _scrollAttached = false;
        _scrollViewer = null;
        _ignoreScrollChanges = _viewModel is not null;
        if (_viewModel is not null)
        {
            _viewModel.Lines.CollectionChanged += OnLinesChanged;
            _viewModel.ContextLines.CollectionChanged += OnContextLinesChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.Sync();
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                TryAttachScrollHandler();
                RequestScrollLogToEnd();
                ScrollContextToSelected();
                var viewModel = _viewModel;
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (ReferenceEquals(_viewModel, viewModel))
                            _ignoreScrollChanges = false;
                    },
                    DispatcherPriority.Render
                );
            },
            DispatcherPriority.Background
        );
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel?.IsFollowing != true)
            return;

        if (Dispatcher.UIThread.CheckAccess())
            RequestScrollLogToEnd();
        else
            Dispatcher.UIThread.Post(RequestScrollLogToEnd, DispatcherPriority.Background);
    }

    private void OnContextLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(ScrollContextToSelected, DispatcherPriority.Background);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogViewViewModel.ContextVisible))
            Dispatcher.UIThread.Post(ScrollContextToSelected, DispatcherPriority.Background);

        if (e.PropertyName != nameof(LogViewViewModel.IsFollowing))
            return;

        Dispatcher.UIThread.Post(RequestScrollLogToEnd, DispatcherPriority.Background);
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
        if (
            _scrollingToEnd
            || _ignoreScrollChanges
            || _viewModel is null
            || _scrollViewer is not { } viewer
            || !ReferenceEquals(sender, viewer)
        )
            return;
        if (viewer.Extent.Height - viewer.Viewport.Height - viewer.Offset.Y > 8)
            _viewModel.IsFollowing = false;
    }

    private void RequestScrollLogToEnd()
    {
        if (_viewModel?.IsFollowing != true)
            return;

        _scrollingToEnd = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_viewModel?.IsFollowing == true && LogList.ItemCount > 0)
                    LogList.ScrollIntoView(LogList.ItemCount - 1);
                Dispatcher.UIThread.Post(() => _scrollingToEnd = false, DispatcherPriority.Render);
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
