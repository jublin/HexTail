using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
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

public sealed class StreamRenderingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Two open files merged into one open stream tab, with the tailers silenced so tests supply events by hand.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly List<string> _paths = [];

        public required MainWindow Window { get; init; }
        public required MainWindowViewModel Workspace { get; init; }
        public required TestPersistence Persistence { get; init; }
        public required LogSourceService Tailers { get; init; }
        public required FileTabState A { get; init; }
        public required FileTabState B { get; init; }
        public required FileTabState Stream { get; init; }

        public AppState State => Workspace.State;
        public FileTabViewModel Tab => Workspace.SelectedFile!;
        public LogViewViewModel View => Tab.SelectedView;

        public string ColorOf(FileTabState member) =>
            Stream
                .Stream!.Definition.Members.Single(m => m.Key == AppState.MemberKey(member))
                .Color;

        public static async Task<Harness> CreateAsync(bool structured)
        {
            var persistence = new TestPersistence();
            var tailers = new LogSourceService(
                new TailerOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(20),
                    UseFileSystemWatcher = false,
                }
            );
            var state = new AppState(
                tailers,
                persistence,
                new AppSettings { TimeZoneMode = AppTimeZoneMode.Utc },
                credentials: new InMemoryCredentialVault()
            );
            state.StreamClock = () => new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
            var workspace = new MainWindowViewModel(
                state,
                scheduler: ImmediateScheduler.Instance,
                startPolling: false
            );
            var window = new MainWindow(workspace) { Width = 1280, Height = 800 };
            var paths = new List<string>();
            foreach (
                var (name, lines) in new[]
                {
                    ("a", new[] { "10:00:01", "10:00:03" }),
                    ("b", new[] { "10:00:02", "10:00:04" }),
                }
            )
            {
                var path = Path.Combine(
                    Path.GetTempPath(),
                    $"{Guid.NewGuid():N}-{name}.{(structured ? "jsonl" : "log")}"
                );
                await File.WriteAllLinesAsync(
                    path,
                    lines.Select(time =>
                        structured
                            ? $"{{\"timestamp\":\"2026-01-01T{time}Z\",\"level\":\"info\",\"msg\":\"{name}{time[^2..]}\"}}"
                            : $"2026-01-01 {time} INFO {name}{time[^2..]}"
                    ),
                    Ct
                );
                paths.Add(path);
            }
            window.Show();
            await workspace.OpenPathsCommand.Execute(paths);
            var members = state.Files.ToArray();
            await UiWait.Until(() =>
            {
                state.DrainTailerEvents();
                return members.All(file => file.Buffer.Count == 2);
            });
            // From here on the tests deliver every line themselves.
            foreach (var member in members)
                await member.Tailer.DisposeAsync();
            var made = await state.CreateStreamAsync("Both", members, Ct);
            var stream = (await state.OpenStreamAsync(made.Id, Ct))!;
            await UiWait.Until(() => ReferenceEquals(workspace.SelectedFile?.Model, stream));
            var harness = new Harness
            {
                Window = window,
                Workspace = workspace,
                Persistence = persistence,
                Tailers = tailers,
                A = members[0],
                B = members[1],
                Stream = stream,
            };
            harness._paths.AddRange(paths);
            await UiWait.Until(() =>
            {
                harness.View.Sync();
                return harness.View.Lines.Count == 4;
            });
            return harness;
        }

        /// <summary>Delivers a line that is older than what the stream already released: it comes out late.</summary>
        public void PublishLate(FileTabState member, string time, string word)
        {
            var offset = B.Buffer[0].Timestamp!.Value.Offset;
            var line = new Line($"2026-01-01 {time} INFO {word}")
            {
                Timestamp = new DateTimeOffset(DateTime.Parse($"2026-01-01T{time}"), offset),
                SourceId = member.Id,
            };
            Assert.True(Tailers.Publish(new SourceLines(member.Id, [line])));
            State.DrainTailerEvents();
            View.Sync();
        }

        public async Task SelectAsync(FileTabState tab)
        {
            State.SelectFile(tab);
            await UiWait.Until(() => ReferenceEquals(Workspace.SelectedFile?.Model, tab));
        }

        public LogLineViewModel Row(int index)
        {
            View.Sync();
            var row = View.Lines[index];
            row.SetVisible(true);
            return row;
        }

        public async ValueTask DisposeAsync()
        {
            Persistence.SaveError = null;
            Window.Close();
            await Workspace.DisposeAsync();
            foreach (var path in _paths)
                File.Delete(path);
        }
    }

    private static string Tail(LogLineViewModel row) => row.Line.Raw[^2..];

    [AvaloniaFact]
    public async Task StructuredStream_DefaultsToTimeSourceLevelMessage_WithTheMemberNameInTheSourceCell()
    {
        await using var h = await Harness.CreateAsync(structured: true);

        var row = h.Row(0); // a01 at 10:00:01

        Assert.True(row.UsesColumns);
        Assert.Equal(["@time", "@source", "@level", "@message"], row.Cells.Select(c => c.Key));
        Assert.Equal(h.A.DisplayName, row.Cells[1].Text);
        Assert.Equal(h.B.DisplayName, h.Row(1).Cells[1].Text);
        Assert.NotEqual(h.A.DisplayName, h.B.DisplayName);
        Assert.Equal(
            ["Time", "Source", "Level", "Message"],
            h.View.ColumnHeader.Columns.Select(c => c.Title)
        );
    }

    [AvaloniaFact]
    public async Task StreamRows_CarryAStripeInTheMemberColour_AndStayColumnsEvenWithAComposedLayoutSaved()
    {
        await using var h = await Harness.CreateAsync(structured: true);
        var first = Assert.IsAssignableFrom<ISolidColorBrush>(h.Row(0).StripeBrush);
        var second = Assert.IsAssignableFrom<ISolidColorBrush>(h.Row(1).StripeBrush);
        Assert.Equal(Color.Parse(h.ColorOf(h.A)), first.Color);
        Assert.Equal(Color.Parse(h.ColorOf(h.B)), second.Color);
        Assert.NotEqual(first.Color, second.Color);
        // One brush per colour, not one per row.
        Assert.Same(h.Row(0).StripeBrush, h.Row(2).StripeBrush);

        await h.State.SetColumnLayoutAsync(
            h.Stream,
            h.View.ComputeLayout() with
            {
                ComposedRows = true,
            },
            Ct
        );
        h.View.Sync();

        // A saved composed layout is ignored for a stream: it always renders columns, with the stripe.
        var row = h.Row(1);
        Assert.True(row.UsesColumns);
        Assert.True(row.IsStreamRow);
        Assert.Equal(Color.Parse(h.ColorOf(h.B)), ((ISolidColorBrush)row.StripeBrush!).Color);
        Assert.Equal(h.B.DisplayName, row.SourceName);
        Assert.Equal(h.B.DisplayName, row.Cells.Single(c => c.Key == "@source").Text);
        Assert.True(h.View.ColumnHeader.IsVisible);
    }

    [AvaloniaFact]
    public async Task ComposedRows_IsNotOfferedForAStream_NeitherInTheMenuNorInThePalette()
    {
        await using var h = await Harness.CreateAsync(structured: true);

        Assert.False(h.View.ColumnHeader.ComposedRowsAvailable);
        await h.View.ColumnHeader.ToggleComposedAsync();
        h.View.Sync();
        Assert.False(h.View.Layout.ComposedRows);
        Assert.True(h.Row(0).UsesColumns);
        var titles = h.Workspace.BuildPaletteItems().Select(item => item.Title).ToList();
        Assert.DoesNotContain("Toggle composed rows", titles);
        Assert.Contains("Reset columns", titles);
    }

    [AvaloniaFact]
    public async Task PlainTextStream_UsesColumns_SoTheSourceNameIsText_NotJustColour()
    {
        await using var h = await Harness.CreateAsync(structured: false);

        var row = h.Row(0);

        Assert.True(row.UsesColumns);
        Assert.Equal(["@time", "@source", "@level", "@message"], row.Cells.Select(c => c.Key));
        Assert.Equal(h.A.DisplayName, row.Cells[1].Text);
        Assert.Equal(row.Line.Raw, row.Cells[3].Text);
        Assert.Equal(Color.Parse(h.ColorOf(h.A)), ((ISolidColorBrush)row.StripeBrush!).Color);
    }

    [AvaloniaFact]
    public async Task NonStreamRow_HasNoStripeAndNoSourceCell()
    {
        await using var h = await Harness.CreateAsync(structured: true);
        var file = h.Workspace.FileTabs.First(tab => ReferenceEquals(tab.Model, h.A));
        file.SyncViews();

        var row = file.Views[0].Lines[0];
        row.SetVisible(true);

        Assert.False(row.IsStreamRow);
        Assert.Null(row.StripeBrush);
        Assert.False(row.IsLate);
        Assert.DoesNotContain("@source", row.Cells.Select(c => c.Key));
    }

    [AvaloniaFact]
    public async Task LateRow_IsMarked_WithATooltipNamingItsSource()
    {
        await using var h = await Harness.CreateAsync(structured: false);

        h.PublishLate(h.A, "10:00:01", "late");

        Assert.Equal(5, h.View.Lines.Count);
        var late = h.Row(4);
        Assert.Equal("te", Tail(late));
        Assert.True(late.IsLate);
        Assert.Equal($"Arrived late from {h.A.DisplayName}", late.LateToolTip);
        var timeTip = late.Cells.Single(c => c.Key == "@time").ToolTip;
        Assert.Contains($"Arrived late from {h.A.DisplayName}", timeTip);
        Assert.DoesNotContain("Arrived late", h.Row(0).Cells.Single(c => c.Key == "@time").ToolTip);
        Assert.All(Enumerable.Range(0, 4), index => Assert.False(h.Row(index).IsLate));
        Assert.All(Enumerable.Range(0, 4), index => Assert.Null(h.Row(index).LateToolTip));
    }

    [AvaloniaFact]
    public async Task RealizedRows_ShowTheStripeTheSourceCellAndTheLateMarker()
    {
        await using var h = await Harness.CreateAsync(structured: true);
        h.PublishLate(h.A, "10:00:01", "late");
        Dispatcher.UIThread.RunJobs();
        var list = h
            .Window.GetVisualDescendants()
            .OfType<LogView>()
            .Single(view => view.IsVisible)
            .FindControl<ListBox>("LogList")!;
        await UiWait.Until(() => list.ContainerFromIndex(4) is not null);

        LogLineView RowView(int index) =>
            Assert
                .IsType<ListBoxItem>(list.ContainerFromIndex(index))
                .GetVisualDescendants()
                .OfType<LogLineView>()
                .Single();

        var stripe = RowView(0).FindControl<Border>("SourceStripe")!;
        await UiWait.Until(() => stripe.IsVisible);
        Assert.Equal(3, stripe.Width);
        Assert.Equal(
            Color.Parse(h.ColorOf(h.A)),
            Assert.IsAssignableFrom<ISolidColorBrush>(stripe.Background).Color
        );
        List<HighlightedTextBlock> Cells() =>
            [
                .. RowView(0)
                    .FindControl<ItemsControl>("RowCells")!
                    .GetVisualDescendants()
                    .OfType<HighlightedTextBlock>(),
            ];
        await UiWait.Until(() => Cells().Count == 4);
        Assert.Equal(h.A.DisplayName, Cells()[1].Text);
        Assert.False(RowView(0).FindControl<Control>("LateMarker")!.IsVisible);
        var marker = RowView(4).FindControl<Control>("LateMarker")!;
        await UiWait.Until(() => marker.IsVisible);
        Assert.Contains("Arrived late", AutomationProperties.GetName(marker));
        // The icon never takes the pointer (rows stay clickable), so it carries no tooltip of its own: the
        // message is on the row's Time cell and in the inspector.
        Assert.False(marker.IsHitTestVisible);
        Assert.Null(ToolTip.GetTip(marker));
    }

    [AvaloniaFact]
    public async Task StreamColumnHeader_TitlesTheSourceColumn_AndOffersItOnlyForStreams()
    {
        await using var h = await Harness.CreateAsync(structured: true);
        var header = h.View.ColumnHeader;
        header.Sync();
        header.SetMenuOpen(true);

        var choice = header.Choices.Single(c => c.Key == "@source");

        Assert.Equal("Source (detected)", choice.Title);
        Assert.True(choice.IsOn);
        Assert.Equal(
            ["@time", "@source", "@level", "@message"],
            header.Choices.Take(4).Select(c => c.Key)
        );
        header.SetMenuOpen(false);

        var file = h.Workspace.FileTabs.First(tab => ReferenceEquals(tab.Model, h.A));
        file.SyncViews();
        var fileHeader = file.Views[0].ColumnHeader;
        fileHeader.SetMenuOpen(true);
        Assert.DoesNotContain(fileHeader.Choices, c => c.Key == "@source");
        fileHeader.SetMenuOpen(false);

        // Hiding the column through the menu works like any other column.
        choice.IsOn = false;
        await UiWait.Until(() =>
        {
            h.View.Sync();
            return h.View.ComputeLayout().Columns.All(c => c.Key != "@source");
        });
    }

    [AvaloniaFact]
    public async Task SourceChips_ListEveryMember_WithItsColourAndName()
    {
        await using var h = await Harness.CreateAsync(structured: false);
        h.Workspace.FilterBar.Sync();

        var chips = h.Workspace.FilterBar.SourceChips;

        Assert.Equal([h.A.DisplayName, h.B.DisplayName], chips.Select(c => c.Name));
        Assert.Equal([h.ColorOf(h.A), h.ColorOf(h.B)], chips.Select(c => c.Color));
        Assert.All(chips, chip => Assert.True(chip.IsOn));
        Assert.All(chips, chip => Assert.True(chip.IsEnabled));
        Assert.Contains($"{h.A.DisplayName} shown", chips[0].AutomationName);
        Assert.True(h.Workspace.FilterBar.HasSourceChips);
    }

    [AvaloniaFact]
    public async Task ResyncingUnchangedChips_RaisesNoPropertyChanges_ButARealChangeDoes()
    {
        await using var h = await Harness.CreateAsync(structured: false);
        var bar = h.Workspace.FilterBar;
        bar.Sync();
        var chips = bar.SourceChips.ToList();
        List<string?> raised = [];
        foreach (var chip in chips)
            chip.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        bar.Sync();
        bar.Sync();

        Assert.Empty(raised);
        Assert.Equal(chips, bar.SourceChips);

        await h.State.SetHiddenSourcesAsync(h.Stream, new HashSet<string> { h.A.Id }, Ct);
        bar.Sync();

        Assert.False(chips[0].IsOn);
        Assert.Contains(nameof(SourceChipViewModel.IsOn), raised);
        Assert.Contains(nameof(SourceChipViewModel.AutomationName), raised);
    }

    [AvaloniaFact]
    public async Task SourceChips_AreAbsentOnAPlainFileTab()
    {
        await using var h = await Harness.CreateAsync(structured: false);
        h.Workspace.FilterBar.Sync();
        Assert.NotEmpty(h.Workspace.FilterBar.SourceChips);

        await h.SelectAsync(h.A);
        h.Workspace.FilterBar.Sync();

        Assert.Empty(h.Workspace.FilterBar.SourceChips);
        Assert.False(h.Workspace.FilterBar.HasSourceChips);
    }

    [AvaloniaFact]
    public async Task TurningAChipOff_HidesThatMembersRowsInEveryView_AndSavesTheChoice()
    {
        await using var h = await Harness.CreateAsync(structured: false);
        h.State.AddSearch(h.Stream, "INFO", MatchMode.Literal, false, "#ff0000");
        h.Tab.SyncViews();
        h.Workspace.FilterBar.Sync();
        Assert.Equal(2, h.Tab.Views.Count);
        var chip = h.Workspace.FilterBar.SourceChips[0];

        await chip.ToggleCommand.Execute();

        Assert.False(chip.IsOn);
        Assert.Contains($"{h.A.DisplayName} hidden", chip.AutomationName);
        h.Tab.SyncViews();
        foreach (var view in h.Tab.Views)
        {
            view.Sync();
            Assert.Equal(["02", "04"], view.Lines.Select(Tail));
        }
        var saved = Assert.Single(h.Persistence.Config!.OpenStreams);
        Assert.Equal([AppState.MemberKey(h.A)], saved.HiddenSources);
        Assert.Equal([h.A.Id], h.Stream.Stream!.HiddenSources);

        await chip.ToggleCommand.Execute();

        Assert.True(chip.IsOn);
        h.Tab.Views[0].Sync();
        Assert.Equal(["01", "02", "03", "04"], h.Tab.Views[0].Lines.Select(Tail));
        Assert.Empty(Assert.Single(h.Persistence.Config.OpenStreams).HiddenSources);
    }

    [AvaloniaFact]
    public async Task AFailedSave_ShowsOnTheFilterBar()
    {
        await using var h = await Harness.CreateAsync(structured: false);
        h.Workspace.FilterBar.Sync();
        h.Persistence.SaveError = new IOException("disk full");

        await h.Workspace.FilterBar.SourceChips[1].ToggleCommand.Execute();

        Assert.Equal("disk full", h.Workspace.FilterBar.Error);
    }

    [AvaloniaFact]
    public async Task AClosedMember_IsADisabledChipLabelledMissing()
    {
        await using var h = await Harness.CreateAsync(structured: false);
        await h.State.CloseFileAsync(h.B, Ct);
        await h.SelectAsync(h.Stream);
        h.Workspace.FilterBar.Sync();

        var chips = h.Workspace.FilterBar.SourceChips;

        Assert.Equal(2, chips.Count);
        Assert.True(chips[0].IsEnabled);
        var missing = chips[1];
        Assert.True(missing.IsMissing);
        Assert.False(missing.IsEnabled);
        Assert.False(missing.IsOn);
        Assert.Contains("missing", missing.Label);
        Assert.Contains(missing.Label, missing.AutomationName);
        // Rows of the closed member stay in the stream, still coloured and named.
        Assert.Equal(4, h.Stream.Buffer.Count);
    }

    [AvaloniaFact]
    public async Task RenderedChipRow_HasAnAccessibleNameContainingItsVisibleText()
    {
        await using var h = await Harness.CreateAsync(structured: false);
        h.Workspace.FilterBar.Sync();
        Dispatcher.UIThread.RunJobs();
        await UiWait.Until(() =>
            h
                .Window.GetVisualDescendants()
                .OfType<ToggleButton>()
                .Any(t => t.Name == "SourceToggle")
        );

        var toggles = h
            .Window.GetVisualDescendants()
            .OfType<ToggleButton>()
            .Where(toggle => toggle.Name == "SourceToggle")
            .ToList();

        Assert.Equal(2, toggles.Count);
        Assert.All(toggles, toggle => Assert.True(toggle.IsChecked));
        Assert.Contains(h.A.DisplayName, AutomationProperties.GetName(toggles[0]));
        Assert.Equal(
            h.A.DisplayName,
            toggles[0].GetVisualDescendants().OfType<TextBlock>().Single().Text
        );
        Assert.Contains(h.B.DisplayName, AutomationProperties.GetName(toggles[1]));
    }

    [AvaloniaFact]
    public async Task Inspector_ShowsTheSourceLine_ForStreamRowsOnly()
    {
        await using var h = await Harness.CreateAsync(structured: false);
        var inspector = h.Workspace.Inspector;
        h.State.SelectLine(h.Stream, h.Stream.Buffer[1]); // b02
        inspector.Sync();

        Assert.True(inspector.HasSource);
        Assert.Equal(h.B.DisplayName, inspector.SourceName);

        await h.SelectAsync(h.A);
        h.State.SelectLine(h.A, h.A.Buffer[0]);
        inspector.Sync();

        Assert.False(inspector.HasSource);
        Assert.Null(inspector.SourceName);
    }

    [AvaloniaFact]
    public async Task Inspector_SourceLine_SaysWhenTheRowArrivedLate()
    {
        await using var h = await Harness.CreateAsync(structured: false);
        h.PublishLate(h.A, "10:00:01", "late");
        var inspector = h.Workspace.Inspector;

        h.State.SelectLine(h.Stream, h.Stream.Buffer[4]);
        inspector.Sync();

        Assert.Equal(h.A.DisplayName, inspector.SourceName);
        Assert.Equal($"{h.A.DisplayName} \u00b7 arrived late", inspector.SourceText);

        h.State.SelectLine(h.Stream, h.Stream.Buffer[0]);
        inspector.Sync();

        Assert.Equal(h.A.DisplayName, inspector.SourceText);
    }

    [AvaloniaFact]
    public async Task RenderedInspector_ShowsTheSourceName_OnAStreamRow()
    {
        await using var h = await Harness.CreateAsync(structured: false);
        h.Workspace.Inspector.Open();
        h.State.SelectLine(h.Stream, h.Stream.Buffer[1]);
        h.Workspace.Inspector.Sync();
        Dispatcher.UIThread.RunJobs();

        var text = h
            .Window.GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .FirstOrDefault(block => block.Name == "InspectorSource");

        Assert.NotNull(text);
        await UiWait.Until(() => text.Text == h.B.DisplayName);
    }
}
