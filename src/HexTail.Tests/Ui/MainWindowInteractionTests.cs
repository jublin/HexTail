using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DialogHostAvalonia;
using HexTail.Application;
using HexTail.Domain;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;
using HexTail.ViewModels;
using HexTail.Views;
using Optris.Icons.Avalonia;
using ObservableExtensions = System.ObservableExtensions;

namespace HexTail.Tests.Ui;

public sealed class MainWindowInteractionTests
{
    [AvaloniaFact]
    public void SettingsModalClosesWhenTheBackdropIsClicked()
    {
        var window = TestWindow.Create(out var viewModel);
        viewModel.SettingsOpen = true;
        window.Show();

        var host = window.FindControl<DialogHost>("SettingsDialogHost");
        Assert.NotNull(host);
        Assert.True(host.IsOpen);
        window.MouseDown(new Point(8, 8), MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(new Point(8, 8), MouseButton.Left, RawInputModifiers.None);

        Assert.False(viewModel.SettingsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ElasticTimeRangePanelIsVisibleForAnOpenElasticTab()
    {
        var connection = new ElasticConnectionSettings
        {
            Id = "server-1",
            Name = "Elastic",
            KibanaUrl = "https://kibana/",
            ElasticsearchUrl = "https://elastic/",
            Views =
            [
                new ElasticViewSettings
                {
                    Id = "view-1",
                    Name = "Logs",
                    DataViewTitle = "logs-*",
                    TimeFieldName = "@timestamp",
                    ServerField = "ident",
                    NamespaceField = "service.name",
                    OutputFields = ["message"],
                    Sources =
                    [
                        new ElasticSourceSettings
                        {
                            Id = "source-1",
                            ServerValue = "app1",
                            NamespaceValue = "prod",
                        },
                    ],
                },
            ],
        };
        await using var state = new AppState(
            new LogSourceService(),
            new TestPersistence(),
            new AppSettings { ElasticConnections = [connection] },
            elastic: new FakeElasticApiClient(),
            credentials: new InMemoryCredentialVault()
        );
        var viewModel = new MainWindowViewModel(
            state,
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        await state.OpenElasticSourceAsync("source-1", save: false);
        var window = new MainWindow(viewModel, registerNativePicker: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.IsElasticSelected);
        Assert.True(window.FindControl<StackPanel>("ElasticTimeRangePanel")!.IsVisible);
        foreach (var width in new[] { 720, 1280, 1920 })
        foreach (var density in viewModel.Settings.DensityOptions)
        {
            window.Width = width;
            window.FontSize = 24;
            viewModel.Settings.Density = density;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            foreach (
                var name in new[]
                {
                    "SettingsButton",
                    "CommandPaletteButton",
                    "OpenFileButton",
                    "SidebarToggleButton",
                    "AutoScrollToggle",
                    "InlineContextToggle",
                    "PauseToggle",
                    "ElasticTimeRangeButton",
                }
            )
            {
                var control =
                    window.FindControl<Control>(name)
                    ?? window
                        .GetVisualDescendants()
                        .OfType<Control>()
                        .First(control => control.Name == name && control.IsEffectivelyVisible);
                Assert.NotNull(control);
                var position = control.TranslatePoint(default, window)!.Value;
                Assert.True(control.IsVisible && control.Bounds.Width > 0);
                Assert.True(
                    position.X >= 0 && position.X + control.Bounds.Width <= window.Bounds.Width,
                    name
                );
            }
        }
        window.Width = 720;
        window.UpdateLayout();
        var rangeButton = window.FindControl<Button>("ElasticTimeRangeButton")!;
        Assert.True(
            rangeButton.TranslatePoint(default, window)!.Value.X + rangeButton.Bounds.Width
                <= window.Bounds.Width
        );
        var flyout = Assert.IsType<Flyout>(rangeButton.Flyout);
        flyout.ShowAt(rangeButton);
        Dispatcher.UIThread.RunJobs();
        var editor = Assert.IsType<StackPanel>(flyout.Content);
        Assert.Empty(editor.GetVisualDescendants().OfType<DatePicker>());
        Assert.Empty(editor.GetVisualDescendants().OfType<TimePicker>());
        Assert.Equal(2, editor.GetVisualDescendants().OfType<TextBox>().Count());

        viewModel.Settings.TimeZoneMode = AppTimeZoneMode.Utc;
        await WaitFor(() => state.Settings.TimeZoneMode == AppTimeZoneMode.Utc);
        viewModel.RangeFromText = "2026-08-20 10:15:30";
        viewModel.ElasticToNow = false;
        viewModel.RangeToText = "2026-08-20 10:16:45";
        Assert.True(viewModel.ElasticRangeDirty);
        Assert.Equal("= 2026-08-20 10:15:30 UTC", viewModel.RangeFromPreview);
        await viewModel.ApplyElasticTimeRangeCommand.Execute().FirstAsync();
        Assert.False(viewModel.ElasticRangeDirty);
        Assert.Null(viewModel.RangeError);
        Assert.Equal("2026-08-20T10:15:30.0000000+00:00", state.SelectedFile!.ElasticFrom);
        Assert.Equal("2026-08-20T10:16:45.0000000+00:00", state.SelectedFile.ElasticTo);
        Assert.Equal(
            "2026-08-20 10:15:30 → 2026-08-20 10:16:45 · loading…",
            viewModel.ElasticRangePill
        );

        viewModel.RangeToText = "2026-08-20 10:14:00";
        await viewModel.ApplyElasticTimeRangeCommand.Execute().FirstAsync();
        Assert.Contains("From must be at or before To", viewModel.RangeError);
        Assert.Equal("2026-08-20T10:16:45.0000000+00:00", state.SelectedFile.ElasticTo);

        viewModel.RangeFromText = "tomorrowish";
        await viewModel.ApplyElasticTimeRangeCommand.Execute().FirstAsync();
        Assert.StartsWith("From:", viewModel.RangeError);

        foreach (
            var expression in new[] { "now-5m", "now-15m", "now-1h", "now-4h", "now-24h", "now-7d" }
        )
        {
            await viewModel.ApplyRangePresetCommand.Execute(expression).FirstAsync();
            Assert.Equal(expression, state.SelectedFile.ElasticFrom);
            Assert.Equal("now", state.SelectedFile.ElasticTo);
            Assert.False(viewModel.ElasticRangeDirty);
        }
        Assert.StartsWith("Last 7 days", viewModel.ElasticRangePill);

        viewModel.Settings.TimeZoneMode = AppTimeZoneMode.Local;
        await WaitFor(() => state.Settings.TimeZoneMode == AppTimeZoneMode.Local);
        viewModel.RangeFromText = "2026-08-20 10:00";
        await viewModel.ApplyElasticTimeRangeCommand.Execute().FirstAsync();
        var wall = new DateTime(2026, 8, 20, 10, 0, 0);
        Assert.Equal(
            new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall)),
            DateTimeOffset.Parse(state.SelectedFile.ElasticFrom)
        );
        Assert.Equal(AppTimeZoneMode.Local, state.SelectedFile.ElasticInputZone);
        flyout.Hide();
        window.Close();
    }

    [AvaloniaFact]
    public async Task LongRangePillTrimsInsteadOfPushingPastTheSettingsButtonAtMinimumWidth()
    {
        await using var viewModel = ElasticFixtures.Workspace(
            ElasticFixtures.Connection("c-1", "prod", "app")
        );
        await viewModel.InitializeAsync();
        await viewModel.State.OpenElasticSourceAsync("c-1-app", save: false);
        var window = new MainWindow(viewModel, registerNativePicker: false);
        window.Width = 720;
        window.Height = 720;
        window.FontSize = 24;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        viewModel.Settings.TimeZoneMode = AppTimeZoneMode.Utc;
        await WaitFor(() => viewModel.State.Settings.TimeZoneMode == AppTimeZoneMode.Utc);
        viewModel.RangeFromText = "2026-08-20 10:15:30";
        viewModel.ElasticToNow = false;
        viewModel.RangeToText = "2026-08-21 11:16:45";
        await viewModel.ApplyElasticTimeRangeCommand.Execute().FirstAsync();
        Assert.Null(viewModel.RangeError);

        foreach (var density in viewModel.Settings.DensityOptions)
        {
            viewModel.Settings.Density = density;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var pill = window.FindControl<Button>("ElasticTimeRangeButton")!;
            var settings = window.FindControl<Button>("SettingsButton")!;
            var text = window.FindControl<TextBlock>("ElasticRangePillText")!;
            var pillRight = pill.TranslatePoint(default, window)!.Value.X + pill.Bounds.Width;
            Assert.True(
                pillRight <= settings.TranslatePoint(default, window)!.Value.X,
                density.ToString()
            );
            Assert.True(
                text.TextLayout.TextLines.Any(line => line.HasCollapsed),
                $"pill text should be trimmed ({density})"
            );
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task SearchPlaceholderAndTextFitAtMinimumWidthInEveryDensity()
    {
        var window = TestWindow.Create(out var vm);
        await vm.InitializeAsync();
        var path = Path.Combine(Path.GetTempPath(), $"hextail-search-{Guid.NewGuid():N}.log");
        await File.WriteAllTextAsync(path, "ready", TestContext.Current.CancellationToken);
        try
        {
            await vm.State.OpenFileAsync(path, save: false);
            window.Width = 720;
            window.Height = 720;
            window.Show();
            foreach (var density in vm.Settings.DensityOptions)
            {
                vm.Settings.Density = density;
                vm.Query = string.Empty;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var query = FindVisual<TextBox>(window, "QueryBox");
                Assert.Equal(VerticalAlignment.Center, query.VerticalContentAlignment);
                Assert.False(query.UseFloatingPlaceholder);
                var placeholder = query
                    .GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Single(text => text.Text == "Search this file" && text.IsVisible);
                Assert.True(placeholder.IsVisible);
                var hintPosition = placeholder.TranslatePoint(default, query)!.Value;
                Assert.True(hintPosition.Y >= 0);
                Assert.True(hintPosition.Y + placeholder.Bounds.Height <= query.Bounds.Height);
                var queryPosition = query.TranslatePoint(default, window)!.Value;
                Assert.True(
                    queryPosition.X >= 0
                        && queryPosition.X + query.Bounds.Width <= window.Bounds.Width
                );
                var add = FindVisual<Button>(window, "AddSearchButton");
                var addPosition = add.TranslatePoint(default, window)!.Value;
                Assert.True(addPosition.X + add.Bounds.Width <= window.Bounds.Width);
                query.Text = "visible search input";
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var presenter = query
                    .GetVisualDescendants()
                    .OfType<Avalonia.Controls.Presenters.TextPresenter>()
                    .Single();
                var textPosition = presenter.TranslatePoint(default, query)!.Value;
                Assert.True(
                    textPosition.Y >= 0
                        && textPosition.Y + presenter.Bounds.Height <= query.Bounds.Height
                );
                Assert.Equal("visible search input", vm.Query);
                var toggle = FindVisual<ToggleButton>(window, "CaseToggle");
                Assert.True(
                    toggle.TranslatePoint(default, window)!.Value.X
                        >= queryPosition.X + query.Bounds.Width
                );
            }
        }
        finally
        {
            window.Close();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void MainWindowComposesDedicatedWorkspaceControls()
    {
        var window = TestWindow.Create(out _);
        window.Show();

        var controlNames = window
            .GetVisualDescendants()
            .Select(control => control.GetType().Name)
            .ToHashSet();

        Assert.Contains("SourceSidebar", controlNames);
        Assert.Contains("WorkspaceError", controlNames);
        Assert.Contains("LogWorkspace", controlNames);
        window.Close();
    }

    [AvaloniaFact]
    public async Task FileAndSearchTabsUseTabalonia()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var viewModel);
            await viewModel.OpenPathsCommand.Execute([path]);
            window.Show();
            viewModel.Query = "error";
            Click(FindVisual<Button>(window, "AddSearchButton"));
            await WaitFor(() => viewModel.SelectedFile!.Model.Searches.Count == 1);
            Dispatcher.UIThread.RunJobs();

            var tabs = window
                .GetVisualDescendants()
                .OfType<TabControl>()
                .Where(tab => tab.DataContext is MainWindowViewModel or FileTabViewModel)
                .ToArray();
            var tab = Assert.Single(tabs);
            Assert.IsType<FileTabViewModel>(tab.DataContext);
            Assert.NotNull(tab.ContentTemplate);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void SettingsInspectorAndEveryComboBoxOpen()
    {
        var window = TestWindow.Create(out var viewModel);
        viewModel.SettingsOpen = true;
        window.Show();

        var panel = window.GetVisualDescendants().OfType<SettingsPanel>().Single();
        Assert.True(viewModel.SettingsOpen);
        Dispatcher.UIThread.RunJobs();
        var colorPickers = panel.GetVisualDescendants().OfType<ColorPicker>().ToArray();
        Assert.NotEmpty(colorPickers);
        Assert.All(colorPickers, picker => Assert.NotNull(picker.Template));
        foreach (var name in new[] { "DensityBox", "FontSizeBox" })
        {
            var combo = FindVisual<ComboBox>(window, name);
            combo.IsDropDownOpen = true;
            Assert.True(combo.IsDropDownOpen);
            combo.IsDropDownOpen = false;
        }

        FindVisual<ComboBox>(window, "DensityBox").SelectedItem = UiDensity.Compact;
        FindVisual<ComboBox>(window, "FontSizeBox").SelectedItem = LogFontSize.Large;
        Assert.Equal(UiDensity.Compact, viewModel.Settings.Density);
        Assert.Equal(LogFontSize.Large, viewModel.Settings.FontSize);
        Assert.Equal(4, viewModel.Settings.TabPadding.Left);
        Assert.Equal(20, viewModel.Settings.TabCloseSize);
        Assert.Equal(12, viewModel.Settings.SecondaryFontSize);
        window.Close();
    }

    [AvaloniaFact]
    public void SettingsUseRoundedInputs()
    {
        var window = TestWindow.Create(out var viewModel);
        viewModel.SettingsOpen = true;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var panel = window.GetVisualDescendants().OfType<SettingsPanel>().Single();
        var inputBorder = panel
            .GetVisualDescendants()
            .OfType<TextBox>()
            .First()
            .GetVisualDescendants()
            .OfType<Border>()
            .First();
        Assert.Equal(new CornerRadius(10), inputBorder.CornerRadius);

        window.Close();
    }

    [AvaloniaFact]
    public void TextBoxesCenterTextVertically()
    {
        var window = TestWindow.Create(out var viewModel);
        viewModel.SettingsOpen = true;
        window.Show();

        var textBox = window
            .GetVisualDescendants()
            .OfType<SettingsPanel>()
            .Single()
            .GetVisualDescendants()
            .OfType<TextBox>()
            .First();
        Assert.Equal(TextAlignment.Start, textBox.TextAlignment);
        Assert.Equal(VerticalAlignment.Center, textBox.VerticalContentAlignment);

        window.Close();
    }

    [AvaloniaFact]
    public async Task SettingsFitInAMinimumSizeWindow()
    {
        var persistence = new DelayedPersistence
        {
            Config = new AppConfig
            {
                Window = new AppWindowState { Width = 720, Height = 720 },
            },
        };
        var viewModel = new MainWindowViewModel(
            new AppState(
                new LogSourceService(),
                persistence,
                credentials: new InMemoryCredentialVault()
            ),
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        var window = new MainWindow(viewModel);
        viewModel.SettingsOpen = true;
        window.Show();
        await WaitFor(() => window.Width == 720 && window.Height == 720);

        var panel = window.GetVisualDescendants().OfType<SettingsPanel>().Single();
        Assert.InRange(panel.MinWidth, 0, window.Bounds.Width);
        Assert.InRange(panel.MinHeight, 0, window.Bounds.Height);
        Assert.InRange(panel.Bounds.Width, 1, window.Bounds.Width);
        Assert.InRange(panel.Bounds.Height, 1, window.Bounds.Height);

        window.Close();
    }

    [AvaloniaFact]
    public void SettingsContentUsesVerticalScrollingWithoutHorizontalPan()
    {
        var window = TestWindow.Create(out var viewModel);
        viewModel.SettingsOpen = true;
        window.Show();

        var panel = window.GetVisualDescendants().OfType<SettingsPanel>().Single();
        var labels = panel.FindControl<ScrollViewer>("LabelsSettingsScrollViewer");
        var elastic = panel.FindControl<ScrollViewer>("ElasticSettingsScrollViewer");
        Assert.Equal(ScrollBarVisibility.Disabled, labels!.HorizontalScrollBarVisibility);
        Assert.Equal(ScrollBarVisibility.Auto, labels.VerticalScrollBarVisibility);
        Assert.Equal(ScrollBarVisibility.Disabled, elastic!.HorizontalScrollBarVisibility);
        Assert.Equal(ScrollBarVisibility.Auto, elastic.VerticalScrollBarVisibility);

        window.Close();
    }

    [AvaloniaFact]
    public void ElasticSettingsUseTheServerCardLayout()
    {
        var window = TestWindow.Create(out var viewModel);
        viewModel.SettingsOpen = true;
        viewModel.Settings.SectionIndex = 2;
        window.Show();

        Assert.NotNull(FindVisual<Button>(window, "AddElasticServerButton"));
        window.Close();
    }

    [AvaloniaFact]
    public void SettingsOpenCloseAndEscape()
    {
        var window = TestWindow.Create(out var viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var button = window.FindControl<Button>("SettingsButton")!;

        Assert.True(button.Command!.CanExecute(button.CommandParameter));
        button.Command.Execute(button.CommandParameter);
        Assert.True(viewModel.SettingsOpen);
        button.Command.Execute(button.CommandParameter);
        Assert.False(viewModel.SettingsOpen);
        viewModel.SettingsOpen = true;
        Dispatcher.UIThread.RunJobs();
        var closeButton = FindVisual<Button>(window, "SettingsCloseButton");
        closeButton.Command!.Execute(closeButton.CommandParameter);
        Assert.False(viewModel.SettingsOpen);
        viewModel.SettingsOpen = true;

        window.Show();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);

        Assert.False(viewModel.SettingsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AppearanceThemeCanBeSelected()
    {
        var persistence = new TestPersistence();
        var window = TestWindow.Create(persistence, out var viewModel);
        await viewModel.InitializeAsync();
        viewModel.SettingsOpen = true;
        viewModel.Settings.SectionIndex = 1;
        window.Show();

        var theme = viewModel.Settings.ThemeOptions.Single(option => option.Id == "spotify");
        FindVisual<ComboBox>(window, "ThemeBox").SelectedItem = theme;
        await WaitFor(() => viewModel.State.Settings.Theme == "spotify");
        Assert.Equal("spotify", viewModel.State.Settings.Theme);
        window.Close();
    }

    [AvaloniaFact]
    public async Task LabelsAndExclusionsAddEditAndRemove()
    {
        var persistence = new TestPersistence();
        var window = TestWindow.Create(persistence, out var viewModel);
        await viewModel.InitializeAsync();
        viewModel.SettingsOpen = true;
        window.Show();

        viewModel.Settings.NewLabelText = "ERROR";
        viewModel.Settings.NewLabelColor = Colors.Magenta;
        Click(FindVisual<Button>(window, "AddLabelButton"));
        Assert.Equal(string.Empty, viewModel.Settings.NewLabelText);
        await WaitFor(() => viewModel.Settings.Labels.Count == 1);
        var label = Assert.Single(viewModel.Settings.Labels);
        Dispatcher.UIThread.RunJobs();
        var labelControls = window
            .GetVisualDescendants()
            .Where(control => ReferenceEquals(control.DataContext, label))
            .ToArray();
        labelControls
            .OfType<TextBox>()
            .Single(textBox => textBox.PlaceholderText == "Text to highlight")
            .Text = "WARN";
        await WaitFor(() => viewModel.State.Settings.GlobalLabels[0].Text == "WARN");
        Click(
            labelControls
                .OfType<Button>()
                .Single(button =>
                    ReferenceEquals(button.Command, viewModel.Settings.RemoveLabelCommand)
                )
        );
        await WaitFor(() => viewModel.Settings.Labels.Count == 0);

        viewModel.Settings.NewExclusionText = "healthcheck";
        Click(FindVisual<Button>(window, "AddExclusionButton"));
        await WaitFor(() => viewModel.Settings.Exclusions.Count == 1);
        var exclusion = Assert.Single(viewModel.Settings.Exclusions);
        Dispatcher.UIThread.RunJobs();
        var exclusionControls = window
            .GetVisualDescendants()
            .Where(control => ReferenceEquals(control.DataContext, exclusion))
            .ToArray();
        exclusionControls
            .OfType<TextBox>()
            .Single(textBox => textBox.PlaceholderText == "Text to hide")
            .Text = "probe";
        await WaitFor(() => viewModel.State.Settings.GlobalExcludeLabels[0] == "probe");
        Click(
            exclusionControls
                .OfType<Button>()
                .Single(button =>
                    ReferenceEquals(button.Command, viewModel.Settings.RemoveExclusionCommand)
                )
        );
        await WaitFor(() => viewModel.Settings.Exclusions.Count == 0);

        Assert.True(persistence.SaveCount >= 6);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SearchCanBeAddedByButtonAndEnter()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var viewModel);
            await viewModel.OpenPathsCommand.Execute([path]);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var query = FindVisual<TextBox>(window, "QueryBox");

            query.Text = "first";
            Dispatcher.UIThread.RunJobs();
            Click(FindVisual<Button>(window, "AddSearchButton"));
            await WaitFor(() => viewModel.SelectedFile!.Model.Searches.Count == 1);
            await WaitFor(() => window.GetVisualDescendants().OfType<SearchBar>().Any());

            query = FindVisual<TextBox>(window, "QueryBox");
            query.Text = "ignored";
            query.Focus();
            window.KeyPress(Key.A, RawInputModifiers.None, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(viewModel.SelectedFile!.Model.Searches);

            query.Text = "second";
            query.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            await WaitFor(() => viewModel.SelectedFile!.Model.Searches.Count == 2);

            Assert.Equal("first", viewModel.SelectedFile!.Model.Searches[0].Query.Query);
            Assert.Equal("second", viewModel.SelectedFile.Model.Searches[1].Query.Query);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task SearchTabsShowColorAndCanBeClosed()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var viewModel);
            await viewModel.OpenPathsCommand.Execute([path]);
            window.Show();
            viewModel.Query = "error";
            Click(FindVisual<Button>(window, "AddSearchButton"));
            await WaitFor(() => viewModel.SelectedFile!.Model.Searches.Count == 1);
            Dispatcher.UIThread.RunJobs();

            var searchView = viewModel.SelectedFile!.Views[1];
            var header = window
                .GetVisualDescendants()
                .OfType<Border>()
                .Single(border =>
                    ReferenceEquals(border.DataContext, searchView)
                    && border.Classes.Contains("search-tab")
                );
            Assert.Equal(Color.Parse("#F59E0B"), ((SolidColorBrush)header.BorderBrush!).Color);
            Assert.Contains(
                header.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == searchView.MatchSummary
            );

            var closeButton = window
                .GetVisualDescendants()
                .OfType<Button>()
                .Single(button => button.Classes.Contains("search-close") && button.IsVisible);
            Assert.IsType<Icon>(closeButton.Content);
            Assert.Equal(viewModel.Settings.SecondaryCloseSize, closeButton.Width);
            Assert.Equal(
                viewModel.Settings.SecondaryCloseButtonSize,
                ((Icon)closeButton.Content).FontSize
            );
            Assert.True(((Icon)closeButton.Content).IsVisible);
            Assert.Equal(50, closeButton.CornerRadius.TopLeft);
            Click(closeButton);
            await WaitFor(() => viewModel.SelectedFile!.Model.Searches.Count == 0);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task LiteralSearchDoesNotInterpretPunctuationAndRegexIsExplicit()
    {
        var path = Path.GetTempFileName();
        var persistence = new TestPersistence();
        var window = TestWindow.Create(persistence, out var vm);
        try
        {
            await vm.InitializeAsync();
            await vm.OpenPathsCommand.Execute([path]);
            window.Show();
            vm.State.SelectedFile!.Buffer.Append([
                new Line("[ERROR] failed"),
                new Line("ERROR plain"),
            ]);
            vm.Query = "[ERROR]";
            Assert.Equal(MatchMode.Literal, vm.MatchMode);
            Click(FindVisual<Button>(window, "AddSearchButton"));
            await WaitFor(() => vm.State.SelectedFile.Searches.Count == 1);
            var literal = Assert.Single(vm.State.SelectedFile.Searches);
            Assert.Equal(MatchMode.Literal, literal.Query.Mode);
            Assert.Equal([0], literal.Results);
            FindVisual<ComboBox>(window, "SearchModePicker").SelectedItem = MatchMode.Regex;
            vm.Query = "^ERROR";
            var queryBox = FindVisual<TextBox>(window, "QueryBox");
            queryBox.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await WaitFor(() => vm.State.SelectedFile.Searches.Count == 2);
            var regex = vm.State.SelectedFile.Searches[1];
            Assert.Equal(MatchMode.Regex, regex.Query.Mode);
            Assert.Equal([1], regex.Results);
            Assert.Equal(MatchMode.Regex, vm.MatchMode);
            await vm.SaveCommand.Execute().FirstAsync();
            Assert.Equal(
                [MatchMode.Literal, MatchMode.Regex],
                Assert.Single(persistence.Config!.OpenFiles).Searches.Select(search => search.Mode)
            );
            Assert.Equal("Literal", vm.SelectedFile!.Views[1].SearchModeLabel);
            Assert.Equal("Regex", vm.SelectedFile.Views[2].SearchModeLabel);
        }
        finally
        {
            window.Close();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task InvalidRegexStaysVisibleAndPreservesQuery()
    {
        var path = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var viewModel);
            await viewModel.OpenPathsCommand.Execute([path]);
            window.Show();
            viewModel.MatchMode = MatchMode.Regex;
            viewModel.Query = "[";
            Dispatcher.UIThread.RunJobs();

            Click(FindVisual<Button>(window, "AddSearchButton"));
            await WaitFor(() => viewModel.HasSearchError);

            Assert.Equal("[", viewModel.Query);
            Assert.True(FindVisual<TextBlock>(window, "SearchError").IsVisible);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task InvalidPathShowsPersistentWorkspaceError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.log");
        var window = TestWindow.Create(out var viewModel);
        await viewModel.OpenPathsCommand.Execute([path]);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var alert = FindVisual<Border>(window, "FileErrorAlert");
        Assert.True(viewModel.HasFileError);
        Assert.NotNull(alert);
        Assert.True(alert.IsVisible);
        Assert.Contains(path, Assert.IsType<TextBlock>(alert.Child).Text);

        window.Close();
    }

    [AvaloniaFact]
    public async Task FileTabsExposeSelectionPathAndFluentCloseAction()
    {
        var firstPath = Path.GetTempFileName();
        var secondPath = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Width = 1280;
            window.Show();
            await viewModel.OpenPathsCommand.Execute([firstPath, secondPath]);
            Dispatcher.UIThread.RunJobs();
            var first = viewModel.Files[0];
            var second = viewModel.Files[1];
            var tabs = window
                .GetVisualDescendants()
                .OfType<Border>()
                .Where(border => border.Classes.Contains("source-row"))
                .ToDictionary(border => Assert.IsType<FileTabViewModel>(border.DataContext));

            Click(
                window
                    .GetVisualDescendants()
                    .OfType<Button>()
                    .Single(button =>
                        ReferenceEquals(button.DataContext, first)
                        && Equals(button.CommandParameter, first)
                        && button.Content is TextBlock
                    )
            );
            Dispatcher.UIThread.RunJobs();

            Assert.Contains("selected", tabs[first].Classes);
            Assert.DoesNotContain("selected", tabs[second].Classes);
            Assert.Equal(firstPath, ToolTip.GetTip(tabs[first]));
            var closeButton = window
                .GetVisualDescendants()
                .OfType<Button>()
                .Single(button =>
                    ReferenceEquals(button.DataContext, first) && button.Content is Icon
                );
            Assert.Equal($"Close {firstPath}", ToolTip.GetTip(closeButton));
            Assert.Equal($"Close {firstPath}", AutomationProperties.GetName(closeButton));

            window.Close();
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [AvaloniaFact]
    public async Task FileTabsSelectAndCloseThroughWindowCommands()
    {
        var firstPath = Path.GetTempFileName();
        var secondPath = Path.GetTempFileName();
        try
        {
            var window = TestWindow.Create(out var viewModel);
            window.Width = 1280;
            window.Show();
            await viewModel.OpenPathsCommand.Execute([firstPath, secondPath]);
            Dispatcher.UIThread.RunJobs();
            var first = viewModel.Files[0];
            var buttons = window
                .GetVisualDescendants()
                .OfType<Button>()
                .Where(button => ReferenceEquals(button.DataContext, first))
                .ToArray();

            Click(
                buttons.Single(button =>
                    Equals(button.CommandParameter, first) && button.Content is TextBlock
                )
            );
            Assert.Same(first, viewModel.SelectedFile);
            Click(
                buttons.Single(button =>
                    Equals(button.CommandParameter, first) && button.Content is not TextBlock
                )
            );
            await WaitFor(() => !viewModel.Files.Contains(first));

            Assert.Single(viewModel.Files);
            window.Close();
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [AvaloniaFact]
    public async Task SwitchingFilesPreservesEachFilesFollowState()
    {
        var firstPath = Path.GetTempFileName();
        var secondPath = Path.GetTempFileName();
        await File.WriteAllLinesAsync(
            firstPath,
            Enumerable.Range(0, 200).Select(index => $"first {index}")
        );
        await File.WriteAllLinesAsync(
            secondPath,
            Enumerable.Range(0, 200).Select(index => $"second {index}")
        );
        try
        {
            var window = TestWindow.Create(out var viewModel);
            await viewModel.OpenPathsCommand.Execute([firstPath, secondPath]);
            await WaitFor(() =>
            {
                viewModel.State.DrainTailerEvents();
                return viewModel.Files.All(file => file.Model.Buffer.Count == 200);
            });

            var first = viewModel.Files[0];
            var second = viewModel.Files[1];
            viewModel.State.SelectFile(first.Model);
            Dispatcher.UIThread.RunJobs();
            window.Width = 1280;
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var firstButton = window
                .GetVisualDescendants()
                .OfType<Button>()
                .Single(button =>
                    ReferenceEquals(button.DataContext, first)
                    && Equals(button.CommandParameter, first)
                    && button.Content is TextBlock
                );
            var secondButton = window
                .GetVisualDescendants()
                .OfType<Button>()
                .Single(button =>
                    ReferenceEquals(button.DataContext, second)
                    && Equals(button.CommandParameter, second)
                    && button.Content is TextBlock
                );

            Click(firstButton);
            Dispatcher.UIThread.RunJobs();
            var following = window
                .GetVisualDescendants()
                .OfType<ToggleButton>()
                .Single(toggle => toggle.Name == "AutoScrollToggle" && toggle.IsEffectivelyVisible);
            following.IsChecked = false;
            Dispatcher.UIThread.RunJobs();

            Assert.False(first.Model.FollowAll);
            Assert.True(second.Model.FollowAll);
            Click(secondButton);
            Dispatcher.UIThread.RunJobs();

            Assert.True(second.Model.FollowAll);
            Assert.False(first.Model.FollowAll);
            window.Close();
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [AvaloniaFact]
    public async Task OpenShortcutAcceptsControlOrMeta()
    {
        var viewModel = new MainWindowViewModel(
            new AppState(
                new LogSourceService(),
                new TestPersistence(),
                credentials: new InMemoryCredentialVault()
            ),
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        using var handler = viewModel.PickFiles.RegisterHandler(context => context.SetOutput([]));
        var window = new ShortcutWindow(viewModel);
        var completions = 0;
        using var subscription = ObservableExtensions.Subscribe(
            viewModel.OpenCommand,
            _ => completions++
        );

        window.Show();
        foreach (var modifier in new[] { KeyModifiers.Control, KeyModifiers.Meta })
        {
            var key = window.PressShortcut(Key.O, modifier);
            Assert.True(key.Handled);
            Dispatcher.UIThread.RunJobs();
            await WaitFor(() => completions > 0);
            completions = 0;
        }

        window.Close();
        await viewModel.DisposeAsync();
    }

    [AvaloniaTheory]
    [InlineData(RawInputModifiers.Control)]
    [InlineData(RawInputModifiers.Meta)]
    public async Task FindShortcutAcceptsControlOrMeta(RawInputModifiers modifier)
    {
        var path = Path.GetTempFileName();
        var window = TestWindow.Create(out var viewModel);
        await viewModel.OpenPathsCommand.Execute([path]);
        window.Show();
        var query = FindVisual<TextBox>(window, "QueryBox");

        window.KeyPress(Key.F, modifier, PhysicalKey.None, null);

        Assert.True(query.IsFocused);
        window.Close();
        File.Delete(path);
    }

    [AvaloniaTheory]
    [InlineData(RawInputModifiers.Control)]
    [InlineData(RawInputModifiers.Meta)]
    public async Task SaveShortcutAcceptsControlOrMeta(RawInputModifiers modifier)
    {
        var persistence = new TestPersistence();
        var window = TestWindow.Create(persistence, out _);
        window.Show();

        window.KeyPress(Key.S, modifier, PhysicalKey.None, null);

        await WaitFor(() => persistence.SaveCount == 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SettingsFailureStaysInsideInspector()
    {
        var persistence = new TestPersistence { SaveError = new IOException("disk full") };
        var window = TestWindow.Create(persistence, out var viewModel);
        viewModel.SettingsOpen = true;
        window.Show();

        await viewModel.Settings.CommitAsync(
            viewModel.State.Settings with
            {
                Density = UiDensity.Compact,
            }
        );

        Assert.True(viewModel.Settings.HasSaveError);
        Assert.Contains("disk full", viewModel.Settings.SaveError);
        Assert.True(FindVisual<TextBlock>(window, "SettingsSaveError").IsVisible);
        Assert.False(viewModel.HasFileError);
        persistence.SaveError = null;
        window.Close();
    }

    [AvaloniaFact]
    public async Task DelayedRestoreAppliesWindowStateOnUiThread()
    {
        var persistence = new DelayedPersistence
        {
            Config = new AppConfig
            {
                Window = new AppWindowState { Width = 1234 },
                Settings = new AppSettings { Density = UiDensity.Compact },
            },
        };
        var viewModel = new MainWindowViewModel(
            new AppState(
                new LogSourceService(),
                persistence,
                credentials: new InMemoryCredentialVault()
            ),
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        var window = new MainWindow(viewModel);

        window.Show();
        await WaitFor(() =>
            window.Width == 1234 && viewModel.Settings.Density == UiDensity.Compact
        );
        window.Close();
    }

    private static void Click(Button button)
    {
        Assert.NotNull(button.Command);
        Assert.True(button.Command.CanExecute(button.CommandParameter));
        button.Command.Execute(button.CommandParameter);
    }

    private static T FindVisual<T>(Window window, string name)
        where T : Control
    {
        if (
            window
                .GetVisualDescendants()
                .OfType<SettingsPanel>()
                .FirstOrDefault()
                ?.FindControl<T>(name) is
            { } settingsControl
        )
            return settingsControl;
        if (
            window
                .GetVisualDescendants()
                .OfType<SearchBar>()
                .FirstOrDefault()
                ?.FindControl<T>(name) is
            { } searchControl
        )
            return searchControl;
        if (
            window
                .GetVisualDescendants()
                .OfType<WorkspaceError>()
                .FirstOrDefault()
                ?.FindControl<T>(name) is
            { } errorControl
        )
            return errorControl;
        if (
            window
                .GetVisualDescendants()
                .OfType<LogWorkspace>()
                .FirstOrDefault()
                ?.FindControl<T>(name) is
            { } workspaceControl
        )
            return workspaceControl;
        throw new InvalidOperationException($"Control '{name}' was not found.");
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < timeout)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class ShortcutWindow(MainWindowViewModel viewModel)
        : MainWindow(viewModel, registerNativePicker: false)
    {
        public KeyEventArgs PressShortcut(Key key, KeyModifiers modifiers)
        {
            var args = new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = key,
                KeyModifiers = modifiers,
            };
            OnKeyDown(args);
            return args;
        }
    }

    private sealed class DelayedPersistence : IAppPersistence
    {
        public AppConfig? Config { get; init; }

        public async ValueTask<AppConfig?> LoadAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            return Config;
        }

        public ValueTask SaveAsync(
            AppConfig config,
            CancellationToken cancellationToken = default
        ) => ValueTask.CompletedTask;
    }
}
