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

    private void FocusStartingControl()
    {
        if (_viewModel is not { IsOpen: true } viewModel)
            return;
        var first = viewModel.Step switch
        {
            ElasticSetupStep.Connection => "ConnectionNameBox",
            ElasticSetupStep.DataView => "DataViewSearchBox",
            // The later steps have no field of their own yet; the buttons are the way on.
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
