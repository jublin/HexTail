using System.Reactive.Concurrency;
using HexTail.Application;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.ViewModels;

namespace HexTail.Tests.Support;

internal static class TestWindow
{
    public static MainWindow Create(out MainWindowViewModel viewModel) =>
        Create(new TestPersistence(), out viewModel);

    public static MainWindow Create(IAppPersistence persistence, out MainWindowViewModel viewModel)
    {
        viewModel = new MainWindowViewModel(
            new AppState(new LogSourceService(), persistence),
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        return new MainWindow(viewModel);
    }
}
