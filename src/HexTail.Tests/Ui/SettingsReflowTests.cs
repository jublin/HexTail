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
        var settings = new AppSettings
        {
            Density = density,
            ElasticConnections =
            [
                new ElasticConnectionSettings
                {
                    Id = "connection",
                    Name = "Operations",
                    KibanaUrl = "https://kibana.example.com/",
                    ElasticsearchUrl = "https://elastic.example.com/",
                    AuthMode = ElasticAuthMode.Basic,
                    Username = "reader",
                    Views =
                    [
                        new ElasticViewSettings
                        {
                            Id = "view",
                            Name = "Application logs",
                            DataViewId = "logs",
                            DataViewTitle = "application-logs-*",
                            TimeFieldName = "@timestamp",
                            ServerField = "server",
                            OutputFields = ["message"],
                            Sources =
                            [
                                new ElasticSourceSettings { Id = "source", ServerValue = "api" },
                            ],
                        },
                    ],
                },
            ],
        };
        var persistence = new TestPersistence();
        await persistence.SaveAsync(new AppConfig { Settings = settings });
        var state = new AppState(
            new LogSourceService(),
            persistence,
            settings,
            elastic: new FakeElasticApiClient()
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
            for (var section = 0; section < 3; section++)
            {
                owner.Settings.SectionIndex = section;
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
                                (
                                    Child: child,
                                    Row: Grid.GetRow(child),
                                    Column: Grid.GetColumn(child)
                                )
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
                    var scroll in panel
                        .GetVisualDescendants()
                        .OfType<ScrollViewer>()
                        .Where(scroll =>
                            scroll.IsEffectivelyVisible
                            && scroll.Name
                                is "LabelsSettingsScrollViewer"
                                    or "ElasticSettingsScrollViewer"
                        )
                )
                    Assert.True(
                        scroll.Extent.Width <= scroll.Viewport.Width + 1,
                        $"{scroll.Name}: horizontal overflow {scroll.Extent.Width} > {scroll.Viewport.Width}"
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
            }

            // A template first created at a narrow width must reflow too.
            window.Width = 720;
            owner.Settings.SectionIndex = 0;
            Settle(window);
            owner.Settings.SectionIndex = 2;
            Settle(window);
            Assert.All(
                panel
                    .GetVisualDescendants()
                    .OfType<Grid>()
                    .Where(grid =>
                        grid.Classes.Contains("responsive-columns") && grid.IsEffectivelyVisible
                    ),
                grid => Assert.Single(grid.ColumnDefinitions)
            );
        }
        finally
        {
            window.Close();
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
