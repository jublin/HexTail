using HexTail.Application;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.ViewModels;

public sealed class SettingsSectionTests
{
    [Fact]
    public async Task SectionsAreAppearanceThenLimitsAndClampToThoseTwo()
    {
        var state = new AppState(
            new LogSourceService(),
            new TestPersistence(),
            credentials: new InMemoryCredentialVault()
        );
        await using var owner = new MainWindowViewModel(state, startPolling: false);
        var settings = owner.Settings;

        Assert.Equal(0, settings.SectionIndex);
        Assert.Equal("appearance", settings.Section);

        settings.SectionIndex = 1;
        Assert.Equal("limits", settings.Section);

        // A stale index beyond the last section must not crash or select nothing.
        settings.SectionIndex = 5;
        Assert.Equal(1, settings.SectionIndex);
        Assert.Equal("limits", settings.Section);

        settings.SectionIndex = -1;
        Assert.Equal(0, settings.SectionIndex);
        Assert.Equal("appearance", settings.Section);
    }

    [Fact]
    public async Task SetUpElasticOpensTheSetupFlow()
    {
        var state = new AppState(
            new LogSourceService(),
            new TestPersistence(),
            credentials: new InMemoryCredentialVault()
        );
        await using var owner = new MainWindowViewModel(state, startPolling: false);

        owner.SetUpElasticCommand.Execute().Subscribe();

        Assert.True(owner.ElasticSetup.IsOpen);
        Assert.False(owner.SettingsOpen);
    }
}
