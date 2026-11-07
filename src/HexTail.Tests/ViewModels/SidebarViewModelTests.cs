using System.IO;
using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.ViewModels;

public sealed class SidebarViewModelTests
{
    [AvaloniaFact]
    public async Task ElasticSourcesAreGroupedByConnectionInSettingsOrder()
    {
        await using var viewModel = ElasticFixtures.Workspace(
            ElasticFixtures.Connection("c-2", "staging", "billing"),
            ElasticFixtures.Connection("c-1", "prod-eu", "checkout", "billing")
        );
        await viewModel.InitializeAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["staging", "prod-eu"], viewModel.ElasticConnectionGroups.Select(g => g.Name));
        Assert.Equal(
            ["Logs · checkout", "Logs · billing"],
            viewModel.ElasticConnectionGroups[1].Sources.Select(source => source.ShortName)
        );
        Assert.All(
            viewModel.ElasticConnectionGroups[1].Sources,
            source => Assert.Equal("c-1", source.ConnectionId)
        );
    }

    [AvaloniaFact]
    public async Task AConnectionWithoutSourcesSaysSoInsteadOfCheckingForever()
    {
        await using var viewModel = ElasticFixtures.Workspace(
            new HexTail.Persistence.ElasticConnectionSettings
            {
                Id = "c-empty",
                Name = "Abandoned",
                KibanaUrl = "https://kibana/",
                ElasticsearchUrl = "https://elastic/",
            },
            ElasticFixtures.Connection("c-1", "prod", "checkout")
        );
        await viewModel.InitializeAsync();
        Dispatcher.UIThread.RunJobs();

        var empty = viewModel.ElasticConnectionGroups.Single(group => group.Id == "c-empty");

        Assert.Empty(empty.Sources);
        Assert.Equal("no sources", empty.StatusText);
        Assert.False(empty.IsConnected);
        Assert.False(empty.IsFailed);
        Assert.Equal(string.Empty, empty.StatusMessage);
        var withSources = viewModel.ElasticConnectionGroups.Single(group => group.Id == "c-1");
        Assert.NotEqual("no sources", withSources.StatusText);
    }

    [AvaloniaFact]
    public async Task ActivatingASourceOpensAndSelectsItAndCloseRemovesIt()
    {
        await using var viewModel = ElasticFixtures.Workspace(
            ElasticFixtures.Connection("c-1", "prod", "checkout")
        );
        await viewModel.InitializeAsync();
        var source = viewModel.ElasticConnectionGroups.Single().Sources.Single();

        await source.ActivateCommand.Execute();
        await UiWait.Until(() => source.IsOpen);

        Assert.True(source.IsSelected);
        Assert.True(viewModel.IsElasticSelected);
        Assert.Empty(viewModel.FileTabs);

        await source.CloseCommand.Execute();
        await UiWait.Until(() => !source.IsOpen);
        Assert.Empty(viewModel.Files);
    }

    [AvaloniaFact]
    public async Task ClosingAnElasticSourceThatFailsToPersistSurfacesOpenErrorInsteadOfThrowing()
    {
        var connection = ElasticFixtures.Connection("c-1", "prod", "checkout");
        var persistence = ElasticFixtures.Persistence(connection);
        await using var viewModel = ElasticFixtures.Workspace(persistence);
        await viewModel.InitializeAsync();
        var source = viewModel.ElasticConnectionGroups.Single().Sources.Single();

        await source.ActivateCommand.Execute();
        await UiWait.Until(() => source.IsOpen);

        persistence.SaveError = new IOException("disk full");
        try
        {
            await source.CloseCommand.Execute();

            await UiWait.Until(() => source.OpenError is not null);
            Assert.Equal("disk full", source.OpenError);
            Assert.False(source.IsOpening);
        }
        finally
        {
            persistence.SaveError = null;
        }
    }

    [AvaloniaFact]
    public async Task FileTabsProjectOnlyFileSourcesAndReportLineCounts()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllLinesAsync(path, ["a", "b", "c"]);
        try
        {
            await using var viewModel = ElasticFixtures.Workspace(
                ElasticFixtures.Connection("c-1", "prod", "checkout")
            );
            await viewModel.InitializeAsync();
            await viewModel.OpenPathsCommand.Execute([path]);
            await viewModel.ElasticConnectionGroups[0].Sources[0].ActivateCommand.Execute();
            await UiWait.Until(() =>
            {
                viewModel.State.DrainTailerEvents();
                return viewModel.FileTabs.Count == 1 && viewModel.FileTabs[0].LineCountText == "3";
            });

            Assert.Equal(2, viewModel.Files.Count);
            Assert.True(viewModel.HasFileTabs);
            Assert.False(viewModel.FileTabs[0].HasError);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(12_400, "12.4k")]
    [InlineData(999_999, "999.9k")]
    [InlineData(1_250_000, "1.2M")]
    public void CompactCountIsShortAndNeverRoundsUp(int count, string expected) =>
        Assert.Equal(expected, FileTabViewModel.CompactCount(count));
}
