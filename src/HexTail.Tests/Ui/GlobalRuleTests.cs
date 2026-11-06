using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HexTail.Domain;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.Ui;

public sealed class GlobalRuleTests
{
    [AvaloniaFact]
    public async Task GlobalRules_HideAndHighlightLinesAcrossTheApp()
    {
        var window = TestWindow.Create(out var owner);
        await owner.InitializeAsync();
        await owner.State.AddRuleAsync(
            null,
            TestRules.Highlight("[ERROR]") with
            {
                Mode = MatchMode.Literal,
            }
        );
        await owner.State.AddRuleAsync(
            null,
            TestRules.Hide("[ERROR]") with
            {
                Mode = MatchMode.Literal,
            }
        );
        Dispatcher.UIThread.RunJobs();

        Assert.Single(HighlightRules(owner));
        Assert.Single(HideRules(owner));
        Assert.False(IsHidden(owner, "READY"));
        Assert.True(IsHidden(owner, "[ERROR] failed"));
        Assert.NotEmpty(
            RuleSet.For(owner.State.Settings.Rules).Highlights(new Line("[ERROR] failed"))
        );
        window.Close();
        await owner.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task ChangingTheTheme_KeepsGlobalRules()
    {
        var window = TestWindow.Create(out var owner);
        await owner.InitializeAsync();
        await owner.State.AddRuleAsync(null, TestRules.Highlight("keep-me"));
        await owner.State.AddRuleAsync(null, TestRules.Hide("noise"));

        var theme = owner.Settings.ThemeOptions.First(option =>
            option.Id != owner.State.Settings.Theme
        );
        owner.Settings.SelectedTheme = theme;
        await UiWait.Until(() => owner.State.Settings.Theme == theme.Id);

        Assert.Equal("keep-me", Assert.Single(HighlightRules(owner)).Pattern);
        Assert.Equal("noise", Assert.Single(HideRules(owner)).Pattern);
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
