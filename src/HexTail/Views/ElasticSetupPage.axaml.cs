using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using HexTail.ViewModels;

namespace HexTail.Views;

public partial class ElasticSetupPage : UserControl
{
    private ElasticSetupViewModel? _viewModel;

    public ElasticSetupPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
                _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel = DataContext as ElasticSetupViewModel;
            if (_viewModel is not null)
                _viewModel.PropertyChanged += OnViewModelChanged;
        };
    }

    // Opening on the connection step puts the cursor in the first field, so keyboard and screen
    // reader users start inside the page instead of on whatever the click left behind.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName == nameof(ElasticSetupViewModel.IsOpen)
            && _viewModel is { IsOpen: true }
        )
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (_viewModel is { IsOpen: true, IsConnectionStep: true })
                        this.FindControl<TextBox>("CloudIdBox")?.Focus();
                },
                DispatcherPriority.Background
            );
    }
}
