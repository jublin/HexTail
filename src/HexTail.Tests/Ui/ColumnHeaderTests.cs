using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Application;
using HexTail.Domain;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;
using HexTail.ViewModels;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class ColumnHeaderTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public required MainWindow Window { get; init; }
        public required MainWindowViewModel Workspace { get; init; }
        public required TestPersistence Persistence { get; init; }
        public required FileTabViewModel File { get; init; }
        public required string Path { get; init; }

        public LogView LogView =>
            Window.GetVisualDescendants().OfType<LogView>().Single(view => view.IsVisible);

        public ColumnHeaderBar Header => LogView.FindControl<ColumnHeaderBar>("ColumnHeader")!;
        public ColumnHeaderViewModel Model => ((LogViewViewModel)LogView.DataContext!).ColumnHeader;
        public ListBox List => LogView.FindControl<ListBox>("LogList")!;
        public Button MenuButton => Header.FindControl<Button>("ColumnsMenuButton")!;

        public async ValueTask DisposeAsync()
        {
            Persistence.SaveError = null;
            Window.Close();
            await Workspace.DisposeAsync();
            System.IO.File.Delete(Path);
        }

        /// <summary>The realized cells of a row; empty while the row is not realized (yet).</summary>
        public IReadOnlyList<HighlightedTextBlock> RowCells(int index)
        {
            if (List.ContainerFromIndex(index) is not ListBoxItem item)
                return [];
            var row = item.GetVisualDescendants().OfType<LogLineView>().SingleOrDefault();
            if (row is null)
                return [];
            return
            [
                .. row.FindControl<ItemsControl>("RowCells")!
                    .GetVisualDescendants()
                    .OfType<HighlightedTextBlock>(),
            ];
        }

        public HighlightedTextBlock? Cell(int row, int column) =>
            RowCells(row) is { } cells && column < cells.Count ? cells[column] : null;

        public IReadOnlyList<TextBlock> Titles() =>
            [
                .. Header
                    .GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Where(block => block.Classes.Contains("column-title")),
            ];

        public IReadOnlyList<Thumb> VisibleThumbs() =>
            [
                .. Header
                    .GetVisualDescendants()
                    .OfType<Thumb>()
                    .Where(thumb => thumb.IsEffectivelyVisible),
            ];

        public Thumb Thumb(string title) =>
            VisibleThumbs()
                .Single(thumb =>
                    Avalonia.Automation.AutomationProperties.GetName(thumb)
                    == $"Resize {title} column"
                );

        public Flyout OpenMenu()
        {
            var flyout = Assert.IsType<Flyout>(MenuButton.Flyout);
            flyout.ShowAt(MenuButton);
            Dispatcher.UIThread.RunJobs();
            return flyout;
        }

        public IReadOnlyList<CheckBox> ChoiceBoxes(Flyout flyout) =>
            [
                .. ((Control)flyout.Content!)
                    .GetVisualDescendants()
                    .OfType<CheckBox>()
                    .Where(box => box.Classes.Contains("column-choice")),
            ];

        public ColumnLayout Layout => File.Model.EffectiveColumns(null);
    }

    private static async Task<Harness> OpenAsync(
        string extension,
        IEnumerable<Line>? lines = null,
        AppSettings? settings = null,
        TestPersistence? persistence = null
    )
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, string.Empty);
        persistence ??= new TestPersistence();
        var state = new AppState(
            new LogSourceService(),
            persistence,
            settings ?? new AppSettings { TimeZoneMode = AppTimeZoneMode.Utc },
            credentials: new InMemoryCredentialVault()
        );
        var workspace = new MainWindowViewModel(
            state,
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        var window = new MainWindow(workspace);
        await workspace.OpenPathsCommand.Execute([path]);
        var file = workspace.SelectedFile!;
        file.Model.Buffer.Append(lines ?? JsonLines());
        file.SyncViews();
        window.Width = 1280;
        window.Height = 800;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var harness = new Harness
        {
            Window = window,
            Workspace = workspace,
            Persistence = persistence,
            File = file,
            Path = path,
        };
        await UiWait.Until(() => harness.List.ContainerFromIndex(0) is not null);
        return harness;
    }

    private static Line[] JsonLines() =>
        [
            new(
                "{\"msg\":\"hello\",\"level\":\"error\",\"service\":\"checkout\"}",
                new Dictionary<string, string>
                {
                    ["msg"] = "hello",
                    ["level"] = "error",
                    ["service"] = "checkout",
                }
            )
            {
                Timestamp = new DateTimeOffset(2020, 1, 2, 3, 4, 5, 6, TimeSpan.Zero),
            },
            new(
                "{\"msg\":\"again\",\"level\":\"info\",\"host\":\"h1\"}",
                new Dictionary<string, string>
                {
                    ["msg"] = "again",
                    ["level"] = "info",
                    ["host"] = "h1",
                }
            )
            {
                Timestamp = new DateTimeOffset(2020, 1, 2, 3, 4, 7, 6, TimeSpan.Zero),
            },
        ];

    private static void Drag(Thumb thumb, double delta) =>
        thumb.RaiseEvent(
            new VectorEventArgs
            {
                RoutedEvent = Avalonia.Controls.Primitives.Thumb.DragDeltaEvent,
                Vector = new Vector(delta, 0),
            }
        );

    private static void EndDrag(Thumb thumb, double total) =>
        thumb.RaiseEvent(
            new VectorEventArgs
            {
                RoutedEvent = Avalonia.Controls.Primitives.Thumb.DragCompletedEvent,
                Vector = new Vector(total, 0),
            }
        );

    private static double PersistedWidth(TestPersistence persistence, string key) =>
        Assert
            .Single(persistence.Config!.OpenFiles)
            .Columns!.Single(column => column.Key == key)
            .Width;

    [AvaloniaFact]
    public async Task Header_ShowsOneTitlePerColumn_WithTheLayoutWidths()
    {
        await using var h = await OpenAsync(".jsonl");

        Assert.True(h.Header.IsVisible);
        Assert.Equal(["Time", "Level", "Message"], h.Titles().Select(t => t.Text));
        Assert.Equal(["Time", "Level", "Message"], h.Model.Columns.Select(c => c.Title));
        Assert.Equal(["@time", "@level", "@message"], h.Model.Columns.Select(c => c.Key));
        Assert.Equal([150d, 48d], h.Model.Columns.Take(2).Select(c => c.Width));
        Assert.Equal([false, false, true], h.Model.Columns.Select(c => c.IsLast));
        // The last column fills and has no thumb; the others do.
        Assert.Equal(
            ["Resize Time column", "Resize Level column"],
            h.VisibleThumbs().Select(t => Avalonia.Automation.AutomationProperties.GetName(t))
        );
    }

    [AvaloniaFact]
    public async Task HeaderTitles_LineUpWithTheRowCells_AlsoOnABookmarkedRow()
    {
        await using var h = await OpenAsync(".jsonl");
        await UiWait.Until(() => h.RowCells(0).Count == 3 && h.Titles().Count == 3);

        double X(Visual visual) => visual.TranslatePoint(new Point(0, 0), h.Window)!.Value.X;

        void AssertAligned()
        {
            var titles = h.Titles();
            var cells = h.RowCells(0);
            for (var index = 0; index < 3; index++)
                Assert.True(
                    Math.Abs(X(titles[index]) - X(cells[index])) < 0.5,
                    $"column {index}: header {X(titles[index])} vs cell {X(cells[index])}"
                );
        }

        AssertAligned();

        // A bookmark glyph in the row must not push its cells away from the header.
        var line = h.File.Model.Buffer[0];
        h.Workspace.State.ToggleBookmark(h.File.Model, line);
        h.File.SyncViews();
        Dispatcher.UIThread.RunJobs();
        Assert.True(h.File.Views[0].Lines[0].IsBookmarked);
        await UiWait.Until(() => h.RowCells(0).Count == 3);
        AssertAligned();
    }

    [AvaloniaFact]
    public async Task DraggingAThumb_ResizesTheHeaderAndRealizedRows_AndPersistsOnDragCompleted()
    {
        await using var h = await OpenAsync(".jsonl");
        await UiWait.Until(() => h.RowCells(0).Count == 3);
        var saves = h.Persistence.SaveCount;
        var thumb = h.Thumb("Time");

        Drag(thumb, 30);
        Drag(thumb, 10);

        Assert.Equal(190, h.Model.Columns[0].Width);
        await UiWait.Until(() => h.Cell(0, 0)?.Width == 190);
        Assert.Equal(190, h.File.Model.EffectiveColumns(null).Columns[0].Width);
        // Nothing is saved while the drag is going on.
        Assert.Equal(saves, h.Persistence.SaveCount);

        EndDrag(thumb, 40);
        await UiWait.Until(() => h.Persistence.SaveCount > saves);
        Assert.Equal(190, PersistedWidth(h.Persistence, "@time"));
        await UiWait.Until(() => h.Cell(0, 0)?.Width == 190);
        Assert.Null(h.Model.Error);
    }

    [AvaloniaFact]
    public async Task DraggingAThumb_ClampsTheWidthBetween40And1200()
    {
        await using var h = await OpenAsync(".jsonl");
        var thumb = h.Thumb("Level");

        Drag(thumb, -500);
        Assert.Equal(40, h.Model.Columns[1].Width);
        // Dragging back out of the clamp follows the pointer from where the drag started.
        Drag(thumb, 520);
        Assert.Equal(68, h.Model.Columns[1].Width);
        Drag(thumb, 5_000);
        Assert.Equal(1_200, h.Model.Columns[1].Width);
        EndDrag(thumb, 5_020);

        await UiWait.Until(() => h.Persistence.Config!.OpenFiles.Single().Columns is not null);
        Assert.Equal(1_200, PersistedWidth(h.Persistence, "@level"));
    }

    [AvaloniaFact]
    public async Task DragRerenders_AreCoalescedToOnePerFrame()
    {
        await using var h = await OpenAsync(".jsonl");
        await UiWait.Until(() => h.RowCells(0).Count == 3);
        var row = h.File.Views[0].Lines[0];
        var renders = 0;
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogLineViewModel.Cells))
                renders++;
        };
        var thumb = h.Thumb("Time");

        for (var step = 0; step < 20; step++)
            Drag(thumb, 1);

        Assert.Equal(0, renders);
        await UiWait.Until(() => h.Cell(0, 0)?.Width == 170);
        Assert.Equal(1, renders);
    }

    [AvaloniaFact]
    public async Task ColumnsMenu_ListsTimeLevelMessageThenKnownFields()
    {
        await using var h = await OpenAsync(".jsonl");

        var flyout = h.OpenMenu();

        var boxes = h.ChoiceBoxes(flyout);
        Assert.Equal(
            ["Time", "Level", "Message", "host", "level", "msg", "service"],
            boxes.Select(box => (string)box.Content!)
        );
        Assert.Equal(
            [true, true, true, false, false, false, false],
            boxes.Select(box => box.IsChecked == true)
        );
        Assert.Equal(
            ["@time", "@level", "@message", "host", "level", "msg", "service"],
            h.Model.Choices.Select(choice => choice.Key)
        );
    }

    [AvaloniaFact]
    public async Task TogglingAFieldOn_AppendsAColumn_AndOff_RemovesIt()
    {
        await using var h = await OpenAsync(".jsonl");
        var flyout = h.OpenMenu();
        var service = h.ChoiceBoxes(flyout).Single(box => (string)box.Content! == "service");

        service.IsChecked = true;
        await UiWait.Until(() => h.Layout.Columns.Count == 4);

        Assert.Equal("service", h.Layout.Columns[^1].Key);
        Assert.Equal(["Time", "Level", "Message", "service"], h.Model.Columns.Select(c => c.Title));
        Assert.Equal("service", Assert.Single(h.Persistence.Config!.OpenFiles).Columns![^1].Key);
        await UiWait.Until(() => h.RowCells(0).Count == 4);
        Assert.Equal("checkout", h.RowCells(0)[3].Text);

        service.IsChecked = false;
        await UiWait.Until(() => h.Layout.Columns.Count == 3);
        Assert.DoesNotContain(h.Layout.Columns, c => c.Key == "service");
    }

    [AvaloniaFact]
    public async Task TheLastRemainingColumn_CannotBeRemoved()
    {
        await using var h = await OpenAsync(".jsonl");
        var flyout = h.OpenMenu();

        foreach (var title in new[] { "Time", "Level" })
            h.ChoiceBoxes(flyout).Single(box => (string)box.Content! == title).IsChecked = false;
        await UiWait.Until(() => h.Layout.Columns.Count == 1);

        var boxes = h.ChoiceBoxes(flyout);
        var message = boxes.Single(box => (string)box.Content! == "Message");
        await UiWait.Until(() => !message.IsEnabled);
        Assert.All(
            boxes.Where(box => !ReferenceEquals(box, message)),
            box => Assert.True(box.IsEnabled)
        );
        Assert.Empty(h.VisibleThumbs());

        // Adding another column enables the first again.
        boxes.Single(box => (string)box.Content! == "Level").IsChecked = true;
        await UiWait.Until(() => message.IsEnabled);
    }

    [AvaloniaFact]
    public async Task ComposedRow_HidesTheHeaderTitles_AndComposesTheRows()
    {
        await using var h = await OpenAsync(".jsonl");
        await UiWait.Until(() => h.RowCells(0).Count == 3);
        var flyout = h.OpenMenu();
        var composed = ((Control)flyout.Content!).FindControl<CheckBox>("ComposedRowsToggle")!;
        Assert.True(h.Model.IsVisible);

        composed.IsChecked = true;
        await UiWait.Until(() => h.Layout.ComposedRows);

        Assert.False(h.Model.IsVisible);
        Assert.False(h.Header.FindControl<Control>("ColumnTitles")!.IsVisible);
        // The menu stays reachable, or the setting could never be turned off again.
        Assert.True(h.MenuButton.IsVisible);
        var rowView = h
            .List.ContainerFromIndex(0)!
            .GetVisualDescendants()
            .OfType<LogLineView>()
            .Single();
        await UiWait.Until(() =>
            rowView.FindControl<HighlightedTextBlock>("ComposedText")!.IsVisible
        );
        Assert.False(rowView.FindControl<ItemsControl>("RowCells")!.IsVisible);
        Assert.True(Assert.Single(h.Persistence.Config!.OpenFiles).ComposedRows);

        composed.IsChecked = false;
        await UiWait.Until(() => !h.Layout.ComposedRows);
        Assert.True(h.Model.IsVisible);
        Assert.True(h.Header.FindControl<Control>("ColumnTitles")!.IsVisible);
    }

    [AvaloniaFact]
    public async Task TimeRelative_SwitchesTimeCellsToDeltas()
    {
        await using var h = await OpenAsync(".jsonl");
        await UiWait.Until(() => h.RowCells(1).Count == 3);
        var flyout = h.OpenMenu();
        Assert.True(
            ((Control)flyout.Content!).FindControl<RadioButton>("AbsoluteTimeRadio")!.IsChecked
        );

        ((Control)flyout.Content!).FindControl<RadioButton>("RelativeTimeRadio")!.IsChecked = true;

        await UiWait.Until(() => h.Layout.TimeDisplay == TimeDisplay.Relative);
        await UiWait.Until(() => h.Cell(1, 0)?.Text == "+2.000 s");
        Assert.Equal(
            TimeDisplay.Relative,
            Assert.Single(h.Persistence.Config!.OpenFiles).TimeDisplay
        );

        ((Control)flyout.Content!).FindControl<RadioButton>("AbsoluteTimeRadio")!.IsChecked = true;
        await UiWait.Until(() => h.Layout.TimeDisplay == TimeDisplay.Absolute);
        await UiWait.Until(() => h.Cell(1, 0)?.Text == "01-02 03:04:07.006");
    }

    [AvaloniaFact]
    public async Task ResetColumns_RestoresTheDefaultColumnsAndWidths()
    {
        await using var h = await OpenAsync(".jsonl");
        var flyout = h.OpenMenu();
        h.ChoiceBoxes(flyout).Single(box => (string)box.Content! == "service").IsChecked = true;
        await UiWait.Until(() => h.Layout.Columns.Count == 4);
        await UiWait.Until(() => h.VisibleThumbs().Count == 3);
        var thumb = h.Thumb("Time");
        Drag(thumb, 60);
        EndDrag(thumb, 60);
        await UiWait.Until(() => h.Layout.Columns[0].Width == 210);

        var reset = ((Control)flyout.Content!).FindControl<Button>("ResetColumnsButton")!;
        Assert.True(reset.Command!.CanExecute(null));
        reset.Command.Execute(null);

        await UiWait.Until(() => h.Layout.Columns.Count == 3);
        Assert.Equal(["@time", "@level", "@message"], h.Layout.Columns.Select(c => c.Key));
        Assert.Equal([150d, 48d, 400d], h.Layout.Columns.Select(c => c.Width));
        Assert.Equal(
            ["@time", "@level", "@message"],
            Assert.Single(h.Persistence.Config!.OpenFiles).Columns!.Select(c => c.Key)
        );
        Assert.Equal(["Time", "Level", "Message"], h.Model.Columns.Select(c => c.Title));
        Assert.Equal(
            [true, true, true, false, false, false, false],
            h.Model.Choices.Select(choice => choice.IsOn)
        );
    }

    [AvaloniaFact]
    public async Task AFailedSave_ShowsTheError_AndKeepsThePreviousLayout()
    {
        await using var h = await OpenAsync(".jsonl");
        var flyout = h.OpenMenu();
        var service = h.ChoiceBoxes(flyout).Single(box => (string)box.Content! == "service");
        var before = h.Layout;
        h.Persistence.SaveError = new IOException("disk is full");

        service.IsChecked = true;

        await UiWait.Until(() => h.Model.Error is not null);
        Assert.Contains("disk is full", h.Model.Error);
        Assert.Equal(["@time", "@level", "@message"], h.Layout.Columns.Select(c => c.Key));
        Assert.Equal(before.Columns.Select(c => c.Width), h.Layout.Columns.Select(c => c.Width));
        await UiWait.Until(() => service.IsChecked == false);
        Assert.False(h.Model.Choices.Single(choice => choice.Key == "service").IsOn);
        var error = h.Header.FindControl<TextBlock>("ColumnsError")!;
        Assert.True(error.IsVisible);
        Assert.Contains("disk is full", error.Text);

        // The next successful change clears it.
        h.Persistence.SaveError = null;
        service.IsChecked = true;
        await UiWait.Until(() => h.Layout.Columns.Count == 4);
        await UiWait.Until(() => h.Model.Error is null);
        Assert.False(error.IsVisible);
    }

    [AvaloniaFact]
    public async Task AFailedDragSave_RestoresTheWidthFromBeforeTheDrag()
    {
        await using var h = await OpenAsync(".jsonl");
        await UiWait.Until(() => h.RowCells(0).Count == 3);
        h.Persistence.SaveError = new IOException("read-only");
        var thumb = h.Thumb("Time");

        Drag(thumb, 70);
        EndDrag(thumb, 70);

        await UiWait.Until(() => h.Model.Error is not null);
        Assert.Equal(150, h.Layout.Columns[0].Width);
        Assert.Equal(150, h.Model.Columns[0].Width);
        await UiWait.Until(() => h.Cell(0, 0)?.Width == 150);
        Assert.Null(h.File.Model.Columns);
    }

    [AvaloniaFact]
    public async Task EveryHeaderControl_HasAnAccessibleNameContainingItsVisibleText()
    {
        await using var h = await OpenAsync(".jsonl");
        var flyout = h.OpenMenu();
        string Name(Control control) =>
            Avalonia.Automation.AutomationProperties.GetName(control) ?? string.Empty;

        Assert.Equal("Columns", Name(h.MenuButton));
        Assert.Equal("Resize Time column", Name(h.Thumb("Time")));
        Assert.Equal("Resize Level column", Name(h.Thumb("Level")));
        foreach (var box in h.ChoiceBoxes(flyout))
            Assert.Contains((string)box.Content!, Name(box));
        var root = (Control)flyout.Content!;
        foreach (var control in root.GetVisualDescendants().OfType<Control>())
        {
            switch (control)
            {
                case CheckBox { Content: string text } box:
                    Assert.Contains(text, Name(box));
                    break;
                case RadioButton { Content: string text } radio:
                    Assert.Contains(text, Name(radio));
                    break;
                case Button { Content: string text } button:
                    Assert.Contains(text, Name(button));
                    break;
            }
        }
        Assert.Equal("Composed row", Name(root.FindControl<CheckBox>("ComposedRowsToggle")!));
        Assert.Contains("Absolute", Name(root.FindControl<RadioButton>("AbsoluteTimeRadio")!));
        Assert.Contains("Relative", Name(root.FindControl<RadioButton>("RelativeTimeRadio")!));
        Assert.Contains("Reset columns", Name(root.FindControl<Button>("ResetColumnsButton")!));
    }

    [AvaloniaFact]
    public async Task PlainTextSource_HasNoHeader()
    {
        await using var h = await OpenAsync(".log", [new Line("plain row")]);

        Assert.False(h.Header.IsVisible);
        Assert.False(h.Model.IsVisible);
        Assert.Empty(h.Titles());
    }

    [AvaloniaFact]
    public async Task TheDefaultTimeWidth_FitsTheTimeTextAtTheLargestFontSize()
    {
        await using var h = await OpenAsync(
            ".jsonl",
            settings: new AppSettings
            {
                TimeZoneMode = AppTimeZoneMode.Utc,
                LogFontSize = LogFontSize.ExtraLarge,
            }
        );
        await UiWait.Until(() => h.RowCells(0).Count == 3);

        var block = h.RowCells(0)[0];
        Assert.Equal("01-02 03:04:05.006", block.Text);
        // 18 monospace glyphs of at most 0.605 em plus the 8 px gutter; the fixed 150 px cut it off here.
        Assert.True(block.Width >= Math.Ceiling(18 * 0.605 * 17) + 8, $"time column {block.Width}");
        Assert.True(block.Width > 150, "the default grows with the font size");
        // The header uses the same width, so the titles keep lining up.
        Assert.Equal(block.Width, h.Model.Columns[0].Width);
    }

    [AvaloniaFact]
    public async Task Palette_OffersComposedRowsAndResetColumns_ForStructuredSources()
    {
        await using var h = await OpenAsync(".jsonl");

        var items = h.Workspace.BuildPaletteItems().ToList();
        var composed = Assert.Single(items, item => item.Title == "Toggle composed rows");
        Assert.Contains(items, item => item.Title == "Reset columns");

        await composed.Execute();
        await UiWait.Until(() => h.Layout.ComposedRows);
        await composed.Execute();
        await UiWait.Until(() => !h.Layout.ComposedRows);
    }

    [AvaloniaFact]
    public async Task Palette_HidesColumnEntries_ForPlainTextSources()
    {
        await using var h = await OpenAsync(".log", [new Line("plain row")]);

        var titles = h.Workspace.BuildPaletteItems().Select(item => item.Title).ToList();

        Assert.DoesNotContain("Toggle composed rows", titles);
        Assert.DoesNotContain("Reset columns", titles);
    }

    private static Line[] AtFieldLines() =>
        [
            new(
                "{\"@timestamp\":\"t\",\"@version\":\"1\",\"msg\":\"hello\"}",
                new Dictionary<string, string>
                {
                    ["@timestamp"] = "t",
                    ["@version"] = "1",
                    ["msg"] = "hello",
                }
            ),
        ];

    [AvaloniaFact]
    public async Task ColumnsMenu_ListsRealFieldsThatStartWithAnAtSign()
    {
        await using var h = await OpenAsync(".jsonl", AtFieldLines());
        var flyout = h.OpenMenu();
        Assert.Contains("@version", h.ChoiceBoxes(flyout).Select(box => (string)box.Content!));

        // Closing the menu and changing the layout must not drop them from the list.
        h.Model.SetMenuOpen(false);
        await h.Workspace.State.SetColumnLayoutAsync(h.File.Model, h.Layout.WithColumn("msg"));
        h.File.Views[0].RefreshLayout();

        Assert.Contains(h.Model.Choices, choice => choice.Key == "@version");
        Assert.Contains(h.Model.Choices, choice => choice.Key == "@timestamp");
    }

    [AvaloniaFact]
    public async Task ADisplayedAtTimestampColumn_CanBeUnchecked()
    {
        // The field is no longer in the sampled lines, only in the saved layout.
        await using var h = await OpenAsync(
            ".jsonl",
            [new Line("{\"msg\":\"x\"}", new Dictionary<string, string> { ["msg"] = "x" })]
        );
        await h.Workspace.State.SetColumnLayoutAsync(
            h.File.Model,
            h.Layout.WithColumn("@timestamp")
        );
        h.File.Views[0].RefreshLayout();

        var flyout = h.OpenMenu();
        var box = h.ChoiceBoxes(flyout).Single(b => (string)b.Content! == "@timestamp");
        Assert.True(box.IsChecked);

        box.IsChecked = false;

        await UiWait.Until(() => h.Layout.Columns.All(column => column.Key != "@timestamp"));
        Assert.Equal(["@time", "@level", "@message"], h.Layout.Columns.Select(c => c.Key));
    }

    [AvaloniaFact]
    public async Task ADragThatEndsWhereItStarted_DoesNotSave()
    {
        await using var h = await OpenAsync(".jsonl");
        await UiWait.Until(() => h.RowCells(0).Count == 3);
        var saves = h.Persistence.SaveCount;
        var thumb = h.Thumb("Time");

        Drag(thumb, 30);
        Drag(thumb, -30);
        EndDrag(thumb, 0);

        Assert.Equal(saves, h.Persistence.SaveCount);
        Assert.Null(h.File.Model.Columns);
        Assert.Equal(150, h.Model.Columns[0].Width);
        await UiWait.Until(() => h.Cell(0, 0)?.Width == 150);
        Assert.Null(h.Model.Error);
    }

    [AvaloniaFact]
    public async Task AColumnChangeDuringADrag_ResetsTheDragState()
    {
        await using var h = await OpenAsync(".jsonl");
        await h.Workspace.State.SetColumnLayoutAsync(h.File.Model, h.Layout.WithColumn("service"));
        h.File.Views[0].RefreshLayout();
        Assert.Equal(4, h.Model.Columns.Count);
        h.Model.Resize("@level", 20);
        Assert.Equal(68, h.Model.Columns[1].Width);

        // The keys change under the unfinished drag.
        await h.Model.ResetCommand.Execute();
        Assert.Equal(3, h.Model.Columns.Count);
        Assert.Equal(48, h.Model.Columns[1].Width);

        // A new drag on the same column starts from where the column is now.
        h.Model.Resize("@level", 10);
        Assert.Equal(58, h.Model.Columns[1].Width);
    }
}
