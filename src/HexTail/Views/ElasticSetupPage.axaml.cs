using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    // Closing it takes the cursor out again, and the delete question gets the cursor while it shows.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ElasticSetupViewModel.IsOpen) when _viewModel is { IsOpen: true }:
                Dispatcher.UIThread.Post(FocusStartingControl, DispatcherPriority.Background);
                break;
            case nameof(ElasticSetupViewModel.IsOpen):
                Dispatcher.UIThread.Post(LeavePage, DispatcherPriority.Background);
                break;
            case nameof(ElasticSetupViewModel.DeleteConfirmationVisible):
                Dispatcher.UIThread.Post(FocusDeleteControl, DispatcherPriority.Background);
                break;
        }
    }

    private void FocusDeleteControl()
    {
        if (_viewModel is not { IsOpen: true } viewModel)
            return;
        var name = viewModel.DeleteConfirmationVisible
            ? "SetupCancelDeleteButton"
            : "SetupDeleteConnectionButton";
        // Cancel is the safe default for the destructive question; afterwards the cursor goes back
        // to the button that asked it.
        if (this.FindControl<Control>(name) is { IsEffectivelyVisible: true } control)
            control.Focus();
    }

    // A hidden control keeps the keyboard focus, and a hidden text box would make the shell treat
    // every single-key shortcut as typing. The cursor goes back to the log list when a source is
    // open and to the sidebar's Elastic button otherwise.
    private void LeavePage()
    {
        if (_viewModel is not { IsOpen: false } || TopLevel.GetTopLevel(this) is not { } top)
            return;
        var focused = top.FocusManager?.GetFocusedElement() as Visual;
        if (
            focused is not null
            && !ReferenceEquals(focused, this)
            && !this.IsVisualAncestorOf(focused)
        )
            return;
        var list = top.GetVisualDescendants()
            .OfType<LogView>()
            .FirstOrDefault(view => view.IsEffectivelyVisible)
            ?.FindControl<ListBox>("LogList");
        if (list is { IsEffectivelyVisible: true } && FocusLogList(list))
            return;
        var add = top.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(button =>
                button.Name == "SidebarElasticSetupButton" && button.IsEffectivelyVisible
            );
        if (add is not null && add.Focus())
            return;
        top.Focus();
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

    // The list itself does not take focus; its rows do, so the selected (or first) one gets it.
    private static bool FocusLogList(ListBox list) =>
        list.Focus()
        || list.ContainerFromIndex(Math.Max(list.SelectedIndex, 0)) is { } row && row.Focus();

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
