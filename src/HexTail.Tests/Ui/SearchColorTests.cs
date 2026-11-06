using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using HexTail.Application;
using HexTail.Domain;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.Ui;

public sealed class SearchColorTests
{
    [Fact]
    public void NextSearchColorSkipsActiveAndGlobalColors()
    {
        var color = MainWindowViewModel.NextSearchColor(["#F59E0B", "#22D3EE"], ["#A78BFA"]);

        Assert.Equal("#34D399", color);
    }

    [AvaloniaFact]
    public async Task HideRulesDoNotReserveTheirUnusedDefaultColor()
    {
        var path = Path.GetTempFileName();
        try
        {
            var settings = new AppSettings { Rules = [TestRules.Hide("healthz")] };
            await using var state = new AppState(
                new LogSourceService(),
                new TestPersistence(),
                settings,
                credentials: new InMemoryCredentialVault()
            );
            await using var viewModel = new MainWindowViewModel(
                state,
                scheduler: System.Reactive.Concurrency.ImmediateScheduler.Instance,
                startPolling: false
            );
            await viewModel.OpenPathsCommand.Execute([path]);
            viewModel.Query = "needle";
            viewModel.SearchColor = Color.Parse("#22D3EE");

            await viewModel.AddSearchCommand.Execute();

            // Amber is the Hide rule's placeholder color; it is still free for a new search.
            Assert.Equal(Color.Parse("#F59E0B"), viewModel.SearchColor);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
