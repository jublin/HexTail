using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HexTail.Domain;
using HexTail.Persistence;
using HexTail.Tests.Support;

namespace HexTail.Tests.ViewModels;

public sealed class FilterBarChipTests
{
    [AvaloniaFact]
    public async Task ChipsListGlobalThenSourceThenPinnedTabsAndSkipPinnedGlobalSearches()
    {
        var path = Path.GetTempFileName();
        try
        {
            var persistence = new TestPersistence();
            await persistence.SaveAsync(
                new AppConfig
                {
                    Settings = new AppSettings
                    {
                        Rules =
                        [
                            TestRules.Highlight("global-hit", pinned: true),
                            TestRules.Hide("noise"),
                        ],
                    },
                }
            );
            var window = TestWindow.Create(persistence, out var owner);
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            await owner.State.SetSourceRulesAsync(file.Model, [TestRules.Source("src-hit")]);
            owner.State.AddSearch(file.Model, "tab-hit", MatchMode.Literal, false, "#22D3EE");
            Dispatcher.UIThread.RunJobs();
            owner.FilterBar.Sync();

            Assert.Equal(
                ["global-hit", "noise", "src-hit", "tab-hit"],
                owner.FilterBar.Chips.Select(chip => chip.Pattern)
            );
            Assert.Equal(
                [true, true, false, false],
                owner.FilterBar.Chips.Select(chip => chip.IsGlobal)
            );
            Assert.True(owner.FilterBar.Chips[3].IsPinnedTab);
            Assert.False(owner.FilterBar.Chips[1].HasSwatch);
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task TogglingAndRemovingChipsEditsTheOwningRuleList()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var owner);
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            await owner.State.SetSourceRulesAsync(file.Model, [TestRules.Source("a")]);
            owner.FilterBar.Sync();

            await owner.FilterBar.Chips[0].ToggleEnabledCommand.Execute();
            Assert.False(file.Model.Rules[0].Enabled);
            owner.FilterBar.Sync();
            Assert.True(owner.FilterBar.Chips[0].IsDisabled);

            await owner.FilterBar.Chips[0].RemoveCommand.Execute();
            Assert.Empty(file.Model.Rules);
            owner.FilterBar.Sync();
            Assert.Empty(owner.FilterBar.Chips);
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ActivatingAPinnedTabChipSelectsItsView()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var owner);
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            owner.State.AddSearch(file.Model, "tab", MatchMode.Literal, false, "#22D3EE");
            file.SyncViews();
            owner.FilterBar.Sync();
            Assert.Equal(0, file.SelectedViewIndex);
            await owner.FilterBar.Chips[0].ActivateCommand.Execute();
            Assert.Equal(1, file.SelectedViewIndex);
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
