using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HexTail.Domain;
using HexTail.Persistence;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.Ui;

public sealed class GlobalRuleTests
{
    [AvaloniaFact]
    public async Task NewGlobalRules_DefaultToLiteralAndKeepSavedModesAfterTextEdits()
    {
        var window = TestWindow.Create(out var owner);
        await owner.InitializeAsync();
        owner.Settings.NewLabelText = "[ERROR]";
        owner.Settings.NewExclusionText = "[ERROR]";
        owner.Settings.AddLabelCommand.Execute().Subscribe();
        owner.Settings.AddExclusionCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(MatchMode.Literal, Assert.Single(HighlightRules(owner)).Mode);
        Assert.Equal(MatchMode.Literal, Assert.Single(HideRules(owner)).Mode);
        Assert.False(IsHidden(owner, "READY"));
        Assert.True(IsHidden(owner, "[ERROR] failed"));

        var exclusion = Assert.Single(owner.Settings.Exclusions);
        exclusion.Text = "health|probe";
        Dispatcher.UIThread.RunJobs();
        var edited = Assert.Single(HideRules(owner));
        Assert.Equal("health|probe", edited.Pattern);
        Assert.Equal(MatchMode.Literal, edited.Mode);
        Assert.False(IsHidden(owner, "health"));
        exclusion.Mode = MatchMode.Regex;
        Dispatcher.UIThread.RunJobs();
        Assert.True(IsHidden(owner, "health"));
        Assert.DoesNotContain(HideRules(owner), rule => rule.Pattern == "[ERROR]");
        window.Close();
        await owner.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task InvalidRegexDrafts_AreVisibleAndDoNotReplaceSavedRules()
    {
        var persistence = new TestPersistence();
        await persistence.SaveAsync(
            new AppConfig
            {
                Settings = new AppSettings
                {
                    Rules = [TestRules.Highlight("ERROR|WARN"), TestRules.Hide("health|probe")],
                },
            }
        );
        var window = TestWindow.Create(persistence, out var owner);
        await owner.InitializeAsync();
        var label = Assert.Single(owner.Settings.Labels);
        var exclusion = Assert.Single(owner.Settings.Exclusions);
        label.Text = "[";
        exclusion.Text = "[";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("ERROR|WARN", Assert.Single(HighlightRules(owner)).Pattern);
        Assert.Equal("health|probe", Assert.Single(HideRules(owner)).Pattern);
        Assert.Equal("[", label.Text);
        Assert.Equal("[", exclusion.Text);
        Assert.False(string.IsNullOrEmpty(label.ValidationError));
        Assert.False(string.IsNullOrEmpty(exclusion.ValidationError));

        owner.Settings.NewLabelText = "[";
        owner.Settings.NewExclusionText = "[";
        owner.Settings.NewLabelMode = MatchMode.Regex;
        owner.Settings.NewExclusionMode = MatchMode.Regex;
        owner.Settings.AddLabelCommand.Execute().Subscribe();
        owner.Settings.AddExclusionCommand.Execute().Subscribe();
        Dispatcher.UIThread.RunJobs();
        Assert.Single(HighlightRules(owner));
        Assert.Single(HideRules(owner));
        Assert.Equal("[", owner.Settings.NewLabelText);
        Assert.Equal("[", owner.Settings.NewExclusionText);
        Assert.False(string.IsNullOrEmpty(owner.Settings.NewLabelError));
        Assert.False(string.IsNullOrEmpty(owner.Settings.NewExclusionError));
        label.Mode = MatchMode.Literal;
        exclusion.Mode = MatchMode.Literal;
        Dispatcher.UIThread.RunJobs();
        Assert.Null(label.ValidationError);
        Assert.Null(exclusion.ValidationError);
        Assert.Equal("[", Assert.Single(HighlightRules(owner)).Pattern);
        Assert.True(IsHidden(owner, "[literal"));
        Assert.False(IsHidden(owner, "ERROR"));
        window.Close();
        await owner.DisposeAsync();
    }

    private static List<ViewRule> HighlightRules(MainWindowViewModel owner) =>
        [.. owner.State.Settings.Rules.Where(rule => rule.Action == RuleAction.Highlight)];

    private static List<ViewRule> HideRules(MainWindowViewModel owner) =>
        [.. owner.State.Settings.Rules.Where(rule => rule.Action == RuleAction.Hide)];

    private static bool IsHidden(MainWindowViewModel owner, string text) =>
        !RuleSet.For(owner.State.Settings.Rules).IsVisible(new Line(text));
}
