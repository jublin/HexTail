using System.Reactive.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Domain;
using HexTail.Tests.Support;
using HexTail.ViewModels;
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

    private static async Task<(
        MainWindow Window,
        MainWindowViewModel Owner,
        string Path
    )> OpenWithRuleAsync()
    {
        var path = System.IO.Path.GetTempFileName();
        var window = TestWindow.Create(out var owner);
        window.Show();
        await owner.InitializeAsync();
        await owner.OpenPathsCommand.Execute([path]);
        await owner.State.SetSourceRulesAsync(
            owner.SelectedFile!.Model,
            [TestRules.Source("boom")]
        );
        owner.FilterBar.Sync();
        await UiWait.Until(() => FindChipBody(window) is not null);
        return (window, owner, path);
    }

    private static Button? FindChipBody(Window window) =>
        window
            .GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(button =>
                button.Classes.Contains("chip-body")
                && button.DataContext is RuleChipViewModel { IsRuleChip: true }
            );

    private static void Click(MainWindow window, Control control)
    {
        var point = control
            .TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!
            .Value;
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
    }

    [AvaloniaFact]
    public async Task ClickingARuleChipOpensTheEditorFlyoutWithItsFields()
    {
        var (window, owner, path) = await OpenWithRuleAsync();
        try
        {
            Click(window, FindChipBody(window)!);
            Dispatcher.UIThread.RunJobs();
            var chip = owner.FilterBar.Chips.Single();
            Assert.True(chip.IsEditorOpen);
            Assert.Equal("Edit boom", AutomationProperties.GetName(FindChipBody(window)!));
            var box = await FindFlyoutControlAsync<TextBox>(window, "DraftPatternBox");
            Assert.Equal("boom", box.Text);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(box)));
            await FindFlyoutControlAsync<ComboBox>(window, "DraftActionPicker");
            await FindFlyoutControlAsync<ToggleSwitch>(window, "DraftScopeSwitch");
            var swatches = await FindFlyoutControlAsync<ItemsControl>(window, "DraftSwatches");
            var buttons = swatches.GetVisualDescendants().OfType<Button>().ToList();
            Assert.Equal(MainWindowViewModel.Swatches.Count, buttons.Count);
            Assert.All(
                buttons,
                button =>
                    Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(button.Background)
            );
            Assert.All(
                buttons,
                button =>
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)))
            );
            buttons[2].Command!.Execute(buttons[2].CommandParameter);
            Assert.Equal(MainWindowViewModel.Swatches[2], chip.Draft!.Color);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ChipEditorColorPickerAndComboBoxesAreTemplatedAndOpen()
    {
        var (window, owner, path) = await OpenWithRuleAsync();
        try
        {
            Click(window, FindChipBody(window)!);
            Dispatcher.UIThread.RunJobs();
            Assert.True(owner.FilterBar.Chips.Single().IsEditorOpen);

            var colorPicker = await FindFlyoutControlAsync<ColorPicker>(window, "DraftColorPicker");
            await UiWait.Until(() =>
            {
                Dispatcher.UIThread.RunJobs();
                return colorPicker.Template is not null;
            });
            Assert.NotNull(colorPicker.Template);

            foreach (var name in new[] { "DraftActionPicker", "DraftModePicker" })
            {
                var combo = await FindFlyoutControlAsync<ComboBox>(window, name);
                await UiWait.Until(() =>
                {
                    Dispatcher.UIThread.RunJobs();
                    return combo.Template is not null;
                });
                Assert.NotNull(combo.Template);
                combo.IsDropDownOpen = true;
                Dispatcher.UIThread.RunJobs();
                Assert.True(combo.IsDropDownOpen);
                combo.IsDropDownOpen = false;
                Dispatcher.UIThread.RunJobs();
                Assert.False(combo.IsDropDownOpen);
            }
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task EnterInTheDraftBoxAppliesAndEscapeCancels()
    {
        var (window, owner, path) = await OpenWithRuleAsync();
        try
        {
            var file = owner.SelectedFile!.Model;
            Click(window, FindChipBody(window)!);
            Dispatcher.UIThread.RunJobs();
            var box = await FindFlyoutControlAsync<TextBox>(window, "DraftPatternBox");
            box.Text = "changed";
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await UiWait.Until(() => !owner.FilterBar.Chips.Single().IsEditorOpen);
            Assert.Equal("boom", file.Rules.Single().Pattern);

            Click(window, FindChipBody(window)!);
            Dispatcher.UIThread.RunJobs();
            box = await FindFlyoutControlAsync<TextBox>(window, "DraftPatternBox");
            Assert.Equal("boom", box.Text);
            box.Text = "changed";
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await UiWait.Until(() => file.Rules.Single().Pattern == "changed");
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task OpeningTheEditorFocusesThePatternSoTypingReplacesItAndEnterApplies()
    {
        var (window, owner, path) = await OpenWithRuleAsync();
        try
        {
            var file = owner.SelectedFile!.Model;
            Click(window, FindChipBody(window)!);
            var box = await FindFlyoutControlAsync<TextBox>(window, "DraftPatternBox");
            // No Focus() call here: opening the flyout has to put the caret in the pattern.
            await UiWait.Until(() =>
                ReferenceEquals(window.FocusManager!.GetFocusedElement(), box)
            );
            Assert.Equal(0, box.SelectionStart);
            Assert.Equal("boom".Length, box.SelectionEnd);

            window.KeyTextInput("fresh");
            Assert.Equal("fresh", box.Text);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await UiWait.Until(() => file.Rules.Single().Pattern == "fresh");
            await UiWait.Until(() => !EditorIsOpen(window));
            Assert.False(owner.FilterBar.Chips.Single().IsEditorOpen);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task TabInsideTheEditorMovesFocusWithinItAndDoesNotSwitchViews()
    {
        var (window, owner, path) = await OpenWithRuleAsync();
        try
        {
            var file = owner.SelectedFile!;
            owner.State.AddSearch(file.Model, "tab", MatchMode.Literal, false, "#22D3EE");
            file.SyncViews();
            file.SelectedViewIndex = 0;
            Assert.True(file.Views.Count > 1);

            Click(window, FindChipBody(window)!);
            var box = await FindFlyoutControlAsync<TextBox>(window, "DraftPatternBox");
            await UiWait.Until(() =>
                ReferenceEquals(window.FocusManager!.GetFocusedElement(), box)
            );

            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, file.SelectedViewIndex);
            var focused = Assert.IsAssignableFrom<Visual>(window.FocusManager!.GetFocusedElement());
            Assert.NotSame(box, focused);
            var flyoutContent = Assert.IsAssignableFrom<Visual>(box.Parent);
            Assert.Contains(focused, flyoutContent.GetVisualDescendants());
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task RemoveAndApplyButtonsInTheEditorCloseTheFlyout()
    {
        var (window, owner, path) = await OpenWithRuleAsync();
        try
        {
            var file = owner.SelectedFile!.Model;
            Click(window, FindChipBody(window)!);
            var apply = await FindFlyoutControlAsync<Button>(window, "DraftApplyButton");
            (await FindFlyoutControlAsync<TextBox>(window, "DraftPatternBox")).Text = "applied";
            Click(window, apply);
            await UiWait.Until(() => file.Rules.Single().Pattern == "applied");
            await UiWait.Until(() => !EditorIsOpen(window));
            Assert.False(owner.FilterBar.Chips.Single().IsEditorOpen);

            owner.FilterBar.Sync();
            await UiWait.Until(() => FindChipBody(window) is not null);
            Click(window, FindChipBody(window)!);
            var remove = await FindFlyoutControlAsync<Button>(window, "DraftRemoveButton");
            Click(window, remove);
            await UiWait.Until(() => file.Rules.Count == 0);
            await UiWait.Until(() => !EditorIsOpen(window));
            Assert.Empty(owner.FilterBar.Chips);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task SaveFilterFlyoutFocusesTheNameEnterSavesAndEscapeCloses()
    {
        var (window, owner, path) = await OpenWithRuleAsync();
        try
        {
            var button = window
                .GetVisualDescendants()
                .OfType<Button>()
                .Single(item => item.Name == "SaveFilterButton");
            Assert.True(button.IsVisible);
            Assert.Equal("Save filter", AutomationProperties.GetName(button));

            Click(window, button);
            var box = await FindFlyoutControlAsync<TextBox>(window, "SaveFilterNameBox");
            await UiWait.Until(() =>
                ReferenceEquals(window.FocusManager!.GetFocusedElement(), box)
            );
            Assert.True(owner.FilterBar.SaveFlyoutOpen);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await UiWait.Until(() => !owner.FilterBar.SaveFlyoutOpen);
            Assert.Empty(owner.State.SavedFilters);

            Click(window, button);
            box = await FindFlyoutControlAsync<TextBox>(window, "SaveFilterNameBox");
            await UiWait.Until(() =>
                ReferenceEquals(window.FocusManager!.GetFocusedElement(), box)
            );
            window.KeyTextInput("mine");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await UiWait.Until(() => owner.State.SavedFilters.Count == 1);
            await UiWait.Until(() => !owner.FilterBar.SaveFlyoutOpen);
            Assert.Equal("mine", owner.State.SavedFilters[0].Name);
            Assert.Equal(["boom"], owner.State.SavedFilters[0].Rules.Select(rule => rule.Pattern));
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task SaveFilterNeedsRulesOrALevelFilterAndReportsFailuresInTheFlyout()
    {
        var path = System.IO.Path.GetTempFileName();
        var persistence = new TestPersistence();
        var window = TestWindow.Create(persistence, out var owner);
        window.Show();
        try
        {
            await owner.InitializeAsync();
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!.Model;
            owner.FilterBar.Sync();
            Assert.False(owner.FilterBar.CanSaveFilter);
            owner.FilterBar.NewFilterName = "nothing";
            await owner.FilterBar.SaveFilterCommand.Execute();
            Assert.Empty(owner.State.SavedFilters);

            await owner.State.SetLevelsAsync(file, LogLevels.Error);
            owner.FilterBar.Sync();
            Assert.True(owner.FilterBar.CanSaveFilter);

            persistence.SaveError = new IOException("disk full");
            await owner.FilterBar.SaveFilterCommand.Execute();
            Assert.Equal("disk full", owner.FilterBar.SaveError);
            Assert.True(owner.FilterBar.HasSaveError);
            Assert.Equal("nothing", owner.FilterBar.NewFilterName);
        }
        finally
        {
            persistence.SaveError = null;
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task SavedFiltersAppearInThePaletteOnlyWithASelectedSource()
    {
        var path = System.IO.Path.GetTempFileName();
        var window = TestWindow.Create(out var owner);
        window.Show();
        try
        {
            await owner.InitializeAsync();
            await owner.State.SaveFilterAsync(
                "errors",
                [TestRules.Source("boom")],
                LogLevels.Error
            );
            Assert.DoesNotContain(owner.BuildPaletteItems(), item => item.Category == "Filters");

            await owner.OpenPathsCommand.Execute([path]);
            var item = Assert.Single(owner.BuildPaletteItems(), i => i.Category == "Filters");
            Assert.Equal("Apply filter: errors", item.Title);
            await item.Execute();
            Assert.Equal(["boom"], owner.SelectedFile!.Model.Rules.Select(rule => rule.Pattern));
            Assert.Equal(LogLevels.Error, owner.SelectedFile.Model.Levels);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    private static bool EditorIsOpen(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        return window
                .GetVisualDescendants()
                .OfType<Avalonia.Controls.Primitives.Popup>()
                .Any(popup => popup.IsOpen)
            || FlyoutRoots(window)
                .SelectMany(root => root.GetVisualDescendants().Prepend(root))
                .OfType<TextBox>()
                .Any(box => box.Name == "DraftPatternBox");
    }

    private static async Task<T> FindFlyoutControlAsync<T>(Window window, string name)
        where T : Control
    {
        T? found = null;
        await UiWait.Until(() =>
        {
            Dispatcher.UIThread.RunJobs();
            found = FlyoutRoots(window)
                .SelectMany(root => root.GetVisualDescendants().Prepend(root))
                .OfType<T>()
                .FirstOrDefault(control => control.Name == name);
            return found is not null;
        });
        return found!;
    }

    private static IEnumerable<Visual> FlyoutRoots(Window window) =>
        new Visual[] { window }.Concat(
            window
                .GetVisualDescendants()
                .OfType<Avalonia.Controls.Primitives.Popup>()
                .Select(popup => popup.Child)
                .OfType<Visual>()
        );
}
