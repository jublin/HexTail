using System.Reactive.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Domain;
using HexTail.Tests.Support;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class FilterBarTests
{
    [AvaloniaFact]
    public async Task ChipRowRendersEveryChipWithAnAccessibleName()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var owner);
            window.Show();
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            await owner.State.SetSourceRulesAsync(
                owner.SelectedFile!.Model,
                [TestRules.Source("boom"), TestRules.Source("noise", RuleAction.Hide)]
            );
            Dispatcher.UIThread.RunJobs();
            owner.FilterBar.Sync();
            await UiWait.Until(() => window.GetVisualDescendants().OfType<FilterBar>().Any());
            List<string> ChipNames() =>
                window
                    .GetVisualDescendants()
                    .OfType<Control>()
                    .Where(control => control.Name == "RuleChip")
                    .Select(control => AutomationProperties.GetName(control) ?? string.Empty)
                    .ToList();
            await UiWait.Until(() => ChipNames().Count == 2);
            var names = ChipNames();
            Assert.Contains(names, name => name.Contains("boom") && name.Contains("Highlight"));
            Assert.Contains(names, name => name.Contains("noise") && name.Contains("Hide"));
            window.Close();
            await owner.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
