using HexTail.Application;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.ViewModels;

public sealed class SettingsSectionTests
{
    [Fact]
    public async Task SectionsAreAppearanceThenElasticAndClampToThoseTwo()
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
        Assert.Equal("elastic", settings.Section);

        // A stale index from the removed third section must not crash or select nothing.
        settings.SectionIndex = 2;
        Assert.Equal(1, settings.SectionIndex);
        Assert.Equal("elastic", settings.Section);

        settings.SectionIndex = -1;
        Assert.Equal(0, settings.SectionIndex);
        Assert.Equal("appearance", settings.Section);
    }

    [Fact]
    public async Task SetUpElasticOpensTheElasticSection()
    {
        var state = new AppState(
            new LogSourceService(),
            new TestPersistence(),
            credentials: new InMemoryCredentialVault()
        );
        await using var owner = new MainWindowViewModel(state, startPolling: false);

        owner.SetUpElasticCommand.Execute().Subscribe();

        Assert.True(owner.SettingsOpen);
        Assert.Equal(1, owner.Settings.SectionIndex);
        Assert.Equal("elastic", owner.Settings.Section);
    }
}
