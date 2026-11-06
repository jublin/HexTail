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
    public async Task EditingTheTextOfASavedRegexRuleKeepsItRegex()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var owner);
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            await owner.State.SetSourceRulesAsync(
                file.Model,
                [TestRules.Source("err.r") with { Mode = MatchMode.Regex }]
            );
            owner.FilterBar.Sync();
            var chip = owner.FilterBar.Chips.Single();

            chip.OpenEditor();
            Assert.Equal(MatchMode.Regex, chip.Draft!.MatchMode);
            chip.Draft.Pattern = "warn.+";
            await chip.ApplyCommand.Execute();

            var rule = Assert.Single(file.Model.Rules);
            Assert.Equal("warn.+", rule.Pattern);
            Assert.Equal(MatchMode.Regex, rule.Mode);
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

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

    [AvaloniaFact]
    public async Task ToggleFailureSetsTheBarErrorWithoutAnUnobservedException()
    {
        var persistence = new TestPersistence();
        await WithRuleChipAsync(
            "a",
            async (owner, file, chip) =>
            {
                var thrown = new List<Exception>();
                chip.ToggleEnabledCommand.ThrownExceptions.Subscribe(thrown.Add);
                persistence.SaveError = new IOException("disk full");

                await chip.ToggleEnabledCommand.Execute();

                persistence.SaveError = null;
                Assert.Empty(thrown);
                Assert.Equal("disk full", owner.FilterBar.Error);
                Assert.True(owner.FilterBar.HasError);
                // The change is applied in memory; only persisting it failed.
                Assert.False(file.Model.Rules.Single().Enabled);

                owner.FilterBar.Sync();
                await owner.FilterBar.Chips.Single().ToggleEnabledCommand.Execute();
                Assert.Null(owner.FilterBar.Error);
                Assert.True(file.Model.Rules.Single().Enabled);
            },
            persistence
        );
    }

    [AvaloniaFact]
    public async Task RemoveFailureSetsTheBarErrorClosesTheEditorWithoutAnUnobservedException()
    {
        var persistence = new TestPersistence();
        await WithRuleChipAsync(
            "a",
            async (owner, file, chip) =>
            {
                var thrown = new List<Exception>();
                chip.RemoveCommand.ThrownExceptions.Subscribe(thrown.Add);
                chip.OpenEditor();
                persistence.SaveError = new UnauthorizedAccessException("read-only");

                await chip.RemoveCommand.Execute();

                persistence.SaveError = null;
                Assert.Empty(thrown);
                Assert.Equal("read-only", owner.FilterBar.Error);
                Assert.Empty(file.Model.Rules);
                Assert.False(chip.IsEditorOpen);
            },
            persistence
        );
    }

    [AvaloniaFact]
    public async Task RemovingAPinnedTabChipFailureIsReportedOnTheBar()
    {
        var path = Path.GetTempFileName();
        try
        {
            var persistence = new TestPersistence();
            var window = TestWindow.Create(persistence, out var owner);
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            owner.State.AddSearch(file.Model, "tab", MatchMode.Literal, false, "#22D3EE");
            file.SyncViews();
            owner.FilterBar.Sync();
            var chip = owner.FilterBar.Chips.Single();
            var thrown = new List<Exception>();
            chip.RemoveCommand.ThrownExceptions.Subscribe(thrown.Add);
            persistence.SaveError = new IOException("disk full");

            await chip.RemoveCommand.Execute();

            persistence.SaveError = null;
            Assert.Empty(thrown);
            Assert.Equal("disk full", owner.FilterBar.Error);
            Assert.Empty(file.Model.Searches);
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task AChipActsOnItsOwnSourceEvenAfterAnotherSourceIsSelected()
    {
        var pathA = Path.GetTempFileName();
        var pathB = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var owner);
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([pathA]);
            var fileA = owner.SelectedFile!;
            await owner.OpenPathsCommand.Execute([pathB]);
            var fileB = owner.SelectedFile!;
            Assert.NotSame(fileA, fileB);
            await owner.State.SetSourceRulesAsync(fileA.Model, [TestRules.Source("a-only")]);
            await owner.State.AddRuleAsync(fileA.Model, TestRules.Highlight("everywhere"));
            owner.State.SelectFile(fileA.Model);
            owner.FilterBar.Sync();
            Assert.Same(fileA, owner.SelectedFile);
            var globalOnA = owner.FilterBar.Chips.Single(chip => chip.IsGlobal);

            RuleChipViewModel ChipForAWhileBIsSelected()
            {
                owner.State.SelectFile(fileA.Model);
                owner.FilterBar.Sync();
                var chip = owner.FilterBar.Chips.Single(chip => !chip.IsGlobal);
                owner.State.SelectFile(fileB.Model);
                owner.FilterBar.Sync();
                Assert.Same(fileB, owner.SelectedFile);
                return chip;
            }

            var toggled = ChipForAWhileBIsSelected();
            // Every chip is rebuilt for the new source, global ones included.
            var globalOnB = owner.FilterBar.Chips.Single();
            Assert.NotSame(globalOnA, globalOnB);
            await toggled.ToggleEnabledCommand.Execute();
            Assert.False(fileA.Model.Rules.Single().Enabled);
            Assert.Empty(fileB.Model.Rules);

            await owner.State.SetSourceRulesAsync(fileA.Model, [TestRules.Source("a-only")]);
            var edited = ChipForAWhileBIsSelected();
            edited.OpenEditor();
            edited.Draft!.Pattern = "renamed";
            await edited.ApplyCommand.Execute();
            Assert.Equal("renamed", fileA.Model.Rules.Single().Pattern);
            Assert.Empty(fileB.Model.Rules);

            var removed = ChipForAWhileBIsSelected();
            await removed.RemoveCommand.Execute();
            Assert.Empty(fileA.Model.Rules);
            Assert.Empty(fileB.Model.Rules);
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(pathA);
            File.Delete(pathB);
        }
    }
}
