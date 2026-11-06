using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using HexTail.ViewModels;

namespace HexTail.Views;

public partial class ElasticSetupPage : UserControl
{
    private ElasticSetupViewModel? _viewModel;
    private bool _restoringSelection;

    public ElasticSetupPage()
    {
        InitializeComponent();
        this.FindControl<TextBox>("FilterValueInput")!.TextChanged += OnFilterValueTextChanged;
        this.FindControl<ListBox>("SelectedFieldsList")!.SelectionChanged +=
            OnSelectedFieldsSelectionChanged;
        this.FindControl<ComboBox>("FilterFieldBox")!.SelectionChanged +=
            OnFilterFieldSelectionChanged;
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelChanged;
                _viewModel.FilteredDataViews.CollectionChanged -= OnDataViewsFiltered;
            }
            _viewModel = DataContext as ElasticSetupViewModel;
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnViewModelChanged;
                _viewModel.FilteredDataViews.CollectionChanged += OnDataViewsFiltered;
            }
        };
    }

    // Opening the page puts the cursor in the first control of the step it starts on, so keyboard
    // and screen reader users start inside the page instead of on whatever the click left behind.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName == nameof(ElasticSetupViewModel.IsOpen)
            && _viewModel is { IsOpen: true }
        )
            Dispatcher.UIThread.Post(FocusStartingControl, DispatcherPriority.Background);
    }

    // The list drops its selection while the chosen data view is filtered out and does not take the
    // view model's unchanged value back when the row returns, so the choice is put back by hand.
    private void OnDataViewsFiltered(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_restoringSelection)
            return;
        _restoringSelection = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _restoringSelection = false;
                if (
                    _viewModel?.View?.SelectedDataView is { } selected
                    && _viewModel.FilteredDataViews.Contains(selected)
                    && this.FindControl<ListBox>("DataViewList") is { } list
                    && !ReferenceEquals(list.SelectedItem, selected)
                )
                    list.SetCurrentValue(SelectingItemsControl.SelectedItemProperty, selected);
            },
            DispatcherPriority.Background
        );
    }

    // The view model cuts a comma-separated entry down to what is still being typed. A binding does
    // not push a value it believes it already sent, so the box is corrected here once it settled.
    private void OnFilterValueTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box)
            return;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_viewModel is not { } viewModel)
                    return;
                var kept = viewModel.NewFilterValue;
                if (!string.Equals(box.Text ?? string.Empty, kept, StringComparison.Ordinal))
                {
                    box.Text = kept;
                    box.CaretIndex = kept.Length;
                }
            },
            DispatcherPriority.Background
        );
    }

    // Moving a column rebuilds the rows and the list lets go of the selected one; the view model
    // keeps it, so the list is told again which one it is.
    private void OnSelectedFieldsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list)
            return;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (
                    _viewModel?.SelectedColumn is { } column
                    && !string.Equals(list.SelectedItem as string, column, StringComparison.Ordinal)
                    && _viewModel.View?.SelectedOutputFields.Contains(column) == true
                )
                    list.SelectedItem = column;
            },
            DispatcherPriority.Background
        );
    }

    // The combo box clears its choice whenever its items are rebuilt (the field list was reloaded);
    // the view model still knows the field, so the box is told again which option it is.
    private void OnFilterFieldSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox box)
            return;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (
                    _viewModel?.SelectedSearchableField is { } option
                    && !ReferenceEquals(box.SelectedItem, option)
                )
                    box.SelectedItem = option;
            },
            DispatcherPriority.Background
        );
    }

    private void FocusStartingControl()
    {
        if (_viewModel is not { IsOpen: true } viewModel)
            return;
        var first = viewModel.Step switch
        {
            ElasticSetupStep.Connection => "ConnectionNameBox",
            ElasticSetupStep.DataView => "DataViewSearchBox",
            ElasticSetupStep.Filter => "FilterValueInput",
            ElasticSetupStep.Columns => "FieldSearchBox",
            ElasticSetupStep.Finish => "ViewNameBox",
            _ => "SetupNextButton",
        };
        foreach (var name in new[] { first, "SetupNextButton", "SetupCloseButton" })
            if (
                this.FindControl<Control>(name)
                    is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } control
                && control.Focus()
            )
                return;
    }
}
