using System.Reactive.Concurrency;
using HexTailSharp.Application;
using HexTailSharp.Persistence;
using HexTailSharp.Tailing;
using HexTailSharp.ViewModels;

namespace HexTailSharp.Tests.Support;

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
