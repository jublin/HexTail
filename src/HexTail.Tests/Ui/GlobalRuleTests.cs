using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HexTail.Domain;
using HexTail.Persistence;
using HexTail.Tests.Support;

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
        Assert.Equal(MatchMode.Literal, Assert.Single(owner.State.Settings.GlobalLabels).Mode);
        Assert.Equal(MatchMode.Literal, owner.State.Settings.GetExcludeMode("[ERROR]"));
        Assert.False(owner.State.Settings.Excludes("READY"));
        Assert.True(owner.State.Settings.Excludes("[ERROR] failed"));

        var exclusion = Assert.Single(owner.Settings.Exclusions);
        exclusion.Text = "health|probe";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(MatchMode.Literal, owner.State.Settings.GetExcludeMode("health|probe"));
        Assert.False(owner.State.Settings.Excludes("health"));
        exclusion.Mode = MatchMode.Regex;
        Dispatcher.UIThread.RunJobs();
        Assert.True(owner.State.Settings.Excludes("health"));
        Assert.DoesNotContain("[ERROR]", owner.State.Settings.GlobalExcludeModes.Keys);
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
                    GlobalLabels = [new GlobalLabel { Text = "ERROR|WARN" }],
                    GlobalExcludeLabels = ["health|probe"],
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

        Assert.Equal("ERROR|WARN", Assert.Single(owner.State.Settings.GlobalLabels).Text);
        Assert.Equal("health|probe", Assert.Single(owner.State.Settings.GlobalExcludeLabels));
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
        Assert.Single(owner.State.Settings.GlobalLabels);
        Assert.Single(owner.State.Settings.GlobalExcludeLabels);
        Assert.Equal("[", owner.Settings.NewLabelText);
        Assert.Equal("[", owner.Settings.NewExclusionText);
        Assert.False(string.IsNullOrEmpty(owner.Settings.NewLabelError));
        Assert.False(string.IsNullOrEmpty(owner.Settings.NewExclusionError));
        label.Mode = MatchMode.Literal;
        exclusion.Mode = MatchMode.Literal;
        Dispatcher.UIThread.RunJobs();
        Assert.Null(label.ValidationError);
        Assert.Null(exclusion.ValidationError);
        Assert.Equal("[", Assert.Single(owner.State.Settings.GlobalLabels).Text);
        Assert.True(owner.State.Settings.Excludes("[literal"));
        Assert.False(owner.State.Settings.Excludes("ERROR"));
        window.Close();
        await owner.DisposeAsync();
    }
}
