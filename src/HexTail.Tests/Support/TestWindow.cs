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

    /// <summary>Polling-only tailing: file-watcher Created events (macOS) otherwise reset the buffer mid-test.</summary>
    public static MainWindow CreatePolling(out MainWindowViewModel viewModel) =>
        Create(
            new TestPersistence(),
            out viewModel,
            new TailerOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(20),
                UseFileSystemWatcher = false,
            }
        );

    public static MainWindow Create(
        IAppPersistence persistence,
        out MainWindowViewModel viewModel
    ) => Create(persistence, out viewModel, null);

    private static MainWindow Create(
        IAppPersistence persistence,
        out MainWindowViewModel viewModel,
        TailerOptions? options
    )
    {
        viewModel = new MainWindowViewModel(
            new AppState(
                new LogSourceService(options),
                persistence,
                credentials: new InMemoryCredentialVault()
            ),
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        return new MainWindow(viewModel);
    }
}
