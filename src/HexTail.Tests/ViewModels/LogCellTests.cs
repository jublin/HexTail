using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using HexTail.Application;
using HexTail.Domain;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.ViewModels;

public sealed class LogCellTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FormatTime_TodayShowsOnlyTheTime()
    {
        var value = new DateTimeOffset(2026, 11, 7, 10, 11, 12, 345, TimeSpan.Zero);
        Assert.Equal("10:11:12.345", LogLineViewModel.FormatTime(value, TimeZoneInfo.Utc, Now));
    }

    [Fact]
    public void FormatTime_OtherDayAddsTheDate()
    {
        var value = new DateTimeOffset(2026, 11, 6, 23, 59, 1, 7, TimeSpan.Zero);
        Assert.Equal(
            "11-06 23:59:01.007",
            LogLineViewModel.FormatTime(value, TimeZoneInfo.Utc, Now)
        );
    }

    [Fact]
    public void FormatTime_ConvertsToTheDisplayZone_AndComparesDaysInThatZone()
    {
        var plusFive = TimeZoneInfo.CreateCustomTimeZone("p5", TimeSpan.FromHours(5), "p5", "p5");
        // 22:00Z on the 7th is 03:00 on the 8th in +05:00; "now" (12:00Z) is the 7th there too.
        var value = new DateTimeOffset(2026, 11, 7, 22, 0, 0, TimeSpan.Zero);
        Assert.Equal("11-08 03:00:00.000", LogLineViewModel.FormatTime(value, plusFive, Now));
        // 02:00Z on the 7th is 07:00 on the 7th in +05:00: today.
        var early = new DateTimeOffset(2026, 11, 7, 2, 0, 0, TimeSpan.Zero);
        Assert.Equal("07:00:00.000", LogLineViewModel.FormatTime(early, plusFive, Now));
    }

    [Theory]
    [InlineData(0, "+0 ms")]
    [InlineData(12, "+12 ms")]
    [InlineData(999, "+999 ms")]
    [InlineData(1000, "+1.000 s")]
    [InlineData(1204, "+1.204 s")]
    [InlineData(59_999, "+59.999 s")]
    [InlineData(60_000, "+1 min 0 s")]
    [InlineData(184_000, "+3 min 4 s")]
    [InlineData(3_725_000, "+1 h 2 min")]
    [InlineData(-500, "-500 ms")]
    [InlineData(-1500, "-1.500 s")]
    [InlineData(-61_000, "-1 min 1 s")]
    public void FormatDelta_PicksAUnitPerMagnitude(long milliseconds, string expected) =>
        Assert.Equal(
            expected,
            LogLineViewModel.FormatDelta(TimeSpan.FromMilliseconds(milliseconds))
        );

    private static Line Json(
        string raw,
        DateTimeOffset? timestamp,
        bool inherited = false,
        params (string Key, string Value)[] fields
    ) =>
        new(raw, fields.ToDictionary(f => f.Key, f => f.Value))
        {
            Timestamp = timestamp,
            TimestampInherited = inherited,
        };

    private static async Task<(
        MainWindowViewModel Workspace,
        AppState State,
        FileTabViewModel File,
        string Path
    )> OpenAsync(string name, AppSettings? settings = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{name}");
        File.WriteAllText(path, string.Empty);
        var state = new AppState(
            new LogSourceService(),
            new TestPersistence(),
            settings ?? new AppSettings { TimeZoneMode = AppTimeZoneMode.Utc },
            credentials: new InMemoryCredentialVault()
        );
        var workspace = new MainWindowViewModel(
            state,
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        await workspace.OpenPathsCommand.Execute([path]);
        return (workspace, state, workspace.SelectedFile!, path);
    }

    private static async Task CleanupAsync(
        MainWindowViewModel workspace,
        AppState state,
        string path
    )
    {
        await workspace.DisposeAsync();
        await state.DisposeAsync();
        File.Delete(path);
    }

    private static LogLineViewModel Row(FileTabViewModel file, int index = 0)
    {
        file.SyncViews();
        var row = file.Views[0].Lines[index];
        row.SetVisible(true);
        return row;
    }

    [AvaloniaFact]
    public async Task StructuredRow_ExposesACellPerColumn()
    {
        var (workspace, state, file, path) = await OpenAsync(".jsonl");
        try
        {
            var stamp = DateTimeOffset.Now.ToUniversalTime();
            var today = new DateTimeOffset(
                stamp.Year,
                stamp.Month,
                stamp.Day,
                10,
                11,
                12,
                345,
                TimeSpan.Zero
            );
            await state.SetColumnLayoutAsync(
                file.Model,
                ColumnLayout
                    .DefaultFor(file.Model.Source, null)
                    .WithColumn("service")
                    .WithColumn("absent"),
                TestContext.Current.CancellationToken
            );
            file.Model.Buffer.Append([
                Json(
                    "{\"level\":\"error\",\"msg\":\"boom\",\"service\":\"checkout\"}",
                    today,
                    false,
                    ("level", "error"),
                    ("msg", "boom"),
                    ("service", "checkout")
                ),
            ]);
            var row = Row(file);

            Assert.True(row.UsesColumns);
            Assert.Equal(
                ["@time", "@level", "@message", "service", "absent"],
                row.Cells.Select(c => c.Key)
            );
            Assert.Equal("10:11:12.345", row.Cells[0].Text);
            Assert.Equal("ERR", row.Cells[1].Text);
            Assert.Equal("err", row.Cells[1].LevelClass);
            Assert.Equal("boom", row.Cells[2].Text);
            Assert.Equal("checkout", row.Cells[3].Text);
            Assert.Equal(string.Empty, row.Cells[4].Text);
            Assert.True(row.Cells[4].IsLast);
            Assert.False(row.Cells[3].IsLast);
            Assert.Equal(150, row.Cells[0].Width);
            Assert.Null(row.Cells[2].LevelClass);
            Assert.True(row.IsErrorRow);
        }
        finally
        {
            await CleanupAsync(workspace, state, path);
        }
    }

    [AvaloniaFact]
    public async Task MessageCell_FallsBackToTheRawLine()
    {
        var (workspace, state, file, path) = await OpenAsync(".jsonl");
        try
        {
            file.Model.Buffer.Append([new Line("not json at all")]);
            var row = Row(file);
            Assert.True(row.UsesColumns);
            Assert.Equal("not json at all", row.Cells.Single(c => c.Key == "@message").Text);
            Assert.Equal(string.Empty, row.Cells.Single(c => c.Key == "@time").Text);
            Assert.Equal(string.Empty, row.Cells.Single(c => c.Key == "@level").Text);
            Assert.Null(row.Cells.Single(c => c.Key == "@level").LevelClass);
        }
        finally
        {
            await CleanupAsync(workspace, state, path);
        }
    }

    [AvaloniaFact]
    public async Task PlainTextSource_KeepsTheSingleComposedText()
    {
        var (workspace, state, file, path) = await OpenAsync(".log");
        try
        {
            file.Model.Buffer.Append([new Line("ERROR plain text line")]);
            var row = Row(file);
            Assert.False(row.UsesColumns);
            Assert.Empty(row.Cells);
            Assert.True(row.IsErrorRow);
        }
        finally
        {
            await CleanupAsync(workspace, state, path);
        }
    }

    [AvaloniaFact]
    public async Task ComposedRows_KeepsTheSingleComposedText_AndStillTintsErrors()
    {
        var (workspace, state, file, path) = await OpenAsync(".jsonl");
        try
        {
            await state.SetColumnLayoutAsync(
                file.Model,
                ColumnLayout.DefaultFor(file.Model.Source, null) with
                {
                    ComposedRows = true,
                },
                TestContext.Current.CancellationToken
            );
            file.Model.Buffer.Append([
                Json("{\"level\":\"error\"}", null, false, ("level", "error")),
            ]);
            var row = Row(file);
            Assert.False(row.UsesColumns);
            Assert.Empty(row.Cells);
            Assert.True(row.IsErrorRow);
        }
        finally
        {
            await CleanupAsync(workspace, state, path);
        }
    }

    [AvaloniaFact]
    public async Task CellSpans_ComeFromRulesAppliedToTheCellText()
    {
        var settings = new AppSettings
        {
            TimeZoneMode = AppTimeZoneMode.Utc,
            Rules = [TestRules.Highlight("checkout", "#FF0000")],
        };
        var (workspace, state, file, path) = await OpenAsync(".jsonl", settings);
        try
        {
            await state.SetColumnLayoutAsync(
                file.Model,
                ColumnLayout.DefaultFor(file.Model.Source, null).WithColumn("service"),
                TestContext.Current.CancellationToken
            );
            file.Model.Buffer.Append([
                Json(
                    "{\"msg\":\"hello\",\"service\":\"the checkout svc\"}",
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    false,
                    ("msg", "hello"),
                    ("service", "the checkout svc")
                ),
            ]);
            var row = Row(file);
            Assert.Equal(
                [new HighlightSpan(4, 8, "#FF0000")],
                row.Cells.Single(c => c.Key == "service").Spans
            );
            Assert.Empty(row.Cells.Single(c => c.Key == "@time").Spans);
            Assert.Empty(row.Cells.Single(c => c.Key == "@message").Spans);
        }
        finally
        {
            await CleanupAsync(workspace, state, path);
        }
    }

    [AvaloniaFact]
    public async Task SearchSpans_AreAppliedPerCell()
    {
        var (workspace, state, file, path) = await OpenAsync(".jsonl");
        try
        {
            file.Model.Buffer.Append([
                Json("{\"msg\":\"a boom b\"}", null, false, ("msg", "a boom b")),
            ]);
            state.AddSearch(file.Model, "boom", MatchMode.Literal, true, "#0000FF");
            var row = Row(file);
            Assert.Equal(
                [new HighlightSpan(2, 4, "#0000FF")],
                row.Cells.Single(c => c.Key == "@message").Spans
            );
        }
        finally
        {
            await CleanupAsync(workspace, state, path);
        }
    }

    [AvaloniaFact]
    public async Task InheritedTimestamps_AreDimmed_AndTheTooltipShowsIsoAndDelta()
    {
        var (workspace, state, file, path) = await OpenAsync(".jsonl");
        try
        {
            var first = new DateTimeOffset(2026, 3, 4, 5, 6, 7, 100, TimeSpan.Zero);
            var second = first.AddMilliseconds(1204);
            file.Model.Buffer.Append([
                Json("{\"msg\":\"a\"}", first, false),
                Json("{\"msg\":\"b\"}", second, true),
            ]);
            file.SyncViews();
            var rows = file.Views[0].Lines;
            rows[0].SetVisible(true);
            rows[1].SetVisible(true);
            var firstTime = rows[0].Cells[0];
            var secondTime = rows[1].Cells[0];
            Assert.False(firstTime.IsDimmed);
            Assert.True(secondTime.IsDimmed);
            Assert.Equal("2026-03-04T05:06:07.100Z", firstTime.ToolTip);
            Assert.Equal(
                "2026-03-04T05:06:08.304Z\nsince previous row: +1.204 s",
                secondTime.ToolTip
            );
            Assert.False(rows[0].Cells[2].IsDimmed);
        }
        finally
        {
            await CleanupAsync(workspace, state, path);
        }
    }

    [AvaloniaFact]
    public async Task RelativeMode_ShowsTheDeltaFromThePreviousVisibleRow_FirstRowAbsolute()
    {
        var (workspace, state, file, path) = await OpenAsync(".jsonl");
        try
        {
            await state.SetColumnLayoutAsync(
                file.Model,
                ColumnLayout.DefaultFor(file.Model.Source, null) with
                {
                    TimeDisplay = TimeDisplay.Relative,
                },
                TestContext.Current.CancellationToken
            );
            var first = new DateTimeOffset(2026, 3, 4, 5, 6, 7, 100, TimeSpan.Zero);
            file.Model.Buffer.Append([
                Json("{\"msg\":\"a\"}", first),
                Json("{\"msg\":\"b\"}", first.AddMilliseconds(1204)),
                Json("{\"msg\":\"c\"}", first.AddMilliseconds(1204 + 12)),
                Json("{\"msg\":\"d\"}", first.AddMilliseconds(1204 + 12 - 500)),
            ]);
            file.SyncViews();
            var rows = file.Views[0].Lines;
            foreach (var row in rows)
                row.SetVisible(true);

            Assert.Equal("03-04 05:06:07.100", rows[0].Cells[0].Text);
            Assert.Equal("+1.204 s", rows[1].Cells[0].Text);
            Assert.Equal("+12 ms", rows[2].Cells[0].Text);
            Assert.Equal("-500 ms", rows[3].Cells[0].Text);
        }
        finally
        {
            await CleanupAsync(workspace, state, path);
        }
    }

    [AvaloniaFact]
    public async Task RelativeMode_RefreshesWhenAnEarlierRowIsFilteredOut()
    {
        var (workspace, state, file, path) = await OpenAsync(".jsonl");
        try
        {
            await state.SetColumnLayoutAsync(
                file.Model,
                ColumnLayout.DefaultFor(file.Model.Source, null) with
                {
                    TimeDisplay = TimeDisplay.Relative,
                },
                TestContext.Current.CancellationToken
            );
            var t0 = new DateTimeOffset(2026, 3, 4, 5, 6, 7, 0, TimeSpan.Zero);
            file.Model.Buffer.Append([
                Json("{\"msg\":\"a\"}", t0, false, ("msg", "a")),
                Json("{\"msg\":\"skip\"}", t0.AddSeconds(1), false, ("msg", "skip")),
                Json("{\"msg\":\"c\"}", t0.AddSeconds(3), false, ("msg", "c")),
            ]);
            file.SyncViews();
            var third = file.Views[0].Lines[2];
            third.SetVisible(true);
            Assert.Equal("+2.000 s", third.Cells[0].Text);

            await state.UpdateSettingsAsync(
                state.Settings with
                {
                    Rules = [TestRules.Hide("skip")],
                },
                TestContext.Current.CancellationToken
            );
            file.SyncViews();

            var rows = file.Views[0].Lines;
            Assert.Equal(2, rows.Count);
            rows[1].SetVisible(true);
            Assert.Equal("+3.000 s", rows[1].Cells[0].Text);
        }
        finally
        {
            await CleanupAsync(workspace, state, path);
        }
    }

    [AvaloniaFact]
    public async Task ChangingTheLayout_ReRendersRealizedRows()
    {
        var (workspace, state, file, path) = await OpenAsync(".jsonl");
        try
        {
            file.Model.Buffer.Append([
                Json(
                    "{\"msg\":\"a\",\"service\":\"s1\"}",
                    null,
                    false,
                    ("msg", "a"),
                    ("service", "s1")
                ),
            ]);
            var row = Row(file);
            Assert.Equal(3, row.Cells.Count);

            await state.SetColumnLayoutAsync(
                file.Model,
                file.Model.EffectiveColumns(null).WithColumn("service"),
                TestContext.Current.CancellationToken
            );
            file.SyncViews();
            Assert.Equal(4, row.Cells.Count);
            Assert.Equal("s1", row.Cells[3].Text);

            await state.SetColumnLayoutAsync(
                file.Model,
                file.Model.EffectiveColumns(null) with
                {
                    ComposedRows = true,
                },
                TestContext.Current.CancellationToken
            );
            file.SyncViews();
            Assert.False(row.UsesColumns);
            Assert.Empty(row.Cells);
        }
        finally
        {
            await CleanupAsync(workspace, state, path);
        }
    }

    [AvaloniaFact]
    public async Task ChangingTheTimeZone_ReRendersRealizedRows()
    {
        var (workspace, state, file, path) = await OpenAsync(".jsonl");
        try
        {
            // An explicit +05:00 "local" zone makes the two modes differ whatever the machine's own zone is.
            workspace.LocalZone = TimeZoneInfo.CreateCustomTimeZone(
                "t+5",
                TimeSpan.FromHours(5),
                "t+5",
                "t+5"
            );
            var stamp = new DateTimeOffset(2020, 6, 1, 12, 0, 0, TimeSpan.Zero);
            file.Model.Buffer.Append([Json("{\"msg\":\"a\"}", stamp)]);
            var row = Row(file);
            Assert.Equal("06-01 12:00:00.000", row.Cells[0].Text);

            await state.UpdateSettingsAsync(
                state.Settings with
                {
                    TimeZoneMode = AppTimeZoneMode.Local,
                },
                TestContext.Current.CancellationToken
            );
            file.SyncViews();
            Assert.Equal("06-01 17:00:00.000", row.Cells[0].Text);

            await state.UpdateSettingsAsync(
                state.Settings with
                {
                    TimeZoneMode = AppTimeZoneMode.Utc,
                },
                TestContext.Current.CancellationToken
            );
            file.SyncViews();
            Assert.Equal("06-01 12:00:00.000", row.Cells[0].Text);
        }
        finally
        {
            await CleanupAsync(workspace, state, path);
        }
    }

    [AvaloniaFact]
    public async Task RelativeMode_AppendedRowShowsTheDeltaFromTheRowBeforeIt()
    {
        var (workspace, state, file, path) = await OpenAsync(".jsonl");
        try
        {
            await state.SetColumnLayoutAsync(
                file.Model,
                ColumnLayout.DefaultFor(file.Model.Source, null) with
                {
                    TimeDisplay = TimeDisplay.Relative,
                },
                TestContext.Current.CancellationToken
            );
            var t0 = new DateTimeOffset(2026, 3, 4, 5, 6, 7, 0, TimeSpan.Zero);
            file.Model.Buffer.Append([
                Json("{\"msg\":\"a\"}", t0),
                Json("{\"msg\":\"b\"}", t0.AddMilliseconds(250)),
            ]);
            file.SyncViews();
            var rows = file.Views[0].Lines;
            Assert.Equal(2, rows.Count);

            file.Model.Buffer.Append([Json("{\"msg\":\"c\"}", t0.AddMilliseconds(250 + 3_400))]);
            file.SyncViews();

            Assert.Equal(3, rows.Count);
            rows[2].SetVisible(true);
            Assert.Equal("+3.400 s", rows[2].Cells[0].Text);
            Assert.Equal(t0.AddMilliseconds(250), rows[2].PreviousTimestamp);
        }
        finally
        {
            await CleanupAsync(workspace, state, path);
        }
    }

    [AvaloniaFact]
    public async Task RelativeMode_AfterAHeadRollover_TheNewFirstRowShowsAbsoluteTime()
    {
        var (workspace, state, file, path) = await OpenAsync(
            ".jsonl",
            new AppSettings { TimeZoneMode = AppTimeZoneMode.Utc, MaxLines = 1_000 }
        );
        try
        {
            await state.SetColumnLayoutAsync(
                file.Model,
                ColumnLayout.DefaultFor(file.Model.Source, null) with
                {
                    TimeDisplay = TimeDisplay.Relative,
                },
                TestContext.Current.CancellationToken
            );
            var t0 = new DateTimeOffset(2026, 3, 4, 5, 6, 0, 0, TimeSpan.Zero);
            file.Model.Buffer.Append(
                Enumerable.Range(0, 1_000).Select(i => Json("{\"msg\":\"x\"}", t0.AddSeconds(i)))
            );
            file.SyncViews();
            var rows = file.Views[0].Lines;
            Assert.Equal(1_000, rows.Count);
            rows[1].SetVisible(true);
            Assert.Equal("+1.000 s", rows[1].Cells[0].Text);

            // Five lines roll out of the head; the old row 5 becomes the first row and loses its predecessor.
            file.Model.Buffer.Append(
                Enumerable.Range(1_000, 5).Select(i => Json("{\"msg\":\"y\"}", t0.AddSeconds(i)))
            );
            file.SyncViews();

            Assert.Equal(1_000, rows.Count);
            Assert.Equal(t0.AddSeconds(5), rows[0].Line.Timestamp);
            Assert.Null(rows[0].PreviousTimestamp);
            rows[0].SetVisible(true);
            Assert.Equal("03-04 05:06:05.000", rows[0].Cells[0].Text);
            rows[1].SetVisible(true);
            Assert.Equal("+1.000 s", rows[1].Cells[0].Text);
            rows[^1].SetVisible(true);
            Assert.Equal("+1.000 s", rows[^1].Cells[0].Text);
            Assert.Equal(t0.AddSeconds(1_003), rows[^2].Line.Timestamp);
        }
        finally
        {
            await CleanupAsync(workspace, state, path);
        }
    }

    [AvaloniaFact]
    public async Task LevelClasses_CoverEveryLevel()
    {
        var (workspace, state, file, path) = await OpenAsync(".jsonl");
        try
        {
            string[] levels = ["trace", "debug", "info", "warn", "error"];
            file.Model.Buffer.Append([
                .. levels.Select(level => Json("{}", null, false, ("level", level))),
            ]);
            file.SyncViews();
            var rows = file.Views[0].Lines;
            foreach (var row in rows)
                row.SetVisible(true);
            Assert.Equal(["TRC", "DBG", "INF", "WRN", "ERR"], rows.Select(r => r.Cells[1].Text));
            Assert.Equal(
                ["trc", "dbg", "inf", "wrn", "err"],
                rows.Select(r => r.Cells[1].LevelClass)
            );
            Assert.Equal([false, false, false, false, true], rows.Select(r => r.IsErrorRow));
        }
        finally
        {
            await CleanupAsync(workspace, state, path);
        }
    }

    [AvaloniaFact]
    public async Task LongCellText_GetsATooltip()
    {
        var (workspace, state, file, path) = await OpenAsync(".jsonl");
        try
        {
            var message = new string('x', 200);
            file.Model.Buffer.Append([
                Json("{}", null, false, ("msg", message)),
                Json("{}", null, false, ("msg", "short")),
            ]);
            file.SyncViews();
            var rows = file.Views[0].Lines;
            foreach (var row in rows)
                row.SetVisible(true);
            Assert.Equal(message, rows[0].Cells[2].ToolTip);
            Assert.Null(rows[1].Cells[2].ToolTip);
        }
        finally
        {
            await CleanupAsync(workspace, state, path);
        }
    }

    [AvaloniaFact]
    public async Task ElasticRow_DefaultsToTimeLevelAndTheViewOutputFields()
    {
        var connection = ElasticFixtures.Connection("c1", "Prod", "api");
        var view = connection.Views[0] with { OutputFields = ["service.name", "message"] };
        connection = connection with { Views = [view] };
        await using var workspace = ElasticFixtures.Workspace();
        await workspace.State.UpdateSettingsAsync(
            new AppSettings { ElasticConnections = [connection] },
            TestContext.Current.CancellationToken
        );
        var tab = await workspace.State.OpenElasticSourceAsync(
            "c1-api",
            save: false,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var file = workspace.Files.Single(f => ReferenceEquals(f.Model, tab));
        file.Model.Buffer.Append([
            new Line(
                "composed elastic text",
                new Dictionary<string, string> { ["service.name"] = "api", ["message"] = "hello" }
            )
            {
                Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            },
        ]);
        var row = Row(file);

        Assert.True(row.UsesColumns);
        Assert.Equal(["@time", "@level", "service.name", "message"], row.Cells.Select(c => c.Key));
        Assert.Equal("api", row.Cells[2].Text);
        Assert.Equal("hello", row.Cells[3].Text);
    }
}
