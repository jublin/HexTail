using System.Reactive.Concurrency;
using HexTailSharp.Application;
using HexTailSharp.Persistence;
using HexTailSharp.Tailing;
using HexTailSharp.Tests.Support;
using HexTailSharp.ViewModels;
using ReactiveUI.Reactive.Builder;

namespace HexTailSharp.Tests.ViewModels;

public sealed class ElasticSettingsViewModelTests
{
    [Fact]
    public async Task Settings_ExposesElasticSectionAndManualSourceEditors()
    {
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
        var state = new AppState(new LogSourceService(), new TestPersistence());
        await using var owner = new MainWindowViewModel(
            state,
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );

        owner.Settings.SectionIndex = 4;
        owner.Settings.AddElasticConnectionCommand.Execute().Subscribe();
        var editor = Assert.Single(owner.Settings.ElasticConnections);
        editor.AddSourceCommand.Execute().Subscribe();

        Assert.Equal("elastic", owner.Settings.Section);
        Assert.Matches("^[0-9a-f]{32}$", Assert.Single(editor.Sources).Id);
    }
}
