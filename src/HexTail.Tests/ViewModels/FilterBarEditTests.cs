using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HexTail.Domain;
using HexTail.Persistence;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.ViewModels;

public sealed class FilterBarEditTests
{
    /// <summary>Opens a temp file with one source rule <paramref name="pattern"/> and runs <paramref name="body"/> on its chip.</summary>
    private static async Task WithRuleChipAsync(
        string pattern,
        Func<MainWindowViewModel, FileTabViewModel, RuleChipViewModel, Task> body,
        IAppPersistence? persistence = null
    )
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(persistence ?? new TestPersistence(), out var owner);
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            await owner.State.SetSourceRulesAsync(file.Model, [TestRules.Source(pattern)]);
            owner.FilterBar.Sync();
            await body(owner, file, owner.FilterBar.Chips.Single());
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public Task ApplyingADraftMovesTheRuleToAnotherScope() =>
        WithRuleChipAsync(
            "a",
            async (owner, file, chip) =>
            {
                chip.OpenEditor();
                Assert.True(chip.IsEditorOpen);
                chip.Draft!.Pattern = "b";
                chip.Draft.AllSources = true;

                await chip.ApplyCommand.Execute();

                Assert.Empty(file.Model.Rules);
                Assert.Equal("b", owner.State.Settings.Rules.Single().Pattern);
                Assert.False(chip.IsEditorOpen);
            }
        );

    [AvaloniaFact]
    public Task ApplyingAnInvalidDraftChangesNothingAndKeepsTheEditorOpen() =>
        WithRuleChipAsync(
            "a",
            async (_, file, chip) =>
            {
                chip.OpenEditor();
                chip.Draft!.Pattern = "  ";

                await chip.ApplyCommand.Execute();

                Assert.Equal("a", file.Model.Rules.Single().Pattern);
                Assert.True(chip.IsEditorOpen);
            }
        );

    [AvaloniaFact]
    public Task CancelLeavesRulesUntouchedAndClosesTheEditor() =>
        WithRuleChipAsync(
            "a",
            async (_, file, chip) =>
            {
                chip.OpenEditor();
                chip.Draft!.Pattern = "changed";

                await chip.CancelCommand.Execute();

                Assert.False(chip.IsEditorOpen);
                Assert.Equal("a", file.Model.Rules.Single().Pattern);
                chip.OpenEditor();
                Assert.Equal("a", chip.Draft!.Pattern);
            }
        );

    [AvaloniaFact]
    public async Task ApplyFailureShowsInTheDraftKeepsTheEditorOpenAndClearsOnEdit()
    {
        var persistence = new TestPersistence();
        await WithRuleChipAsync(
            "a",
            async (owner, file, chip) =>
            {
                chip.OpenEditor();
                var draft = chip.Draft!;
                draft.AllSources = true;
                draft.Pattern = "b";
                owner.FilterBar.Error = "stale";
                persistence.SaveError = new IOException("disk full");

                await chip.ApplyCommand.Execute();

                Assert.Equal("disk full", draft.SaveError);
                Assert.True(draft.HasSaveError);
                Assert.True(chip.IsEditorOpen);
                Assert.Null(owner.FilterBar.Error);

                draft.Pattern = "c";
                Assert.Null(draft.SaveError);

                persistence.SaveError = null;
                await chip.ApplyCommand.Execute();
                Assert.Null(draft.SaveError);
                Assert.False(chip.IsEditorOpen);
                Assert.Contains(owner.State.Settings.Rules, rule => rule.Pattern == "c");
            },
            persistence
        );
    }

    [AvaloniaFact]
    public Task RemovingFromTheEditorRemovesTheRuleAndClosesIt() =>
        WithRuleChipAsync(
            "a",
            async (owner, file, chip) =>
            {
                chip.OpenEditor();

                await chip.RemoveCommand.Execute();

                Assert.Empty(file.Model.Rules);
                Assert.False(chip.IsEditorOpen);
            }
        );

    [AvaloniaFact]
    public async Task PinnedTabChipsHaveNoEditorAndAreNamedAsTabSelectors()
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
            var chip = owner.FilterBar.Chips.Single();
            Assert.Null(chip.Draft);
            Assert.False(chip.IsRuleChip);
            Assert.Equal("Select tab tab", chip.BodyAutomationName);
            chip.OpenEditor();
            Assert.False(chip.IsEditorOpen);
            await chip.ApplyCommand.Execute();
            Assert.Single(file.Model.Searches);
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task UnrelatedSyncKeepsTheOpenEditorChipInstance()
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
            var chipA = owner.FilterBar.Chips.Single();
            chipA.OpenEditor();
            var draft = chipA.Draft;

            await owner.State.SetSourceRulesAsync(
                file.Model,
                [TestRules.Source("b"), TestRules.Source("a")]
            );
            Dispatcher.UIThread.RunJobs();
            owner.FilterBar.Sync();

            Assert.Equal(["b", "a"], owner.FilterBar.Chips.Select(chip => chip.Pattern));
            Assert.Same(chipA, owner.FilterBar.Chips[1]);
            Assert.True(chipA.IsEditorOpen);
            Assert.Same(draft, chipA.Draft);

            await owner.State.SetSourceRulesAsync(file.Model, [TestRules.Source("a")]);
            owner.FilterBar.Sync();
            Assert.Same(chipA, owner.FilterBar.Chips.Single());
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task EditingARuleRebuildsItsChip()
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
            var before = owner.FilterBar.Chips.Single();
            await before.ToggleEnabledCommand.Execute();
            owner.FilterBar.Sync();
            var after = owner.FilterBar.Chips.Single();
            Assert.NotSame(before, after);
            Assert.True(after.IsDisabled);
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
