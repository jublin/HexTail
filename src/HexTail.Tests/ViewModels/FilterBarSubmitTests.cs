using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Domain;
using HexTail.Persistence;
using HexTail.Tests.Support;

namespace HexTail.Tests.ViewModels;

public sealed class FilterBarSubmitTests
{
    [AvaloniaFact]
    public async Task SubmitCreatesSourceRulesForEachAction()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var owner);
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            var model = owner.SelectedFile!.Model;
            var bar = owner.FilterBar;

            bar.Query = "boom";
            await bar.SubmitAsync(RuleAction.Highlight);
            bar.Query = "slow";
            await bar.SubmitAsync(RuleAction.ShowOnly);
            bar.Query = "health";
            await bar.SubmitAsync(RuleAction.Hide);

            Assert.Equal(
                [
                    ("boom", RuleAction.Highlight),
                    ("slow", RuleAction.ShowOnly),
                    ("health", RuleAction.Hide),
                ],
                model.Rules.Select(rule => (rule.Pattern, rule.Action))
            );
            Assert.All(model.Rules, rule => Assert.Equal(RuleScope.Source, rule.Scope));
            Assert.Equal(
                model.Rules.Count,
                model.Rules.Select(rule => rule.Color).Distinct().Count()
            );
            Assert.Equal(string.Empty, bar.Query);
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task PinAsTabMakesAPlainEnterCreateATabSearchButNotShiftEnter()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var owner);
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            var model = owner.SelectedFile!.Model;
            owner.FilterBar.PinAsTab = true;
            owner.FilterBar.Query = "tabbed";
            await owner.FilterBar.SubmitAsync(RuleAction.Highlight);
            Assert.Equal(["tabbed"], model.Searches.Select(search => search.Query.Query));
            Assert.Empty(model.Rules);
            owner.FilterBar.Query = "narrow";
            await owner.FilterBar.SubmitAsync(RuleAction.ShowOnly);
            Assert.Equal(["narrow"], model.Rules.Select(rule => rule.Pattern));
            Assert.Single(model.Searches);
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task InvalidRegexSetsAnErrorAndCreatesNothing()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var owner);
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            owner.FilterBar.MatchMode = MatchMode.Regex;
            owner.FilterBar.Query = "[";
            await owner.FilterBar.SubmitAsync(RuleAction.Highlight);
            Assert.True(owner.FilterBar.HasError);
            Assert.Empty(owner.SelectedFile!.Model.Rules);
            Assert.Equal("[", owner.FilterBar.Query);
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ShiftEnterAndAltEnterInTheInputPickTheAction()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var owner);
            window.Show();
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            Dispatcher.UIThread.RunJobs();
            var box = window
                .GetVisualDescendants()
                .OfType<TextBox>()
                .Single(b => b.Name == "QueryBox");
            box.Focus();
            owner.FilterBar.Query = "one";
            window.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, null);
            await UiWait.Until(() => owner.SelectedFile!.Model.Rules.Count == 1);
            owner.FilterBar.Query = "two";
            window.KeyPress(Key.Enter, RawInputModifiers.Alt, PhysicalKey.Enter, null);
            await UiWait.Until(() => owner.SelectedFile!.Model.Rules.Count == 2);
            owner.FilterBar.Query = "three";
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await UiWait.Until(() => owner.SelectedFile!.Model.Rules.Count == 3);
            Assert.Equal(
                [RuleAction.ShowOnly, RuleAction.Hide, RuleAction.Highlight],
                owner.SelectedFile!.Model.Rules.Select(rule => rule.Action)
            );
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task SubmitDoesNothingWithoutASourceOrWithABlankQuery()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var owner);
            await owner.InitializeAsync();
            owner.FilterBar.Query = "orphan";
            await owner.FilterBar.SubmitAsync(RuleAction.Highlight);
            Assert.Equal("orphan", owner.FilterBar.Query);
            await owner.OpenPathsCommand.Execute([path]);
            owner.FilterBar.Query = "   ";
            await owner.FilterBar.SubmitAsync(RuleAction.Highlight);
            Assert.Empty(owner.SelectedFile!.Model.Rules);
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task SaveFailureSurfacesAsErrorAndKeepsTheQuery()
    {
        var path = Path.GetTempFileName();
        try
        {
            var persistence = new TestPersistence();
            var window = TestWindow.Create(persistence, out var owner);
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            var bar = owner.FilterBar;
            persistence.SaveError = new IOException("disk full");
            bar.Query = "  boom  ";
            await bar.SubmitAsync(RuleAction.Highlight);
            Assert.True(bar.HasError);
            Assert.Equal("disk full", bar.Error);
            Assert.Equal("  boom  ", bar.Query);
            persistence.SaveError = null;
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ConcurrentSubmitsOfTheSameQueryCreateExactlyOneRule()
    {
        var path = Path.GetTempFileName();
        try
        {
            var persistence = new GatedPersistence();
            var window = TestWindow.Create(persistence, out var owner);
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            var bar = owner.FilterBar;
            persistence.Gate = new TaskCompletionSource();
            bar.Query = "boom";
            var first = bar.SubmitAsync(RuleAction.Highlight);
            var second = bar.SubmitAsync(RuleAction.Highlight);
            persistence.Gate.SetResult();
            await Task.WhenAll(first, second);
            Assert.Equal(["boom"], owner.SelectedFile!.Model.Rules.Select(rule => rule.Pattern));
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task SecondSubmitWhileFirstIsSavingIsIgnoredEvenWithTheSameQueryRetyped()
    {
        var path = Path.GetTempFileName();
        try
        {
            var persistence = new GatedPersistence();
            var window = TestWindow.Create(persistence, out var owner);
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            var bar = owner.FilterBar;
            persistence.Gate = new TaskCompletionSource();
            bar.Query = "boom";
            var first = bar.SubmitAsync(RuleAction.Highlight);
            Assert.Equal(string.Empty, bar.Query);
            bar.Query = "boom";
            await bar.SubmitAsync(RuleAction.Highlight);
            persistence.Gate.SetResult();
            await first;
            Assert.Single(owner.SelectedFile!.Model.Rules);
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task EditingQueryOrMatchModeClearsTheError()
    {
        var window = TestWindow.Create(out var owner);
        await owner.InitializeAsync();
        var bar = owner.FilterBar;
        bar.Error = "oops";
        bar.Query = "x";
        Assert.False(bar.HasError);
        bar.Error = "oops";
        bar.MatchMode = MatchMode.Regex;
        Assert.False(bar.HasError);
        window.Close();
        await owner.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task StoredPatternIsTrimmed()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var owner);
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            owner.FilterBar.Query = "  padded \t";
            await owner.FilterBar.SubmitAsync(RuleAction.Highlight);
            Assert.Equal(["padded"], owner.SelectedFile!.Model.Rules.Select(rule => rule.Pattern));
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task AltEnterIgnoresPinAsTab()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var owner);
            window.Show();
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            Dispatcher.UIThread.RunJobs();
            var box = window
                .GetVisualDescendants()
                .OfType<TextBox>()
                .Single(b => b.Name == "QueryBox");
            box.Focus();
            owner.FilterBar.PinAsTab = true;
            owner.FilterBar.Query = "hidden";
            window.KeyPress(Key.Enter, RawInputModifiers.Alt, PhysicalKey.Enter, null);
            await UiWait.Until(() => owner.SelectedFile!.Model.Rules.Count == 1);
            Assert.Equal(RuleAction.Hide, owner.SelectedFile!.Model.Rules[0].Action);
            Assert.Empty(owner.SelectedFile.Model.Searches);
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ControlEnterDoesNotSubmit()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var owner);
            window.Show();
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            Dispatcher.UIThread.RunJobs();
            var box = window
                .GetVisualDescendants()
                .OfType<TextBox>()
                .Single(b => b.Name == "QueryBox");
            box.Focus();
            owner.FilterBar.Query = "keep";
            window.KeyPress(Key.Enter, RawInputModifiers.Control, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
            Assert.Empty(owner.SelectedFile!.Model.Rules);
            Assert.Equal("keep", owner.FilterBar.Query);
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class GatedPersistence : IAppPersistence
    {
        public TaskCompletionSource? Gate { get; set; }

        public ValueTask<AppConfig?> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AppConfig?>(new AppConfig());

        public async ValueTask SaveAsync(
            AppConfig config,
            CancellationToken cancellationToken = default
        )
        {
            if (Gate is { } gate)
                await gate.Task;
        }
    }
}
