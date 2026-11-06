using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Application;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;
using HexTail.ViewModels;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class SettingsReflowTests
{
    [AvaloniaTheory]
    [InlineData(UiDensity.Compact, 14)]
    [InlineData(UiDensity.Cozy, 14)]
    [InlineData(UiDensity.Comfortable, 14)]
    [InlineData(UiDensity.Compact, 24)]
    [InlineData(UiDensity.Cozy, 24)]
    [InlineData(UiDensity.Comfortable, 24)]
    public async Task NarrowSettingsStackFieldsWithoutHorizontalPanAndRestoreWhenWidened(
        UiDensity density,
        int fontSize
    )
    {
        var settings = new AppSettings { Density = density };
        var persistence = new TestPersistence();
        await persistence.SaveAsync(new AppConfig { Settings = settings });
        var state = new AppState(
            new LogSourceService(),
            persistence,
            settings,
            elastic: new FakeElasticApiClient(),
            credentials: new InMemoryCredentialVault()
        );
        await using var owner = new MainWindowViewModel(state, startPolling: false);
        await owner.InitializeAsync();
        owner.SettingsOpen = true;
        var window = new MainWindow(owner, registerNativePicker: false);
        window.Show();
        var panel = window.GetVisualDescendants().OfType<SettingsPanel>().Single();
        panel.FontSize = fontSize;
        window.Height = 720;
        try
        {
            owner.Settings.SectionIndex = 0;
            window.Width = 1280;
            Settle(window);
            var grids = panel
                .GetVisualDescendants()
                .OfType<Grid>()
                .Where(grid =>
                    grid.Classes.Contains("responsive-columns") && grid.IsEffectivelyVisible
                )
                .ToArray();
            Assert.NotEmpty(grids);
            var original = grids.ToDictionary(
                grid => grid,
                grid =>
                    grid.Children.Select(child =>
                            (Child: child, Row: Grid.GetRow(child), Column: Grid.GetColumn(child))
                        )
                        .ToArray()
            );
            Assert.All(grids, grid => Assert.Equal(2, grid.ColumnDefinitions.Count));

            window.Width = 720;
            Settle(window);
            Assert.All(
                grids,
                grid =>
                {
                    Assert.Single(grid.ColumnDefinitions);
                    Assert.All(grid.Children, child => Assert.Equal(0, Grid.GetColumn(child)));
                }
            );
            foreach (
                var control in panel
                    .GetVisualDescendants()
                    .OfType<Control>()
                    .Where(control =>
                        control.IsEffectivelyVisible
                        && control.Bounds.Width > 0
                        && control is Button or TextBox or ComboBox or AutoCompleteBox
                    )
            )
            {
                var left = control.TranslatePoint(default, panel)!.Value.X;
                Assert.InRange(left, -1, panel.Bounds.Width);
                Assert.True(
                    left + control.Bounds.Width <= panel.Bounds.Width + 1,
                    $"{control.GetType().Name} {control.Name} extends beyond settings"
                );
            }

            window.Width = 1280;
            Settle(window);
            foreach (var grid in grids)
            {
                Assert.Equal(2, grid.ColumnDefinitions.Count);
                foreach (var placement in original[grid])
                {
                    Assert.Equal(placement.Row, Grid.GetRow(placement.Child));
                    Assert.Equal(placement.Column, Grid.GetColumn(placement.Child));
                }
            }
            window.Width = 1920;
            Settle(window);
            Assert.All(grids, grid => Assert.Equal(2, grid.ColumnDefinitions.Count));

            // A template first created at a narrow width must reflow too.
            window.Width = 720;
            owner.Settings.SectionIndex = 1;
            Settle(window);
            owner.Settings.SectionIndex = 0;
            Settle(window);
            var narrowGrids = panel
                .GetVisualDescendants()
                .OfType<Grid>()
                .Where(grid =>
                    grid.Classes.Contains("responsive-columns") && grid.IsEffectivelyVisible
                )
                .ToArray();
            Assert.NotEmpty(narrowGrids);
            Assert.All(narrowGrids, grid => Assert.Single(grid.ColumnDefinitions));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ModalIsCompactAndLimitsStayInsideNarrowWindows()
    {
        var window = TestWindow.Create(out var owner);
        try
        {
            owner.SettingsOpen = true;
            owner.Settings.SectionIndex = 1;
            window.Width = 1280;
            window.Height = 800;
            window.Show();
            Settle(window);
            var panel = window.GetVisualDescendants().OfType<SettingsPanel>().Single();
            var modal = panel
                .GetVisualDescendants()
                .OfType<Border>()
                .Single(border => border.Classes.Contains("settings-modal"));
            Assert.Equal(960, modal.Bounds.Width, 1);
            Assert.Equal(640, modal.Bounds.Height, 1);
            var tabs = panel.GetVisualDescendants().OfType<TabControl>().Single();
            Assert.Equal(Dock.Left, tabs.TabStripPlacement);

            window.Width = 720;
            window.Height = 560;
            Settle(window);
            Assert.True(modal.Bounds.Width <= window.Bounds.Width + 1);
            Assert.True(modal.Bounds.Height <= window.Bounds.Height + 1);
            var boxes = panel
                .GetVisualDescendants()
                .OfType<NumericUpDown>()
                .Where(box => box.IsEffectivelyVisible)
                .ToArray();
            Assert.Equal(4, boxes.Length);
            foreach (var box in boxes)
            {
                var left = box.TranslatePoint(default, panel)!.Value.X;
                Assert.InRange(left, -1, panel.Bounds.Width);
                Assert.True(
                    left + box.Bounds.Width <= panel.Bounds.Width + 1,
                    $"{box.Name} extends beyond settings"
                );
            }
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
        }
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
