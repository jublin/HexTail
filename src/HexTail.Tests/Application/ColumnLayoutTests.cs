using HexTail.Application;
using HexTail.Domain;
using HexTail.Persistence;
using HexTail.Tailing;

namespace HexTail.Tests.Application;

public sealed class ColumnLayoutTests
{
    private static readonly LogSourceDescriptor FileSource = new(
        "file-1",
        LogSourceKind.File,
        "a.log",
        "/tmp/a.log",
        "/tmp/a.log"
    );

    private static readonly LogSourceDescriptor ElasticSource = new(
        "src-1",
        LogSourceKind.Elastic,
        "Ops / logs",
        "tip",
        ElasticSourceId: "src-1"
    );

    [Fact]
    public void DefaultFor_File_IsTimeLevelMessage()
    {
        var layout = ColumnLayout.DefaultFor(FileSource, null);

        Assert.Equal(["@time", "@level", "@message"], layout.Columns.Select(c => c.Key));
        Assert.Equal([150d, 48d, 400d], layout.Columns.Select(c => c.Width));
        Assert.False(layout.ComposedRows);
        Assert.Equal(TimeDisplay.Absolute, layout.TimeDisplay);
    }

    [Fact]
    public void DefaultFor_File_IgnoresElasticOutputFields() =>
        Assert.Equal(
            ["@time", "@level", "@message"],
            ColumnLayout.DefaultFor(FileSource, ["service"]).Columns.Select(c => c.Key)
        );

    [Fact]
    public void DefaultFor_Elastic_UsesTheViewsOutputFieldsInOrder()
    {
        var layout = ColumnLayout.DefaultFor(ElasticSource, ["service", "message"]);

        Assert.Equal(["@time", "@level", "service", "message"], layout.Columns.Select(c => c.Key));
        Assert.Equal([150d, 48d, 140d, 140d], layout.Columns.Select(c => c.Width));
    }

    [Fact]
    public void DefaultFor_ElasticWithoutOutputFields_FallsBackToMessage() =>
        Assert.Equal(
            ["@time", "@level", "@message"],
            ColumnLayout.DefaultFor(ElasticSource, []).Columns.Select(c => c.Key)
        );

    [Fact]
    public void WithColumn_AppendsOnceWithTheDefaultWidth()
    {
        var layout = ColumnLayout.DefaultFor(FileSource, null);

        var added = layout.WithColumn("service").WithColumn("service").WithColumn("@time");

        Assert.Equal(["@time", "@level", "@message", "service"], added.Columns.Select(c => c.Key));
        Assert.Equal(140, added.Columns[^1].Width);
    }

    [Fact]
    public void WithoutColumn_RemovesButKeepsAtLeastOne()
    {
        var layout = ColumnLayout.DefaultFor(FileSource, null);

        var one = layout.WithoutColumn("@time").WithoutColumn("@level");
        Assert.Equal(["@message"], one.Columns.Select(c => c.Key));

        Assert.Equal(["@message"], one.WithoutColumn("@message").Columns.Select(c => c.Key));
        Assert.Same(layout, layout.WithoutColumn("missing"));
    }

    [Theory]
    [InlineData(5, 40)]
    [InlineData(300, 300)]
    [InlineData(99999, 1200)]
    [InlineData(double.NaN, 40)]
    public void WithWidth_Clamps(double requested, double expected)
    {
        var layout = ColumnLayout.DefaultFor(FileSource, null).WithWidth("@level", requested);

        Assert.Equal(expected, layout.Columns.Single(c => c.Key == "@level").Width);
        Assert.Equal(150, layout.Columns.Single(c => c.Key == "@time").Width);
    }

    [Theory]
    [InlineData("@time", 150)]
    [InlineData("@level", 48)]
    [InlineData("@message", 400)]
    [InlineData("service", 140)]
    public void DefaultWidth_PerKey(string key, double expected) =>
        Assert.Equal(expected, ColumnLayout.DefaultWidth(key));

    [Theory]
    [InlineData(11, 128)]
    [InlineData(13, 150)]
    [InlineData(14, 161)]
    [InlineData(17, 194)]
    public void DefaultWidth_OfTheTimeColumn_GrowsWithTheFontSize(double fontSize, double expected)
    {
        Assert.Equal(expected, ColumnLayout.DefaultWidth("@time", fontSize));
        Assert.Equal(
            expected,
            ColumnLayout.DefaultFor(FileSource, null, fontSize).Columns[0].Width
        );
        Assert.Equal(
            expected,
            ColumnLayout
                .DefaultFor(FileSource, null, fontSize)
                .WithoutColumn("@time")
                .WithColumn("@time", fontSize)
                .Columns[^1]
                .Width
        );
        // 18 glyphs of the widest common monospace fonts (0.605 em) plus the 8 px gutter fit.
        Assert.True(ColumnLayout.DefaultWidth("@time", fontSize) >= 18 * 0.605 * fontSize + 8);
        Assert.Equal(48, ColumnLayout.DefaultWidth("@level", fontSize));
        Assert.Equal(400, ColumnLayout.DefaultWidth("@message", fontSize));
    }

    [Fact]
    public void FromPersisted_DropsBlankAndDuplicateKeysAndClampsWidths()
    {
        var layout = ColumnLayout.FromPersisted(
            [
                new PersistedColumn { Key = "@time", Width = 10 },
                new PersistedColumn { Key = " ", Width = 100 },
                new PersistedColumn { Key = "", Width = 100 },
                null,
                new PersistedColumn { Key = "@time", Width = 300 },
                new PersistedColumn { Key = "svc", Width = 5000 },
            ],
            composedRows: true,
            TimeDisplay.Relative
        );

        Assert.NotNull(layout);
        Assert.Equal(["@time", "svc"], layout.Columns.Select(c => c.Key));
        Assert.Equal([40d, 1200d], layout.Columns.Select(c => c.Width));
        Assert.True(layout.ComposedRows);
        Assert.Equal(TimeDisplay.Relative, layout.TimeDisplay);
    }

    [Fact]
    public void FromPersisted_UnknownTimeDisplayBecomesAbsolute() =>
        Assert.Equal(
            TimeDisplay.Absolute,
            ColumnLayout
                .FromPersisted(
                    [new PersistedColumn { Key = "@time", Width = 100 }],
                    false,
                    (TimeDisplay)7
                )!
                .TimeDisplay
        );

    [Fact]
    public void FromPersisted_NothingUsable_IsNull()
    {
        Assert.Null(ColumnLayout.FromPersisted(null, true, TimeDisplay.Relative));
        Assert.Null(
            ColumnLayout.FromPersisted([new PersistedColumn { Key = " " }], false, default)
        );
    }

    [Fact]
    public void MessageField_PicksTheFirstCandidatePresent()
    {
        var fields = new Dictionary<string, string>
        {
            ["body"] = "b",
            ["msg"] = "m",
            ["text"] = "t",
        };

        Assert.Equal("m", MessageField.Pick(fields));
        Assert.Equal("m", MessageField.Pick(new Dictionary<string, string> { ["msg"] = "m" }));
        Assert.Equal(
            "x",
            MessageField.Pick(new Dictionary<string, string> { ["message"] = "x", ["msg"] = "m" })
        );
    }

    [Fact]
    public void MessageField_IsCaseInsensitive_AndNullWhenAbsent()
    {
        Assert.Equal(
            "hi",
            MessageField.Pick(new Dictionary<string, string> { ["Message"] = "hi" })
        );
        Assert.Null(MessageField.Pick(new Dictionary<string, string> { ["other"] = "x" }));
        Assert.Null(MessageField.Pick(null));
        Assert.Null(MessageField.Pick(new Dictionary<string, string>()));
    }

    // ---- FileTabState ------------------------------------------------------------------

    private sealed class NullTailer : ILogTailer
    {
        public string SourceId => "t";
        public string DisplayName => "t";
        public Task Completion => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static FileTabState Tab(ILogParser parser, LogSourceDescriptor? source = null) =>
        new(source ?? FileSource, new FileBuffer(), parser, new NullTailer());

    private static Line Fields(params (string Key, string Value)[] fields) =>
        new("raw", fields.ToDictionary(f => f.Key, f => f.Value));

    [Fact]
    public void KnownFields_AreSortedAndDistinct()
    {
        var tab = Tab(new JsonlParser());
        tab.Buffer.Append([
            Fields(("b", "1"), ("a", "1")),
            Fields(("c", "1"), ("a", "2")),
            new("x"),
        ]);

        Assert.Equal(["a", "b", "c"], tab.KnownFields());
    }

    [Fact]
    public void KnownFields_SampleOnlyTheTail()
    {
        var tab = Tab(new JsonlParser());
        tab.Buffer.Append(Fields(("old", "1")));
        tab.Buffer.Append(Enumerable.Range(0, 5).Select(_ => Fields(("new", "1"))));

        Assert.Equal(["new"], tab.KnownFields(sample: 5));
        Assert.Equal(["new", "old"], tab.KnownFields(sample: 6));
    }

    [Fact]
    public void KnownFields_AreCapped()
    {
        var tab = Tab(new JsonlParser());
        tab.Buffer.Append(Enumerable.Range(0, 150).Select(i => Fields(($"f{i:D3}", "1"))));

        var known = tab.KnownFields(sample: 500, cap: 100);

        Assert.Equal(100, known.Count);
        Assert.Equal("f000", known[0]);
        Assert.Equal(known.Order(StringComparer.Ordinal), known);
    }

    [Fact]
    public void KnownFields_EmptyBuffer_IsEmpty() =>
        Assert.Empty(Tab(new JsonlParser()).KnownFields());

    [Fact]
    public void IsStructured_ForParserAndSourceKind()
    {
        static TimestampingParser Wrap(ILogParser inner) => new(inner, () => TimeZoneInfo.Utc);

        Assert.False(Tab(new PlainTextParser()).IsStructured);
        Assert.True(Tab(new JsonlParser()).IsStructured);
        Assert.True(Tab(new LogfmtParser()).IsStructured);
        Assert.False(Tab(Wrap(new PlainTextParser())).IsStructured);
        Assert.True(Tab(Wrap(new JsonlParser())).IsStructured);
        Assert.True(Tab(Wrap(new LogfmtParser())).IsStructured);
        Assert.True(Tab(new PlainTextParser(), ElasticSource).IsStructured);
    }

    [Fact]
    public void EffectiveColumns_UsesTheDefaultsUntilALayoutIsSet_AndEveryChangeBumpsTheVersion()
    {
        var tab = Tab(new PlainTextParser());
        Assert.Null(tab.Columns);
        var before = tab.ColumnsVersion;
        Assert.Equal(
            ["@time", "@level", "@message"],
            tab.EffectiveColumns(null).Columns.Select(c => c.Key)
        );

        var custom = ColumnLayout.DefaultFor(FileSource, null).WithoutColumn("@level");
        tab.Columns = custom;

        Assert.Same(custom, tab.EffectiveColumns(null));
        Assert.Equal(before + 1, tab.ColumnsVersion);
    }

    [Fact]
    public void EffectiveColumns_ElasticDefaultsUseTheOutputFields()
    {
        var tab = Tab(new PlainTextParser(), ElasticSource);

        Assert.Equal(
            ["@time", "@level", "service", "message"],
            tab.EffectiveColumns(["service", "message"]).Columns.Select(c => c.Key)
        );
    }
}
